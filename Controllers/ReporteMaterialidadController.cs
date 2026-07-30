using Microsoft.AspNetCore.Mvc;
using velios.Api.Services;

namespace velios.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReporteMaterialidadController : ControllerBase
{
    private readonly IReporteMaterialidadService _reporteMaterialidadService;
    private readonly ProgresoStore _progresoStore;

    public ReporteMaterialidadController(
        IReporteMaterialidadService reporteMaterialidadService,
        ProgresoStore progresoStore)
    {
        _reporteMaterialidadService = reporteMaterialidadService;
        _progresoStore = progresoStore;
    }

    // Endpoint original SIN TOCAR: descarga directa y bloqueante (por si algo más lo usa así).
    [HttpGet("tarea/{tareaId}")]
    [Produces("application/pdf")]
    public async Task<IActionResult> GenerarPorTarea(int tareaId)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var pdfBytes = await _reporteMaterialidadService.GenerarPdfPorTareaAsync(tareaId);

        stopwatch.Stop();

        Response.Headers["X-Tiempo-Generacion"] = $"{stopwatch.ElapsedMilliseconds} ms";
        Response.Headers["Content-Length"] = pdfBytes.Length.ToString();

        return File(pdfBytes, "application/pdf", $"reporte-materialidad-{tareaId}.pdf");
    }

    // ==================== NUEVO: flujo con barra de progreso ====================

    [HttpPost("tarea/{tareaId}/iniciar")]
    public IActionResult IniciarGeneracion(int tareaId)
    {
        var jobId = _progresoStore.Crear();

        _ = Task.Run(async () =>
        {
            try
            {
                await _reporteMaterialidadService.GenerarPdfPorTareaAsync(tareaId, jobId, _progresoStore);
            }
            catch
            {
                // El servicio ya deja Estado="Error" en su propio catch; esto solo
                // evita que una excepción no observada tumbe el proceso en background.
            }
        });

        return Ok(new { jobId });
    }

    [HttpGet("progreso/{jobId}")]
    public IActionResult ConsultarProgreso(Guid jobId)
    {
        var progreso = _progresoStore.Obtener(jobId);
        if (progreso is null) return NotFound(new { mensaje = "Job no encontrado o expiró." });

        return Ok(new
        {
            estado = progreso.Estado,
            porcentaje = progreso.Porcentaje,
            procesadas = progreso.Procesadas,
            total = progreso.Total,
            mensaje = progreso.Mensaje
        });
    }

    [HttpGet("descargar/{jobId}")]
    public IActionResult Descargar(Guid jobId)
    {
        var progreso = _progresoStore.Obtener(jobId);
        if (progreso?.PdfBytes is null)
            return NotFound(new { mensaje = "El PDF aún no está listo o el job expiró." });

        return File(progreso.PdfBytes, "application/pdf", $"reporte-materialidad-{jobId}.pdf");
    }
}