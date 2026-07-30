using Microsoft.AspNetCore.Mvc;
using velios.Api.Services;

namespace velios.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReporteMaterialidadPreeliminarController : ControllerBase
{
    private readonly IReporteMaterialidadPreeliminarService _reporteMaterialidadPreeliminarService;

    public ReporteMaterialidadPreeliminarController(IReporteMaterialidadPreeliminarService reporteMaterialidadPreeliminarService)
    {
        _reporteMaterialidadPreeliminarService = reporteMaterialidadPreeliminarService;
    }

    [HttpGet("tarea/{tareaId}")]
    [Produces("application/pdf")]
    public async Task<IActionResult> GenerarPorTarea(int tareaId)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var pdfBytes = await _reporteMaterialidadPreeliminarService.GenerarPdfPorTareaAsync(tareaId);

        stopwatch.Stop();

        Response.Headers["X-Tiempo-Generacion"] = $"{stopwatch.ElapsedMilliseconds} ms";
        Response.Headers["Content-Length"] = pdfBytes.Length.ToString();

        return File(
            pdfBytes,
            "application/pdf",
            $"reporte-materialidad-preeliminar-{tareaId}.pdf"
        );
    }
}