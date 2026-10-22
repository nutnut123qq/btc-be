using System.Collections.Concurrent;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Worker post-observe steps for a Decision symbol, all fail-soft (a failing step is logged and
/// reported in the detail, never aborts the session or the other steps):
/// 1. base-rates rebuild via AI (EquityIngestion:BaseRatesRebuild);
/// 2. quant forecast for the latest finalized bar (EquityIngestion:RecordForecasts);
/// 3. write-once outcome resolution of pending forecasts;
/// 4. context sync (VCI events + KBS fundamentals), at most once per symbol per session.
/// </summary>
public sealed class EquitySessionPostProcessor : IEquitySessionPostProcessor
{
    // Keyed by catalog-canonical symbol only → bounded by the catalog (pitfall #20).
    private static readonly ConcurrentDictionary<string, long> ContextSyncedSession = new(StringComparer.Ordinal);

    private readonly EquityBaseRatesService _baseRates;
    private readonly EquityForecastJournalService _journal;
    private readonly EquityContextCollector _context;
    private readonly EquityIngestionOptions _options;
    private readonly ILogger<EquitySessionPostProcessor> _logger;

    public EquitySessionPostProcessor(
        EquityBaseRatesService baseRates,
        EquityForecastJournalService journal,
        EquityContextCollector context,
        IOptions<EquityIngestionOptions> options,
        ILogger<EquitySessionPostProcessor>? logger = null)
    {
        _baseRates = baseRates;
        _journal = journal;
        _context = context;
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquitySessionPostProcessor>.Instance;
    }

    internal static void ResetContextSyncMemory() => ContextSyncedSession.Clear();

    public async Task<string?> RunAsync(InstrumentDescriptor instrument, long sessionOpenTimeMs, CancellationToken ct)
    {
        if (!EquityScope.Has(instrument, EquityCapability.Decision)) return null;
        var sym = instrument.Symbol;
        var parts = new List<string>();

        if (_options.BaseRatesRebuild)
        {
            await Step(parts, "base-rates", async () =>
            {
                var r = await _baseRates.RebuildAsync(sym, ct);
                return r.StatusCode == StatusCodes.Status200OK ? "rebuilt" : $"{r.Code}: {r.Message}";
            });
        }
        if (_options.RecordForecasts)
        {
            await Step(parts, "forecast", async () =>
            {
                var r = await _journal.RecordQuantAsync(sym, ct);
                return $"{r.Status} {r.Recorded} (abstained {r.Abstained})";
            });
        }
        await Step(parts, "outcomes", async () => $"{await _journal.ResolveOutcomesAsync(sym, ct)} resolved");

        if (!ContextSyncedSession.TryGetValue(sym, out var last) || last != sessionOpenTimeMs)
        {
            ContextSyncedSession[sym] = sessionOpenTimeMs; // bounded: one attempt per session even on failure
            await Step(parts, "context", async () =>
            {
                var r = await _context.SyncAsync(sym, ct);
                return $"{r.Status} ({r.ProviderRequests} req)";
            });
        }
        return string.Join("; ", parts);
    }

    private async Task Step(List<string> parts, string name, Func<Task<string>> run)
    {
        try
        {
            parts.Add($"{name} {await run()}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Equity post-process step {Step} failed", name);
            parts.Add($"{name} failed: {ex.GetType().Name}");
        }
    }
}
