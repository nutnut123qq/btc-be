namespace Backend.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backend.Data;

public interface ISmartMoneyService
{
    Task<List<SmartMoneyStructure>> GetSmartMoneyStructuresAsync(string symbol, string timeframe, int lookbackBars, CancellationToken ct = default);
}
