using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>Raw provider fetch for equity context (spec §6, sources pinned by the W0 probe).</summary>
public interface IEquityContextSource
{
    string VciEventsUrl(string symbol, DateOnly fromLocal, DateOnly toLocal, int page, int size);
    string KbsFinanceInfoUrl(string symbol);
    Task<string> GetAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP source: VCI IQ events (vietcap Referer/Origin + browser UA, as probed) and KBS
/// finance-info CSTC quarterly. Public, no auth. Errors surface as exceptions to the collector.
/// </summary>
public sealed class HttpEquityContextSource(HttpClient http) : IEquityContextSource
{
    public const string VciIqBase = "https://iq.vietcap.com.vn/api/iq-insight-service";
    public const string KbsFinanceInfoBase = "https://kbbuddywts.kbsec.com.vn/iis-server/investment/stock/finance-info";
    public const string VciEventCodes = "DIV,ISS,AGME,AGMR,EGME,AIS,NLIS";
    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    public string VciEventsUrl(string symbol, DateOnly fromLocal, DateOnly toLocal, int page, int size) =>
        $"{VciIqBase}/v1/events?ticker={Uri.EscapeDataString(symbol)}&fromDate={fromLocal:yyyyMMdd}&toDate={toLocal:yyyyMMdd}" +
        $"&eventCode={VciEventCodes}&page={page}&size={size}";

    public string KbsFinanceInfoUrl(string symbol) =>
        $"{KbsFinanceInfoBase}/{Uri.EscapeDataString(symbol)}?page=1&pageSize=8&type=CSTC&unit=1000&termtype=2&languageid=1";

    public async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUa);
        if (url.StartsWith(VciIqBase, StringComparison.Ordinal))
        {
            request.Headers.TryAddWithoutValidation("Referer", "https://trading.vietcap.com.vn/");
            request.Headers.TryAddWithoutValidation("Origin", "https://trading.vietcap.com.vn");
        }
        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new MarketDataProviderException("context", $"HTTP {(int)response.StatusCode} from {new Uri(url).Host}",
                response.StatusCode, isTransient: (int)response.StatusCode >= 500);
        return body;
    }
}

/// <summary>Shared enums/validation for provider rows and admin imports (spec §3/§6).</summary>
public static class EquityContextRules
{
    public static readonly IReadOnlySet<string> ActionTypes = new HashSet<string>(StringComparer.Ordinal)
        { "cash-dividend", "stock-dividend", "bonus-shares", "rights-issue", "split", "other" };
    public static readonly IReadOnlySet<string> EventTypes = new HashSet<string>(StringComparer.Ordinal)
        { "earnings", "agm", "additional-listing", "other" };
    public static readonly IReadOnlySet<string> Metrics = new HashSet<string>(StringComparer.Ordinal)
        { "BVPS", "PB", "ROE", "NIM", "NPL", "COVERAGE", "LLR", "CAR", "LDR", "TOTAL_ASSETS", "NET_PROFIT", "EQUITY" };

    /// <summary>EQX v2 C1: absolute statement amounts (from issuer documents) — always unit VND.</summary>
    public static readonly IReadOnlySet<string> AmountMetrics = new HashSet<string>(StringComparer.Ordinal)
        { "TOTAL_ASSETS", "NET_PROFIT", "EQUITY" };

    /// <summary>Source of metrics read from an original issuer document (EquitySourceDocuments); never merged with KBS/VCI rows.</summary>
    public const string IssuerReportSource = "issuer-report";
    public const int MaxSourceLocationLength = 500;

    /// <summary>Upper bound for an imported ShareRatio (extra shares per old share).</summary>
    public const decimal MaxShareRatio = 10m;
    public static readonly IReadOnlySet<string> Units = new HashSet<string>(StringComparer.Ordinal) { "VND", "ratio", "percent" };
    public static readonly IReadOnlySet<string> Verification = new HashSet<string>(StringComparer.Ordinal)
        { "unverified", "verified", "rejected" };

    public const string ProvenanceVci = "provider:VCI";
    public const string ProvenanceKbs = "provider:KBS";
    public const string ProvenanceAdmin = "admin-manual";
    public const string KbsSource = "KBS finance-info CSTC";

    /// <summary>Metrics with no verified point-in-time source (W0 probe): never inferred.</summary>
    public static readonly IReadOnlyList<string> UnavailableMetrics = ["NPL", "COVERAGE", "CAR"];
    public const string NoPitSourceReason =
        "no point-in-time source verified (VCI statistics-financial has no publication date and labels periods early; KBS CSTC does not publish it)";

    /// <summary>Static notes per metric (KBS definitions; values kept as published).</summary>
    public static string? NoteFor(string metric, string? source) => (metric, source) switch
    {
        ("NIM", KbsSource) => "quarterly NIM as published by KBS, NOT annualized",
        ("ROE", KbsSource) => "trailing 4-quarter average ROE as published by KBS",
        ("LLR", KbsSource) => "KBS 'loan loss provision ratio'; sign as published (negative = provision balance)",
        ("LDR", KbsSource) => "KBS definition: customer loans / customer deposits (differs from VCI's LDR)",
        (_, IssuerReportSource) => "read from the issuer's original document (documentId/sourceLocation); never merged with KBS/VCI rows of the same metric",
        _ => null,
    };

    private static readonly Regex PeriodPattern = new("^[0-9]{4}Q[1-4]$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
        && url!.Length <= 1000;

    public static bool IsPeriod(string? period) => period is not null && PeriodPattern.IsMatch(period);

    public static DateOnly QuarterEnd(string period)
    {
        var year = int.Parse(period[..4], CultureInfo.InvariantCulture);
        var q = period[5] - '0';
        var month = q * 3;
        return new DateOnly(year, month, DateTime.DaysInMonth(year, month));
    }

    /// <summary>Date-only publication ⇒ available at 00:00 Asia/Ho_Chi_Minh of the NEXT day (conservative).</summary>
    public static DateTime AvailableAfterPublication(DateOnly published) =>
        DateTimeOffset.FromUnixTimeMilliseconds(VietnamMarketCalendar.ToUtcMs(published.AddDays(1), TimeOnly.MinValue)).UtcDateTime;

    public static DateOnly? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.Length >= 10 && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            return d;
        return null;
    }
}

/// <summary>
/// Collects corporate actions + events (VCI IQ events) and bank fundamentals (KBS CSTC) into the
/// Equity* tables, and validates admin imports with the same rules. Provider rows carry
/// provenance provider:VCI|provider:KBS, verification "unverified", the request URL as source and
/// AvailableAtUtc = 00:00 VN of the day after the (date-only) publication date. Upserts are
/// idempotent by the tables' unique keys and never overwrite admin-manual rows. Requests are
/// bounded (≤ 3 VCI pages + 1 KBS call per symbol). Each field comes from exactly one source.
/// </summary>
public sealed class EquityContextCollector
{
    public const int VciPageSize = 200;
    public const int MaxVciPages = 3;
    public const int MaxImportRows = 500;
    private static readonly DateOnly VciHistoryStart = new(2008, 1, 1);

    /// <summary>KBS ReportNormID → (metric, expected KBS unit, unit).</summary>
    private static readonly IReadOnlyDictionary<int, (string Metric, string KbsUnit, string Unit)> KbsMetrics =
        new Dictionary<int, (string, string, string)>
        {
            [54] = ("BVPS", "VNĐ", "VND"),
            [57] = ("PB", "Lần", "ratio"),
            [122] = ("ROE", "%", "percent"),
            [108] = ("NIM", "%", "percent"),
            [80] = ("LDR", "%", "percent"),
            [117] = ("LLR", "%", "percent"),
        };

    private readonly AppDbContext _db;
    private readonly IEquityContextSource _source;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;
    private readonly ILogger<EquityContextCollector> _logger;
    private readonly bool _kbsFundamentals;

    public EquityContextCollector(
        AppDbContext db,
        IEquityContextSource source,
        TimeProvider? timeProvider = null,
        InstrumentCatalog? catalog = null,
        ILogger<EquityContextCollector>? logger = null,
        IOptions<EquityIngestionOptions>? options = null)
    {
        _kbsFundamentals = (options?.Value ?? new EquityIngestionOptions()).KbsFundamentalsEnabled;
        _db = db;
        _source = source;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityContextCollector>.Instance;
    }

    /// <summary>Context symbols: catalog equities with ingestion (not the index: no events/fundamentals).</summary>
    public string RequireContextEquity(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (instrument.AssetClass != InstrumentAssetClasses.Equity || !instrument.Capabilities.Ingestion)
            throw new ArgumentException(
                $"Symbol '{instrument.Symbol}' has no corporate actions/events/fundamentals (equities only).", nameof(symbol));
        return instrument.Symbol;
    }

    // --- provider sync -------------------------------------------------------------------

    public async Task<EquityContextSyncReport> SyncAsync(string symbol, CancellationToken ct = default)
    {
        var sym = RequireContextEquity(symbol);
        var now = _timeProvider.GetUtcNow();
        var today = DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(now.ToUnixTimeMilliseconds()).DateTime);
        var errors = new List<string>();
        int requests = 0, ca = 0, ev = 0, fu = 0, skipped = 0;

        try
        {
            var actions = new List<EquityCorporateAction>();
            var events = new List<EquityEvent>();
            for (var page = 0; page < MaxVciPages; page++)
            {
                var url = _source.VciEventsUrl(sym, VciHistoryStart, today, page, VciPageSize);
                var body = await _source.GetAsync(url, ct);
                requests++;
                var parsed = ParseVciEvents(sym, body, SourceUrlWithoutPaging(url), now.UtcDateTime);
                actions.AddRange(parsed.Actions);
                events.AddRange(parsed.Events);
                skipped += parsed.Skipped;
                if (parsed.ItemsOnPage < VciPageSize || (page + 1) * VciPageSize >= parsed.TotalElements) break;
            }
            ca = await UpsertCorporateActionsAsync(sym, actions, provider: true, ct);
            ev = await UpsertEventsAsync(sym, events, provider: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "VCI events sync failed for {Symbol}", sym);
            errors.Add($"vci-events: {ex.GetType().Name}: {ex.Message}");
        }

        if (_kbsFundamentals) try
        {
            var url = _source.KbsFinanceInfoUrl(sym);
            var body = await _source.GetAsync(url, ct);
            requests++;
            var parsed = ParseKbsFinanceInfo(sym, body, url, now.UtcDateTime);
            skipped += parsed.Skipped;
            errors.AddRange(parsed.Warnings);
            fu = await UpsertFundamentalsAsync(sym, parsed.Rows, provider: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "KBS finance-info sync failed for {Symbol}", sym);
            errors.Add($"kbs-finance-info: {ex.GetType().Name}: {ex.Message}");
        }

        // "failed" = nothing written and every enabled source errored (KBS is optional since VNONLY-1).
        var sources = _kbsFundamentals ? 2 : 1;
        var status = errors.Count == 0 ? "succeeded" : requests == 0 || (ca + ev + fu == 0 && errors.Count >= sources) ? "failed" : "partial";
        return new EquityContextSyncReport(sym, now.UtcDateTime, status, requests, ca, ev, fu, skipped, errors);
    }

    private static string SourceUrlWithoutPaging(string url)
    {
        var i = url.IndexOf("&page=", StringComparison.Ordinal);
        return i < 0 ? url : url[..i];
    }

    internal sealed record VciParse(
        IReadOnlyList<EquityCorporateAction> Actions, IReadOnlyList<EquityEvent> Events,
        int ItemsOnPage, int TotalElements, int Skipped);

    /// <summary>Parse one VCI IQ events page: <c>{status, data:{content:[…], totalElements}}</c>.</summary>
    internal static VciParse ParseVciEvents(string symbol, string body, string sourceUrl, DateTime nowUtc)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new MarketDataPayloadException("VCI", "events payload is not {data:{content:[...]}}");
        var total = data.TryGetProperty("totalElements", out var te) && te.ValueKind == JsonValueKind.Number
            && te.TryGetInt32(out var t) ? t : content.GetArrayLength();

        var actions = new Dictionary<(DateOnly, string), EquityCorporateAction>();
        var events = new Dictionary<(string, DateOnly, string), EquityEvent>();
        var skipped = 0;
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { skipped++; continue; }
            var code = Str(item, "eventCode");
            var ticker = Str(item, "ticker") ?? Str(item, "organCode");
            if (code is null || (ticker is not null && !string.Equals(ticker, symbol, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }
            var published = EquityContextRules.ParseDate(Str(item, "publicDate"));
            if (published is null) { skipped++; continue; } // no PIT anchor → never stored
            var available = EquityContextRules.AvailableAfterPublication(published.Value);
            var title = Str(item, "eventTitleVi") ?? Str(item, "eventTitleEn") ?? code;

            if (code is "DIV" or "ISS")
            {
                var exDate = EquityContextRules.ParseDate(Str(item, "exrightDate"));
                if (exDate is null) { skipped++; continue; }
                var type = code == "DIV" ? "cash-dividend" : ClassifyIssuance(title);
                var ratio = Num(item, "exerciseRatio");
                var cash = Num(item, "valuePerShare");
                var ratioText = code == "DIV" && cash is not null
                    ? $"{cash.Value.ToString(CultureInfo.InvariantCulture)} VND/share"
                    : ratio is not null ? $"ratio {ratio.Value.ToString(CultureInfo.InvariantCulture)}" : null;
                var row = new EquityCorporateAction
                {
                    Symbol = symbol,
                    ExDate = exDate.Value,
                    RecordDate = EquityContextRules.ParseDate(Str(item, "recordDate")),
                    AnnouncedDate = published,
                    ActionType = type,
                    RatioText = ratioText is { Length: > 64 } ? ratioText[..64] : ratioText,
                    CashPerShareVnd = code == "DIV" && cash is >= 0 ? cash : null,
                    AdjustmentFactor = null, // never derived; only when a source provides/verifies one
                    SourceUrl = sourceUrl,
                    Provenance = EquityContextRules.ProvenanceVci,
                    VerificationStatus = "unverified",
                    AvailableAtUtc = available,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                };
                actions.TryAdd((row.ExDate, row.ActionType), row);
                continue;
            }

            var eventType = code switch
            {
                "AGME" or "AGMR" or "EGME" => "agm",
                "AIS" or "NLIS" => "additional-listing",
                _ => null,
            };
            if (eventType is null) { skipped++; continue; }
            var eventDate = EquityContextRules.ParseDate(Str(item, "issueDate"))
                ?? EquityContextRules.ParseDate(Str(item, "listingDate"))
                ?? EquityContextRules.ParseDate(Str(item, "displayDate1"))
                ?? published.Value;
            var trimmed = title.Length > 500 ? title[..500] : title;
            events.TryAdd((eventType, eventDate, trimmed), new EquityEvent
            {
                Symbol = symbol,
                EventType = eventType,
                EventDate = eventDate,
                Title = trimmed,
                SourceUrl = sourceUrl,
                Provenance = EquityContextRules.ProvenanceVci,
                AvailableAtUtc = available,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc,
            });
        }
        return new VciParse(actions.Values.ToArray(), events.Values.ToArray(), content.GetArrayLength(), total, skipped);
    }

    /// <summary>VCI "ISS" covers bonus shares, stock dividends, rights and private placements.</summary>
    internal static string ClassifyIssuance(string title)
    {
        var t = title.ToLowerInvariant();
        if (t.Contains("thưởng")) return "bonus-shares";
        if (t.Contains("cổ tức")) return "stock-dividend";
        if (t.Contains("quyền mua") || t.Contains("rights")) return "rights-issue";
        return "other"; // private placement / ESOP / other issuance
    }

    internal sealed record KbsParse(IReadOnlyList<EquityFundamental> Rows, int Skipped, IReadOnlyList<string> Warnings);

    /// <summary>
    /// Parse KBS CSTC: <c>{Head:[{YearPeriod, TermCode, PeriodEnd, ReportDate, DatePubDepartment}], Content:{group:[{ReportNormID, Unit, Value1…}]}}</c>.
    /// Only Value1 ↔ Head[0] (the latest quarter) is taken: the Value_i ↔ Head mapping beyond the latest
    /// period is unverified (Head repeats periods), so older quarters accumulate across syncs instead.
    /// </summary>
    internal static KbsParse ParseKbsFinanceInfo(string symbol, string body, string sourceUrl, DateTime nowUtc)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Head", out var head) || head.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("Content", out var content) || content.ValueKind != JsonValueKind.Object)
            throw new MarketDataPayloadException("KBS", "finance-info payload is not {Head:[...], Content:{...}}");
        var warnings = new List<string>();
        if (head.GetArrayLength() == 0 || head[0].ValueKind != JsonValueKind.Object)
            return new KbsParse([], 0, ["kbs-finance-info: empty Head"]);
        var h = head[0];
        var year = h.TryGetProperty("YearPeriod", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yy) ? yy : (int?)null;
        var term = Str(h, "TermCode");
        if (year is null || term is null || !Regex.IsMatch(term, "^Q[1-4]$"))
            return new KbsParse([], 0, [$"kbs-finance-info: latest period is not quarterly ({term ?? "null"})"]);
        var period = $"{year}{term}";
        var periodEnd = EquityContextRules.QuarterEnd(period);
        var published = EquityContextRules.ParseDate(Str(h, "DatePubDepartment")) ?? EquityContextRules.ParseDate(Str(h, "ReportDate"));
        if (published is null)
            return new KbsParse([], 0, ["kbs-finance-info: no publication date for the latest period; nothing stored"]);
        var available = EquityContextRules.AvailableAfterPublication(published.Value);

        var rows = new Dictionary<string, EquityFundamental>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var group in content.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in group.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("ReportNormID", out var idEl) || idEl.ValueKind != JsonValueKind.Number
                    || !idEl.TryGetInt32(out var id) || !KbsMetrics.TryGetValue(id, out var map))
                    continue;
                var unit = Str(item, "Unit");
                if (!string.Equals(unit, map.KbsUnit, StringComparison.Ordinal))
                {
                    skipped++;
                    warnings.Add($"kbs-finance-info: {map.Metric} unit '{unit}' != expected '{map.KbsUnit}'; skipped");
                    continue;
                }
                var value = Num(item, "Value1");
                rows[map.Metric] = new EquityFundamental
                {
                    Symbol = symbol,
                    Period = period,
                    PeriodEndDate = periodEnd,
                    Metric = map.Metric,
                    Value = value,
                    Unit = map.Unit,
                    UnavailableReason = value is null ? "KBS returned no value for this period" : null,
                    PublishedDate = published,
                    AvailableAtUtc = available,
                    AsOfUtc = nowUtc,
                    Source = EquityContextRules.KbsSource,
                    SourceUrl = sourceUrl,
                    Provenance = EquityContextRules.ProvenanceKbs,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc,
                };
            }
        }
        foreach (var metric in KbsMetrics.Values.Select(m => m.Metric).Where(m => !rows.ContainsKey(m)))
            warnings.Add($"kbs-finance-info: {metric} not present in the payload");
        return new KbsParse(rows.Values.ToArray(), skipped, warnings);
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim() : null;

    private static decimal? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    // --- upserts (shared by provider sync and admin import) ------------------------------

    internal async Task<int> UpsertCorporateActionsAsync(string symbol, IReadOnlyList<EquityCorporateAction> rows,
        bool provider, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var existing = await _db.EquityCorporateActions.Where(x => x.Symbol == symbol).ToListAsync(ct);
        var byKey = existing.ToDictionary(x => (x.ExDate, x.ActionType));
        var changed = 0;
        foreach (var row in rows)
        {
            if (!byKey.TryGetValue((row.ExDate, row.ActionType), out var cur))
            {
                _db.EquityCorporateActions.Add(row);
                byKey[(row.ExDate, row.ActionType)] = row;
                changed++;
                continue;
            }
            if (provider && cur.Provenance == EquityContextRules.ProvenanceAdmin) continue; // admin wins
            if (cur.RecordDate == row.RecordDate && cur.AnnouncedDate == row.AnnouncedDate && cur.RatioText == row.RatioText
                && cur.CashPerShareVnd == row.CashPerShareVnd && cur.AdjustmentFactor == row.AdjustmentFactor
                && cur.SourceUrl == row.SourceUrl && cur.Provenance == row.Provenance
                && cur.AvailableAtUtc == row.AvailableAtUtc
                && (provider || (cur.VerificationStatus == row.VerificationStatus && cur.ShareRatio == row.ShareRatio)))
                continue;
            cur.RecordDate = row.RecordDate;
            cur.AnnouncedDate = row.AnnouncedDate;
            cur.RatioText = row.RatioText;
            cur.CashPerShareVnd = row.CashPerShareVnd;
            cur.AdjustmentFactor = row.AdjustmentFactor;
            cur.SourceUrl = row.SourceUrl;
            cur.Provenance = row.Provenance;
            cur.AvailableAtUtc = row.AvailableAtUtc;
            if (!provider)
            {
                // Admin-curated fields; provider rows never carry them (EQX v2 A3).
                cur.VerificationStatus = row.VerificationStatus;
                cur.ShareRatio = row.ShareRatio;
            }
            cur.UpdatedAtUtc = row.UpdatedAtUtc;
            changed++;
        }
        await _db.SaveChangesAsync(ct);
        return changed;
    }

    internal async Task<int> UpsertEventsAsync(string symbol, IReadOnlyList<EquityEvent> rows, bool provider, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var existing = await _db.EquityEvents.Where(x => x.Symbol == symbol).ToListAsync(ct);
        var byKey = existing.ToDictionary(x => (x.EventType, x.EventDate, x.Title));
        var changed = 0;
        foreach (var row in rows)
        {
            if (!byKey.TryGetValue((row.EventType, row.EventDate, row.Title), out var cur))
            {
                _db.EquityEvents.Add(row);
                byKey[(row.EventType, row.EventDate, row.Title)] = row;
                changed++;
                continue;
            }
            if (provider && cur.Provenance == EquityContextRules.ProvenanceAdmin) continue;
            if (cur.SourceUrl == row.SourceUrl && cur.Provenance == row.Provenance && cur.AvailableAtUtc == row.AvailableAtUtc)
                continue;
            cur.SourceUrl = row.SourceUrl;
            cur.Provenance = row.Provenance;
            cur.AvailableAtUtc = row.AvailableAtUtc;
            cur.UpdatedAtUtc = row.UpdatedAtUtc;
            changed++;
        }
        await _db.SaveChangesAsync(ct);
        return changed;
    }

    internal async Task<int> UpsertFundamentalsAsync(string symbol, IReadOnlyList<EquityFundamental> rows, bool provider, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var existing = await _db.EquityFundamentals.Where(x => x.Symbol == symbol).ToListAsync(ct);
        var byKey = existing.ToDictionary(x => (x.Period, x.Metric, x.Source));
        var changed = 0;
        foreach (var row in rows)
        {
            if (!byKey.TryGetValue((row.Period, row.Metric, row.Source), out var cur))
            {
                _db.EquityFundamentals.Add(row);
                byKey[(row.Period, row.Metric, row.Source)] = row;
                changed++;
                continue;
            }
            if (provider && cur.Provenance == EquityContextRules.ProvenanceAdmin) continue;
            if (cur.Value == row.Value && cur.Unit == row.Unit && cur.UnavailableReason == row.UnavailableReason
                && cur.PublishedDate == row.PublishedDate && cur.AvailableAtUtc == row.AvailableAtUtc
                && cur.SourceUrl == row.SourceUrl && cur.PeriodEndDate == row.PeriodEndDate
                && (provider || (cur.DocumentId == row.DocumentId && cur.SourceLocation == row.SourceLocation)))
                continue; // unchanged: keep the original AsOfUtc (first observation of this value)
            cur.Value = row.Value;
            cur.Unit = row.Unit;
            cur.UnavailableReason = row.UnavailableReason;
            cur.PublishedDate = row.PublishedDate;
            cur.PeriodEndDate = row.PeriodEndDate;
            cur.AvailableAtUtc = row.AvailableAtUtc;
            cur.AsOfUtc = row.AsOfUtc;
            cur.SourceUrl = row.SourceUrl;
            cur.Provenance = row.Provenance;
            if (!provider)
            {
                // Document provenance is admin-curated (EQX v2 C1); provider rows never carry it.
                cur.DocumentId = row.DocumentId;
                cur.SourceLocation = row.SourceLocation;
            }
            cur.UpdatedAtUtc = row.UpdatedAtUtc;
            changed++;
        }
        await _db.SaveChangesAsync(ct);
        return changed;
    }

    // --- admin import (validation per spec §6) --------------------------------------------

    private (DateTime NowUtc, DateOnly Today) Clock()
    {
        var now = _timeProvider.GetUtcNow();
        return (now.UtcDateTime, DateOnly.FromDateTime(VietnamMarketCalendar.ToLocal(now.ToUnixTimeMilliseconds()).DateTime));
    }

    private static DateOnly RequireDate(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw) || !DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d))
            throw new ArgumentException($"{field} must be a yyyy-MM-dd date.");
        return d;
    }

    private static DateOnly? OptionalDate(string? raw, string field) =>
        string.IsNullOrWhiteSpace(raw) ? null : RequireDate(raw, field);

    /// <summary>An explicit availableAtUtc may not be in the future; the derived default (publication + 1 day,
    /// 00:00 VN) may be, which simply keeps the row hidden from point-in-time readers until then.</summary>
    private static DateTime NotFutureUtc(DateTime? value, DateTime fallback, DateTime nowUtc, string field)
    {
        if (!value.HasValue) return fallback;
        var v = value.Value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
            : value.Value.ToUniversalTime();
        if (v > nowUtc) throw new ArgumentException($"{field} cannot be in the future.");
        return v;
    }

    private static void RequireBatch<T>(IReadOnlyList<T>? rows)
    {
        if (rows is null || rows.Count == 0) throw new ArgumentException("body must be a non-empty array.");
        if (rows.Count > MaxImportRows) throw new ArgumentException($"at most {MaxImportRows} rows per request.");
    }

    public async Task<EquityImportReport> ImportCorporateActionsAsync(string symbol, IReadOnlyList<EquityCorporateActionImport>? body, CancellationToken ct)
    {
        var sym = RequireContextEquity(symbol);
        RequireBatch(body);
        var (nowUtc, today) = Clock();
        var rows = new List<EquityCorporateAction>();
        var keys = new HashSet<(DateOnly, string)>();
        for (var i = 0; i < body!.Count; i++)
        {
            var r = body[i] ?? throw new ArgumentException($"[{i}] is null.");
            var p = $"[{i}].";
            var ex = RequireDate(r.ExDate, p + "exDate");
            if (!EquityContextRules.ActionTypes.Contains(r.ActionType ?? ""))
                throw new ArgumentException($"{p}actionType must be one of {string.Join(", ", EquityContextRules.ActionTypes)}.");
            if (!EquityContextRules.IsHttpUrl(r.SourceUrl)) throw new ArgumentException($"{p}sourceUrl must be an http(s) URL.");
            var announced = OptionalDate(r.AnnouncedDate, p + "announcedDate");
            if (announced > today) throw new ArgumentException($"{p}announcedDate cannot be in the future.");
            var verification = string.IsNullOrWhiteSpace(r.VerificationStatus) ? "unverified" : r.VerificationStatus.Trim();
            if (!EquityContextRules.Verification.Contains(verification))
                throw new ArgumentException($"{p}verificationStatus must be unverified|verified|rejected.");
            if (r.CashPerShareVnd is < 0) throw new ArgumentException($"{p}cashPerShareVnd cannot be negative.");
            if (r.AdjustmentFactor is <= 0) throw new ArgumentException($"{p}adjustmentFactor must be > 0 when present.");
            if (r.ShareRatio is <= 0) throw new ArgumentException($"{p}shareRatio must be > 0 when present.");
            // ShareRatio = EXTRA shares received per 1 old share (100:15 ⇒ 0.15; split 1:2 ⇒ 1.0). Above 10
            // (≥ 11× shares) is implausible for a HOSE action and is far more likely a convention mistake.
            if (r.ShareRatio is > EquityContextRules.MaxShareRatio)
                throw new ArgumentException(
                    $"{p}shareRatio {r.ShareRatio} is implausible (> {EquityContextRules.MaxShareRatio}): it is EXTRA shares per 1 old share (100:15 => 0.15; split 1:2 => 1.0).");
            if (r.ShareRatio is not null && r.ActionType == "cash-dividend")
                throw new ArgumentException($"{p}shareRatio is not allowed for a cash-dividend (no share change).");
            if (r.RatioText is { Length: > 64 }) throw new ArgumentException($"{p}ratioText is at most 64 characters.");
            if (!keys.Add((ex, r.ActionType!))) throw new ArgumentException($"{p}duplicate (exDate, actionType) in the batch.");
            var available = NotFutureUtc(r.AvailableAtUtc,
                announced is null ? nowUtc : EquityContextRules.AvailableAfterPublication(announced.Value),
                nowUtc, p + "availableAtUtc");
            rows.Add(new EquityCorporateAction
            {
                Symbol = sym, ExDate = ex, ActionType = r.ActionType!, RatioText = r.RatioText,
                CashPerShareVnd = r.CashPerShareVnd, AdjustmentFactor = r.AdjustmentFactor, ShareRatio = r.ShareRatio,
                RecordDate = OptionalDate(r.RecordDate, p + "recordDate"), AnnouncedDate = announced,
                SourceUrl = r.SourceUrl, Provenance = EquityContextRules.ProvenanceAdmin, VerificationStatus = verification,
                AvailableAtUtc = available, CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc,
            });
        }
        var before = await _db.EquityCorporateActions.CountAsync(x => x.Symbol == sym, ct);
        var changed = await UpsertCorporateActionsAsync(sym, rows, provider: false, ct);
        var inserted = await _db.EquityCorporateActions.CountAsync(x => x.Symbol == sym, ct) - before;
        return new EquityImportReport(sym, "corporate-actions", inserted, changed - inserted);
    }

    public async Task<EquityImportReport> ImportEventsAsync(string symbol, IReadOnlyList<EquityEventImport>? body, CancellationToken ct)
    {
        var sym = RequireContextEquity(symbol);
        RequireBatch(body);
        var (nowUtc, _) = Clock();
        var rows = new List<EquityEvent>();
        var keys = new HashSet<(string, DateOnly, string)>();
        for (var i = 0; i < body!.Count; i++)
        {
            var r = body[i] ?? throw new ArgumentException($"[{i}] is null.");
            var p = $"[{i}].";
            if (!EquityContextRules.EventTypes.Contains(r.EventType ?? ""))
                throw new ArgumentException($"{p}eventType must be one of {string.Join(", ", EquityContextRules.EventTypes)}.");
            var date = RequireDate(r.EventDate, p + "eventDate");
            if (string.IsNullOrWhiteSpace(r.Title) || r.Title.Length > 500)
                throw new ArgumentException($"{p}title is required (≤ 500 characters).");
            if (!EquityContextRules.IsHttpUrl(r.SourceUrl)) throw new ArgumentException($"{p}sourceUrl must be an http(s) URL.");
            var title = r.Title.Trim();
            if (!keys.Add((r.EventType!, date, title))) throw new ArgumentException($"{p}duplicate event in the batch.");
            rows.Add(new EquityEvent
            {
                Symbol = sym, EventType = r.EventType!, EventDate = date, Title = title, SourceUrl = r.SourceUrl,
                Provenance = EquityContextRules.ProvenanceAdmin,
                AvailableAtUtc = NotFutureUtc(r.AvailableAtUtc, nowUtc, nowUtc, p + "availableAtUtc"),
                CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc,
            });
        }
        var before = await _db.EquityEvents.CountAsync(x => x.Symbol == sym, ct);
        var changed = await UpsertEventsAsync(sym, rows, provider: false, ct);
        var inserted = await _db.EquityEvents.CountAsync(x => x.Symbol == sym, ct) - before;
        return new EquityImportReport(sym, "events", inserted, changed - inserted);
    }

    public async Task<EquityImportReport> ImportFundamentalsAsync(string symbol, IReadOnlyList<EquityFundamentalImport>? body, CancellationToken ct)
    {
        var sym = RequireContextEquity(symbol);
        RequireBatch(body);
        var (nowUtc, today) = Clock();
        var rows = new List<EquityFundamental>();
        var keys = new HashSet<(string, string, string)>();
        for (var i = 0; i < body!.Count; i++)
        {
            var r = body[i] ?? throw new ArgumentException($"[{i}] is null.");
            var p = $"[{i}].";
            if (!EquityContextRules.IsPeriod(r.Period)) throw new ArgumentException($"{p}period must be YYYYQn.");
            if (!EquityContextRules.Metrics.Contains(r.Metric ?? ""))
                throw new ArgumentException($"{p}metric must be one of {string.Join(", ", EquityContextRules.Metrics)}.");
            if (!EquityContextRules.Units.Contains(r.Unit ?? "")) throw new ArgumentException($"{p}unit must be VND|ratio|percent.");
            if (EquityContextRules.AmountMetrics.Contains(r.Metric!) && r.Unit != "VND")
                throw new ArgumentException($"{p}unit must be VND for {r.Metric}.");
            if (r.SourceLocation is { Length: > EquityContextRules.MaxSourceLocationLength })
                throw new ArgumentException($"{p}sourceLocation is at most {EquityContextRules.MaxSourceLocationLength} characters.");
            var issuerReport = string.Equals(r.Source?.Trim(), EquityContextRules.IssuerReportSource, StringComparison.Ordinal);
            if (issuerReport && (r.DocumentId is null || string.IsNullOrWhiteSpace(r.SourceLocation)))
                throw new ArgumentException(
                    $"{p}source '{EquityContextRules.IssuerReportSource}' requires documentId (a registered source document of {sym}) and sourceLocation (page/table/line).");
            if (r.DocumentId is { } docId)
            {
                if (docId == Guid.Empty) throw new ArgumentException($"{p}documentId must not be empty.");
                var docExists = await _db.EquitySourceDocuments.AsNoTracking()
                    .AnyAsync(d => d.Id == docId && d.Symbol == sym, ct);
                if (!docExists)
                    throw new ArgumentException($"{p}documentId {docId} is not a registered source document of {sym}.");
            }
            var hasReason = !string.IsNullOrWhiteSpace(r.UnavailableReason);
            if (r.Value is null != hasReason)
                throw new ArgumentException($"{p}value must be null exactly when unavailableReason is given.");
            if (string.IsNullOrWhiteSpace(r.Source) || r.Source.Length > 64) throw new ArgumentException($"{p}source is required (≤ 64).");
            if (!EquityContextRules.IsHttpUrl(r.SourceUrl)) throw new ArgumentException($"{p}sourceUrl must be an http(s) URL.");
            var published = OptionalDate(r.PublishedDate, p + "publishedDate");
            if (published > today) throw new ArgumentException($"{p}publishedDate cannot be in the future.");
            var periodEnd = OptionalDate(r.PeriodEndDate, p + "periodEndDate") ?? EquityContextRules.QuarterEnd(r.Period);
            var source = r.Source.Trim();
            if (!keys.Add((r.Period, r.Metric!, source))) throw new ArgumentException($"{p}duplicate (period, metric, source) in the batch.");
            rows.Add(new EquityFundamental
            {
                Symbol = sym, Period = r.Period, PeriodEndDate = periodEnd, Metric = r.Metric!, Value = r.Value, Unit = r.Unit!,
                UnavailableReason = hasReason ? r.UnavailableReason!.Trim() : null, PublishedDate = published,
                AvailableAtUtc = NotFutureUtc(r.AvailableAtUtc,
                    published is null ? nowUtc : EquityContextRules.AvailableAfterPublication(published.Value),
                    nowUtc, p + "availableAtUtc"),
                AsOfUtc = nowUtc, Source = source, SourceUrl = r.SourceUrl, Provenance = EquityContextRules.ProvenanceAdmin,
                DocumentId = r.DocumentId,
                SourceLocation = string.IsNullOrWhiteSpace(r.SourceLocation) ? null : r.SourceLocation.Trim(),
                CreatedAtUtc = nowUtc, UpdatedAtUtc = nowUtc,
            });
        }
        var before = await _db.EquityFundamentals.CountAsync(x => x.Symbol == sym, ct);
        var changed = await UpsertFundamentalsAsync(sym, rows, provider: false, ct);
        var inserted = await _db.EquityFundamentals.CountAsync(x => x.Symbol == sym, ct) - before;
        return new EquityImportReport(sym, "fundamentals", inserted, changed - inserted);
    }
}
