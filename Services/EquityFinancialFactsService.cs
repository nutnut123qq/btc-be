using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>One metric definition from <c>ai/equity_facts/metric_catalog.json</c> (only what coverage needs).</summary>
public sealed record EquityMetricDefinition(
    string Code,
    string Group,
    string? Statement,
    string? Unit,
    bool Expected,
    bool Core,
    IReadOnlySet<string>? Consolidations,
    IReadOnlySet<string>? PeriodTypes);

/// <summary>Parsed metric catalog. Status ok | catalog-unavailable (missing/unreadable/invalid file — explicit, never guessed).</summary>
public sealed record EquityMetricCatalog(
    string Status, string? Reason, string? Version, string? Sha256, IReadOnlyList<EquityMetricDefinition> Metrics)
{
    public const string Ok = "ok";
    public const string Unavailable = "catalog-unavailable";

    public bool IsOk => Status == Ok;

    public static EquityMetricCatalog Missing(string reason) => new(Unavailable, reason, null, null, []);

    /// <summary>Spec v3 §3 core BS/IS metrics, used when the catalog marks none as core.</summary>
    public static readonly string[] DefaultCoreMetrics =
    [
        "TOTAL_ASSETS", "TOTAL_EQUITY", "CUSTOMER_LOANS_GROSS", "CUSTOMER_DEPOSITS",
        "NET_INTEREST_INCOME", "PROFIT_BEFORE_TAX", "PROFIT_AFTER_TAX",
    ];

    public bool CoreFromCatalog => Metrics.Any(m => m.Core);

    public IReadOnlyList<string> CoreMetrics => CoreFromCatalog
        ? Metrics.Where(m => m.Core).Select(m => m.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray()
        : DefaultCoreMetrics.OrderBy(c => c, StringComparer.Ordinal).ToArray();

    public EquityCoverageCatalogInfo Info() =>
        new(Status, Reason, Version, Sha256, Metrics.Count, CoreMetrics, CoreFromCatalog);

    /// <summary>
    /// Tolerant parser: root object with <c>metrics</c> (or a root array); each item needs <c>code</c>
    /// (or <c>metricCode</c>) and <c>group</c> (or <c>metricGroup</c>). A metric is expected unless it is
    /// marked optional / required:false / expected:false / derived, has statement "derived" or a _CALC code.
    /// Every access is ValueKind-guarded (AGENTS pitfall #17).
    /// </summary>
    public static EquityMetricCatalog Parse(byte[] bytes)
    {
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(bytes); }
        catch (JsonException) { return Missing("metric-catalog-invalid-json") with { Sha256 = sha }; }
        using (doc)
        {
            var root = doc.RootElement;
            JsonElement items;
            string? version = null;
            if (root.ValueKind == JsonValueKind.Array) items = root;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("metrics", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                items = m;
                version = Str(root, "schemaVersion") ?? Str(root, "catalogVersion") ?? Str(root, "version");
            }
            else return Missing("metric-catalog-no-metrics-array") with { Sha256 = sha };

            var list = new List<EquityMetricDefinition>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var code = Str(item, "code") ?? Str(item, "metricCode");
                var group = Str(item, "group") ?? Str(item, "metricGroup");
                if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(group)) continue;
                var statement = Str(item, "statement");
                var derived = Bool(item, "derived") == true || Bool(item, "isDerived") == true || statement == "derived"
                    || code.EndsWith("_CALC", StringComparison.Ordinal);
                var expected = !derived && Bool(item, "optional") != true && Bool(item, "required") != false
                    && Bool(item, "expected") != false;
                list.Add(new EquityMetricDefinition(code.Trim(), group.Trim(), statement, Str(item, "unit"), expected,
                    Bool(item, "core") == true, StrSet(item, "consolidations") ?? StrSet(item, "consolidation"),
                    StrSet(item, "periodTypes") ?? StrSet(item, "periodType")));
            }
            if (list.Count == 0) return Missing("metric-catalog-empty") with { Sha256 = sha, Version = version };
            return new EquityMetricCatalog(Ok, null, version, sha, list);
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        } : null;

    private static IReadOnlySet<string>? StrSet(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return new HashSet<string>(StringComparer.Ordinal) { v.GetString()! };
        if (v.ValueKind != JsonValueKind.Array) return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())) set.Add(e.GetString()!);
        return set.Count == 0 ? null : set;
    }
}

/// <summary>Loads the metric catalog file (EquityFacts:MetricCatalogPath), cached by size + mtime.</summary>
public sealed class EquityMetricCatalogProvider
{
    private readonly string? _path;
    private readonly object _gate = new();
    private (long Length, DateTime Mtime, EquityMetricCatalog Catalog)? _cached;

    public EquityMetricCatalogProvider(IOptions<EquityFactsOptions> options, IHostEnvironment environment)
        : this(Resolve(options.Value.MetricCatalogPath, environment.ContentRootPath))
    {
    }

    internal EquityMetricCatalogProvider(string? absolutePath) => _path = absolutePath;

    internal static string? Resolve(string? configured, string contentRoot) =>
        string.IsNullOrWhiteSpace(configured) ? null
        : Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured));

    public EquityMetricCatalog Get()
    {
        if (_path is null) return EquityMetricCatalog.Missing("metric-catalog-path-unconfigured");
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists) return EquityMetricCatalog.Missing("metric-catalog-file-missing");
            lock (_gate)
            {
                if (_cached is { } c && c.Length == info.Length && c.Mtime == info.LastWriteTimeUtc) return c.Catalog;
                var catalog = EquityMetricCatalog.Parse(File.ReadAllBytes(_path));
                _cached = (info.Length, info.LastWriteTimeUtc, catalog);
                return catalog;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return EquityMetricCatalog.Missing("metric-catalog-unreadable");
        }
    }
}

/// <summary>Rows that are visible at a cutoff (documents, extractions, attempts, facts) for one symbol.</summary>
/// <remarks>
/// <see cref="Facts"/> holds only facts of each document's CURRENT extraction (latest succeeded|partial extraction
/// visible at the cutoff, by FinishedAtUtc then ExtractorVersion) plus derived facts whose inputs are all current;
/// facts of superseded extractions (kept in the DB as history) are in <see cref="SupersededFacts"/> and are never
/// selected or counted. A failed newer extraction does not supersede an older successful one.
/// </remarks>
internal sealed record EquityFactsVisibleRows(
    IReadOnlyList<EquitySourceDocument> Documents,
    IReadOnlyList<EquityDocumentExtraction> Extractions,
    IReadOnlyList<EquitySourceAttempt> Attempts,
    IReadOnlyList<EquityFinancialFact> Facts,
    IReadOnlyList<EquityFinancialFact>? SupersededFacts = null,
    IReadOnlyDictionary<Guid, EquityDocumentExtraction>? CurrentExtractions = null)
{
    public IReadOnlyList<EquityFinancialFact> Superseded => SupersededFacts ?? [];

    /// <summary>Current extraction of a document: latest succeeded|partial, else the latest attempt (failed) for status display.</summary>
    public EquityDocumentExtraction? CurrentOrLatest(Guid documentId) =>
        CurrentExtractions?.GetValueOrDefault(documentId)
        ?? Extractions.Where(e => e.DocumentId == documentId).OrderByDescending(e => e.FinishedAtUtc)
            .ThenByDescending(e => e.ExtractorVersion, StringComparer.Ordinal).ThenByDescending(e => e.Id).FirstOrDefault();
}

/// <summary>
/// Financial facts + fundamentals coverage (spec v3 §4), public and market-only. As-of rule (shared with the
/// research snapshot): a document is visible when <see cref="EquitySourceDocumentService.AvailableAt"/> ≤ asOf
/// (a revision only from its first-seen time); a fact when its AvailableAtUtc ≤ asOf AND its document is visible;
/// an attempt when AttemptedAtUtc ≤ asOf. Extractions are NOT gated by FinishedAtUtc: the current extraction of a
/// visible document is the latest successful one known now (retrospective processing, <see cref="RetrospectiveRule"/>).
/// </summary>
public sealed class EquityFinancialFactsService
{
    public const int DefaultTake = 2000;
    public const int MaxTake = 10000;
    public const int FirstYear = 2016;

    public static readonly string[] MetricGroups =
        ["balance-sheet", "income-statement", "cash-flow", "credit-quality", "deposits-funding", "capital", "shares"];
    public static readonly string[] Consolidations = ["consolidated", "separate"];
    public static readonly string[] FactStatuses = ["extracted", "validated", "review-needed", "quarantined"];
    public static readonly string[] PeriodTypes = ["quarter", "ytd", "half-year", "annual", "point-in-time"];
    private static readonly HashSet<string> StatementDocTypes = new(StringComparer.Ordinal) { "financial-statement", "annual-report" };
    private static readonly HashSet<string> PositiveAttempts = new(StringComparer.Ordinal) { "listed", "downloaded", "unchanged", "changed", "too-large" };
    private static readonly System.Text.RegularExpressions.Regex PeriodLabelPattern =
        new("^([0-9]{4})(Q[1-4]|H[12]|-9M|-6M|-3M)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly AppDbContext _db;
    private readonly EquityMetricCatalogProvider _catalogProvider;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;

    public EquityFinancialFactsService(AppDbContext db, EquityMetricCatalogProvider catalogProvider, TimeProvider timeProvider)
        : this(db, catalogProvider, timeProvider, null)
    {
    }

    internal EquityFinancialFactsService(AppDbContext db, EquityMetricCatalogProvider catalogProvider, TimeProvider? timeProvider, InstrumentCatalog? catalog)
    {
        _db = db;
        _catalogProvider = catalogProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    private string RequireDecision(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        return instrument.Symbol;
    }

    public static readonly string[] AsOfRules =
    [
        "document visible when availableAt <= asOf (issuer-published time, else first-seen; a revision (supersedesDocumentId set) only from its first-seen time)",
        "fact visible when fact.availableAtUtc <= asOf AND its document is visible",
        RetrospectiveRule,
        "fact status is the CURRENT validation status (a later quarantine/validation is not reconstructible as-of)",
        ValueRule,
        "selected = the fact the snapshot series uses for its (metric, periodLabel, periodType, consolidation) at this asOf: latest-available-validated; a fact whose supersededBy names a validated fact available at asOf is superseded-by-later-report (the superseding fact fills its series slot); other validated facts are superseded-by-later-available-validated",
    ];

    // --- loading (shared with the snapshot; runs inside the caller's transaction) -------

    internal static async Task<EquityFactsVisibleRows> LoadVisibleAsync(
        AppDbContext db, string sym, DateTime asOfUtc, IReadOnlyList<EquitySourceDocument>? allDocs, CancellationToken ct)
    {
        allDocs ??= await db.EquitySourceDocuments.AsNoTracking().Where(d => d.Symbol == sym).ToListAsync(ct);
        var docs = allDocs.Where(d => EquitySourceDocumentService.AvailableAt(d) <= asOfUtc).ToList();
        var docIds = docs.Select(d => d.Id).ToHashSet();
        var allDocIds = allDocs.Select(d => d.Id).ToList();
        // Extraction/validation are retrospective PROCESSING of information that was available at the document's
        // availability time: the time WE extracted it never hides a fact (spec v3 §4/§6). So the current extraction is
        // the latest successful one known now; only document availability and fact.AvailableAtUtc gate facts.
        // Discovery attempts are different: each is the system's own observation of an issuer listing AT
        // AttemptedAtUtc (listed / not-listed / failed), i.e. operational bookkeeping, so only attempts made at or
        // before asOf are visible — a later poll must not rewrite a past snapshot (or its hash).
        var extractions = (await db.EquityDocumentExtractions.AsNoTracking()
                .Where(e => allDocIds.Contains(e.DocumentId)).ToListAsync(ct))
            .Where(e => docIds.Contains(e.DocumentId)).ToList();
        var attempts = await db.EquitySourceAttempts.AsNoTracking()
            .Where(a => a.Symbol == sym && a.AttemptedAtUtc <= asOfUtc).ToListAsync(ct);
        var facts = (await db.EquityFinancialFacts.AsNoTracking()
                .Where(f => f.Symbol == sym && f.AvailableAtUtc <= asOfUtc).ToListAsync(ct))
            .Where(f => docIds.Contains(f.DocumentId)).ToList();
        return Partition(docs, extractions, attempts, facts);
    }

    /// <summary>Splits visible facts into current-extraction facts and superseded-extraction facts (see <see cref="EquityFactsVisibleRows"/>).</summary>
    internal static EquityFactsVisibleRows Partition(IReadOnlyList<EquitySourceDocument> docs, IReadOnlyList<EquityDocumentExtraction> extractions,
        IReadOnlyList<EquitySourceAttempt> attempts, IReadOnlyList<EquityFinancialFact> facts)
    {
        var current = extractions.Where(e => e.Status is "succeeded" or "partial").GroupBy(e => e.DocumentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.FinishedAtUtc)
                .ThenByDescending(e => e.ExtractorVersion, StringComparer.Ordinal).ThenByDescending(e => e.Id).First());
        var currentIds = current.Values.Select(e => e.Id).ToHashSet();
        var superseded = facts.Where(f => f.ExtractionId is { } x && !currentIds.Contains(x)).Select(f => f.FactKey).ToHashSet(StringComparer.Ordinal);
        // A derived fact is superseded when any input (transitively) is.
        var derived = facts.Where(f => f.IsDerived).Select(f => (f.FactKey, Inputs: DerivationInputs(f.DerivationJson))).ToList();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (key, inputs) in derived)
                if (!superseded.Contains(key) && inputs.Any(superseded.Contains)) changed = superseded.Add(key) || changed;
        }
        return new EquityFactsVisibleRows(docs, extractions, attempts,
            facts.Where(f => !superseded.Contains(f.FactKey)).ToList(), facts.Where(f => superseded.Contains(f.FactKey)).ToList(), current);
    }

    /// <summary>Input FactKeys of a derived fact (DerivationJson.inputs: string[]); ValueKind-guarded.</summary>
    internal static string[] DerivationInputs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("inputs", out var inputs)
                || inputs.ValueKind != JsonValueKind.Array) return [];
            return inputs.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray();
        }
        catch (JsonException) { return []; }
    }

    // --- facts API ---------------------------------------------------------------------

    public sealed record FactsQuery(string? Metric, string? PeriodType, string? From, string? To, DateTimeOffset AsOf, string? Status, int? Take);

    public sealed record FactsResult(EquityFinancialFactsResponse? Response, string? Error);

    public async Task<FactsResult> GetFactsAsync(string symbol, FactsQuery q, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var status = string.IsNullOrWhiteSpace(q.Status) ? "validated" : q.Status.Trim();
        if (status != "all" && !FactStatuses.Contains(status))
            return new(null, $"status must be one of {string.Join(", ", FactStatuses)} or all.");
        var metrics = (q.Metric ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m.ToUpperInvariant()).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToArray();
        if (metrics.Any(m => m.Length > 64)) return new(null, "metric codes are at most 64 characters.");
        var periodType = string.IsNullOrWhiteSpace(q.PeriodType) ? null : q.PeriodType.Trim();
        if (periodType is not null && !PeriodTypes.Contains(periodType))
            return new(null, $"periodType must be one of {string.Join(", ", PeriodTypes)}.");
        if (!TryBound(q.From, start: true, out var from, out var err) || !TryBound(q.To, start: false, out var to, out err))
            return new(null, err);
        var take = Math.Clamp(q.Take ?? DefaultTake, 1, MaxTake);
        var asOfUtc = q.AsOf.UtcDateTime;

        var rows = await LoadVisibleAsync(_db, sym, asOfUtc, null, ct);
        var docs = rows.Documents.ToDictionary(d => d.Id);
        var extractions = rows.Extractions.ToDictionary(e => e.Id);
        var supersededIds = rows.Superseded.Select(f => f.Id).ToHashSet();
        var filtered = rows.Facts.Concat(rows.Superseded)
            .Where(f => status == "all" || f.Status == status)
            .Where(f => metrics.Length == 0 || metrics.Contains(f.MetricCode))
            .Where(f => periodType is null || f.PeriodType == periodType)
            .Where(f => from is null || f.PeriodEnd >= from)
            .Where(f => to is null || f.PeriodEnd <= to)
            .OrderBy(f => f.MetricCode, StringComparer.Ordinal).ThenBy(f => f.PeriodEnd).ThenBy(f => f.PeriodType, StringComparer.Ordinal)
            .ThenBy(f => f.Consolidation, StringComparer.Ordinal).ThenBy(f => f.FactKey, StringComparer.Ordinal)
            .ToList();
        var selected = SelectSeries(rows).Values.Select(s => s.Fact.Id).ToHashSet();
        var supersededByReport = SupersededByLaterReport(rows);
        var items = filtered.Take(take).Select(f => ToDto(f, docs.GetValueOrDefault(f.DocumentId),
            f.ExtractionId is { } eid ? extractions.GetValueOrDefault(eid) : null) with
        {
            Selected = selected.Contains(f.Id),
            Selection = supersededIds.Contains(f.Id) ? SelectionSupersededExtraction
                : f.Status != "validated" ? SelectionNotValidated
                : supersededByReport.Contains(f.FactKey) ? SelectionSupersededByLaterReport
                : selected.Contains(f.Id) ? SelectionLatest : SelectionSuperseded,
        }).ToArray();
        return new(new EquityFinancialFactsResponse(EquityDocumentPipelineContracts.FactsVersion, sym, asOfUtc, status, metrics,
            periodType, from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            items.Length, filtered.Count > items.Length, items, AsOfRules), null);
    }

    /// <summary>from/to: yyyy-MM-dd (period end, inclusive) or a period label (2020Q1, 2020H1, 2020).</summary>
    internal static bool TryBound(string? raw, bool start, out DateOnly? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var text = raw.Trim();
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            value = d;
            return true;
        }
        if (PeriodBounds(text) is { } b)
        {
            value = start ? b.Start : b.End;
            return true;
        }
        error = "from/to must be yyyy-MM-dd or a period label (2020Q1, 2020H1, 2020).";
        return false;
    }

    /// <summary>Calendar bounds of a period label (fiscal year = calendar year for BID/MBB).</summary>
    internal static (DateOnly Start, DateOnly End)? PeriodBounds(string label)
    {
        var m = PeriodLabelPattern.Match(label);
        if (!m.Success) return null;
        var y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (y < 1900 || y > 2999) return null;
        var suffix = m.Groups[2].Value;
        return suffix switch
        {
            "" => (new DateOnly(y, 1, 1), new DateOnly(y, 12, 31)),
            "H1" or "-6M" => (new DateOnly(y, 1, 1), new DateOnly(y, 6, 30)),
            "H2" => (new DateOnly(y, 7, 1), new DateOnly(y, 12, 31)),
            "-9M" => (new DateOnly(y, 1, 1), new DateOnly(y, 9, 30)),
            "-3M" => (new DateOnly(y, 1, 1), new DateOnly(y, 3, 31)),
            _ => QuarterBounds(y, suffix[1] - '0'),
        };
    }

    private static (DateOnly, DateOnly) QuarterBounds(int y, int q)
    {
        var start = new DateOnly(y, (q - 1) * 3 + 1, 1);
        return (start, start.AddMonths(3).AddDays(-1));
    }

    internal static EquityFinancialFactDto ToDto(EquityFinancialFact f, EquitySourceDocument? d, EquityDocumentExtraction? e) => new(
        f.FactKey, f.MetricCode, f.MetricDefinitionVersion, f.MetricGroup, f.Statement, f.PeriodLabel, f.PeriodType,
        f.PeriodStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), f.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        f.FiscalYear, f.Consolidation, f.Value, f.Unit, f.Scale, f.RawLabel, f.RawValueText, f.RawUnitText, f.ColumnRole,
        f.PageNumber, f.Location, f.Status, Json(f.ValidationJson), f.IsDerived, Json(f.DerivationJson), Utc(f.AvailableAtUtc),
        d is null ? null : new EquityFactDocumentRef(d.Id, d.SourceUrl, d.ContentSha256, d.Title, d.DocType, d.ReportingPeriod,
            d.Revision, d.Provider, EquitySourceDocumentService.AvailableAt(d), EquitySourceDocumentService.AvailabilityBasis(d)),
        e is null ? null : new EquityFactExtractionRef(e.Id, e.ExtractorVersion, e.Method, e.Status, e.PageCount, e.PagesOcr, Utc(e.FinishedAtUtc)),
        NormalizedValue: Normalized(f));

    /// <summary>Value × Scale (exact decimal, in Unit); null only on decimal overflow.</summary>
    internal static decimal? Normalized(EquityFinancialFact f)
    {
        try { return checked(f.Value * f.Scale); }
        catch (OverflowException) { return null; }
    }

    public const string ValueRule = "normalizedValue is in unit (= value × scale, exact decimal); value is as printed in the document (scale = unit per printed unit, e.g. 1000000 for triệu đồng)";

    internal static JsonElement? Json(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    internal static DateTime Utc(DateTime v) => v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };

    // --- coverage ----------------------------------------------------------------------

    public static readonly string[] CoverageRules =
    [
        "grid: 2016Q1..latest quarter ended before asOf (VN date) × half-year (YYYYH1) × annual (YYYY); metric groups × consolidated|separate",
        "stage = highest reached: not-attempted < not-listed < discovered < downloaded < extracted < validated < persisted",
        "validated = >= 1 validated fact in the cell but not every expected catalog metric; persisted = every expected metric validated (needs the metric catalog)",
        "documents/attempts count for a cell when reportingPeriod/targetPeriod equals the cell label (YYYYQn, YYYYH1, YYYY); documents: financial-statement|annual-report; consolidation unknown counts for both",
        RetrospectiveRule,
        "source: own-report = at least one counted fact is printed as the period's own (current/cumulative-current) or derived column; comparative-only = every counted fact is a comparative column of a later report (sourceDocumentIds) — real printed numbers, but no own-period report is stored; summary.comparativeOnlyCells counts them",
        "documents not yet available at asOf and attempts made after asOf do not exist for that asOf (a later publication or poll never changes a past grid)",
        "extraction: each document counts through its current extraction (latest succeeded|partial by finishedAtUtc, then extractorVersion; a failed newer run keeps the older one); facts of superseded extractions are never counted; extractorVersion = that extraction",
        "expected metrics per cell come from the catalog periodTypes: cash-flow metrics are printed cumulative only (ytd|half-year|annual), so quarter cells have no cash-flow group",
        "facts: quarter cell = label YYYYQn with periodType quarter|point-in-time; H1 cell = YYYYH1 half-year; FY cell = YYYY annual (a Q4 report's YYYY ytd column never counts as FY); any point-in-time fact whose periodEnd equals the cell's period end also counts; quarantined/review-needed facts never count as validated",
    ];

    public async Task<EquityFundamentalsCoverageResponse> GetCoverageAsync(string symbol, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var asOf = _timeProvider.GetUtcNow().UtcDateTime;
        var rows = await LoadVisibleAsync(_db, sym, asOf, null, ct);
        var catalog = _catalogProvider.Get();
        var (first, last, cells, summary) = ComputeCoverage(rows, asOf, catalog);
        return new EquityFundamentalsCoverageResponse(EquityDocumentPipelineContracts.CoverageVersion, sym, asOf, first, last,
            catalog.Info(), summary, cells, CoverageRules);
    }

    /// <summary>A grid period; <see cref="FactTypes"/> = the PeriodTypes a fact with a matching label must have.</summary>
    internal sealed record GridPeriod(string Label, string Kind, DateOnly End, string[] Labels, string[] FactTypes);

    internal static List<GridPeriod> Grid(DateTime asOfUtc)
    {
        var asOfDate = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(new DateTimeOffset(Utc(asOfUtc)).ToUnixTimeMilliseconds()).DateTime);
        var list = new List<GridPeriod>();
        for (var y = FirstYear; y <= asOfDate.Year; y++)
        {
            for (var q = 1; q <= 4; q++)
            {
                var (_, end) = QuarterBounds(y, q);
                if (end >= asOfDate) break;
                var label = $"{y}Q{q}";
                // quarter: the quarter column (quarter) + balances labelled by the balance-date quarter (point-in-time);
                // H1: the semi-annual report (half-year), FY: the audited annual report (annual) — a Q4 report's
                // 12-month column ("YYYY", ytd) and the 6M ytd column never count as H1/FY.
                list.Add(new GridPeriod(label, "quarter", end, [label], ["quarter", "point-in-time"]));
                if (q == 2) list.Add(new GridPeriod($"{y}H1", "half-year", end, [$"{y}H1"], ["half-year"]));
                if (q == 4) list.Add(new GridPeriod($"{y}", "annual", end, [$"{y}"], ["annual"]));
            }
        }
        return list;
    }

    private static bool KindAccepts(string kind, IReadOnlySet<string>? periodTypes) =>
        periodTypes is null || kind switch
        {
            "quarter" => periodTypes.Contains("quarter") || periodTypes.Contains("point-in-time"),
            "half-year" => periodTypes.Contains("half-year") || periodTypes.Contains("ytd") || periodTypes.Contains("point-in-time"),
            _ => periodTypes.Contains("annual") || periodTypes.Contains("point-in-time"),
        };

    internal static (string First, string Last, List<EquityCoverageCell> Cells, EquityCoverageSummary Summary) ComputeCoverage(
        EquityFactsVisibleRows rows, DateTime asOfUtc, EquityMetricCatalog catalog)
    {
        var grid = Grid(asOfUtc);
        var groups = catalog.IsOk
            ? catalog.Metrics.Select(m => m.Group).Distinct()
                .OrderBy(g => Array.IndexOf(MetricGroups, g) is var i && i >= 0 ? i : int.MaxValue).ThenBy(g => g, StringComparer.Ordinal).ToArray()
            : MetricGroups;
        var statementDocs = rows.Documents.Where(d => StatementDocTypes.Contains(d.DocType)).ToList();
        var latestExtraction = rows.Documents.Select(d => (d.Id, E: rows.CurrentOrLatest(d.Id))).Where(x => x.E is not null)
            .ToDictionary(x => x.Id, x => x.E!);
        var cells = new List<EquityCoverageCell>();

        foreach (var p in grid)
        {
            var labelSet = p.Labels.ToHashSet(StringComparer.Ordinal);
            var periodFacts = rows.Facts.Where(f => (labelSet.Contains(f.PeriodLabel) && p.FactTypes.Contains(f.PeriodType))
                || (f.PeriodType == "point-in-time" && f.PeriodEnd == p.End)).ToList();
            foreach (var consolidation in Consolidations)
            {
                var docs = statementDocs.Where(d => d.ReportingPeriod is not null && labelSet.Contains(d.ReportingPeriod)
                    && (d.Consolidation == consolidation || d.Consolidation == "unknown")).ToList();
                var attempts = rows.Attempts.Where(a => a.TargetPeriod is not null && labelSet.Contains(a.TargetPeriod)
                    && (a.Consolidation is null || a.Consolidation == consolidation)
                    && (a.DocType is null || StatementDocTypes.Contains(a.DocType)))
                    .OrderByDescending(a => a.AttemptedAtUtc).ThenByDescending(a => a.Id).ToList();
                var docExtractions = docs.Select(d => latestExtraction.GetValueOrDefault(d.Id)).Where(e => e is not null).Select(e => e!).ToList();

                foreach (var group in groups)
                {
                    var expected = catalog.IsOk
                        ? catalog.Metrics.Where(m => m.Group == group && m.Expected
                                && (m.Consolidations is null || m.Consolidations.Contains(consolidation)) && KindAccepts(p.Kind, m.PeriodTypes))
                            .Select(m => m.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray()
                        : [];
                    if (catalog.IsOk && expected.Length == 0) continue; // the catalog expects nothing here
                    var facts = periodFacts.Where(f => f.MetricGroup == group && f.Consolidation == consolidation).ToList();
                    var validatedMetrics = facts.Where(f => f.Status == "validated").Select(f => f.MetricCode).ToHashSet(StringComparer.Ordinal);
                    var validatedExpected = expected.Count(validatedMetrics.Contains);
                    var counts = new EquityCoverageCellCounts(docs.Count, docExtractions.Count, facts.Count,
                        facts.Count(f => f.Status == "validated"), facts.Count(f => f.Status == "extracted"),
                        facts.Count(f => f.Status == "review-needed"), facts.Count(f => f.Status == "quarantined"),
                        facts.Count(f => f.IsDerived), expected.Length, catalog.IsOk ? validatedExpected : validatedMetrics.Count);
                    var bestDoc = docs.OrderByDescending(d => latestExtraction.GetValueOrDefault(d.Id)?.Status is "succeeded" or "partial")
                        .ThenByDescending(d => d.Consolidation == consolidation).ThenByDescending(d => d.Revision)
                        .ThenByDescending(d => d.FirstSeenAtUtc).ThenBy(d => d.Id).FirstOrDefault();
                    var factDoc = facts.Where(f => f.Status == "validated").Select(f => (Guid?)f.DocumentId).FirstOrDefault()
                        ?? facts.Select(f => (Guid?)f.DocumentId).FirstOrDefault();
                    var bestExtraction = bestDoc is null ? null : latestExtraction.GetValueOrDefault(bestDoc.Id);
                    var latestAttempt = attempts.FirstOrDefault();
                    var missing = catalog.IsOk ? expected.Where(c => !validatedMetrics.Contains(c)).ToArray() : [];

                    string stage;
                    string? reason;
                    if (validatedMetrics.Count > 0)
                    {
                        if (catalog.IsOk && missing.Length == 0) { stage = EquityCoverageStages.Persisted; reason = null; }
                        else
                        {
                            stage = EquityCoverageStages.Validated;
                            reason = !catalog.IsOk ? EquityMetricCatalog.Unavailable : $"missing-validated:{missing.Length}";
                        }
                    }
                    else if (facts.Count > 0)
                    {
                        stage = EquityCoverageStages.Extracted;
                        reason = $"facts-not-validated:extracted={counts.Extracted},review-needed={counts.ReviewNeeded},quarantined={counts.Quarantined}";
                    }
                    else if (docExtractions.Any(e => e.Status is "succeeded" or "partial"))
                    {
                        stage = EquityCoverageStages.Extracted;
                        reason = "no-facts-for-group";
                    }
                    else if (docs.Count > 0)
                    {
                        stage = EquityCoverageStages.Downloaded;
                        reason = docExtractions.Count > 0 ? "extraction-failed" : "not-extracted";
                    }
                    else if (attempts.FirstOrDefault(a => PositiveAttempts.Contains(a.Outcome)) is { } listed)
                    {
                        stage = EquityCoverageStages.Discovered;
                        latestAttempt = listed;
                        reason = listed.Outcome == "too-large" ? "too-large" : "listed-not-downloaded";
                    }
                    else if (latestAttempt is { Outcome: "not-listed" })
                    {
                        stage = EquityCoverageStages.NotListed;
                        reason = "not-listed";
                    }
                    else if (latestAttempt is not null)
                    {
                        stage = EquityCoverageStages.NotAttempted;
                        reason = $"attempt-failed:{latestAttempt.Outcome}";
                    }
                    else
                    {
                        stage = EquityCoverageStages.NotAttempted;
                        reason = "no-attempt";
                    }
                    var showMissing = stage is EquityCoverageStages.Validated or EquityCoverageStages.Extracted or EquityCoverageStages.Downloaded;
                    // Source: judged on the facts that set the stage (validated ones when present).
                    var sourceFacts = facts.Any(f => f.Status == "validated") ? facts.Where(f => f.Status == "validated").ToList() : facts;
                    var comparativeOnly = sourceFacts.Count > 0 && sourceFacts.All(f => f.ColumnRole is "comparative" or "cumulative-comparative");
                    var source = sourceFacts.Count == 0 ? null : comparativeOnly ? SourceComparativeOnly : SourceOwnReport;
                    IReadOnlyList<Guid>? sourceDocs = comparativeOnly
                        ? sourceFacts.Select(f => f.DocumentId).Distinct().OrderBy(id => id).ToArray() : null;
                    if (comparativeOnly) factDoc = sourceDocs![0];
                    cells.Add(new EquityCoverageCell(p.Label, p.Kind, group, consolidation, stage, reason,
                        latestAttempt?.Id, latestAttempt?.Outcome, factDoc ?? bestDoc?.Id, bestExtraction?.Id, counts,
                        showMissing ? missing : [],
                        (factDoc is { } fd ? latestExtraction.GetValueOrDefault(fd) : bestExtraction)?.ExtractorVersion,
                        source, sourceDocs));
                }
            }
        }

        var byStage = EquityCoverageStages.Ordered.ToDictionary(s => s, s => cells.Count(c => c.Stage == s));
        var byKind = new SortedDictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var kind in cells.Select(c => c.PeriodKind).Distinct())
            byKind[kind] = EquityCoverageStages.Ordered.ToDictionary(s => s, s => cells.Count(c => c.PeriodKind == kind && c.Stage == s));
        var summary = new EquityCoverageSummary(cells.Count, byStage, byKind, rows.Documents.Count, rows.Extractions.Count,
            rows.Attempts.Count, rows.Facts.Count, rows.Facts.Count(f => f.Status == "validated"),
            rows.Facts.Count(f => f.Status == "quarantined"), rows.Facts.Count(f => f.Status == "review-needed"),
            cells.Count(c => c.Source == SourceComparativeOnly),
            EquityCoverageStages.Ordered.ToDictionary(s => s, s => cells.Count(c => c.Source == SourceComparativeOnly && c.Stage == s)));
        return (grid.Count == 0 ? "" : grid[0].Label, grid.Count == 0 ? "" : grid.Where(g => g.Kind == "quarter").Select(g => g.Label).LastOrDefault() ?? "",
            cells, summary);
    }

    // --- snapshot series ---------------------------------------------------------------

    private static int RoleRank(EquityFinancialFact f) => f.ColumnRole switch
    {
        "current" or "cumulative-current" => 0,
        "derived" => 1,
        _ => 2,
    };

    public const string SelectionLatest = "latest-available-validated";
    public const string SelectionSuperseded = "superseded-by-later-available-validated";
    public const string SelectionNotValidated = "not-validated";
    public const string SourceOwnReport = "own-report";
    public const string SourceComparativeOnly = "comparative-only";

    public const string RetrospectiveRule =
        "extraction and validation are retrospective processing: the latest successful extraction known NOW is used for every asOf and its time never hides a fact; extraction/validation status is current, not as-of; discovery attempts are observations at attemptedAtUtc and count only when attemptedAtUtc <= asOf";
    public const string SelectionSupersededExtraction = "superseded-extraction";
    public const string SelectionSupersededByLaterReport = "superseded-by-later-report";

    /// <summary>
    /// Series selection (spec v3 §6): per (metric, periodLabel, periodType, consolidation) the VALIDATED fact with the
    /// LATEST AvailableAtUtc among those visible at the cutoff — a later audited annual report or restatement supersedes
    /// the earlier unaudited/original figure once it is known. Ties: current column > derived > comparative, newer
    /// document revision, then factKey (ordinal).
    /// </summary>
    /// <summary>The fact a series slot uses; <see cref="Replaced"/> is set when the slot's own candidate was superseded by a later report.</summary>
    internal sealed record SeriesSlot(EquityFinancialFact Fact, EquityFinancialFact? Replaced = null, string? Reason = null);

    /// <summary>
    /// Supersession links written by the extractor into ValidationJson: <c>supersededBy</c>/<c>supersessionReason</c> at the
    /// root or under <c>crossDocument</c>, and <c>supersededByDerived {factKeys, reason}</c>. ValueKind-guarded.
    /// </summary>
    internal static (string[] Keys, string? Reason) SupersessionOf(string? validationJson)
    {
        if (string.IsNullOrWhiteSpace(validationJson)) return ([], null);
        try
        {
            using var doc = JsonDocument.Parse(validationJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], null);
            var keys = new List<string>();
            string? reason = null;
            void Read(JsonElement obj, string keysName, string reasonName)
            {
                if (obj.ValueKind != JsonValueKind.Object) return;
                if (obj.TryGetProperty(keysName, out var k))
                {
                    if (k.ValueKind == JsonValueKind.String) keys.Add(k.GetString()!);
                    else if (k.ValueKind == JsonValueKind.Array)
                        keys.AddRange(k.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
                }
                if (reason is null && obj.TryGetProperty(reasonName, out var r) && r.ValueKind == JsonValueKind.String) reason = r.GetString();
            }
            Read(root, "supersededBy", "supersessionReason");
            if (root.TryGetProperty("crossDocument", out var cross)) Read(cross, "supersededBy", "supersessionReason");
            if (root.TryGetProperty("supersededByDerived", out var derived)) Read(derived, "factKeys", "reason");
            return (keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToArray(), reason);
        }
        catch (JsonException) { return ([], null); }
    }

    /// <summary>
    /// Series selection. A validated candidate is NOT selectable when its supersession links name a fact that is validated,
    /// from the current extraction and available at the cutoff (i.e. present in <c>rows.Facts</c>); among the remaining
    /// candidates the latest-available rule applies. When every candidate of a slot is superseded, the slot takes the
    /// (transitively) superseding fact — across period types (12-month ytd ↔ audited annual) and for restatements.
    /// </summary>
    internal static Dictionary<(string, string, string, string), SeriesSlot> SelectSeries(EquityFactsVisibleRows rows)
    {
        var revisions = rows.Documents.ToDictionary(d => d.Id, d => d.Revision);
        var finished = rows.Extractions.ToDictionary(e => e.Id, e => Utc(e.FinishedAtUtc));
        var live = rows.Facts.Where(f => f.Status == "validated").GroupBy(f => f.FactKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        IOrderedEnumerable<EquityFinancialFact> Order(IEnumerable<EquityFinancialFact> fs) => fs.OrderByDescending(f => Utc(f.AvailableAtUtc))
            .ThenByDescending(f => f.ExtractionId is { } x && finished.TryGetValue(x, out var t) ? t : DateTime.MinValue)
            .ThenBy(RoleRank)
            .ThenByDescending(f => revisions.GetValueOrDefault(f.DocumentId))
            .ThenBy(f => f.FactKey, StringComparer.Ordinal);
        (EquityFinancialFact? By, string? Reason) Superseder(EquityFinancialFact f)
        {
            var (keys, reason) = SupersessionOf(f.ValidationJson);
            var by = Order(keys.Where(live.ContainsKey).Select(k => live[k]).Where(o => o.FactKey != f.FactKey)).FirstOrDefault();
            return (by, by is null ? null : reason ?? "superseded");
        }
        EquityFinancialFact Final(EquityFinancialFact f)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { f.FactKey };
            for (var (next, _) = Superseder(f); next is not null && seen.Add(next.FactKey); (next, _) = Superseder(f)) f = next;
            return f;
        }
        var result = new Dictionary<(string, string, string, string), SeriesSlot>();
        foreach (var g in live.Values.GroupBy(f => (f.MetricCode, f.PeriodLabel, f.PeriodType, f.Consolidation)))
        {
            var ordered = Order(g).ToList();
            var own = ordered.FirstOrDefault(f => Superseder(f).By is null);
            if (own is not null) { result[g.Key] = new SeriesSlot(own); continue; }
            var replaced = ordered[0];
            result[g.Key] = new SeriesSlot(Final(replaced), replaced, Superseder(replaced).Reason);
        }
        return result;
    }

    /// <summary>FactKeys of validated facts that are superseded by a live later report (never selectable).</summary>
    internal static HashSet<string> SupersededByLaterReport(EquityFactsVisibleRows rows)
    {
        var live = rows.Facts.Where(f => f.Status == "validated").Select(f => f.FactKey).ToHashSet(StringComparer.Ordinal);
        return rows.Facts.Where(f => f.Status == "validated" && SupersessionOf(f.ValidationJson).Keys.Any(k => k != f.FactKey && live.Contains(k)))
            .Select(f => f.FactKey).ToHashSet(StringComparer.Ordinal);
    }

    internal static readonly string[] SeriesRules =
    [
        "validated facts only; fact.availableAtUtc <= asOf AND document available at asOf",
        "one point per (metric, period, periodType, consolidation): the validated fact with the LATEST availableAtUtc (a later audited annual report / restatement supersedes the earlier figure once known); ties: newer extraction, current column > derived > comparative, newer document revision, factKey",
        "a candidate whose ValidationJson supersededBy (root/crossDocument) or supersededByDerived names a validated, current-extraction fact available at asOf is not selectable; if no candidate of the slot remains, the point takes the superseding fact (selection superseded-by-later-report, supersededFactKey = the original, supersededBy, supersessionReason, sourcePeriodType) — e.g. a Q4 report's 12-month ytd figure replaced by the audited annual",
        "only facts of each document's current extraction (latest succeeded|partial by finishedAtUtc, then extractorVersion) count; facts of superseded extractions are history (financial-facts lists them as superseded-extraction)",
        "status is the CURRENT validation status (not reconstructible as-of)",
        RetrospectiveRule,
        ValueRule,
        "full provenance per fact: GET /api/equities/{symbol}/financial-facts?asOf=<asOf>",
    ];

    internal static EquityFundamentalsSeriesSection Series(EquityFactsVisibleRows rows)
    {
        var validated = rows.Facts.Where(f => f.Status == "validated").ToList();
        if (validated.Count == 0)
            return new EquityFundamentalsSeriesSection("empty", "no-validated-facts-at-asof", 0, 0, [], SeriesRules);
        var slots = SelectSeries(rows);
        var metrics = slots.GroupBy(kv => kv.Key.Item1)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new EquityFundamentalsSeriesMetric(g.Key,
                g.Select(kv => (kv.Value.Replaced ?? kv.Value.Fact).MetricGroup).OrderBy(x => x, StringComparer.Ordinal).First(),
                g.Select(kv => (Slot: kv.Value.Replaced ?? kv.Value.Fact, kv.Value))
                    .OrderBy(x => x.Slot.PeriodEnd).ThenBy(x => x.Slot.PeriodType, StringComparer.Ordinal)
                    .ThenBy(x => x.Slot.Consolidation, StringComparer.Ordinal).ThenBy(x => x.Slot.PeriodLabel, StringComparer.Ordinal)
                    .Select(x =>
                    {
                        var (s, f) = (x.Slot, x.Value.Fact);
                        return new EquityFundamentalsSeriesPoint(s.PeriodLabel, s.PeriodType,
                            s.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s.Consolidation, f.Value, f.Unit,
                            f.FactKey, f.DocumentId, f.PageNumber, f.IsDerived, Utc(f.AvailableAtUtc), f.Scale, Normalized(f), f.ColumnRole,
                            x.Value.Replaced is null ? SelectionLatest : SelectionSupersededByLaterReport,
                            x.Value.Replaced?.FactKey, x.Value.Replaced is null ? null : [f.FactKey], x.Value.Reason,
                            x.Value.Replaced is null ? null : f.PeriodType);
                    })
                    .ToArray()))
            .ToArray();
        return new EquityFundamentalsSeriesSection("ok", null, slots.Count, validated.Count - slots.Values.Count(s => s.Replaced is null),
            metrics, SeriesRules);
    }

    public const int SnapshotGapLimit = 200;

    internal static EquityFundamentalsCoverageSection CoverageSection(EquityFactsVisibleRows rows, DateTime asOfUtc, EquityMetricCatalog catalog)
    {
        var (first, last, cells, summary) = ComputeCoverage(rows, asOfUtc, catalog);
        var gaps = cells.Where(c => c.Stage != EquityCoverageStages.Persisted)
            .OrderByDescending(c => PeriodBounds(c.Period)?.End ?? default)
            .ThenBy(c => c.PeriodKind, StringComparer.Ordinal).ThenBy(c => c.Consolidation, StringComparer.Ordinal)
            .ThenBy(c => c.MetricGroup, StringComparer.Ordinal)
            .ToList();
        var shown = gaps.Take(SnapshotGapLimit).Select(c => new EquityFundamentalsCoverageGap(c.Period, c.PeriodKind, c.MetricGroup,
            c.Consolidation, c.Stage, c.Reason, c.AttemptId, c.DocumentId, c.MissingMetrics, c.Source)).ToArray();
        return new EquityFundamentalsCoverageSection(first, last, catalog.Info(), summary, gaps.Count, gaps.Count > shown.Length, shown,
            [.. CoverageRules, $"gaps: newest first, at most {SnapshotGapLimit}; full grid: GET /api/equities/{{symbol}}/fundamentals/coverage (current, not as-of)"]);
    }

    internal static EquityDocumentExtractionStatusDto ExtractionStatus(EquityFactsVisibleRows rows, Guid documentId)
    {
        var e = rows.CurrentOrLatest(documentId);
        var facts = rows.Facts.Count(f => f.DocumentId == documentId);
        var validated = rows.Facts.Count(f => f.DocumentId == documentId && f.Status == "validated");
        return e is null
            ? new EquityDocumentExtractionStatusDto("not-extracted", null, null, null, null, facts, validated)
            : new EquityDocumentExtractionStatusDto(e.Status, e.Method, e.ExtractorVersion, e.PageCount, e.PagesOcr, facts, validated);
    }
}
