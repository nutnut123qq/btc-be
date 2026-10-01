namespace Backend.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backend.Data;
using Backend.Services.Models;

public interface ISmartMoneyService
{
    Task<List<SmartMoneyStructure>> GetSmartMoneyStructuresAsync(string symbol, string timeframe, int lookbackBars, CancellationToken ct = default);
    Task<TechnicalReplayResponse> GetReplayAsync(
        string symbol,
        string timeframe,
        long asOfTimeMs,
        int lookbackBars,
        CancellationToken ct = default);
}
