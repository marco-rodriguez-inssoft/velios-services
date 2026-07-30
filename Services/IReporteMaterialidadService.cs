

namespace velios.Api.Services;

public interface IReporteMaterialidadService
{
    Task<byte[]> GenerarPdfPorTareaAsync(int tareaId);
}