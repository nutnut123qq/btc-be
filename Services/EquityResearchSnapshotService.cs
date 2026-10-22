using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Public, market-only research snapshot + readiness (EQX v2 §C2). Never reads positions, lots
/// or cash. As-of semantics:
/// <list type="bullet">
/// <item>signal bar = latest stored finalized 1d bar at asOf; effectiveCutoffMs = its close (the DATA cutoff);
///   every series/fingerprint uses stored bars with CloseTimeMs ≤ cutoff;</item>
/// <item>latestExpectedOpenTimeMsAtAsOf = the EXPECTED data time: the latest 1d session that should be finalized
///   at requestedAsOf (in-memory HOSE calendar: trading days, 14:45 close + publish grace). It never comes from
///   stored bars, so a stale DB cannot prove itself fresh; it is null when asOf's date is outside the calendar
///   coverage (in-memory source or MarketCalendarDays) — freshness is then not evaluable and fails closed.
///   Readiness <c>latest-expected-bar-stored</c> compares the signal bar with it (freshness at the END of the window);
///   coverage.upToDate / recent-gaps stay completeness checks INSIDE the window ending at the cutoff. Only 1d is
///   checked (the snapshot is daily by contract); nothing is fetched and wall-clock now is not used;</item>
/// <item>context (RS, corporate actions, events, fundamentals) and base rates are evaluated by the
///   existing services with their clock pinned to asOf (AvailableAtUtc ≤ asOf, finalization ≤ asOf);</item>
/// <item>a base-rates artifact whose dataAsOf is after the signal bar is withheld
///   (<c>artifact-not-pit-for-asof</c>);</item>
/// <item>conditions are only served when the signal bar is also the latest stored finalized bar now
///   (the AI conditions endpoint cannot recompute the past) — else
///   <c>conditions-not-reconstructible-for-past-asof</c>;</item>
/// <item>prices are latest-adjusted ⇒ <c>pit.reconstruction = partial</c>.</item>
/// </list>
/// DB reads run in one RepeatableRead transaction (skipped on non-relational providers). The AI
/// conditions payload is fetched over HTTP and is outside that snapshot. snapshotSha256 is the
/// SHA-256 of the canonical JSON (sorted keys) of the snapshot without generatedAt / snapshotId /
/// snapshotSha256; snapshotId = <c>snap/{SYMBOL}/{cutoffMs}/{sha12}</c>.
/// </summary>
public sealed class EquityResearchSnapshotService
{
    public const string ReasonArtifactNotPit = "artifact-not-pit-for-asof";
    public const string ReasonConditionsPast = "conditions-not-reconstructible-for-past-asof";
    public const string ReasonConditionsAsOfMismatch = "conditions-asof-mismatch";
    public const string ReasonNoSignalBar = "no-finalized-daily-bar-at-asof";
    public const int SeriesPageLimit = 2000;
    public const int RecentSessions = 250;

    private readonly AppDbContext _db;
    private readonly VietnamMarketCalendar _calendar;
    private readonly Func<TimeProvider, EquityBaseRatesService> _baseRatesAt;
    private readonly CurrentConditionsService _conditions;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;
    private readonly EquityMetricCatalogProvider _metricCatalog;
    private readonly ILogger<EquityResearchSnapshotService> _logger;

    public EquityResearchSnapshotService(
        AppDbContext db,
        VietnamMarketCalendar calendar,
        IOptions<EvidenceCatalogOptions> evidenceOptions,
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        IResearchEvidenceCatalog evidenceCatalog,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        IMemoryCache? cache = null,
        EquityMetricCatalogProvider? metricCatalog = null)
        : this(db, calendar,
            tp => new EquityBaseRatesService(db, evidenceOptions, environment, calendar, httpClientFactory, tp,
                loggerFactory.CreateLogger<EquityBaseRatesService>()),
            new CurrentConditionsService(httpClientFactory, evidenceCatalog,
                loggerFactory.CreateLogger<CurrentConditionsService>(), cache),
            timeProvider, null, loggerFactory.CreateLogger<EquityResearchSnapshotService>(), metricCatalog)
    {
    }

    internal EquityResearchSnapshotService(
        AppDbContext db,
        VietnamMarketCalendar calendar,
        Func<TimeProvider, EquityBaseRatesService> baseRatesAt,
        CurrentConditionsService conditions,
        TimeProvider? timeProvider = null,
        InstrumentCatalog? catalog = null,
        ILogger<EquityResearchSnapshotService>? logger = null,
        EquityMetricCatalogProvider? metricCatalog = null)
    {
        _db = db;
        _calendar = calendar;
        _baseRatesAt = baseRatesAt;
        _conditions = conditions;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _metricCatalog = metricCatalog ?? new EquityMetricCatalogProvider((string?)null);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityResearchSnapshotService>.Instance;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Parse <c>asOf</c>: epoch ms or ISO-8601 with an explicit offset/Z. Null ⇒ now. Truncated to ms.</summary>
    public static bool TryParseAsOf(string? raw, DateTimeOffset now, out DateTimeOffset asOf, out string? error)
    {
        error = null;
        asOf = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var text = raw.Trim();
        if (text.All(char.IsAsciiDigit) && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
        {
            asOf = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        else
        {
            var hasOffset = text.EndsWith('Z') || text.EndsWith('z')
                || (text.Length > 6 && (text[^6] is '+' or '-') && text[^3] == ':');
            if (!hasOffset || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                error = "asOf must be epoch milliseconds or ISO-8601 with an explicit offset (e.g. 2026-10-16T09:00:00Z).";
                return false;
            }
            asOf = DateTimeOffset.FromUnixTimeMilliseconds(parsed.ToUnixTimeMilliseconds());
        }
        if (asOf > now)
        {
            error = "asOf is in the future.";
            return false;
        }
        return true;
    }

    private string RequireDecision(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        return instrument.Symbol;
    }

    public async Task<EquityResearchSnapshot> BuildAsync(string symbol, DateTimeOffset asOf, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var instrument = _catalog.Get(sym)!;
        var now = _timeProvider.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var asOfMs = asOf.ToUnixTimeMilliseconds();
        var asOfClock = new FixedTimeProvider(asOf);

        IDbContextTransaction? tx = null;
        if (_db.Database.IsRelational() && _db.Database.CurrentTransaction is null)
            tx = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        try
        {
            var baseRatesAsOf = _baseRatesAt(asOfClock);
            var baseRatesNow = _baseRatesAt(_timeProvider);
            var signalOpen = await baseRatesAsOf.LatestFinalizedDailyOpenAsync(sym, asOfMs, ct);
            var latestNow = await baseRatesNow.LatestFinalizedDailyOpenAsync(sym, nowMs, ct);
            var signalRow = signalOpen is null ? null : await _db.InstrumentCandles.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Symbol == sym && c.Timeframe == EquityCandleTimeframes.Day
                                          && c.OpenTimeMs == signalOpen.Value, ct);
            var cutoff = signalRow?.CloseTimeMs ?? asOfMs;
            var isLatest = signalRow is not null && signalOpen == latestNow;
            var unavailable = new List<EquitySnapshotUnavailable>();
            if (signalRow is null) unavailable.Add(new("signalBar", ReasonNoSignalBar));

            var calendar = await CalendarAsync(cutoff, signalRow?.OpenTimeMs, asOfMs, ct);
            var expectedAtAsOf = ExpectedDailyOpenAtAsOf(asOfMs, calendar);
            var beforeCoverage = new SortedDictionary<string, long>(StringComparer.Ordinal);
            DateOnly? coverageFrom = calendar.CoverageFrom is null ? null
                : DateOnly.ParseExact(calendar.CoverageFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture);

            // --- series, fingerprints, coverage --------------------------------------------
            var dataset = new SortedDictionary<string, EquitySnapshotDatasetDto>(StringComparer.Ordinal);
            var revised = new SortedDictionary<string, long>(StringComparer.Ordinal);
            var coverage = new List<EquitySnapshotCoverageDto>();
            var series = new List<EquitySnapshotSeriesDto>();
            foreach (var tf in instrument.ActiveTimeframes.OrderBy(t => t, StringComparer.Ordinal))
            {
                var rows = await _db.InstrumentCandles.AsNoTracking()
                    .Where(c => c.Symbol == sym && c.Timeframe == tf && c.CloseTimeMs <= cutoff)
                    .OrderBy(c => c.OpenTimeMs)
                    .ToListAsync(ct);
                var asOfUtc = asOf.UtcDateTime;
                revised[tf] = rows.LongCount(r => ToUtc(r.UpdatedAtUtc) > asOfUtc);
                beforeCoverage[tf] = coverageFrom is { } cf
                    ? rows.LongCount(r => DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(r.OpenTimeMs).DateTime) < cf) : 0;
                var fp = Fingerprint(rows);
                dataset[tf] = fp;
                coverage.Add(Coverage(tf, rows, cutoff));
                series.Add(new EquitySnapshotSeriesDto(tf, $"/api/equities/{sym}/candles?timeframe={tf}", cutoff,
                    fp.BarCount, fp.Sha256, EquityResearchContracts.SeriesFingerprintMethod, SeriesPageLimit));
            }

            // --- context (clock pinned to asOf) ---------------------------------------------
            var context = await new EquityContextService(_db, _calendar, asOfClock, _catalog).GetAsync(sym, ct);
            if (context.RelativeStrength.Status == EquityContextStatuses.Unavailable)
                unavailable.Add(new("relativeStrength", context.RelativeStrength.Reason ?? "unavailable"));
            if (context.Fundamentals.Status == EquityContextStatuses.Unavailable)
                unavailable.Add(new("fundamentals", context.Fundamentals.Reason ?? "unavailable"));
            if (context.CorporateActions.Status == EquityContextStatuses.Unavailable)
                unavailable.Add(new("corporateActions", context.CorporateActions.Reason ?? "unavailable"));
            if (context.Events.Status == EquityContextStatuses.Unavailable)
                unavailable.Add(new("events", context.Events.Reason ?? "unavailable"));

            // --- base rates (clock pinned to asOf; withheld when not PIT) -------------------
            var baseRates = await baseRatesAsOf.GetAsync(sym, ct);
            if (baseRates.Artifact is { } art && (signalOpen is null || art.DataAsOfOpenTimeMs > signalOpen.Value))
                baseRates = baseRates with
                {
                    Status = EquityBaseRatesStatuses.Abstained,
                    Reasons = [ReasonArtifactNotPit],
                    Configs = [],
                };
            if (baseRates.Status != EquityBaseRatesStatuses.Ok)
                unavailable.Add(new("baseRates", baseRates.Reasons.Count > 0 ? string.Join(";", baseRates.Reasons) : baseRates.Status));

            // --- conditions (latest only) ----------------------------------------------------
            var conditions = await ConditionsAsync(sym, isLatest, signalOpen, ct);
            if (conditions.Status != "ok") unavailable.Add(new("conditions", conditions.Reason ?? "unavailable"));

            // --- source documents + issuer-report metrics: ONE availability rule -------------
            // Document availableAt = issuer-stated PublishedAtUtc, else server-stamped FirstSeenAtUtc.
            // A metric extracted from a document is shown only when that document is available
            // at asOf (and the metric itself is), so a metric never appears without its document.
            var asOfUtcDoc = asOf.UtcDateTime;
            var allDocs = await _db.EquitySourceDocuments.AsNoTracking().Where(d => d.Symbol == sym).ToListAsync(ct);
            var availableDocIds = allDocs.Where(d => EquitySourceDocumentService.AvailableAt(d) <= asOfUtcDoc)
                .Select(d => d.Id).ToHashSet();
            // hasExtraction = metric values stored from this document AND available at asOf (not "an extraction ran").
            var extractedCounts = (await _db.EquityFundamentals.AsNoTracking()
                    .Where(f => f.Symbol == sym && f.DocumentId != null && f.Value != null && f.AvailableAtUtc <= asOfUtcDoc)
                    .Select(f => f.DocumentId!.Value).ToListAsync(ct))
                .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            // v3 financial facts / extractions / attempts visible at asOf (same transaction, same document rule).
            var factRows = await EquityFinancialFactsService.LoadVisibleAsync(_db, sym, asOfUtcDoc, allDocs, ct);
            var metricCatalog = _metricCatalog.Get();
            var fundamentalsSeries = EquityFinancialFactsService.Series(factRows);
            var fundamentalsCoverage = EquityFinancialFactsService.CoverageSection(factRows, asOfUtcDoc, metricCatalog);
            if (fundamentalsSeries.Status != "ok") unavailable.Add(new("fundamentalsSeries", fundamentalsSeries.Reason ?? "unavailable"));
            if (!metricCatalog.IsOk) unavailable.Add(new("fundamentalsCoverage", $"{EquityMetricCatalog.Unavailable}:{metricCatalog.Reason}"));
            var docs = allDocs.Where(d => availableDocIds.Contains(d.Id))
                .OrderByDescending(d => d.ReportingPeriod ?? "", StringComparer.Ordinal)
                .ThenBy(d => d.DocType, StringComparer.Ordinal).ThenBy(d => d.FirstSeenAtUtc).ThenBy(d => d.Id)
                .Select(d => new EquitySourceDocumentRefDto(d.Id, d.DocType, d.Title, d.ReportingPeriod, d.Consolidation,
                    d.AuditStatus, d.ContentSha256, d.SourceUrl, d.Provider, d.PublishedAtUtc, d.FirstSeenAtUtc,
                    d.Revision, d.SupersedesDocumentId, extractedCounts.GetValueOrDefault(d.Id) > 0,
                    EquitySourceDocumentService.AvailableAt(d), EquitySourceDocumentService.AvailabilityBasis(d),
                    extractedCounts.GetValueOrDefault(d.Id), d.ExtractionJson is not null,
                    EquityFinancialFactsService.ExtractionStatus(factRows, d.Id)))
                .ToArray();
            if (docs.Length == 0) unavailable.Add(new("sourceDocuments", "no-source-documents"));
            var hiddenMetricKeys = (await _db.EquityFundamentals.AsNoTracking()
                    .Where(f => f.Symbol == sym && f.DocumentId != null).ToListAsync(ct))
                .Where(f => !availableDocIds.Contains(f.DocumentId!.Value))
                .Select(f => (f.Period, f.Metric, f.Source, ToUtc(f.AvailableAtUtc))).ToHashSet();
            var fundamentals = Sorted(context.Fundamentals);
            if (hiddenMetricKeys.Count > 0)
            {
                var kept = fundamentals.Items.Where(i => !(i.Period is not null && i.Source is not null && i.AvailableAtUtc is not null
                    && hiddenMetricKeys.Contains((i.Period, i.Metric, i.Source, ToUtc(i.AvailableAtUtc.Value))))).ToArray();
                if (kept.Length != fundamentals.Items.Count)
                {
                    var anyValue = kept.Any(i => i.Value is not null);
                    fundamentals = fundamentals with
                    {
                        Items = kept,
                        Status = anyValue ? fundamentals.Status : EquityContextStatuses.Empty,
                        Reason = anyValue ? fundamentals.Reason : "no-fundamentals-with-available-document-at-asof",
                    };
                }
            }

            calendar = calendar with { BarsBeforeCoverage = beforeCoverage };
            if (calendar.Status != "ok") unavailable.Add(new("calendar", calendar.Reason ?? "unavailable"));

            var pit = new EquitySnapshotPit(
                "partial",
                instrument.PriceBasis,
                revised,
                PitLimitations(isLatest, revised));

            var snapshot = new EquityResearchSnapshot(
                EquityResearchContracts.SnapshotVersion, "", "", sym,
                asOf.UtcDateTime, asOfMs, cutoff, now.UtcDateTime,
                EquityResearchContracts.SnapshotMethodVersion, isLatest,
                signalRow is null ? null : new EquitySnapshotBarDto(signalRow.OpenTimeMs, signalRow.CloseTimeMs,
                    signalRow.Open, signalRow.High, signalRow.Low, signalRow.Close, signalRow.Volume),
                expectedAtAsOf,
                instrument.PriceBasis, instrument.DataProvider, calendar, pit, dataset, coverage, conditions, series,
                fundamentals, Sorted(context.CorporateActions), Sorted(context.Events),
                context.RelativeStrength, context.Limitations, baseRates, docs, unavailable,
                HowToGoDeeper(sym, cutoff, asOfMs), fundamentalsSeries, fundamentalsCoverage);
            var sha = Sha256Of(snapshot);
            return snapshot with { SnapshotId = $"snap/{sym}/{cutoff}/{sha[..12]}", SnapshotSha256 = sha };
        }
        finally
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
                await tx.DisposeAsync();
            }
        }
    }

    private async Task<EquitySnapshotConditions> ConditionsAsync(string sym, bool isLatest, long? signalOpen, CancellationToken ct)
    {
        if (signalOpen is null) return new("unavailable", ReasonNoSignalBar, null);
        if (!isLatest) return new("unavailable", ReasonConditionsPast, null);
        try
        {
            var result = await _conditions.GetAsync(sym, EquityCandleTimeframes.Day, ct);
            if (result.Outcome != CurrentConditionsOutcome.Ok || result.Payload is null)
                return new("unavailable", result.Outcome == CurrentConditionsOutcome.SourceUnavailable
                    ? "conditions-source-unavailable" : "conditions-payload-invalid", null);
            var payload = (JsonObject)result.Payload.DeepClone();
            payload.Remove("generatedAtMs");
            var barOpen = payload["latestClosedBar"] is JsonObject bar && bar["openTimeMs"] is JsonValue v
                && v.TryGetValue<long>(out var open) ? open : (long?)null;
            if (barOpen != signalOpen) return new("unavailable", ReasonConditionsAsOfMismatch, null);
            return new("ok", null, payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Snapshot conditions failed for {Symbol}", sym);
            return new("unavailable", "conditions-section-error", null);
        }
    }

    private const string CalendarUsedFor =
        "coverage/gaps/finalization in this snapshot use the in-memory VietnamMarketCalendar; Python jobs read MarketCalendarDays";

    public const string ReasonCalendarCoverageExpired = "calendar-coverage-expired";

    /// <summary>
    /// MarketCalendarDays (HOSE) provenance, read-only, inside the caller's transaction. Coverage is the
    /// intersection of the rows' [CoverageFromDate, CoverageToDate]; the cutoff date and the first
    /// weekday after the signal bar (the session a forward note/forecast depends on) must lie inside it.
    /// </summary>
    private const string CalendarSavepoint = "eqx_snapshot_calendar";

    /// <summary>Calendar row reader; a test seam (PostgreSQL test points it at a missing relation).</summary>
    internal Func<AppDbContext, CancellationToken, Task<List<MarketCalendarDay>>> CalendarReader { get; set; } =
        (db, ct) => db.MarketCalendarDays.AsNoTracking().Where(d => d.Venue == "HOSE").OrderBy(d => d.Date).ToListAsync(ct);

    /// <summary>
    /// Expected data time at <paramref name="asOfMs"/>: latest 1d open that must be finalized by then (calendar +
    /// publish grace), independent of what is stored. Null when asOf's date is outside the in-memory calendar
    /// coverage or the MarketCalendarDays coverage (holidays unknown ⇒ not evaluable, never "assume weekdays").
    /// </summary>
    private long? ExpectedDailyOpenAtAsOf(long asOfMs, EquitySnapshotCalendar calendar)
    {
        var asOfDate = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(asOfMs).DateTime);
        if (_calendar.IsBeyondCoverage(asOfDate) || _calendar.IsBelowCoverage(asOfDate)) return null;
        if (calendar.CoverageTo is { } to
            && asOfDate > DateOnly.ParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture)) return null;
        if (calendar.CoverageFrom is { } from
            && asOfDate < DateOnly.ParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture)) return null;
        return _calendar.LastExpectedFinalizedOpenUtcMs(EquityCandleTimeframes.Day, asOfMs);
    }

    private async Task<EquitySnapshotCalendar> CalendarAsync(long cutoffMs, long? signalOpenMs, long asOfMs, CancellationToken ct)
    {
        List<MarketCalendarDay> rows;
        // On PostgreSQL a failed statement aborts the surrounding RepeatableRead transaction, so the
        // read runs under a savepoint and a failure rolls back to it; later snapshot queries still work.
        var tx = _db.Database.CurrentTransaction;
        if (tx is not null) await tx.CreateSavepointAsync(CalendarSavepoint, ct);
        try
        {
            rows = await CalendarReader(_db, ct);
            if (tx is not null) await tx.ReleaseSavepointAsync(CalendarSavepoint, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (tx is not null) await tx.RollbackToSavepointAsync(CalendarSavepoint, CancellationToken.None);
            _logger.LogWarning(ex, "MarketCalendarDays could not be read for the research snapshot");
            return new("unavailable", "market-calendar-days-unreadable", "HOSE", "db:MarketCalendarDays", [], 0, 0, 0,
                null, null, null, null, CalendarUsedFor);
        }
        if (rows.Count == 0)
            return new("unavailable", "market-calendar-days-empty", "HOSE", "db:MarketCalendarDays", [], 0, 0, 0,
                null, null, null, null, CalendarUsedFor);
        var dates = rows.Select(r => r.Date).Distinct().OrderBy(d => d).ToArray();
        var text = string.Join('\n', dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var table = dates.ToHashSet();
        var matches = true;
        for (var d = dates[0]; d <= dates[^1] && matches; d = d.AddDays(1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && _calendar.IsNonTradingDate(d) != table.Contains(d))
                matches = false;
        var verified = rows.Count(r => r.Verification == "verified");
        var coverageFrom = rows.Max(r => r.CoverageFromDate);
        var coverageTo = rows.Min(r => r.CoverageToDate);
        var cutoffDate = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(cutoffMs).DateTime);
        DateOnly? nextNeeded = null;
        if (signalOpenMs is { } so)
        {
            var d = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(so).DateTime).AddDays(1);
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
            nextNeeded = d;
        }
        var asOfDate = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(asOfMs).DateTime);
        var expired = cutoffDate > coverageTo || nextNeeded > coverageTo || asOfDate > coverageTo;
        var (status, reason) = expired ? ("coverage-expired", ReasonCalendarCoverageExpired)
            : matches ? ("ok", (string?)null) : ("mismatch", "in-memory-calendar-differs-from-market-calendar-days");
        return new EquitySnapshotCalendar(
            status, reason,
            "HOSE", "db:MarketCalendarDays",
            rows.Select(r => r.CalendarVersion).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToArray(),
            dates.Length, verified, rows.Count - verified,
            dates[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dates[^1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            sha, matches, CalendarUsedFor,
            coverageFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            coverageTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            nextNeeded?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    // --- readiness ------------------------------------------------------------------------

    public static EquityResearchReadiness Readiness(EquityResearchSnapshot s)
    {
        var purposes = new SortedDictionary<string, EquityReadinessPurpose>(StringComparer.Ordinal)
        {
            ["technical"] = Technical(s),
            ["fundamental"] = Fundamental(s),
            ["quantitative"] = Quantitative(s),
            ["portfolio-action"] = new(EquityReadinessStatuses.RequiresAdmin,
                ["position-data-requires-admin"],
                [new("position", EquityReadinessCheckStatuses.Fail,
                    "Public readiness is market-only; position/cash/lots need AdminGuard (research_snapshot.py export --include-portfolio).")]),
        };
        return new EquityResearchReadiness(EquityResearchContracts.ReadinessVersion, s.Symbol, s.RequestedAsOf,
            s.EffectiveCutoffMs, s.AsOfIsLatest, s.SnapshotId, s.SnapshotSha256, purposes);
    }

    private static EquityReadinessPurpose Purpose(List<EquityReadinessCheck> checks)
    {
        var status = checks.Any(c => c.Status == EquityReadinessCheckStatuses.Fail) ? EquityReadinessStatuses.NotReady
            : checks.Any(c => c.Status == EquityReadinessCheckStatuses.Warn) ? EquityReadinessStatuses.Partial
            : EquityReadinessStatuses.Ready;
        var reasons = checks.Where(c => c.Status != EquityReadinessCheckStatuses.Pass).Select(c => c.Name).ToArray();
        return new EquityReadinessPurpose(status, reasons, checks);
    }

    private static EquityReadinessCheck Check(string name, bool pass, string detail, bool failHard = false) =>
        new(name, pass ? EquityReadinessCheckStatuses.Pass : failHard ? EquityReadinessCheckStatuses.Fail : EquityReadinessCheckStatuses.Warn, detail);

    private static EquityReadinessPurpose Technical(EquityResearchSnapshot s)
    {
        var day = s.Coverage.FirstOrDefault(c => c.Timeframe == EquityCandleTimeframes.Day);
        var checks = new List<EquityReadinessCheck>
        {
            Check("signal-bar", s.SignalBar is not null,
                s.SignalBar is null ? "no finalized 1d bar stored at asOf" : $"signal bar {s.SignalBar.OpenTimeMs}", failHard: true),
            Check("daily-history-warmup", (day?.BarCount ?? 0) >= 200,
                $"{day?.BarCount ?? 0} stored 1d bars (SMA200 warm-up needs >= 200)", failHard: true),
            Freshness(s),
            Check("recent-gaps", day is { RecentMissingBars: 0 },
                $"{day?.RecentMissingBars ?? 0} missing of the last {day?.RecentExpectedBars ?? 0} expected 1d sessions"),
            Check("conditions", s.Conditions.Status == "ok", s.Conditions.Reason ?? "conditions available"),
            Check("calendar", s.Calendar.Status == "ok",
                s.Calendar.Status == "ok"
                    ? $"MarketCalendarDays {string.Join("+", s.Calendar.Versions)} ({s.Calendar.Count} days, {s.Calendar.UnverifiedLegacyCount} unverified-legacy, coverage {s.Calendar.CoverageFrom}..{s.Calendar.CoverageTo})"
                    : $"{s.Calendar.Reason ?? "calendar unavailable"} (coverage {s.Calendar.CoverageFrom ?? "?"}..{s.Calendar.CoverageTo ?? "?"}, next session needed {s.Calendar.NextSessionCheckDate ?? "-"})",
                failHard: s.Calendar.Status == "coverage-expired"),
            Check("pit-prices", s.AsOfIsLatest,
                s.AsOfIsLatest ? "latest bar: adjusted levels are current" : "past asOf: prices are latest-adjusted (pit-reconstruction partial)"),
        };
        return Purpose(checks);
    }

    private static string Session(long? openMs) => openMs is { } o
        ? $"{VietnamMarketCalendar.ToLocal(o):yyyy-MM-dd} (open {o})" : "none";

    /// <summary>
    /// Freshness at requestedAsOf: the signal bar (latest stored finalized 1d bar ≤ asOf) must be the session the
    /// calendar expects to be finalized at asOf. Fails closed when the expected time is not evaluable.
    /// </summary>
    private static EquityReadinessCheck Freshness(EquityResearchSnapshot s)
    {
        var expected = s.LatestExpectedOpenTimeMsAtAsOf;
        var last = s.SignalBar?.OpenTimeMs;
        var asOf = s.RequestedAsOf.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (expected is null)
            return Check("latest-expected-bar-stored", false,
                s.Calendar.Status == "coverage-expired"
                    ? $"not evaluable at requestedAsOf {asOf}: {ReasonCalendarCoverageExpired} (coverage to {s.Calendar.CoverageTo ?? "?"}); last stored {Session(last)}"
                    : $"not evaluable at requestedAsOf {asOf}: expected session unknown (asOf outside the HOSE calendar coverage, or no trading session in the 20-day window); last stored {Session(last)}",
                failHard: true);
        var fresh = last is not null && last >= expected;
        return Check("latest-expected-bar-stored", fresh,
            fresh
                ? $"expected finalized 1d session at requestedAsOf {asOf}: {Session(expected)}; last stored {Session(last)}"
                : $"stale at requestedAsOf {asOf}: expected finalized 1d session {Session(expected)} but last stored {Session(last)}",
            failHard: true);
    }

    private static EquityReadinessPurpose Fundamental(EquityResearchSnapshot s)
    {
        var items = s.Fundamentals.Items;
        var withValue = items.Count(i => i.Value is not null);
        var missing = items.Where(i => i.Value is null).Select(i => i.Metric).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToArray();
        var statements = s.SourceDocuments.Count(d => d.DocType is "financial-statement" or "annual-report");
        var latest = items.Where(i => i.Value is not null && i.PeriodEndDate is not null)
            .Select(i => DateOnly.ParseExact(i.PeriodEndDate!, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .DefaultIfEmpty().Max();
        var series = s.FundamentalsSeries;
        var factPoints = series?.Metrics.Sum(m => m.Points.Count) ?? 0;
        var latestFact = series?.Metrics.SelectMany(m => m.Points)
            .Select(p => DateOnly.ParseExact(p.PeriodEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture)).DefaultIfEmpty().Max() ?? default;
        if (latestFact > latest) latest = latestFact;
        var asOfDate = DateOnly.FromDateTime(s.RequestedAsOf);
        var ageDays = latest == default ? (int?)null : asOfDate.DayNumber - latest.DayNumber;
        var core = CoreQuarters(s);
        var checks = new List<EquityReadinessCheck>
        {
            Check("fundamental-values", withValue > 0 || factPoints > 0,
                $"{withValue} provider/issuer metric rows and {factPoints} validated financial-fact points available at asOf", failHard: true),
            Check("facts-core-quarters", core.Complete,
                core.Detail),
            // Provider/issuer ratio rows (EquityFundamentals) are informational once the facts core is complete.
            Check("metrics-unavailable", missing.Length == 0 || core.Complete,
                missing.Length == 0 ? "all metrics present"
                : core.Complete ? $"no PIT provider/issuer ratio value: {string.Join(", ", missing)} (informational: facts core complete)"
                : $"no PIT value: {string.Join(", ", missing)}"),
            Check("issuer-documents", statements > 0, $"{statements} issuer financial statement/annual report document(s) known at asOf"),
            Check("latest-period-age", ageDays is <= 200,
                ageDays is null ? "no dated period" : $"latest period ended {ageDays} days before asOf"),
        };
        return Purpose(checks);
    }

    public const int CoreQuartersRequired = 8;

    /// <summary>
    /// Facts-based fundamental readiness: anchored at the latest quarter with any validated consolidated
    /// core-metric point, the <see cref="CoreQuartersRequired"/> consecutive quarters ending there must each
    /// have every core metric (fundamentalsCoverage.catalog.coreMetrics) validated — a flow metric as the
    /// quarter's own value (periodLabel = YYYYQn), a balance metric as point-in-time at the quarter end.
    /// Missing cells are listed exactly ("2025Q3/consolidated/PROFIT_AFTER_TAX").
    /// </summary>
    internal static (bool Complete, string Detail, IReadOnlyList<string> Missing) CoreQuarters(EquityResearchSnapshot s)
    {
        var coreMetrics = s.FundamentalsCoverage?.Catalog.CoreMetrics ?? EquityMetricCatalog.DefaultCoreMetrics;
        var points = (s.FundamentalsSeries?.Metrics ?? [])
            .Where(m => coreMetrics.Contains(m.MetricCode))
            .SelectMany(m => m.Points.Where(p => p.Consolidation == "consolidated").Select(p => (m.MetricCode, Point: p)))
            .ToList();
        bool Has(string metric, string label, DateOnly end) => points.Any(x => x.MetricCode == metric
            && ((x.Point.Period == label && x.Point.PeriodType is "quarter" or "point-in-time")
                || (x.Point.PeriodType == "point-in-time" && x.Point.PeriodEnd == end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        var quarters = points.Select(x => EquityFinancialFactsService.PeriodBounds(x.Point.Period) is { } b
                && x.Point.Period.Length == 6 && x.Point.Period[4] == 'Q' ? x.Point.Period : null)
            .Where(q => q is not null).Select(q => q!).Distinct().OrderBy(q => q, StringComparer.Ordinal).ToList();
        if (quarters.Count == 0)
            return (false, $"no validated consolidated core-metric quarter at asOf (core: {string.Join(", ", coreMetrics)})", []);
        var anchor = quarters[^1];
        var year = int.Parse(anchor[..4], CultureInfo.InvariantCulture);
        var q = anchor[5] - '0';
        var window = new List<string>();
        for (var i = 0; i < CoreQuartersRequired; i++)
        {
            window.Add($"{year}Q{q}");
            if (--q == 0) { q = 4; year--; }
        }
        var missing = new List<string>();
        foreach (var label in window.AsEnumerable().Reverse())
        {
            var end = EquityFinancialFactsService.PeriodBounds(label)!.Value.End;
            foreach (var metric in coreMetrics.OrderBy(m => m, StringComparer.Ordinal))
                if (!Has(metric, label, end)) missing.Add($"{label}/consolidated/{metric}");
        }
        var complete = missing.Count == 0;
        var detail = complete
            ? $"{CoreQuartersRequired} quarters {window[^1]}..{anchor} have all core metrics validated ({string.Join(", ", coreMetrics)})"
            : $"{CoreQuartersRequired} quarters {window[^1]}..{anchor}: {missing.Count} missing validated cell(s): {string.Join(", ", missing)}";
        return (complete, detail, missing);
    }

    private static EquityReadinessPurpose Quantitative(EquityResearchSnapshot s)
    {
        var br = s.BaseRates;
        var reasons = string.Join(";", br.Reasons);
        var hasArtifact = br.Artifact is not null && !br.Reasons.Contains(ReasonArtifactNotPit);
        var stale = br.Reasons.Any(r => r is EquityBaseRatesService.ReasonStaleAsOf or EquityBaseRatesService.ReasonStaleRevision);
        var checks = new List<EquityReadinessCheck>
        {
            Check("base-rates-artifact", hasArtifact, hasArtifact ? "artifact present and PIT for asOf" : $"{br.Status}: {reasons}", failHard: true),
            Check("artifact-fresh", hasArtifact && !stale,
                !hasArtifact ? $"not evaluated: no point-in-time artifact for this asOf ({br.Status}: {reasons})"
                : stale ? reasons : "fingerprint and dataAsOf match", failHard: true),
            Check("oos-gate", br.Status == EquityBaseRatesStatuses.Ok,
                br.Status == EquityBaseRatesStatuses.Ok ? "decisionUse available" : "gate not passed / abstained: research-only, unvalidated"),
        };
        return Purpose(checks);
    }

    // --- helpers --------------------------------------------------------------------------

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>Decimal without trailing zeros, invariant culture ("40000", "27650.5").</summary>
    public static string NormalizeDecimal(decimal value) =>
        (value / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    public static string SeriesLine(long open, long close, decimal o, decimal h, decimal l, decimal c, decimal v) =>
        string.Join(',', open.ToString(CultureInfo.InvariantCulture), close.ToString(CultureInfo.InvariantCulture),
            NormalizeDecimal(o), NormalizeDecimal(h), NormalizeDecimal(l), NormalizeDecimal(c), NormalizeDecimal(v));

    internal static EquitySnapshotDatasetDto Fingerprint(IReadOnlyList<InstrumentCandle> rows)
    {
        var text = string.Join('\n', rows.Select(r => SeriesLine(r.OpenTimeMs, r.CloseTimeMs, r.Open, r.High, r.Low, r.Close, r.Volume)));
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new EquitySnapshotDatasetDto(rows.Count,
            rows.Count == 0 ? null : rows[0].OpenTimeMs,
            rows.Count == 0 ? null : rows[^1].OpenTimeMs,
            rows.Count == 0 ? null : EquityBaseRatesService.FormatFingerprintTimestamp(rows.Max(r => r.UpdatedAtUtc)),
            sha, EquityResearchContracts.SeriesFingerprintMethod);
    }

    private EquitySnapshotCoverageDto Coverage(string tf, IReadOnlyList<InstrumentCandle> rows, long cutoff)
    {
        var lastStored = rows.Count == 0 ? (long?)null : rows[^1].OpenTimeMs;
        if (rows.Count == 0)
            return new EquitySnapshotCoverageDto(tf, 0, 0, 0, 0, 0, 0, null, null, false, []);
        var from = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(rows[0].OpenTimeMs).DateTime);
        var to = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(cutoff).DateTime);
        var expected = _calendar.EnumerateExpectedOpensUtcMs(tf, from, to)
            .Where(o => o >= rows[0].OpenTimeMs && _calendar.SessionCloseUtcMs(tf, o) <= cutoff)
            .ToArray();
        var stored = rows.Select(r => r.OpenTimeMs).ToHashSet();
        var missing = expected.Where(o => !stored.Contains(o)).ToHashSet();
        var recent = tf == EquityCandleTimeframes.Day ? RecentSessions : RecentSessions * 5;
        var recentExpected = expected.TakeLast(recent).ToArray();
        var unexpected = rows.LongCount(r => (tf == EquityCandleTimeframes.Day
            ? _calendar.ClassifyDailyOpenUtcMs(r.OpenTimeMs)
            : _calendar.ClassifyHourlyOpenUtcMs(r.OpenTimeMs)) != SessionOpenKind.Expected);
        var latestExpected = expected.Length == 0 ? (long?)null : expected[^1];
        var ranges = VietnamMarketCalendar.GroupMissingRanges(expected, missing)
            .OrderByDescending(r => r.MissingBars).ThenByDescending(r => r.StartOpenTimeMs).Take(10).ToArray();
        return new EquitySnapshotCoverageDto(tf, rows.Count, expected.Length, missing.Count, recentExpected.Length,
            recentExpected.LongCount(missing.Contains), unexpected, latestExpected, lastStored,
            latestExpected is not null && lastStored >= latestExpected, ranges);
    }

    private static EquityFundamentalsSection Sorted(EquityFundamentalsSection s) => s with
    {
        Items = s.Items.OrderByDescending(i => i.Period ?? "", StringComparer.Ordinal)
            .ThenBy(i => i.Metric, StringComparer.Ordinal).ThenBy(i => i.Source ?? "", StringComparer.Ordinal)
            .ThenBy(i => i.AvailableAtUtc).ThenBy(i => i.Value).ToArray(),
    };

    private static EquityCorporateActionsSection Sorted(EquityCorporateActionsSection s) => s with
    {
        Items = s.Items.OrderByDescending(i => i.ExDate, StringComparer.Ordinal)
            .ThenBy(i => i.ActionType, StringComparer.Ordinal).ThenBy(i => i.RatioText ?? "", StringComparer.Ordinal)
            .ThenBy(i => i.SourceUrl, StringComparer.Ordinal).ThenBy(i => i.AvailableAtUtc).ToArray(),
    };

    private static EquityEventsSection Sorted(EquityEventsSection s) => s with
    {
        Items = s.Items.OrderByDescending(i => i.EventDate, StringComparer.Ordinal)
            .ThenBy(i => i.EventType, StringComparer.Ordinal).ThenBy(i => i.Title, StringComparer.Ordinal)
            .ThenBy(i => i.SourceUrl, StringComparer.Ordinal).ToArray(),
    };

    private static string[] PitLimitations(bool isLatest, IReadOnlyDictionary<string, long> revised)
    {
        var list = new List<string>
        {
            "pit-reconstruction: partial — prices are latest-adjusted (the provider back-adjusts the whole history at every ex-date); levels before a later ex-date differ from what was quoted at asOf.",
            "Series and fingerprints cover stored bars with CloseTimeMs <= effectiveCutoffMs (close of the signal bar); intraday bars of an unfinished session are excluded.",
            "Context rows are filtered by AvailableAtUtc <= asOf; provider rows carry date-only availability (00:00 VN of the next day).",
            "Financial facts: visible when fact.AvailableAtUtc <= asOf and the document is available at asOf; extraction/validation are retrospective (latest successful extraction known now; status is current, not as-of).",
            "Base rates: the current artifact is shown only when its dataAsOf <= signal bar; otherwise withheld (artifact-not-pit-for-asof).",
            "Relative strength loads the latest ~400 stored daily bars before applying asOf, so very old asOf values can come back unavailable.",
        };
        if (!isLatest)
            list.Add("Conditions are not reconstructible for a past asOf (the AI conditions endpoint only evaluates the latest finalized bar).");
        foreach (var (tf, n) in revised.Where(kv => kv.Value > 0))
            list.Add($"{n} stored {tf} bar(s) <= cutoff were written or updated after asOf (ingestion/adjustment/revision); their stored values may differ from what was known at asOf.");
        list.Add("Conditions are fetched from the AI service outside the database snapshot transaction.");
        list.Add("Missing expected opens before the calendar coverage start (calendar.coverageFrom) may be unlisted holidays; see calendar.barsBeforeCoverage.");
        return list.ToArray();
    }

    private static string[] HowToGoDeeper(string sym, long cutoff, long asOfMs) =>
    [
        $"python ai/research_snapshot.py export --symbol {sym} --as-of {asOfMs} --out-dir <dir>  (snapshot.json + full series CSV <= cutoff + manifest sha256; verifies fingerprints)",
        $"GET /api/equities/{sym}/candles?timeframe=1d&endTimeMs={cutoff}&limit={SeriesPageLimit} (page backwards with endTimeMs = first openTimeMs - 1; keep closeTimeMs <= {cutoff})",
        $"GET /api/equities/{sym}/source-documents (issuer document metadata; originals live under EquityDocuments:RootPath)",
        $"GET /api/equities/{sym}/financial-facts?asOf={asOfMs}&status=validated (every fact with document url/sha256/page/location + extraction; add metric=, periodType=, from=, to=)",
        $"GET /api/equities/{sym}/fundamentals/coverage (full period x metric-group x consolidation grid with attempt/document refs; current, not as-of)",
        $"GET /api/equities/{sym}/forecast-journal (forward forecasts + outcomes)",
        $"GET /api/equities/{sym}/data-quality (latest coverage report; not point-in-time)",
        $"python ai/research_snapshot.py export --symbol {sym} --as-of {asOfMs} --out-dir <dir> --include-portfolio (admin key; position/decision into a separate portfolio.json)",
    ];

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>SHA-256 of the canonical snapshot content (generatedAt/snapshotId/snapshotSha256 excluded).</summary>
    internal static string Sha256Of(EquityResearchSnapshot snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot, WebJson)!.AsObject();
        node.Remove("generatedAt");
        node.Remove("snapshotId");
        node.Remove("snapshotSha256");
        return EquityLlmRunService.Sha256Hex(EquityLlmRunService.CanonicalJson(node));
    }
}
