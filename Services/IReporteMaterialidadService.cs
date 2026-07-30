

namespace velios.Api.Services;
/// <summary>
/// Interfaz para el servicio de generación de reportes de materialidad.
/// </summary>
public interface IReporteMaterialidadService
{
    Task<byte[]> GenerarPdfPorTareaAsync(int tareaId);
}