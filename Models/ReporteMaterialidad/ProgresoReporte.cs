namespace velios.Api.Models.ReporteMaterialidad;

/// <summary>
/// Representa el estado de avance de una generación de reporte de materialidad nuevo
/// que corre en segundo plano. Se actualiza desde ReporteMaterialidadService
/// y se consulta desde el endpoint de progreso.
/// </summary>
public class ProgresoReporte
{
    /// <summary>Total de evidencias a procesar. Se llena en cuanto se consultan de BD.</summary>
    public int Total { get; set; }

    /// <summary>Evidencias ya procesadas (con o sin éxito en su descarga).</summary>
    public int Procesadas { get; set; }

    /// <summary>
    /// Procesando   -> descargando imágenes/mapas de las evidencias (aquí sí hay % real)
    /// GenerandoPdf -> QuestPDF está armando el documento (no hay % interno, es una sola operación)
    /// Completado   -> el PDF ya está listo en PdfBytes
    /// Error        -> algo falló, revisar Mensaje
    /// </summary>
    public string Estado { get; set; } = "Procesando";

    /// <summary>Detalle del error cuando Estado == "Error".</summary>
    public string? Mensaje { get; set; }

    /// <summary>PDF final, solo se llena cuando Estado == "Completado".</summary>
    public byte[]? PdfBytes { get; set; }

    /// <summary>
    /// Porcentaje 0-100. Durante "GenerandoPdf" se queda fijo en 100 sobre la fase
    /// de descargas (que es la parte que sí se puede medir) hasta que termine.
    /// </summary>
    public int Porcentaje => Total == 0 ? 0 : Math.Min(100, (int)(Procesadas * 100.0 / Total));
}