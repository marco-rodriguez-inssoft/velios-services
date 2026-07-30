namespace velios.Api.Services;

public interface IReporteMaterialidadPreeliminarService
{
    Task<byte[]> GenerarPdfPorTareaAsync(int tareaId, Guid? jobId = null, ProgresoStore? progresoStore = null);
}