using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Data;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Services;

/// <summary>
/// Session research journal (EQX v2 §B). Notes are immutable after insert (DB trigger).
/// <list type="bullet">
/// <item><c>forward</c>: only for the latest expected finalized 1d bar, stored, and before the next
///   session's 09:00 open (409 SIGNAL_BAR_NOT_LATEST / FORECAST_WINDOW_CLOSED) — same rule as the
///   retired LLM runs;</item>
/// <item><c>retrospective</c>: any past as-of; stored, but never produces a forecast record;</item>
/// <item>snapshot binding: the backend rebuilds the snapshot for (symbol, requestedAsOf) and the note
///   is stored only if snapshotId + snapshotSha256 match it (else 409 SNAPSHOT_MISMATCH). There is
///   no "unverified" storage path, so every stored note has snapshotVerified = true by construction;</item>
/// <item>probability is accepted only with a non-empty method and methodRef; a forward note with a
///   probability adds one <c>session-research</c> EquityForecastRecords row (Validated = false) that
///   the outcome resolver labels like quant/LLM rows.</item>
/// </list>
/// </summary>
public sealed class EquityResearchNoteService
{
    public const string SourceSessionResearch = "session-research";
    public const int MaxBodyBytes = 64 * 1024;

    public enum Outcome { Created, Invalid, ProbabilityMethodRequired, SignalBarNotLatest, ForecastWindowClosed, SnapshotMismatch, CalendarCoverageExpired }

    public sealed record WriteResult(Outcome Outcome, string? Code, string? Message, EquityResearchNoteDto? Item);

    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "forward", "retrospective" };
    private static readonly HashSet<string> Directions = new(StringComparer.Ordinal) { "up", "down", "sideways", "no-view" };
    private static readonly HashSet<string> ActionViews = new(StringComparer.Ordinal) { "buy-more", "hold", "reduce", "sell", "wait", "none" };
    private static readonly HashSet<string> ActionBases = new(StringComparer.Ordinal) { "position-aware", "market-only", "insufficient-input" };
    private static readonly HashSet<string> BodyKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "thesis", "evidence", "counterEvidence", "scenarios", "limitations", "missingData",
        "outcomeCheck", "probability",
    };
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private readonly AppDbContext _db;
    private readonly EquityResearchSnapshotService _snapshots;
    private readonly VietnamMarketCalendar _calendar;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;

    public EquityResearchNoteService(
        AppDbContext db,
        EquityResearchSnapshotService snapshots,
        VietnamMarketCalendar calendar,
        TimeProvider? timeProvider = null,
        InstrumentCatalog? catalog = null)
    {
        _db = db;
        _snapshots = snapshots;
        _calendar = calendar;
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

    public async Task<EquityResearchNoteListResponse> ListAsync(string symbol, int take, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var rows = await _db.EquityResearchNotes.AsNoTracking().Where(n => n.Symbol == sym)
            .OrderByDescending(n => n.SignalBarOpenTimeMs).ThenByDescending(n => n.AvailableTimeMs).ThenBy(n => n.NoteId)
            .Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        var ids = rows.Select(r => (Guid?)r.Id).ToArray();
        var forecasts = await _db.EquityForecastRecords.AsNoTracking()
            .Where(f => f.ResearchNoteId != null && ids.Contains(f.ResearchNoteId))
            .Select(f => new { f.ResearchNoteId, f.ForecastId }).ToListAsync(ct);
        return new EquityResearchNoteListResponse(EquityResearchContracts.NotesVersion, sym,
            rows.Select(r => ToDto(r, forecasts.Where(f => f.ResearchNoteId == r.Id).Select(f => f.ForecastId)
                .OrderBy(x => x, StringComparer.Ordinal).ToArray())).ToArray());
    }

    internal static EquityResearchNoteDto ToDto(EquityResearchNote n, IReadOnlyList<string> forecastIds)
    {
        JsonElement? body = null;
        try
        {
            using var doc = JsonDocument.Parse(n.BodyJson);
            body = doc.RootElement.Clone();
        }
        catch (JsonException) { }
        return new EquityResearchNoteDto(n.NoteId, n.Symbol, n.Mode, n.RequestedAsOfUtc, n.SignalBarOpenTimeMs,
            n.SignalBarCloseTimeMs, n.HorizonSessions, n.SnapshotId, n.SnapshotSha256, true, n.SkillVersion,
            n.MethodVersion, n.Direction, n.ActionView, n.ActionBasis, body, n.AvailableTimeMs, n.CreatedAtUtc, forecastIds);
    }

    private sealed record Probability(string ConfigId, double ProbTargetFirst, double ProbStopFirst, string Method, string MethodRef);

    private static WriteResult Invalid(string message) => new(Outcome.Invalid, "INVALID_NOTE", message, null);

    public async Task<WriteResult> RecordAsync(string symbol, EquityResearchNoteRequest? request, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        if (request is null) return Invalid("Request body is required.");
        var now = _timeProvider.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();

        if (!Modes.Contains(request.Mode)) return Invalid("mode must be forward or retrospective.");
        if (!Directions.Contains(request.Direction)) return Invalid("direction must be up, down, sideways or no-view.");
        if (!ActionViews.Contains(request.ActionView)) return Invalid("actionView must be buy-more, hold, reduce, sell, wait or none.");
        if (!ActionBases.Contains(request.ActionBasis)) return Invalid("actionBasis must be position-aware, market-only or insufficient-input.");
        if (request.ActionBasis == "insufficient-input" && request.ActionView != "none")
            return Invalid("actionBasis insufficient-input requires actionView none.");
        if (!Bounded(request.SkillVersion, 80) || !Bounded(request.MethodVersion, 80))
            return Invalid("skillVersion and methodVersion are required (<= 80 chars).");
        if (!Bounded(request.SnapshotId, 200)) return Invalid("snapshotId is required (<= 200 chars).");
        if (request.SnapshotSha256 is null || !Sha256Pattern.IsMatch(request.SnapshotSha256))
            return Invalid("snapshotSha256 must be 64 lowercase hex characters.");
        if (request.HorizonSessions is not (>= 1 and <= 250)) return Invalid("horizonSessions must be within 1..250.");
        if (request.SignalBarOpenTimeMs is not { } signal) return Invalid("signalBarOpenTimeMs is required.");
        if (!EquityResearchSnapshotService.TryParseAsOf(request.RequestedAsOf, now, out var asOf, out var asOfError)
            || string.IsNullOrWhiteSpace(request.RequestedAsOf))
            return Invalid(asOfError ?? "requestedAsOf is required (the asOf the snapshot was exported for).");

        var (bodyError, probabilityError, bodyJson, probability) = ValidateBody(request.Body);
        if (probabilityError is not null)
            return new WriteResult(Outcome.ProbabilityMethodRequired, "PROBABILITY_METHOD_REQUIRED", probabilityError, null);
        if (bodyError is not null) return Invalid(bodyError);
        EquityBarrierConfigs.Config? config = null;
        if (probability is not null)
        {
            config = EquityBarrierConfigs.All.FirstOrDefault(c => c.ConfigId == probability.ConfigId);
            if (config is null)
                return Invalid($"probability.configId must be one of {string.Join(", ", EquityBarrierConfigs.All.Select(c => c.ConfigId))}.");
        }

        var forward = request.Mode == "forward";
        if (forward)
        {
            var expected = _calendar.LastExpectedFinalizedOpenUtcMs(EquityCandleTimeframes.Day, nowMs);
            var stored = expected is not null && await _db.InstrumentCandles.AsNoTracking()
                .AnyAsync(c => c.Symbol == sym && c.Timeframe == EquityCandleTimeframes.Day && c.OpenTimeMs == expected.Value, ct);
            if (!stored || signal != expected)
                return new WriteResult(Outcome.SignalBarNotLatest, "SIGNAL_BAR_NOT_LATEST",
                    $"Forward notes must use the latest expected finalized 1d bar ({expected?.ToString(CultureInfo.InvariantCulture) ?? "none"}" +
                    $"{(stored ? "" : ", not stored yet")}); got {signal}. Record it as retrospective instead.", null);
            var maybeClose = EquityForecastJournalService.TryNextSessionOpenUtcMs(_calendar, signal);
            if (maybeClose is null)
                return new WriteResult(Outcome.ForecastWindowClosed, "CALENDAR_COVERAGE_EXPIRED",
                    $"The session after signal bar {signal} is beyond the HOSE calendar coverage ({_calendar.CoverageTo:yyyy-MM-dd}); a forward note's window cannot be determined.", null);
            var windowClose = maybeClose.Value;
            if (nowMs >= windowClose)
                return new WriteResult(Outcome.ForecastWindowClosed, "FORECAST_WINDOW_CLOSED",
                    $"The session after signal bar {signal} opened at {windowClose}; a forward note now could see it (look-ahead).", null);
        }

        var snapshot = await _snapshots.BuildAsync(sym, asOf, ct);
        if (snapshot.SignalBar?.OpenTimeMs != signal || snapshot.SnapshotId != request.SnapshotId
            || snapshot.SnapshotSha256 != request.SnapshotSha256)
            return new WriteResult(Outcome.SnapshotMismatch, "SNAPSHOT_MISMATCH",
                $"The snapshot reproduced for {sym} at {asOf.UtcDateTime:O} is {snapshot.SnapshotId} (signal bar " +
                $"{snapshot.SignalBar?.OpenTimeMs.ToString(CultureInfo.InvariantCulture) ?? "none"}); the note cites {request.SnapshotId} " +
                $"(signal bar {signal}). Snapshot content changed since export (e.g. artifact rebuilt, data revised, document " +
                "registered) or the note does not match its snapshot — re-export and re-record.", null);

        if (forward && snapshot.Calendar.Status == "coverage-expired")
            return new WriteResult(Outcome.CalendarCoverageExpired, "CALENDAR_COVERAGE_EXPIRED",
                $"The HOSE calendar ({snapshot.Calendar.Status}: coverage {snapshot.Calendar.CoverageFrom ?? "?"}.." +
                $"{snapshot.Calendar.CoverageTo ?? "?"}) does not cover the session after signal bar {signal} " +
                $"({snapshot.Calendar.NextSessionCheckDate}); the forward window cannot be bounded.", null);
        var signalClose = snapshot.SignalBar!.CloseTimeMs;
        var note = new EquityResearchNote
        {
            Id = Guid.NewGuid(),
            Symbol = sym,
            Mode = request.Mode,
            RequestedAsOfUtc = asOf.UtcDateTime,
            SignalBarOpenTimeMs = signal,
            SignalBarCloseTimeMs = signalClose,
            HorizonSessions = request.HorizonSessions.Value,
            SnapshotId = request.SnapshotId,
            SnapshotSha256 = request.SnapshotSha256,
            SkillVersion = request.SkillVersion.Trim(),
            MethodVersion = request.MethodVersion.Trim(),
            Direction = request.Direction,
            ActionView = request.ActionView,
            ActionBasis = request.ActionBasis,
            BodyJson = bodyJson!,
            AvailableTimeMs = nowMs,
            CreatedAtUtc = now.UtcDateTime,
        };
        note.NoteId = $"session/{sym}/{signal}/{note.Id.ToString("N")[..8]}";
        _db.EquityResearchNotes.Add(note);

        var forecastIds = new List<string>();
        if (forward && probability is not null && config is not null)
        {
            var forecastId = EquityForecastJournalService.ForecastIdFor(SourceSessionResearch, sym, config.ConfigId, signal, note.Id);
            _db.EquityForecastRecords.Add(new EquityForecastRecord
            {
                Id = Guid.NewGuid(),
                ForecastId = forecastId,
                Source = SourceSessionResearch,
                Symbol = sym,
                Timeframe = EquityCandleTimeframes.Day,
                ConfigId = config.ConfigId,
                TargetPct = config.TargetPct,
                StopPct = config.StopPct,
                HorizonSessions = config.HorizonSessions,
                SignalBarOpenTimeMs = signal,
                SignalBarCloseTimeMs = signalClose,
                AvailableTimeMs = Math.Max(nowMs, signalClose),
                CreatedAtUtc = now.UtcDateTime,
                ProbTargetFirst = probability.ProbTargetFirst,
                ProbStopFirst = probability.ProbStopFirst,
                Abstained = false,
                Validated = false, // CK_EquityForecastRecords_SessionUnvalidated
                MethodVersion = Truncate(probability.Method, 80),
                ModelId = null,
                PromptVersion = Truncate(note.SkillVersion, 80),
                SchemaVersion = EquityResearchContracts.NoteBodyVersion,
                InputSnapshotSha256 = note.SnapshotSha256,
                ResearchNoteId = note.Id,
            });
            forecastIds.Add(forecastId);
        }
        await _db.SaveChangesAsync(ct);
        return new WriteResult(Outcome.Created, null, null, ToDto(note, forecastIds));
    }

    private static bool Bounded(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Strict equity-research-note-v1 body check (ValueKind-guarded, AGENTS pitfall #17).</summary>
    private static (string? BodyError, string? ProbabilityError, string? Json, Probability? Probability) ValidateBody(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b) return ("body must be an equity-research-note-v1 object.", null, null, null);
        var raw = b.GetRawText();
        if (System.Text.Encoding.UTF8.GetByteCount(raw) > MaxBodyBytes) return ($"body exceeds {MaxBodyBytes} bytes.", null, null, null);
        foreach (var p in b.EnumerateObject())
            if (!BodyKeys.Contains(p.Name)) return ($"body.{p.Name} is not part of {EquityResearchContracts.NoteBodyVersion}.", null, null, null);
        if (Str(b, "schemaVersion") != EquityResearchContracts.NoteBodyVersion)
            return ($"body.schemaVersion must be {EquityResearchContracts.NoteBodyVersion}.", null, null, null);
        if (Str(b, "thesis") is not { Length: <= 4000 }) return ("body.thesis must be a non-empty string (<= 4000 chars).", null, null, null);

        if (!Array(b, "evidence", 100, out var evidence)) return ("body.evidence must be an array.", null, null, null);
        foreach (var e in evidence)
            if (!IsClaim(e)) return ("body.evidence[] items must be {claim, evidenceIds[]}.", null, null, null);
        if (!Array(b, "counterEvidence", 100, out var counter)) return ("body.counterEvidence must be an array.", null, null, null);
        foreach (var e in counter)
            if (!(e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString())) && !IsClaim(e))
                return ("body.counterEvidence[] items must be strings or {claim, evidenceIds[]}.", null, null, null);
        if (!Array(b, "scenarios", 20, out var scenarios)) return ("body.scenarios must be an array.", null, null, null);
        foreach (var s in scenarios)
        {
            if (s.ValueKind != JsonValueKind.Object || Str(s, "name") is null || Str(s, "description", allowEmpty: true) is null
                || !PositiveInt(s, "horizonSessions") || !StringArray(s, "invalidation"))
                return ("body.scenarios[] items must be {name, horizonSessions>0, description, invalidation[]}.", null, null, null);
        }
        if (!StringArray(b, "limitations")) return ("body.limitations must be an array of strings.", null, null, null);
        if (!StringArray(b, "missingData")) return ("body.missingData must be an array of strings.", null, null, null);
        if (!b.TryGetProperty("outcomeCheck", out var oc) || oc.ValueKind != JsonValueKind.Object
            || Str(oc, "how") is null || !PositiveInt(oc, "dueAfterSessions"))
            return ("body.outcomeCheck must be {how, dueAfterSessions>0}.", null, null, null);

        Probability? probability = null;
        if (b.TryGetProperty("probability", out var pr) && pr.ValueKind != JsonValueKind.Null)
        {
            if (pr.ValueKind != JsonValueKind.Object) return ("body.probability must be an object or null.", null, null, null);
            var method = Str(pr, "method");
            var methodRef = Str(pr, "methodRef");
            if (method is null || methodRef is null)
                return (null, "body.probability requires a non-empty method and methodRef (document/script that reproduces it).", null, null);
            if (Str(pr, "configId") is not { } configId) return ("body.probability.configId is required.", null, null, null);
            if (!Prob(pr, "probTargetFirst", out var pt) || !Prob(pr, "probStopFirst", out var ps))
                return ("body.probability.probTargetFirst/probStopFirst must be numbers within [0, 1].", null, null, null);
            if (pt + ps > 1.0 + 1e-9) return ("body.probability: probTargetFirst + probStopFirst must be <= 1.", null, null, null);
            probability = new Probability(configId, pt, ps, method.Trim(), methodRef.Trim());
        }
        return (null, null, raw, probability);
    }

    private static bool IsClaim(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && Str(e, "claim") is not null && StringArray(e, "evidenceIds");

    private static bool Array(JsonElement obj, string name, int max, out JsonElement[] items)
    {
        items = [];
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return false;
        items = v.EnumerateArray().ToArray();
        return items.Length <= max;
    }

    private static bool StringArray(JsonElement obj, string name) =>
        Array(obj, name, 200, out var items) && items.All(i => i.ValueKind == JsonValueKind.String);

    private static bool PositiveInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n > 0;

    private static bool Prob(JsonElement obj, string name, out double value)
    {
        value = double.NaN;
        return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && double.IsFinite(value = v.GetDouble()) && value is >= 0 and <= 1;
    }

    private static string? Str(JsonElement obj, string name, bool allowEmpty = false) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && (allowEmpty || !string.IsNullOrWhiteSpace(v.GetString())) ? v.GetString() : null;
}

/// <summary>
/// Issuer source documents (EQX v2 §C1): public metadata list + admin registration of a file that
/// was already downloaded under <c>EquityDocuments:RootPath</c>. Registration re-hashes the file on
/// disk; the path must stay inside the root (no rooted paths, no "..").
/// </summary>
public sealed class EquitySourceDocumentService
{
    private static readonly HashSet<string> DocTypes = new(StringComparer.Ordinal)
        { "financial-statement", "investor-presentation", "disclosure", "annual-report", "other" };
    private static readonly HashSet<string> Consolidations = new(StringComparer.Ordinal) { "consolidated", "separate", "unknown" };
    private static readonly HashSet<string> AuditStatuses = new(StringComparer.Ordinal) { "audited", "reviewed", "unaudited", "unknown" };
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex PeriodPattern = new("^[0-9]{4}(Q[1-4])?$", RegexOptions.CultureInvariant);
    public const long MaxFileBytes = 512L * 1024 * 1024;

    public enum Outcome { Ok, Invalid, FileNotFound, HashMismatch, RootUnavailable, PublishedAtImmutable }

    public sealed record RegisterResult(Outcome Outcome, string? Code, string? Message, EquitySourceDocumentRegistrationResult? Result);

    private readonly AppDbContext _db;
    private readonly string? _rootPath;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;

    public EquitySourceDocumentService(
        AppDbContext db, IOptions<EquityDocumentsOptions> options, IHostEnvironment environment, TimeProvider timeProvider)
        : this(db, ResolveRoot(options.Value.RootPath, environment.ContentRootPath), timeProvider)
    {
    }

    internal EquitySourceDocumentService(AppDbContext db, string? rootPath, TimeProvider? timeProvider = null, InstrumentCatalog? catalog = null)
    {
        _db = db;
        _rootPath = string.IsNullOrWhiteSpace(rootPath) ? null : Path.GetFullPath(rootPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    private static string? ResolveRoot(string? configured, string contentRoot) =>
        string.IsNullOrWhiteSpace(configured) ? null
        : Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);

    private string RequireDecision(string symbol)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        return instrument.Symbol;
    }

    public async Task<EquitySourceDocumentListResponse> ListAsync(string symbol, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var counts = await ExtractedCountsAsync(sym, ct);
        var rows = (await _db.EquitySourceDocuments.AsNoTracking().Where(d => d.Symbol == sym).ToListAsync(ct))
            .OrderByDescending(d => d.ReportingPeriod ?? "", StringComparer.Ordinal)
            .ThenBy(d => d.DocType, StringComparer.Ordinal).ThenBy(d => d.FirstSeenAtUtc).ThenBy(d => d.Id)
            .Select(d => ToDto(d, counts.GetValueOrDefault(d.Id))).ToArray();
        return new EquitySourceDocumentListResponse(EquityResearchContracts.SourceDocumentsVersion, sym, rows);
    }

    /// <summary>Metric values actually stored per document (EquityFundamentals.DocumentId, Value not null).</summary>
    private async Task<Dictionary<Guid, int>> ExtractedCountsAsync(string sym, CancellationToken ct) =>
        (await _db.EquityFundamentals.AsNoTracking()
            .Where(f => f.Symbol == sym && f.DocumentId != null && f.Value != null)
            .Select(f => f.DocumentId!.Value).ToListAsync(ct))
        .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

    internal static EquitySourceDocumentDto ToDto(EquitySourceDocument d, int extractedMetricCount = 0)
    {
        JsonElement? extraction = null;
        if (d.ExtractionJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(d.ExtractionJson);
                extraction = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return new EquitySourceDocumentDto(d.Id, d.Symbol, d.Entity, d.DocType, d.Title, d.SourceUrl, d.Provider,
            d.ContentSha256, d.StoragePath, d.ByteSize, d.MimeType, d.ReportingPeriod, d.Consolidation, d.AuditStatus,
            d.PublishedAtUtc, d.FirstSeenAtUtc, d.FetchedAtUtc, d.Revision, d.SupersedesDocumentId, extraction, d.CreatedAtUtc,
            AvailableAt(d), AvailabilityBasis(d), extractedMetricCount, extractedMetricCount > 0);
    }

    private static RegisterResult Invalid(string message) => new(Outcome.Invalid, "INVALID_SOURCE_DOCUMENT", message, null);

    public async Task<RegisterResult> RegisterAsync(string symbol, EquitySourceDocumentRegistration? body, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        if (body is null) return Invalid("Request body is required.");
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var consolidation = string.IsNullOrWhiteSpace(body.Consolidation) ? "unknown" : body.Consolidation;
        var audit = string.IsNullOrWhiteSpace(body.AuditStatus) ? "unknown" : body.AuditStatus;

        if (!Bounded(body.Entity, 200)) return Invalid("entity is required (<= 200 chars).");
        if (!DocTypes.Contains(body.DocType)) return Invalid($"docType must be one of {string.Join(", ", DocTypes)}.");
        if (!Bounded(body.Title, 500)) return Invalid("title is required (<= 500 chars).");
        if (!Uri.TryCreate(body.SourceUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") || body.SourceUrl.Length > 1000)
            return Invalid("sourceUrl must be an absolute http(s) URL (<= 1000 chars).");
        if (!Bounded(body.Provider, 64)) return Invalid("provider is required (<= 64 chars), e.g. issuer:BIDV.");
        if (body.ContentSha256 is null || !Sha256Pattern.IsMatch(body.ContentSha256))
            return Invalid("contentSha256 must be 64 lowercase hex characters.");
        if (!Bounded(body.MimeType, 100)) return Invalid("mimeType is required (<= 100 chars).");
        if (body.ReportingPeriod is not null && !PeriodPattern.IsMatch(body.ReportingPeriod))
            return Invalid("reportingPeriod must be YYYY or YYYYQn.");
        if (!Consolidations.Contains(consolidation)) return Invalid("consolidation must be consolidated, separate or unknown.");
        if (!AuditStatuses.Contains(audit)) return Invalid("auditStatus must be audited, reviewed, unaudited or unknown.");
        // Availability is server-stamped (review-v2 #5): a caller-supplied firstSeen/fetched value is
        // honoured only within [now - skew, now]; anything older is ignored, so a registration can
        // never backdate when the system first knew the document.
        var firstSeen = WithinSkew(ToUtc(body.FirstSeenAtUtc), now) ?? now;
        var fetched = WithinSkew(ToUtc(body.FetchedAtUtc), now) ?? now;
        if (fetched < firstSeen) fetched = firstSeen;
        DateTime? publishedAt = ToUtc(body.PublishedAtUtc);
        if (!string.IsNullOrWhiteSpace(body.PublishedDate))
        {
            if (publishedAt is not null) return Invalid("send publishedDate (date-only) or publishedAtUtc, not both.");
            if (!DateOnly.TryParseExact(body.PublishedDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return Invalid("publishedDate must be yyyy-MM-dd.");
            publishedAt = DateOnlyAvailability(day);
        }
        if (publishedAt is { } published && published > now)
            return Invalid("publishedAtUtc/publishedDate cannot be in the future (a date-only value becomes available at 00:00 VN of the next day).");
        if (body.Extraction is { } ex && ex.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            return Invalid("extraction must be an object or null.");
        if (!Bounded(body.StoragePath, 500) || Path.IsPathRooted(body.StoragePath)
            || body.StoragePath.Replace('\\', '/').Split('/').Any(seg => seg is ".." or "."))
            return Invalid("storagePath must be a relative path inside EquityDocuments:RootPath (no rooted paths, no '.'/'..').");

        if (_rootPath is null)
            return new RegisterResult(Outcome.RootUnavailable, "DOCUMENT_ROOT_UNCONFIGURED", "EquityDocuments:RootPath is not configured.", null);
        var rootWithSep = _rootPath.EndsWith(Path.DirectorySeparatorChar) ? _rootPath : _rootPath + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(_rootPath, body.StoragePath));
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return Invalid("storagePath escapes EquityDocuments:RootPath.");
        if (!File.Exists(full))
            return new RegisterResult(Outcome.FileNotFound, "DOCUMENT_FILE_NOT_FOUND",
                $"No file at '{body.StoragePath}' under EquityDocuments:RootPath.", null);
        var info = new FileInfo(full);
        if (info.Length <= 0 || info.Length > MaxFileBytes)
            return Invalid($"file size {info.Length} is outside 1..{MaxFileBytes} bytes.");
        string actual;
        await using (var stream = File.OpenRead(full))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (actual != body.ContentSha256)
            return new RegisterResult(Outcome.HashMismatch, "DOCUMENT_HASH_MISMATCH",
                $"contentSha256 does not match the file on disk (actual {actual}).", null);

        var existing = await _db.EquitySourceDocuments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Symbol == sym && d.ContentSha256 == actual, ct);
        if (existing is not null)
        {
            var filled = false;
            if (publishedAt is { } pub)
            {
                if (existing.PublishedAtUtc is null)
                {
                    // Write-once fill of the issuer-stated publication time; never changes a set value.
                    var tracked = await _db.EquitySourceDocuments.FirstAsync(d => d.Id == existing.Id, ct);
                    if (tracked.PublishedAtUtc is null)
                    {
                        tracked.PublishedAtUtc = pub;
                        await _db.SaveChangesAsync(ct);
                        filled = true;
                    }
                    _db.Entry(tracked).State = EntityState.Detached;
                    existing = tracked;
                }
                if (ToUtc(existing.PublishedAtUtc) != pub)
                    return new RegisterResult(Outcome.PublishedAtImmutable, "DOCUMENT_PUBLISHED_AT_IMMUTABLE",
                        $"publishedAtUtc is already {existing.PublishedAtUtc:O}; a set publication time is never changed.", null);
            }
            var counts = await ExtractedCountsAsync(sym, ct);
            return new RegisterResult(Outcome.Ok, null, null,
                new EquitySourceDocumentRegistrationResult(false, ToDto(existing, counts.GetValueOrDefault(existing.Id)), filled));
        }

        var revision = 1;
        if (body.SupersedesDocumentId is { } sup)
        {
            var prior = await _db.EquitySourceDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == sup && d.Symbol == sym, ct);
            if (prior is null) return Invalid("supersedesDocumentId does not reference a document of this symbol.");
            revision = prior.Revision + 1;
        }
        var row = new EquitySourceDocument
        {
            Id = Guid.NewGuid(),
            Symbol = sym,
            Entity = body.Entity.Trim(),
            DocType = body.DocType,
            Title = body.Title.Trim(),
            SourceUrl = body.SourceUrl,
            Provider = body.Provider.Trim(),
            ContentSha256 = actual,
            StoragePath = body.StoragePath.Replace('\\', '/'),
            ByteSize = info.Length,
            MimeType = body.MimeType.Trim(),
            ReportingPeriod = body.ReportingPeriod,
            Consolidation = consolidation,
            AuditStatus = audit,
            PublishedAtUtc = publishedAt,
            FirstSeenAtUtc = firstSeen,
            FetchedAtUtc = fetched,
            Revision = revision,
            SupersedesDocumentId = body.SupersedesDocumentId,
            ExtractionJson = body.Extraction is { ValueKind: JsonValueKind.Object } e ? e.GetRawText() : null,
            CreatedAtUtc = now,
        };
        _db.EquitySourceDocuments.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.Entry(row).State = EntityState.Detached;
            var raced = await _db.EquitySourceDocuments.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Symbol == sym && d.ContentSha256 == actual, ct);
            if (raced is null) throw;
            return new RegisterResult(Outcome.Ok, null, null, new EquitySourceDocumentRegistrationResult(false, ToDto(raced)));
        }
        return new RegisterResult(Outcome.Ok, null, null, new EquitySourceDocumentRegistrationResult(true, ToDto(row)));
    }

    private static bool Bounded(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= max;

    /// <summary>Clock skew tolerated for caller-supplied firstSeen/fetched timestamps.</summary>
    public static readonly TimeSpan RegistrationSkew = TimeSpan.FromMinutes(5);

    /// <summary>Date-only issuer publication ⇒ available from 00:00 VN of the NEXT day (conservative).</summary>
    public static DateTime DateOnlyAvailability(DateOnly publishedLocalDate) =>
        DateTimeOffset.FromUnixTimeMilliseconds(
            VietnamMarketCalendar.ToUtcMs(publishedLocalDate.AddDays(1), TimeOnly.MinValue)).UtcDateTime;

    private static DateTime? WithinSkew(DateTime? value, DateTime now) =>
        value is { } v && v <= now && v >= now - RegistrationSkew ? v : null;

    /// <summary>
    /// Point-in-time availability of a document: the issuer-stated publication time when one exists,
    /// else the server-stamped first registration. Snapshots filter documents AND their extracted
    /// metrics by this one rule. A revision (SupersedesDocumentId set — same URL, new bytes) is never
    /// visible before this system first saw it (spec v3 §4): the issuer's publication time belongs to
    /// the original release, not to the revised bytes.
    /// </summary>
    public static DateTime AvailableAt(EquitySourceDocument d)
    {
        var firstSeen = ToUtc(d.FirstSeenAtUtc)!.Value;
        if (ToUtc(d.PublishedAtUtc) is not { } published) return firstSeen;
        return d.SupersedesDocumentId is not null && firstSeen > published ? firstSeen : published;
    }

    public static string AvailabilityBasis(EquitySourceDocument d) =>
        d.PublishedAtUtc is null ? "first-seen"
        : d.SupersedesDocumentId is not null && ToUtc(d.FirstSeenAtUtc)!.Value > ToUtc(d.PublishedAtUtc)!.Value ? "revision-first-seen"
        : "issuer-published";

    private static DateTime? ToUtc(DateTime? value) => value is not { } v ? null : v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };
}
