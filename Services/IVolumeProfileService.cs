namespace Backend.Services;

using System.Threading;
using System.Threading.Tasks;
using Backend.Data;

public interface IVolumeProfileService
{
    Task<VolumeProfileSnapshot?> GetVolumeProfileAsync(string symbol, string timeframe, int lookbackBars, CancellationToken ct = default);
}
