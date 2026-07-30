namespace velios.Api.Services;
//reflejo cambiso 
public interface IReporteMaterialidadPreeliminarService
{
    Task<byte[]> GenerarPdfPorTareaAsync(int tareaId);
}