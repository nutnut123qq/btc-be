namespace Backend.Services;

using Backend.Data;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface IConfluenceService
{
    Task<ConfluenceSnapshot> CalculateConfluenceAsync(string symbol, CancellationToken ct = default);
    Task<ConfluenceSnapshot?> GetLatestConfluenceAsync(string symbol, CancellationToken ct = default);
    Task<List<ConfluenceSnapshot>> GetConfluenceHistoryAsync(string symbol, int limit, CancellationToken ct = default);
}
