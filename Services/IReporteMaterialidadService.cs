

namespace velios.Api.Services;

public interface IReporteMaterialidadService
{
    Task<byte[]> GenerarPdfPorTareaAsync(int tareaId, Guid? jobId = null, ProgresoStore? progresoStore = null);
}