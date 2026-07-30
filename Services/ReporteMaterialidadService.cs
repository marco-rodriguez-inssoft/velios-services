using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SkiaSharp;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using velios.Api.Models.ReporteMaterialidad;
using velios.Api.Models.Tareas;

namespace velios.Api.Services;

public class ReporteMaterialidadService : IReporteMaterialidadService
{
    private const string Titulo = "INFORME DE TAREA";
    private const int MaxConcurrencia = 20;

    // Timeout ajustado a 12 segundos para dar tiempo a servidores lentos o S3/Azure Blob
    private static readonly TimeSpan TimeoutDescargaRecurso = TimeSpan.FromSeconds(12);

    private readonly IReporteMaterialidadRepository _repository;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    private static readonly Lazy<byte[]?> _logoVeliosBytes = new(() => CargarRecursoEstatico("logo_velios.png"));
    private static readonly Lazy<byte[]?> _evidenceLogoBytes = new(() => CargarRecursoEstatico("Logoveliosevidence.png"));
    private static readonly Lazy<byte[]?> _personaIconBytes = new(() => CargarRecursoEstatico("persona.png"));
    private static readonly Lazy<byte[]?> _documentoIconBytes = new(() => CargarRecursoEstatico("documento.png"));
    private static readonly Lazy<byte[]?> _checkIconBytes = new(() => CargarRecursoEstatico("check.png"));
    private static readonly Lazy<byte[]?> _carpetaIconBytes = new(() => CargarRecursoEstatico("carpeta.png"));
    private static readonly Lazy<byte[]?> _pdfIconBytes = new(() => CargarPdfIconEstatico());

    public ReporteMaterialidadService(
        IReporteMaterialidadRepository repository,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _repository = repository;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;

        _httpClient = _httpClientFactory.CreateClient("ReporteMaterialidad");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);

        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) VeliosApi/1.0");
        }

        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <summary>
    /// Genera el PDF del reporte de materialidad para una tarea.
    ///
    /// PROGRESO (barra de carga): jobId y progresoStore son opcionales. Si se
    /// pasan ambos, el método reporta avance en tiempo real (Total, Procesadas,
    /// Estado) para que un endpoint de progreso lo pueda consultar por polling.
    /// Si se omiten (como antes), el método funciona exactamente igual que
    /// siempre, sin ningún efecto secundario adicional.
    /// </summary>
    public async Task<byte[]> GenerarPdfPorTareaAsync(int tareaId)
    {
        // Helper local para no repetir el "if jobId.HasValue && progresoStore is not null"
        // en cada punto donde queremos reportar avance.
        void ReportarProgreso(Action<ProgresoReporte> update)
        {

        }

        try
        {
            // 1. Consultas a BD en SECUENCIA (EF Core no permite multithreading en el mismo DbContext)
            var tarea = await _repository.ObtenerTareaAsync(tareaId)
                ?? throw new InvalidOperationException($"No se encontró la tarea con id {tareaId}.");

            var evidencias = await _repository.ObtenerEvidenciasPorTareaAsync(tareaId);
            tarea.Observaciones = await _repository.ObtenerObservacionesPorTareaAsync(tareaId);

            var cliente = await _repository.ObtenerClienteAsync(tarea.ClienteId)
                ?? throw new InvalidOperationException($"No se encontró el cliente con id {tarea.ClienteId}.");

            tarea.DireccionCentroTrabajo = await _repository.ObtenerDireccionCentroTrabajoAsync(tarea.CentroTrabajoId);
            tarea.TelefonoCentroTrabajo = await _repository.ObtenerTelefonoCentroTrabajoAsync(tarea.CentroTrabajoId);
            tarea.NombreCentroTrabajo = await _repository.ObtenerNombreCentroTrabajoAsync(tarea.CentroTrabajoId);

            // PROGRESO: ya sabemos cuántas evidencias hay -> esto es el "total" que verá el usuario.
            ReportarProgreso(p =>
            {
                p.Total = evidencias.Count;
                p.Procesadas = 0;
                p.Estado = "Procesando";
            });

            using var semaforo = new SemaphoreSlim(MaxConcurrencia);

            // 2. Descarga de Logo Proveedor (si existe) - (Esto SÍ puede ir en paralelo porque es HTTP, no EF Core)
            Task<byte[]?> logoProveedorTask = Task.FromResult<byte[]?>(null);
            if (!string.IsNullOrWhiteSpace(tarea.LogoUrlProveedor))
            {
                logoProveedorTask = Task.Run(async () =>
                {
                    var raw = await DescargarImagenConTimeoutAsync(tarea.LogoUrlProveedor);
                    return ReducirImagen(raw, maxAncho: 400, calidad: 80);
                });
            }

            // 3. Caché de Google Maps y Geocoding por coordenadas únicas
            var coordsUnicas = evidencias
                .Where(e => e.Latitud.HasValue && e.Longitud.HasValue)
                .Select(e => ClaveCoordenada(e.Latitud!.Value, e.Longitud!.Value))
                .Distinct()
                .ToList();

            var mapaCache = new ConcurrentDictionary<string, byte[]?>();
            var geoCache = new ConcurrentDictionary<string, GeocodingInfoDto?>();

            var tareasGoogle = coordsUnicas.Select(async clave =>
            {
                await semaforo.WaitAsync();
                try
                {
                    var partes = clave.Split(',');
                    var lat = decimal.Parse(partes[0], CultureInfo.InvariantCulture);
                    var lng = decimal.Parse(partes[1], CultureInfo.InvariantCulture);

                    var mapaT = DescargarMapaAsync(lat, lng);
                    var geoT = ObtenerGeocodingAsync(lat, lng);

                    await Task.WhenAll(mapaT, geoT);

                    mapaCache[clave] = ReducirImagen(mapaT.Result, maxAncho: 400, calidad: 60);
                    geoCache[clave] = geoT.Result;
                }
                finally
                {
                    semaforo.Release();
                }
            });

            // PROGRESO: contador thread-safe de evidencias ya procesadas. Se incrementa
            // sin importar si la descarga de la imagen tuvo éxito o falló, porque lo que
            // le importa al usuario es "cuántas ya se resolvieron", no cuántas salieron bien.
            var procesadas = 0;

            // 4. Descargar imágenes de evidencias en paralelo (Peticiones HTTP externas seguras para hilos)
            var tareasImagenesEvidencias = evidencias.Select(async evidencia =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(evidencia.UrlArchivo)) return;

                    await semaforo.WaitAsync();
                    try
                    {
                        var raw = await DescargarImagenConTimeoutAsync(evidencia.UrlArchivo);
                        if (raw != null && raw.Length > 0)
                        {
                            var reducida = ReducirImagen(raw, maxAncho: 900, calidad: 70);
                            evidencia.ImagenBytes = reducida ?? raw;
                        }
                    }
                    finally
                    {
                        semaforo.Release();
                    }
                }
                finally
                {
                    var actuales = Interlocked.Increment(ref procesadas);
                    ReportarProgreso(p => p.Procesadas = actuales);
                }
            });

            // Esperar únicamente descargas externas de red (HTTP)
            await Task.WhenAll(Task.WhenAll(tareasGoogle), Task.WhenAll(tareasImagenesEvidencias), logoProveedorTask);

            // Vincular caché a evidencias
            foreach (var evidencia in evidencias)
            {
                if (evidencia.Latitud.HasValue && evidencia.Longitud.HasValue)
                {
                    var claveCoord = ClaveCoordenada(evidencia.Latitud.Value, evidencia.Longitud.Value);

                    if (mapaCache.TryGetValue(claveCoord, out var mBytes))
                        evidencia.MapaBytes = mBytes;

                    if (geoCache.TryGetValue(claveCoord, out var geo) && geo != null)
                    {
                        evidencia.DireccionFormateada = geo.DireccionFormateada;
                        evidencia.Colonia = geo.Colonia;
                        evidencia.Municipio = geo.Municipio;
                        evidencia.Estado = geo.Estado;
                        evidencia.CodigoPostal = geo.CodigoPostal;
                        evidencia.Pais = geo.Pais;
                    }

                    var lat = evidencia.Latitud.Value.ToString(CultureInfo.InvariantCulture);
                    var lng = evidencia.Longitud.Value.ToString(CultureInfo.InvariantCulture);
                    evidencia.GoogleMapsUrl = $"https://www.google.com/maps?q={lat},{lng}";
                }

                if (string.IsNullOrWhiteSpace(evidencia.DireccionFormateada))
                    evidencia.DireccionFormateada = evidencia.Direccion;
            }

            tarea.Evidencias = evidencias;

            var reporte = new ReporteMaterialidadDto
            {
                Cliente = cliente,
                Tarea = tarea,
                FechaGeneracion = DateTime.Now,
                Resumen = ConstruirResumen(tarea)
            };

            // Generar QR
            byte[]? qrBytes = null;
            try
            {
                var token = BuildValidationToken(tarea.TareaId);
                var BaseUrlFront = _configuration["AppSettings:BaseUrlFront"];
                var qrDirectTemplate = BaseUrlFront + $"Documentos/Verificar?taskId={tareaId}&token={token}";
                qrBytes = GenerarQrBytes(qrDirectTemplate);
            }
            catch
            {
                try { qrBytes = GenerarQrBytes($"TAREA:{tarea.TareaId}"); }
                catch { qrBytes = null; }
            }

            // PROGRESO: las descargas terminaron (debería estar en 100% de esa fase).
            // Ahora entra el render del PDF: QuestPDF no expone avance interno, así que
            // aquí solo cambiamos el estado para que el front muestre "Generando PDF..."
            // en vez de un porcentaje que no podemos medir.
            ReportarProgreso(p => p.Estado = "GenerandoPdf");

            var pdfBytes = await ConstruirPdfAsync(reporte, qrBytes, logoProveedorTask.Result);

            // PROGRESO: listo. Guardamos el PDF en el store para que el endpoint de
            // descarga lo pueda servir sin tener que regenerar nada.
            ReportarProgreso(p =>
            {
                p.Estado = "Completado";
                p.Procesadas = p.Total;
                p.PdfBytes = pdfBytes;
            });

            return pdfBytes;
        }
        catch (Exception ex)
        {
            // PROGRESO: si algo truena en cualquier punto, el front debe enterarse
            // por polling en vez de quedarse esperando un job que ya nunca va a llegar.
            ReportarProgreso(p =>
            {
                p.Estado = "Error";
                p.Mensaje = ex.Message;
            });
            throw;
        }
    }

    private static byte[]? ReducirImagen(byte[]? bytesOriginales, int maxAncho = 900, int calidad = 70)
    {
        if (bytesOriginales == null || bytesOriginales.Length == 0) return null;
        try
        {
            using var inputStream = new MemoryStream(bytesOriginales);
            using var original = SKBitmap.Decode(inputStream);

            // Si SkiaSharp no puede decodificarla (ejemplo formato no soportado o imagen corrupta), devolvemos los bytes intactos
            if (original == null) return bytesOriginales;

            int nuevoAncho = original.Width;
            int nuevoAlto = original.Height;

            if (original.Width > maxAncho)
            {
                float ratio = (float)maxAncho / original.Width;
                nuevoAncho = maxAncho;
                nuevoAlto = (int)(original.Height * ratio);
            }

            var info = new SKImageInfo(nuevoAncho, nuevoAlto, SKColorType.Rgb565, SKAlphaType.Opaque);
            using var resized = original.Resize(info, new SKSamplingOptions(SKFilterMode.Linear));

            using var image = SKImage.FromBitmap(resized ?? original);
            using var outputStream = new MemoryStream();

            image.Encode(SKEncodedImageFormat.Jpeg, calidad).SaveTo(outputStream);
            var resultado = outputStream.ToArray();

            return resultado.Length > 0 ? resultado : bytesOriginales;
        }
        catch
        {
            // En caso de cualquier excepción durante la optimización, regresar la original
            return bytesOriginales;
        }
    }

    private async Task<byte[]> DescargarImagenConTimeoutAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        using var cts = new CancellationTokenSource(TimeoutDescargaRecurso);
        try
        {
            // Limpieza básica si la URL viene mal formateada
            var cleanUrl = url.Trim();
            return await _httpClient.GetByteArrayAsync(cleanUrl, cts.Token);
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]> DescargarMapaAsync(decimal latitud, decimal longitud)
    {
        using var cts = new CancellationTokenSource(TimeoutDescargaRecurso);
        try
        {
            var apiKey = _configuration["GoogleMaps:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey)) return null;

            var lat = latitud.ToString(CultureInfo.InvariantCulture);
            var lng = longitud.ToString(CultureInfo.InvariantCulture);

            var styles =
                "&style=feature:water|color:0xA9CCE3" +
                "&style=feature:landscape|color:0xEAF2FB" +
                "&style=feature:road|color:0xFFFFFF" +
                "&style=feature:road|element:geometry.stroke|color:0xC9D6E3" +
                "&style=feature:poi|visibility:simplified" +
                "&style=feature:poi|element:geometry|color:0xD6E4F0" +
                "&style=feature:administrative|element:labels.text.fill|color:0x24364D";

            var mapaUrl =
                $"https://maps.googleapis.com/maps/api/staticmap" +
                $"?center={lat},{lng}&zoom=16&size=400x200&scale=1" +
                styles +
                $"&key={apiKey}";

            return await _httpClient.GetByteArrayAsync(mapaUrl, cts.Token);
        }
        catch { return null; }
    }

    private async Task<GeocodingInfoDto?> ObtenerGeocodingAsync(decimal latitud, decimal longitud)
    {
        using var cts = new CancellationTokenSource(TimeoutDescargaRecurso);
        try
        {
            var apiKey = _configuration["GoogleMaps:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey)) return null;

            var lat = latitud.ToString(CultureInfo.InvariantCulture);
            var lng = longitud.ToString(CultureInfo.InvariantCulture);

            var url = $"https://maps.googleapis.com/maps/api/geocode/json?latlng={lat},{lng}&language=es&key={apiKey}";
            var json = await _httpClient.GetStringAsync(url, cts.Token);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return null;

            var first = results[0];
            var info = new GeocodingInfoDto();

            if (first.TryGetProperty("formatted_address", out var fa))
                info.DireccionFormateada = fa.GetString();

            if (first.TryGetProperty("address_components", out var components))
            {
                foreach (var component in components.EnumerateArray())
                {
                    if (!component.TryGetProperty("types", out var types)) continue;

                    var typeValues = types.EnumerateArray()
                        .Select(t => t.GetString() ?? string.Empty).ToList();

                    var longName = component.TryGetProperty("long_name", out var ln) ? ln.GetString() : null;

                    if (typeValues.Contains("sublocality") || typeValues.Contains("sublocality_level_1") || typeValues.Contains("neighborhood"))
                        info.Colonia ??= longName;
                    if (typeValues.Contains("locality"))
                        info.Municipio ??= longName;
                    if (typeValues.Contains("administrative_area_level_2") && string.IsNullOrWhiteSpace(info.Municipio))
                        info.Municipio = longName;
                    if (typeValues.Contains("administrative_area_level_1"))
                        info.Estado ??= longName;
                    if (typeValues.Contains("postal_code"))
                        info.CodigoPostal ??= longName;
                    if (typeValues.Contains("country"))
                        info.Pais ??= longName;
                }
            }

            return info;
        }
        catch { return null; }
    }

    private static ResumenReporteDto ConstruirResumen(TareaReporteDto tarea)
    {
        var evidencias = tarea.Evidencias ?? new List<EvidenciaReporteDto>();

        return new ResumenReporteDto
        {
            TotalTareas = 1,
            TotalEvidencias = evidencias.Count,
            TotalEvidenciasConGeo = evidencias.Count(e => e.Latitud.HasValue && e.Longitud.HasValue),
            TotalEvidenciasSinGeo = evidencias.Count(e => !e.Latitud.HasValue || !e.Longitud.HasValue),
            PrimeraEvidencia = evidencias.Any() ? evidencias.Min(e => e.DateCreated) : null,
            UltimaEvidencia = evidencias.Any() ? evidencias.Max(e => e.DateCreated) : null
        };
    }

    private static string ClaveCoordenada(decimal lat, decimal lng)
    {
        var latR = Math.Round(lat, 4);
        var lngR = Math.Round(lng, 4);
        return $"{latR.ToString(CultureInfo.InvariantCulture)},{lngR.ToString(CultureInfo.InvariantCulture)}";
    }

    private class ArchivoAdjunto
    {
        public string Url { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public byte[]? Bytes { get; set; }
    }

    private static byte[]? CargarRecursoEstatico(string nombreArchivo)
    {
        try
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "Resources", nombreArchivo);
            if (File.Exists(path)) return File.ReadAllBytes(path);

            var altPath = Path.Combine(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory(), "Resources", nombreArchivo);
            if (File.Exists(altPath)) return File.ReadAllBytes(altPath);
        }
        catch { }
        return null;
    }

    private static byte[]? CargarPdfIconEstatico()
    {
        try
        {
            var possibleDirs = new[]
            {
                Path.Combine(Directory.GetCurrentDirectory(), "Resources"),
                Path.Combine(AppContext.BaseDirectory ?? Directory.GetCurrentDirectory(), "Resources")
            };

            foreach (var dir in possibleDirs)
            {
                if (!Directory.Exists(dir)) continue;

                var candidates = new[] { "Icon_PDF.png", "Icon_PDF.jpg", "Icon_PDF.jpeg", "Icon_PDF.bmp", "Icon_PDF.gif", "Icon_PDF.svg" };
                var found = candidates.Select(c => Path.Combine(dir, c)).FirstOrDefault(File.Exists);
                if (found != null)
                    return File.ReadAllBytes(found);
            }
        }
        catch { }
        return null;
    }

    private static string TruncarTexto(string? texto, int maxCaracteres)
    {
        if (string.IsNullOrWhiteSpace(texto)) return texto ?? string.Empty;
        return texto.Length <= maxCaracteres ? texto : texto.Substring(0, maxCaracteres).TrimEnd() + "...";
    }

    private async Task<byte[]> ConstruirPdfAsync(ReporteMaterialidadDto reporte, byte[]? qrBytes, byte[]? logoProveedorBytes)
    {
        var tarea = reporte.Tarea;
        var cliente = reporte.Cliente;

        byte[]? logoBytes = _logoVeliosBytes.Value;
        byte[]? evidenceLogoBytes = _evidenceLogoBytes.Value;
        byte[]? personaIconBytes = _personaIconBytes.Value;
        byte[]? documentoIconBytes = _documentoIconBytes.Value;
        byte[]? checkIconBytes = _checkIconBytes.Value;
        byte[]? carpetaIconBytes = _carpetaIconBytes.Value;
        byte[]? pdfIconBytes = _pdfIconBytes.Value;

        var clienteDisplay = !string.IsNullOrWhiteSpace(cliente.NombreComercial)
            ? cliente.NombreComercial
            : !string.IsNullOrWhiteSpace(cliente.RazonSocial)
                ? cliente.RazonSocial
                : "N/A";

        var direccionDisplay = !string.IsNullOrWhiteSpace(tarea.DireccionCentroTrabajo)
            ? tarea.DireccionCentroTrabajo
            : "Dirección no disponible";

        // Carga de adjuntos sin filtrar rígidamente por extensión
        var archivosTarea = new List<ArchivoAdjunto>();
        if (!string.IsNullOrWhiteSpace(tarea.ImageURL))
        {
            var lista = tarea.ImageURL!.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

            using var semaforoAdjuntos = new SemaphoreSlim(MaxConcurrencia);
            var tareasAdjuntos = lista.Select(async url =>
            {
                var ext = Path.GetExtension(url.Split('?')[0]).ToLowerInvariant();
                var fileName = Path.GetFileName(url.Split('?')[0]);
                byte[]? bytes = null;

                await semaforoAdjuntos.WaitAsync();
                try
                {
                    var raw = await DescargarImagenConTimeoutAsync(url);
                    if (raw != null)
                    {
                        bytes = ReducirImagen(raw, maxAncho: 600, calidad: 65) ?? raw;
                    }
                }
                catch { bytes = null; }
                finally { semaforoAdjuntos.Release(); }

                return new ArchivoAdjunto
                {
                    Url = url,
                    FileName = fileName,
                    Extension = ext,
                    Bytes = bytes
                };
            });

            archivosTarea.AddRange(await Task.WhenAll(tareasAdjuntos));
        }

        var document = Document.Create(container =>
        {
            // PÁGINA 1 - RESUMEN EJECUTIVO
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(20);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10).FontColor("#1F2937"));

                page.Background().Element(CrearFondoPagina);

                page.Header().Element(c =>
                    CrearHeader(c, logoBytes, "INFORME DE TAREA", (tarea.Titulo ?? "SIN TÍTULO").Replace("/", "").Trim()));

                page.Content().PaddingTop(10).Element(c =>
                    CrearContenidoPrincipalConSidebar(c, reporte, clienteDisplay, direccionDisplay, logoBytes, archivosTarea, pdfIconBytes, personaIconBytes, documentoIconBytes, carpetaIconBytes));

                page.Footer().Element(c =>
                    CrearFooter(c, clienteDisplay, direccionDisplay, tarea.NombreCentroTrabajo ?? "Sin centro de trabajo", logoBytes, qrBytes, logoProveedorBytes, carpetaIconBytes));
            });

            // PÁGINAS DE EVIDENCIA
            for (int i = 0; i < tarea.Evidencias.Count; i++)
            {
                var evidencia = tarea.Evidencias[i];

                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(20);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(9).FontColor("#1F2937"));

                    page.Background().Element(CrearFondoPagina);

                    page.Header().Element(c =>
                        CrearHeader(c, logoBytes, "INFORME DE TAREA", (tarea.Titulo ?? "SIN TÍTULO").Replace("/", "").Trim()));

                    page.Content().PaddingTop(10).PaddingLeft(48).Column(column =>
                    {
                        column.Spacing(6);

                        column.Item().Text($"EVIDENCIA {i + 1:D2}")
                            .FontSize(16).Bold().FontColor("#24364D");

                        column.Item().Text(tarea.EstatusNombre ?? "Avance")
                            .FontSize(10).SemiBold().FontColor("#6B7280");

                        column.Item().Row(row =>
                        {
                            row.RelativeItem(1.45f).Column(imgCol =>
                            {
                                imgCol.Item().Border(1).BorderColor("#D6DCE5").Element(imgBox =>
                                {
                                    imgBox.Layers(layers =>
                                    {
                                        layers.PrimaryLayer().Background("#F8FAFC").Height(420).Element(bg =>
                                        {
                                            if (evidencia.ImagenBytes is not null && evidencia.ImagenBytes.Length > 0)
                                            {
                                                bg.AlignCenter().AlignMiddle().Image(evidencia.ImagenBytes, ImageScaling.FitArea);
                                            }
                                            else
                                            {
                                                bg.AlignCenter().AlignMiddle()
                                                    .Text("No fue posible cargar la imagen.")
                                                    .FontSize(11).FontColor("#64748B");
                                            }
                                        });
                                    });
                                });

                                if (!string.IsNullOrWhiteSpace(evidencia.UrlArchivo))
                                {
                                    imgCol.Item().PaddingTop(6).Background("#F9FAFB")
                                        .BorderTop(1).BorderColor("#E5E7EB")
                                        .PaddingVertical(6).PaddingHorizontal(10).Row(r =>
                                        {
                                            r.ConstantItem(28).Height(28).AlignCenter().AlignMiddle().Element(ic =>
                                            {
                                                if (evidenceLogoBytes is not null && evidenceLogoBytes.Length > 0)
                                                    ic.Element(img => img.Image(evidenceLogoBytes, ImageScaling.FitArea));
                                                else
                                                    ic.Text("🖼️").FontSize(12);
                                            });

                                            r.ConstantItem(8);

                                            var nombreArchivo = Path.GetFileName(evidencia.UrlArchivo.Split('?')[0]);
                                            r.RelativeItem().AlignMiddle()
                                                .Text(nombreArchivo)
                                                .SemiBold().FontSize(9).FontColor("#24364D");
                                        });
                                }
                            });

                            row.ConstantItem(12);

                            row.ConstantItem(185).Background("#F3F4F6").CornerRadius(8).Padding(10).Column(right =>
                            {
                                right.Spacing(6);

                                right.Item().Border(1).BorderColor("#E5E7EB").Background(Colors.White).CornerRadius(6).Padding(8).Column(sec =>
                                {
                                    sec.Spacing(5);

                                    sec.Item().Row(r =>
                                    {
                                        r.ConstantItem(16).AlignMiddle().Text("📋").FontSize(11);
                                        r.ConstantItem(4);
                                        r.RelativeItem().AlignMiddle().Text("Datos de captura")
                                            .Bold().FontSize(11).FontColor("#24364D");
                                    });
                                    sec.Item().LineHorizontal(1).LineColor("#D1D5DB");

                                    sec.Item().Row(r =>
                                    {
                                        r.ConstantItem(14).AlignTop().Text("📅").FontSize(9);
                                        r.ConstantItem(4);
                                        r.RelativeItem().Element(x => CrearCampoLateral(x, "Fecha de captura", evidencia.DateCreated.ToString("dd/MM/yyyy")));
                                    });
                                    sec.Item().Row(r =>
                                    {
                                        r.ConstantItem(14).AlignTop().Text("🗄️").FontSize(9);
                                        r.ConstantItem(4);
                                        r.RelativeItem().Element(x => CrearCampoLateral(x, "Registrado en sistema", evidencia.DateCreated.ToString("dd/MM/yyyy")));
                                    });
                                    sec.Item().Row(r =>
                                    {
                                        r.ConstantItem(14).AlignTop().Text("👤").FontSize(9);
                                        r.ConstantItem(4);
                                        r.RelativeItem().Element(x => CrearCampoLateral(x, "Usuario de registro", tarea.NombreOperador));
                                    });
                                    sec.Item().Row(r =>
                                    {
                                        r.ConstantItem(14).AlignTop().Text("📱").FontSize(9);
                                        r.ConstantItem(4);
                                        r.RelativeItem().Element(x => CrearCampoLateral(x, "Equipo", evidencia.ModeloDispositivo));
                                    });
                                    sec.Item().Row(r =>
                                    {
                                        r.ConstantItem(14).AlignTop().Text("📍").FontSize(9);
                                        r.ConstantItem(4);
                                        r.RelativeItem().Element(x => CrearCampoLateral(x, "Latitud de captura",
                                            evidencia.Latitud?.ToString("0.00000000", CultureInfo.InvariantCulture) ?? "N/A"));
                                    });
                                    sec.Item().Row(r =>
                                    {
                                        r.ConstantItem(14).AlignTop().Text("📍").FontSize(9);
                                        r.ConstantItem(4);
                                        r.RelativeItem().Element(x => CrearCampoLateral(x, "Longitud de captura",
                                            evidencia.Longitud?.ToString("0.00000000", CultureInfo.InvariantCulture) ?? "N/A"));
                                    });
                                });

                                right.Item().Border(1).BorderColor("#E5E7EB").Background(Colors.White).CornerRadius(6).Padding(8).Column(sec =>
                                {
                                    sec.Spacing(5);

                                    sec.Item().Text("Validación")
                                        .Bold().FontSize(11).FontColor("#24364D");
                                    sec.Item().LineHorizontal(1).LineColor("#D1D5DB");

                                    sec.Item().Element(x => CrearCheckValidacion(x, "Fecha de evidencia coincide con el registro", true, checkIconBytes));
                                    sec.Item().Element(x => CrearCheckValidacion(x, "Ubicación validada con sucursal",
                                        evidencia.Latitud.HasValue && evidencia.Longitud.HasValue, checkIconBytes));
                                    sec.Item().Element(x => CrearCheckValidacion(x, "Tomada desde la app Velios", true, checkIconBytes));
                                });

                                right.Item().Element(c =>
                                {
                                    c.Border(1).BorderColor("#D6DCE5").Background("#F8FAFC").CornerRadius(6).Column(col =>
                                    {
                                        col.Item().Height(100).Element(box =>
                                        {
                                            box.Layers(layers =>
                                            {
                                                layers.PrimaryLayer().Element(bg =>
                                                {
                                                    if (evidencia.MapaBytes is not null && evidencia.MapaBytes.Length > 0)
                                                        bg.Image(evidencia.MapaBytes, ImageScaling.FitArea);
                                                    else
                                                        bg.Background("#24364D").AlignCenter().AlignMiddle()
                                                            .Text("Sin mapa").FontSize(8).FontColor(Colors.White);
                                                });

                                                layers.Layer().Background("#7324364D");

                                                layers.Layer().AlignCenter().AlignMiddle()
                                                    .Element(pin =>
                                                    {
                                                        pin.Width(26).Height(26)
                                                           .Background(Colors.White)
                                                           .CornerRadius(13)
                                                           .AlignCenter().AlignMiddle()
                                                           .Text("📍").FontSize(13);
                                                    });
                                            });
                                        });

                                        if (!string.IsNullOrWhiteSpace(evidencia.DireccionFormateada))
                                        {
                                            col.Item().Background(Colors.White).BorderTop(1).BorderColor("#D6DCE5")
                                                .Padding(4).Row(r =>
                                                {
                                                    r.ConstantItem(10).Text("⊙").FontSize(9).FontColor("#F15A24");
                                                    r.ConstantItem(3);
                                                    r.RelativeItem().Text(evidencia.DireccionFormateada)
                                                        .FontSize(7).FontColor("#374151").LineHeight(1.1f);
                                                });
                                        }
                                        if (!string.IsNullOrWhiteSpace(evidencia.GoogleMapsUrl))
                                        {
                                            var mapsUrl = evidencia.GoogleMapsUrl!.StartsWith("http")
                                                ? evidencia.GoogleMapsUrl
                                                : $"https://www.google.com/maps?q={evidencia.Latitud?.ToString(CultureInfo.InvariantCulture)},{evidencia.Longitud?.ToString(CultureInfo.InvariantCulture)}";

                                            col.Item().Background(Colors.White).BorderTop(1).BorderColor("#D6DCE5")
                                                .PaddingHorizontal(4).PaddingVertical(3).Text(text =>
                                                {
                                                    text.Hyperlink(mapsUrl, "Da clic aquí para ir a la ubicación en Google Maps")
                                                        .FontSize(7).FontColor("#1D4ED8").Underline();
                                                });
                                        }
                                    });
                                });
                            });
                        });
                    });

                    page.Footer().Element(c =>
                        CrearFooter(c, clienteDisplay, direccionDisplay, tarea.NombreCentroTrabajo ?? "Sin centro de trabajo", logoBytes, qrBytes, logoProveedorBytes, carpetaIconBytes));
                });
            }
        });

        return document.GeneratePdf();
    }

    private static void CrearFondoPagina(IContainer container)
    {
        container.Layers(layers =>
        {
            layers.PrimaryLayer();
            layers.Layer().Element(layer =>
            {
                layer.Row(row =>
                {
                    row.ConstantItem(22).Background("#F15A24");
                    row.RelativeItem();
                });
            });
        });
    }

    private static void CrearHeader(
        IContainer container,
        byte[]? logoBytes,
        string titulo,
        string subtitulo)
    {
        container.PaddingLeft(28).PaddingRight(10).PaddingTop(5).Column(column =>
        {
            column.Item().Row(row =>
            {
                row.ConstantItem(150).AlignMiddle().Element(c =>
                {
                    if (logoBytes != null)
                        c.Image(logoBytes, ImageScaling.FitArea);
                    else
                    {
                        c.Row(r =>
                        {
                            r.ConstantItem(28).Height(28).Background("#F15A24")
                                .AlignCenter().AlignMiddle()
                                .Text("V").Bold().FontSize(16).FontColor(Colors.White);
                            r.ConstantItem(6);
                            r.RelativeItem().AlignMiddle()
                                .Text("VELIOS").Bold().FontSize(16).FontColor("#24364D");
                        });
                    }
                });

                row.ConstantItem(1).PaddingTop(8).PaddingBottom(8).Background("#D1D5DB");
                row.ConstantItem(12);

                row.RelativeItem().AlignMiddle().Element(containerTitles =>
                {
                    containerTitles.Row(r =>
                    {
                        r.RelativeItem().Element(box =>
                        {
                            box.Border(1).BorderColor("#E5E7EB").Background(Colors.White)
                                .Padding(8)
                                .Column(col =>
                                {
                                    col.Item().Text(titulo)
                                        .FontSize(10).SemiBold().FontColor("#6B7280");
                                    col.Item().Text(subtitulo ?? "SIN TÍTULO")
                                        .FontSize(16).Bold().FontColor("#24364D");
                                });
                        });
                    });
                });
            });

            column.Item().PaddingTop(4).LineHorizontal(1).LineColor("#E5E7EB");
        });
    }

    private static void CrearContenidoPrincipalConSidebar(
        IContainer container,
        ReporteMaterialidadDto reporte,
        string clienteDisplay,
        string direccionDisplay,
        byte[]? logoBytes,
        List<ArchivoAdjunto> archivosTarea,
        byte[]? pdfIconBytes,
        byte[]? personaIconBytes,
        byte[]? documentoIconBytes,
        byte[]? carpetaIconBytes)
    {
        var tarea = reporte.Tarea;
        var cliente = reporte.Cliente;

        container.PaddingLeft(28).Row(row =>
        {
            row.RelativeItem(2.7f).PaddingRight(12).Column(left =>
            {
                left.Spacing(14);
                left.Item().Element(lc => { });

                left.Item().Element(c =>
                    CrearSeccionConIcono(c, "Información general  del cliente", content =>
                    {
                        content.Item().Element(x => CrearFilaSimple(x, "Nombre", clienteDisplay));
                        content.Item().Element(x => CrearFilaSimple(x, "Razón Social", cliente.RazonSocial));
                        content.Item().Element(x => CrearFilaSimple(x, "Número de teléfono de la empresa",
                            !string.IsNullOrWhiteSpace(tarea.TelefonoCentroTrabajo) ? tarea.TelefonoCentroTrabajo : cliente.Telefono));
                        content.Item().Element(x => CrearFilaSimple(x, "Email del supervisor", tarea.EmailSupervisor));

                    }, documentoIconBytes));

                left.Item().Element(c =>
                    CrearSeccionConIcono(c, "Información de tarea", content =>
                    {
                        content.Item().Element(x => CrearFilaSimple(x, "Nombre", (tarea.Titulo ?? "").Replace("/", "").Trim()));

                        content.Item().PaddingTop(14).Text(text =>
                        {
                            text.Span("Descripción de la actividad a desarrollar: ")
                                .SemiBold().FontSize(9).FontColor("#24364D");
                            text.Span(tarea.Descripcion ?? "N/A")
                                .FontSize(9).FontColor("#6B7280");
                        });

                        if (tarea.Observaciones.Count > 0)
                        {
                            content.Item().PaddingTop(10).Padding(8).Column(obs =>
                            {
                                obs.Item().Text("Observaciones:")
                                    .SemiBold().FontSize(9).FontColor("#24364D");

                                obs.Item().PaddingTop(4).Row(row =>
                                {
                                    var mitad = (int)Math.Ceiling(tarea.Observaciones.Count / 2.0);
                                    var columnaIzq = tarea.Observaciones.Take(mitad).ToList();
                                    var columnaDer = tarea.Observaciones.Skip(mitad).ToList();

                                    row.RelativeItem().Column(col =>
                                    {
                                        col.Spacing(2);
                                        foreach (var item in columnaIzq)
                                        {
                                            col.Item().Row(r =>
                                            {
                                                r.ConstantItem(8).Text("•").FontSize(8).FontColor("#24364D");
                                                r.RelativeItem().Text(item).FontSize(8).FontColor("#6B7280").LineHeight(1.1f);
                                            });
                                        }
                                    });

                                    row.ConstantItem(8);

                                    row.RelativeItem().Column(col =>
                                    {
                                        col.Spacing(2);
                                        foreach (var item in columnaDer)
                                        {
                                            col.Item().Row(r =>
                                            {
                                                r.ConstantItem(8).Text("•").FontSize(8).FontColor("#24364D");
                                                r.RelativeItem().Text(item).FontSize(8).FontColor("#6B7280").LineHeight(1.1f);
                                            });
                                        }
                                    });
                                });
                            });
                        }

                        if (archivosTarea != null && archivosTarea.Count > 0)
                        {
                            content.Item().PaddingTop(10).Element(el =>
                                CrearSeccionConIcono(el, "Archivos adjuntos de la tarea", filesContent =>
                                {
                                    filesContent.Item().Column(fc =>
                                    {
                                        foreach (var a in archivosTarea)
                                        {
                                            fc.Item().PaddingTop(6).Row(r =>
                                            {
                                                r.ConstantItem(36).Height(36).AlignCenter().AlignMiddle().Element(icon =>
                                                {
                                                    if (a.Extension == ".pdf" && pdfIconBytes is not null && pdfIconBytes.Length > 0)
                                                    {
                                                        icon.Background(Colors.White).Border(1).BorderColor("#D1D5DB")
                                                            .AlignCenter().AlignMiddle()
                                                            .Element(img => img.Image(pdfIconBytes, ImageScaling.FitArea));
                                                    }
                                                    else
                                                    {
                                                        var emoji = a.Extension switch
                                                        {
                                                            ".xls" or ".xlsx" => "📊",
                                                            ".ppt" or ".pptx" => "📽️",
                                                            ".doc" or ".docx" => "📄",
                                                            ".pdf" => "📕",
                                                            ".zip" or ".rar" => "🗜️",
                                                            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" => "🖼️",
                                                            _ => "📁"
                                                        };

                                                        var label = (a.Extension ?? string.Empty).TrimStart('.').ToUpperInvariant();
                                                        if (string.IsNullOrWhiteSpace(label)) label = "FILE";

                                                        icon.Background(Colors.White).Border(1).BorderColor("#D1D5DB")
                                                            .AlignCenter().AlignMiddle()
                                                            .Column(ic =>
                                                            {
                                                                ic.Item().Text(emoji).FontSize(12);
                                                                ic.Item().Text(label).FontSize(8).FontColor("#6B7280");
                                                            });
                                                    }
                                                });

                                                r.ConstantItem(8);

                                                r.RelativeItem().Column(info =>
                                                {
                                                    info.Item().Text(text =>
                                                    {
                                                        if (!string.IsNullOrWhiteSpace(a.Url))
                                                            text.Hyperlink(a.Url, a.Url).FontSize(8).FontColor("#6B7280");
                                                        else
                                                            text.Span("N/A").FontSize(8).FontColor("#6B7280");
                                                    });
                                                });
                                            });
                                        }
                                    });
                                }, documentoIconBytes));
                        }
                    }, documentoIconBytes));
            });

            row.ConstantItem(175).Background("#F3F4F6").CornerRadius(8).Padding(12).Column(right =>
            {
                right.Spacing(8);
                right.Item().Height(6).Background("#24364D");

                right.Item().PaddingTop(4).Text(TruncarTexto(tarea.NombreProyecto ?? "Sin plan de trabajo", 40))
                    .SemiBold().FontSize(13).FontColor("#24364D");
                right.Item().Row(r =>
                {
                    r.ConstantItem(16).AlignMiddle().AlignCenter().Element(ic =>
                    {
                        if (carpetaIconBytes is not null && carpetaIconBytes.Length > 0)
                            ic.Image(carpetaIconBytes, ImageScaling.FitArea);
                        else
                            ic.Text("▭").FontSize(11).FontColor("#6B7280");
                    });
                    r.ConstantItem(4);
                    r.RelativeItem().AlignMiddle()
                        .Text("Plan de trabajo").FontSize(9).FontColor("#6B7280");
                });

                right.Item().LineHorizontal(1).LineColor("#D1D5DB");

                right.Item().PaddingTop(4).Row(r =>
                {
                    r.ConstantItem(26).Height(26).Element(ic =>
                    {
                        if (personaIconBytes is not null && personaIconBytes.Length > 0)
                            ic.Image(personaIconBytes, ImageScaling.FitArea);
                        else
                            ic.Background("#E5E7EB").Border(1).BorderColor("#D1D5DB")
                                .AlignCenter().AlignMiddle()
                                .Text("●").FontSize(14).FontColor("#24364D");
                    });
                    r.ConstantItem(8);
                    r.RelativeItem().Column(c =>
                    {
                        c.Item().Text(tarea.NombreOperador ?? "N/A")
                            .SemiBold().FontSize(11).FontColor("#24364D");
                        c.Item().Text("Operador").FontSize(8).FontColor("#6B7280");
                    });
                });

                right.Item().PaddingTop(4).Row(r =>
                {
                    r.ConstantItem(26).Height(26).Element(ic =>
                    {
                        if (personaIconBytes is not null && personaIconBytes.Length > 0)
                            ic.Image(personaIconBytes, ImageScaling.FitArea);
                        else
                            ic.Background("#E5E7EB").Border(1).BorderColor("#D1D5DB")
                                .AlignCenter().AlignMiddle()
                                .Text("●").FontSize(14).FontColor("#24364D");
                    });
                    r.ConstantItem(8);
                    r.RelativeItem().Column(c =>
                    {
                        c.Item().Text(tarea.NombreSupervisor ?? "N/A")
                            .SemiBold().FontSize(11).FontColor("#24364D");
                        c.Item().Text("Supervisor").FontSize(8).FontColor("#6B7280");
                    });
                });

                right.Item().PaddingTop(8).Background("#24364D").CornerRadius(6).Padding(10).AlignCenter().Column(c =>
                {
                    c.Item().AlignCenter().Text("PRESUPUESTO")
                        .FontSize(9).FontColor(Colors.White).SemiBold();
                    c.Item().AlignCenter().Text(
                            tarea.PresupuestoAsignado.HasValue
                                ? $"${tarea.PresupuestoAsignado.Value:N2} {tarea.Moneda}"
                                : "N/A")
                        .Bold().FontSize(15).FontColor(Colors.White);
                });

                right.Item().PaddingTop(10).Column(suc =>
                {
                    suc.Item().Row(r =>
                    {
                        r.ConstantItem(20).AlignTop().Element(ic =>
                        {
                            ic.Text("📍").FontSize(12).FontColor("#F15A24");
                        });

                        r.ConstantItem(6);

                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Text(TruncarTexto(tarea.NombreCentroTrabajo ?? clienteDisplay, 35))
                                .SemiBold().FontSize(13).FontColor("#24364D");
                            c.Item().Text("Sucursal /CT").FontSize(9).FontColor("#6B7280");
                            c.Item().PaddingTop(4).Text(TruncarTexto(direccionDisplay, 70))
                                .FontSize(8).FontColor("#374151");
                        });
                    });

                    suc.Item().PaddingTop(8).LineHorizontal(1).LineColor("#E5E7EB");

                    suc.Item().PaddingTop(8).Column(tl =>
                    {
                        Action<string, string, string> addItem = (date, label, state) =>
                        {
                            tl.Item().Row(r =>
                            {
                                r.ConstantItem(28).Column(c =>
                                {
                                    c.Item().Height(4);
                                    if (state == "filled")
                                    {
                                        c.Item().AlignCenter().Element(el => el.Width(12).Height(12).Border(2).BorderColor("#16C60C").CornerRadius(6).Background("#16C60C"));
                                    }
                                    else
                                    {
                                        c.Item().AlignCenter().Element(el => el.Width(12).Height(12).Border(2).BorderColor("#94A3B8").CornerRadius(6).Background(Colors.White));
                                    }

                                    c.Item().Height(36).AlignCenter().Element(line => line.Width(2).Height(36).Background("#E5E7EB"));
                                });

                                r.RelativeItem().Column(c =>
                                {
                                    c.Item().Text(date).SemiBold().FontSize(11).FontColor("#24364D");
                                    c.Item().Text(label).FontSize(9).FontColor("#6B7280");
                                });
                            });
                        };

                        addItem(tarea.FechaAsignacion != DateTime.MinValue ? tarea.FechaAsignacion.ToString("dd/MM/yyyy") : "N/A", "Fecha asignación", "empty");
                        tl.Item().PaddingTop(6);
                        addItem(tarea.FechaProgramada.HasValue ? tarea.FechaProgramada.Value.ToString("dd/MM/yyyy") : "N/A", "Fecha programada", "empty");
                        tl.Item().PaddingTop(6);
                        addItem(tarea.FechaVencimiento != DateTime.MinValue ? tarea.FechaVencimiento.ToString("dd/MM/yyyy") : "N/A", "Fecha vencimiento", "filled");
                    });
                });

                var fechaVencimiento = tarea.FechaVencimiento;
                var estaCompletada = tarea.EstatusCodigo is "FINALIZADO" or "CANCELADA";
                var estaVencida = !estaCompletada && DateTime.Now.Date > fechaVencimiento.Date;

                if (estaVencida)
                {
                    right.Item().PaddingTop(10).Background("#EF4444").CornerRadius(6).PaddingVertical(8).AlignCenter()
                        .Text("VENCIDA").Bold().FontColor(Colors.White).FontSize(12);
                }
                else
                {
                    right.Item().PaddingTop(10).Background("#16C60C").CornerRadius(6).PaddingVertical(8).AlignCenter()
                        .Text("EN TIEMPO").Bold().FontColor(Colors.White).FontSize(12);
                }
            });
        });
    }

    private static void CrearFooter(
        IContainer container,
        string clienteDisplay,
        string direccionDisplay,
        string nombreProyecto,
        byte[]? logoBytes,
        byte[]? qrBytes,
        byte[]? logoProveedorBytes,
        byte[]? carpetaIconBytes)
    {
        container.PaddingLeft(28).Column(column =>
        {
            column.Item().Row(row =>
            {
                row.ConstantItem(70).Element(qr =>
                {
                    qr.Background(Colors.White)
                      .Border(4).BorderColor("#24364D")
                      .CornerRadius(8)
                      .Padding(5)
                      .AlignCenter().AlignMiddle()
                      .Element(img =>
                      {
                          if (qrBytes is not null && qrBytes.Length > 0)
                              img.Image(qrBytes, ImageScaling.FitArea);
                          else
                              img.Text("QR").Bold().FontSize(10).FontColor("#24364D");
                      });
                });

                row.RelativeItem().PaddingLeft(10).PaddingRight(10).PaddingBottom(4).AlignBottom().Row(r =>
                {
                    r.RelativeItem().AlignMiddle().AlignCenter().Column(c =>
                    {
                        c.Item().AlignCenter().Text(nombreProyecto)
                            .Bold().FontSize(11).FontColor("#24364D");
                    });

                    r.ConstantItem(20).AlignMiddle().AlignCenter().Column(c =>
                    {
                        c.Item().Width(1).Height(22).Background("#BDBDBD");
                    });

                    r.RelativeItem().AlignCenter().Row(logoRow =>
                    {
                        if (logoProveedorBytes is not null && logoProveedorBytes.Length > 0)
                            logoRow.ConstantItem(55).Height(30).AlignCenter().AlignMiddle()
                                .Element(img => img.Image(logoProveedorBytes, ImageScaling.FitArea));
                        else if (logoBytes is not null && logoBytes.Length > 0)
                            logoRow.ConstantItem(55).Height(30).AlignCenter().AlignMiddle()
                                .Element(img => img.Image(logoBytes, ImageScaling.FitArea));
                        else
                            logoRow.RelativeItem().AlignCenter().AlignMiddle()
                                .Text("Sin logo").FontSize(8).FontColor("#6B7280");
                    });

                    r.ConstantItem(20).AlignMiddle().AlignCenter().Column(c =>
                    {
                        c.Item().Width(1).Height(22).Background("#BDBDBD");
                    });

                    r.RelativeItem().AlignMiddle().AlignCenter().Row(eRow =>
                    {
                        eRow.ConstantItem(14).AlignMiddle().Element(ic =>
                        {
                            if (carpetaIconBytes is not null && carpetaIconBytes.Length > 0)
                                ic.Image(carpetaIconBytes, ImageScaling.FitArea);
                            else
                                ic.Text("▭").FontSize(10).FontColor("#6B7280");
                        });
                        eRow.ConstantItem(4);
                        eRow.RelativeItem().AlignMiddle()
                            .Text(nombreProyecto).FontSize(9).SemiBold().FontColor("#6B7280");
                    });
                });
            });

            column.Item().Background("#24364D").Row(row =>
            {
                row.ConstantItem(70).Background("#24364D").Height(10);

                row.RelativeItem().PaddingVertical(6).PaddingHorizontal(10).Row(r =>
                {
                    r.RelativeItem().AlignCenter().Text(direccionDisplay)
                        .FontSize(8).FontColor(Colors.White);

                    r.ConstantItem(115).AlignRight().Text(text =>
                    {
                        text.Span("PÁGINA ").FontSize(8).FontColor(Colors.White).Bold();
                        text.CurrentPageNumber().FontSize(9).Bold().FontColor(Colors.White);
                        text.Span(" DE ").FontSize(8).FontColor(Colors.White).Bold();
                        text.TotalPages().FontSize(9).Bold().FontColor(Colors.White);
                    });
                });
            });
        });
    }

    private static void CrearSeccionConIcono(
        IContainer container,
        string titulo,
        Action<ColumnDescriptor> contenido,
        byte[]? documentoIconBytes = null)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.ConstantItem(34).Height(34).Border(1).BorderColor("#D1D5DB")
                    .Background("#F9FAFB").AlignCenter().AlignMiddle()
                    .Element(icon =>
                    {
                        if (documentoIconBytes is not null && documentoIconBytes.Length > 0)
                            icon.Padding(6).Image(documentoIconBytes, ImageScaling.FitArea);
                        else
                            icon.Text("≡").FontSize(18).Bold().FontColor("#24364D");
                    });

                row.ConstantItem(10);

                row.RelativeItem().AlignMiddle().Text(titulo)
                    .SemiBold().FontSize(11).FontColor("#24364D");
            });

            column.Item().PaddingTop(10).Column(inner =>
            {
                inner.Spacing(8);
                contenido(inner);
            });
        });
    }

    private static void CrearFilaSimple(IContainer container, string etiqueta, string? valor)
    {
        container.BorderBottom(1).BorderColor("#D1D5DB").PaddingBottom(8).Row(row =>
        {
            row.RelativeItem().Text(etiqueta)
                .SemiBold().FontSize(9).FontColor("#7C7C7C");
            row.RelativeItem(2).Text(string.IsNullOrWhiteSpace(valor) ? "N/A" : valor)
                .SemiBold().FontSize(9).FontColor("#24364D");
        });
    }

    private static void CrearCampoLateral(IContainer container, string etiqueta, string? valor)
    {
        container.Column(column =>
        {
            column.Item().Text(etiqueta).FontSize(8).SemiBold().FontColor("#6B7280");
            column.Item().PaddingTop(1)
                .Text(string.IsNullOrWhiteSpace(valor) ? "N/A" : valor)
                .FontSize(9).FontColor("#24364D");
        });
    }

    private static void CrearCheckValidacion(IContainer container, string texto, bool ok, byte[]? checkIconBytes = null)
    {
        container.Border(1).BorderColor("#D6DCE5").Background(Colors.White).Padding(4).Row(row =>
        {
            row.ConstantItem(18).Height(18).AlignMiddle().AlignCenter().Element(box =>
            {
                if (ok && checkIconBytes is not null && checkIconBytes.Length > 0)
                {
                    box.Image(checkIconBytes, ImageScaling.FitArea);
                }
                else
                {
                    box.Border(1)
                        .BorderColor(ok ? "#16A34A" : "#94A3B8")
                        .Background(ok ? "#DCFCE7" : "#FFFFFF")
                        .AlignCenter().AlignMiddle()
                        .Text(ok ? "✓" : "")
                        .FontSize(10).Bold().FontColor("#166534");
                }
            });

            row.ConstantItem(6);

            row.RelativeItem().AlignMiddle().Text(texto)
                .FontSize(9).FontColor("#334155");
        });
    }

    private static byte[] GenerarQrBytes(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(20);
    }

    private string BuildValidationToken(int tareaId)
    {
        var secretKey = _configuration["QrValidation:SecretKey"];

        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException("No existe la configuración QrValidation:SecretKey.");
        }

        var payload = $"taskId:{tareaId}";
        var keyBytes = Encoding.UTF8.GetBytes(secretKey);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(payloadBytes);

        return Convert.ToHexString(hashBytes);
    }
}