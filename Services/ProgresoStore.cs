using Microsoft.Extensions.Caching.Memory;
using velios.Api.Models.ReporteMaterialidad;

namespace velios.Api.Services;

public class ProgresoStore
{
    private readonly IMemoryCache _cache;

    private static readonly MemoryCacheEntryOptions Opciones = new()
    {
        SlidingExpiration = TimeSpan.FromMinutes(20)
    };

    public ProgresoStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public Guid Crear()
    {
        var jobId = Guid.NewGuid();
        _cache.Set(Llave(jobId), new ProgresoReporte(), Opciones);
        return jobId;
    }

    public ProgresoReporte? Obtener(Guid jobId)
    {
        return _cache.TryGetValue(Llave(jobId), out ProgresoReporte? progreso) ? progreso : null;
    }

    public void Actualizar(Guid jobId, Action<ProgresoReporte> update)
    {
        if (_cache.TryGetValue(Llave(jobId), out ProgresoReporte? progreso) && progreso is not null)
        {
            update(progreso);
            _cache.Set(Llave(jobId), progreso, Opciones);
        }
    }

    private static string Llave(Guid jobId) => $"reporte-progreso:{jobId}";
}