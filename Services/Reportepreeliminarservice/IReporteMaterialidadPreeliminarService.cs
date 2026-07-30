namespace velios.Api.Services;

public interface IReporteMaterialidadPreeliminarService
{
    Task<byte[]> GenerarPdfPorTareaAsync(int tareaId);
}