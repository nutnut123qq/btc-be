using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Data;
using Backend.Options;
using Backend.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Services;

/// <summary>
/// Builds the public, market-only dossier (equity-dossier-v1, spec §7): 1d current conditions,
/// base rates, context, latest LLM run and journal scoring for the latest stored finalized 1d
/// bar. Never reads positions/lots/cash. Every section is isolated: a failing section becomes an
/// "unavailable" instance with a reason and the rest of the dossier is still served.
/// </summary>
public sealed class EquityDossierService
{
    private readonly AppDbContext _db;
    private readonly CurrentConditionsService _conditions;
    private readonly EquityBaseRatesService _baseRates;
    private readonly EquityContextService _context;
    private readonly EquityForecastJournalService _journal;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;
    private readonly ILogger<EquityDossierService> _logger;

    public EquityDossierService(
        AppDbContext db,
        IHttpClientFactory httpClientFactory,
        IResearchEvidenceCatalog evidenceCatalog,
        ILogger<CurrentConditionsService> conditionsLogger,
        EquityBaseRatesService baseRates,
        EquityContextService context,
        EquityForecastJournalService journal,
        TimeProvider timeProvider,
        ILogger<EquityDossierService> logger,
        IMemoryCache? cache = null,
        InstrumentCatalog? catalog = null)
        : this(db, new CurrentConditionsService(httpClientFactory, evidenceCatalog, conditionsLogger, cache),
            baseRates, context, journal, timeProvider, logger, catalog)
    {
    }

    internal EquityDossierService(
        AppDbContext db,
        CurrentConditionsService conditions,
        EquityBaseRatesService baseRates,
        EquityContextService context,
        EquityForecastJournalService journal,
        TimeProvider? timeProvider = null,
        ILogger<EquityDossierService>? logger = null,
        InstrumentCatalog? catalog = null)
    {
        _db = db;
        _conditions = conditions;
        _baseRates = baseRates;
        _context = context;
        _journal = journal;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityDossierService>.Instance;
        _catalog = catalog ?? InstrumentCatalog.Default;
    }

    public async Task<EquityDossierResponse> BuildAsync(string symbol, CancellationToken ct = default)
    {
        var instrument = _catalog.Get(symbol)
            ?? throw new ArgumentException($"Unknown symbol '{InstrumentCatalog.Canonicalize(symbol)}'.", nameof(symbol));
        if (!EquityScope.Has(instrument, EquityCapability.Decision))
            throw new ArgumentException($"Symbol '{instrument.Symbol}' does not support capability 'decision'.", nameof(symbol));
        var sym = instrument.Symbol;
        var now = _timeProvider.GetUtcNow();

        long? signal = null;
        try { signal = await _baseRates.LatestFinalizedDailyOpenAsync(sym, now.ToUnixTimeMilliseconds(), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "Dossier signal bar lookup failed for {Symbol}", sym); }

        var conditions = await Section("conditions", Unavailable("section-error"), async () =>
        {
            var result = await _conditions.GetAsync(sym, EquityCandleTimeframes.Day, ct);
            return result.Outcome switch
            {
                CurrentConditionsOutcome.Ok when result.Payload is not null => (JsonObject)result.Payload.DeepClone(),
                CurrentConditionsOutcome.SourceUnavailable => Unavailable("conditions-source-unavailable"),
                _ => Unavailable("conditions-payload-invalid"),
            };
        });
        var baseRates = await Section("base-rates",
            new EquityBaseRatesResponse(EquityBaseRatesResponse.Version, sym, EquityCandleTimeframes.Day,
                EquityBaseRatesStatuses.Unavailable, ["section-error"], null, []),
            () => _baseRates.GetAsync(sym, ct));
        var context = await Section("context", UnavailableContext(sym, now.UtcDateTime), () => _context.GetAsync(sym, ct));
        var latestRun = await Section<EquityLlmRunDto?>("latest-llm-run", null, async () =>
        {
            var row = await _db.EquityLlmRuns.AsNoTracking().Where(r => r.Symbol == sym)
                .OrderByDescending(r => r.CreatedAtUtc).FirstOrDefaultAsync(ct);
            return row is null ? null : EquityLlmRunService.ToDto(row);
        });
        var journal = await Section<IReadOnlyList<EquityForecastScoringDto>>("journal", [],
            async () => (await _journal.GetJournalAsync(sym, null, 1, ct)).Scoring);

        return new EquityDossierResponse(EquityDossierResponse.Version, sym, now.UtcDateTime, signal,
            conditions, baseRates, context, latestRun, journal);
    }

    private static JsonObject Unavailable(string reason) => new() { ["status"] = "unavailable", ["reason"] = reason };

    private static EquityContextResponse UnavailableContext(string symbol, DateTime nowUtc) => new(
        EquityContextResponse.Version, symbol, nowUtc,
        new EquityRelativeStrengthSection(EquityContextStatuses.Unavailable, "section-error", EquityContextService.Windows, []),
        new EquityCorporateActionsSection(EquityContextStatuses.Unavailable, "section-error", []),
        new EquityEventsSection(EquityContextStatuses.Unavailable, "section-error", []),
        new EquityFundamentalsSection(EquityContextStatuses.Unavailable, "section-error", []),
        []);

    private async Task<T> Section<T>(string name, T fallback, Func<Task<T>> build)
    {
        try { return await build(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dossier section {Section} failed; served as unavailable", name);
            return fallback;
        }
    }
}

/// <summary>
/// POST/GET llm-runs (spec §7/§11). Builds the dossier, refuses anything but the latest expected
/// finalized 1d bar (409 SIGNAL_BAR_NOT_LATEST, no backdating), calls AI POST /api/equity-llm/run,
/// re-validates the judge output defensively, and persists EVERY outcome as an EquityLlmRuns row
/// (succeeded / failed / unavailable). Succeeded runs add one llm-judge EquityForecastRecords row per
/// adjusted forecast (Validated=false). Prompts, outputs and keys are never logged.
/// </summary>
public sealed class EquityLlmRunService
{
    public const string AiRunPath = "/api/equity-llm/run";
    public const string DefaultPromptVersion = "equity-llm-prompt-v1";
    public const string JudgeSchemaVersion = "equity-llm-judge-v1";

    public enum Outcome { Created, Unavailable, UpstreamFailed, SignalBarNotLatest, ForecastWindowClosed }

    public sealed record RunResult(Outcome Outcome, EquityLlmRunDto? Item, string? Message);

    private readonly AppDbContext _db;
    private readonly EquityDossierService _dossier;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly VietnamMarketCalendar _calendar;
    private readonly TimeProvider _timeProvider;
    private readonly InstrumentCatalog _catalog;
    private readonly ILogger<EquityLlmRunService> _logger;

    public EquityLlmRunService(
        AppDbContext db,
        EquityDossierService dossier,
        IHttpClientFactory httpClientFactory,
        VietnamMarketCalendar calendar,
        TimeProvider? timeProvider = null,
        ILogger<EquityLlmRunService>? logger = null,
        InstrumentCatalog? catalog = null)
    {
        _db = db;
        _dossier = dossier;
        _httpClientFactory = httpClientFactory;
        _calendar = calendar;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EquityLlmRunService>.Instance;
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

    public async Task<EquityLlmRunListResponse> ListAsync(string symbol, int take, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var rows = await _db.EquityLlmRuns.AsNoTracking().Where(r => r.Symbol == sym)
            .OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id)
            .Take(Math.Clamp(take, 1, 50)).ToListAsync(ct);
        return new EquityLlmRunListResponse(rows.Select(ToDto).ToArray());
    }

    internal static EquityLlmRunDto ToDto(EquityLlmRun r)
    {
        JsonElement? output = null;
        if (r.OutputJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(r.OutputJson);
                output = doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return new EquityLlmRunDto(r.Id, r.Symbol, r.SignalBarOpenTimeMs, r.CreatedAtUtc, r.Status, r.Reason,
            r.ResearchModel, r.JudgeModel, r.PromptVersion, r.SchemaVersion, output, r.DurationMs);
    }

    private sealed record RunConfig(string ConfigId, double TargetPct, double StopPct, int HorizonSessions,
        double? QuantProbTargetFirst, double? QuantProbStopFirst, bool QuantValidated);

    public async Task<RunResult> RunAsync(string symbol, CancellationToken ct = default)
    {
        var sym = RequireDecision(symbol);
        var started = Stopwatch.StartNew();
        var dossier = await _dossier.BuildAsync(sym, ct);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expected = _calendar.LastExpectedFinalizedOpenUtcMs(EquityCandleTimeframes.Day, nowMs);
        var expectedStored = expected is not null && await _db.InstrumentCandles.AsNoTracking()
            .AnyAsync(c => c.Symbol == sym && c.Timeframe == EquityCandleTimeframes.Day && c.OpenTimeMs == expected.Value, ct);
        if (!expectedStored || dossier.SignalBarOpenTimeMs != expected)
            return new RunResult(Outcome.SignalBarNotLatest, null,
                $"The dossier signal bar ({dossier.SignalBarOpenTimeMs?.ToString() ?? "none"}) is not the latest expected finalized 1d bar " +
                $"({expected?.ToString() ?? "none"}{(expectedStored ? "" : ", not stored yet")}); LLM runs are never backdated.");
        var signal = expected!.Value;
        var maybeClose = EquityForecastJournalService.TryNextSessionOpenUtcMs(_calendar, signal);
        if (maybeClose is null)
            return new RunResult(Outcome.ForecastWindowClosed, null,
                $"{VietnamMarketCalendar.CoverageExpiredReason}: the session after signal bar {signal} is beyond the HOSE calendar coverage; no forecast window can be determined.");
        var windowClose = maybeClose.Value;
        if (nowMs >= windowClose)
            return new RunResult(Outcome.ForecastWindowClosed, null,
                $"The next session after signal bar {signal} opened at {windowClose}; a forecast now could see that session " +
                "(look-ahead). Runs reopen once the next finalized 1d bar is stored.");

        var configs = BuildConfigs(dossier.BaseRates);
        var dossierNode = JsonSerializer.SerializeToNode(dossier, WebJson)!;
        var configsNode = JsonSerializer.SerializeToNode(configs, WebJson)!;
        var snapshotJson = CanonicalJson(new JsonObject { ["dossier"] = dossierNode.DeepClone(), ["configs"] = configsNode.DeepClone() });
        var body = new JsonObject { ["symbol"] = sym, ["dossier"] = dossierNode, ["configs"] = configsNode };

        var run = new EquityLlmRun
        {
            Id = Guid.NewGuid(),
            Symbol = sym,
            SignalBarOpenTimeMs = signal,
            Status = EquityLlmRunStatuses.Failed,
            PromptVersion = DefaultPromptVersion,
            SchemaVersion = JudgeSchemaVersion,
            InputSnapshotJson = snapshotJson,
            InputSnapshotSha256 = Sha256Hex(snapshotJson),
        };
        var outcome = Outcome.Created;
        string? message = null;
        JsonElement? record = null;
        // From here on the request-aborted token is ignored: once the (paid) AI call starts, its
        // outcome is always persisted. The call is bounded only by the EquityLlm client timeout.
        var upstream = CancellationToken.None;
        try
        {
            var client = _httpClientFactory.CreateClient(EquityLlmOptions.HttpClientName);
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(AiRunPath, content, upstream);
            var text = await response.Content.ReadAsStringAsync(upstream);
            record = TryParseObject(text);
            var code = (int)response.StatusCode;
            switch (code)
            {
                case 200:
                    ApplyRecord(run, record);
                    if (run.Status == EquityLlmRunStatuses.Succeeded)
                    {
                        var problem = ValidateJudgeOutput(record, sym, signal, configs.Select(c => c.ConfigId).ToHashSet(StringComparer.Ordinal));
                        if (problem is not null)
                        {
                            run.Status = EquityLlmRunStatuses.Failed;
                            run.Reason = $"backend-output-invalid:{problem}";
                            run.OutputJson = null;
                        }
                    }
                    else if (run.Status != EquityLlmRunStatuses.Failed)
                    {
                        run.Status = EquityLlmRunStatuses.Failed;
                        run.Reason ??= "ai-record-status-invalid";
                    }
                    break;
                case 503:
                    ApplyRecord(run, record);
                    run.Status = EquityLlmRunStatuses.Unavailable;
                    run.Reason ??= "llm-unavailable";
                    run.OutputJson = null;
                    outcome = Outcome.Unavailable;
                    message = "The LLM provider is unavailable.";
                    break;
                case 504:
                    ApplyRecord(run, record);
                    run.Status = EquityLlmRunStatuses.Failed;
                    run.Reason = $"llm-timeout{(run.Reason is null ? "" : $":{run.Reason}")}";
                    run.OutputJson = null;
                    break;
                default:
                    run.Status = EquityLlmRunStatuses.Failed;
                    run.Reason = code == 400
                        ? $"ai-rejected-input:{DetailMessage(record) ?? "400"}"
                        : $"ai-http-{code}";
                    outcome = Outcome.UpstreamFailed;
                    message = $"The AI LLM service answered HTTP {code}.";
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            run.Status = EquityLlmRunStatuses.Failed;
            run.Reason = ex is TaskCanceledException ? "ai-request-timeout" : "ai-unreachable";
            outcome = Outcome.UpstreamFailed;
            message = "The AI LLM service is unreachable.";
            _logger.LogWarning("Equity LLM run for {Symbol} could not reach the AI service ({Reason})", sym, run.Reason);
        }

        if (run.DurationMs <= 0) run.DurationMs = started.ElapsedMilliseconds;
        run.CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        run.Reason = Truncate(run.Reason, 1000);
        _db.EquityLlmRuns.Add(run);
        if (run.Status == EquityLlmRunStatuses.Succeeded && record is { } rec)
            AddForecasts(run, rec, configs, dossier);
        await _db.SaveChangesAsync(upstream);
        _logger.LogInformation("Equity LLM run {RunId} {Symbol} status={Status} reason={Reason}", run.Id, sym, run.Status, run.Reason);
        return new RunResult(outcome, ToDto(run), message);
    }

    private static IReadOnlyList<RunConfig> BuildConfigs(EquityBaseRatesResponse baseRates)
    {
        if (baseRates.Configs.Count > 0)
            return baseRates.Configs.Select(c => new RunConfig(c.ConfigId, c.TargetPct, c.StopPct, c.HorizonSessions,
                c.Abstained ? null : c.Research?.ProbTargetFirst,
                c.Abstained ? null : c.Research?.ProbStopFirst,
                c.DecisionUse is not null)).ToArray();
        return EquityBarrierConfigs.All.Select(c => new RunConfig(c.ConfigId, c.TargetPct, c.StopPct, c.HorizonSessions,
            null, null, false)).ToArray();
    }

    private void AddForecasts(EquityLlmRun run, JsonElement record, IReadOnlyList<RunConfig> configs, EquityDossierResponse dossier)
    {
        var output = record.GetProperty("output");
        var byId = configs.ToDictionary(c => c.ConfigId, StringComparer.Ordinal);
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var signalClose = _calendar.SessionCloseUtcMs(EquityCandleTimeframes.Day, run.SignalBarOpenTimeMs);
        foreach (var fc in output.GetProperty("adjustedForecasts").EnumerateArray())
        {
            var cfg = byId[fc.GetProperty("configId").GetString()!];
            _db.EquityForecastRecords.Add(new EquityForecastRecord
            {
                Id = Guid.NewGuid(),
                ForecastId = EquityForecastJournalService.ForecastIdFor(EquityForecastJournalService.SourceLlm, run.Symbol,
                    cfg.ConfigId, run.SignalBarOpenTimeMs, run.Id),
                Source = EquityForecastJournalService.SourceLlm,
                Symbol = run.Symbol,
                Timeframe = EquityCandleTimeframes.Day,
                ConfigId = cfg.ConfigId,
                TargetPct = cfg.TargetPct,
                StopPct = cfg.StopPct,
                HorizonSessions = cfg.HorizonSessions,
                SignalBarOpenTimeMs = run.SignalBarOpenTimeMs,
                SignalBarCloseTimeMs = signalClose,
                AvailableTimeMs = Math.Max(nowMs, signalClose),
                CreatedAtUtc = run.CreatedAtUtc,
                ProbTargetFirst = fc.GetProperty("probTargetFirst").GetDouble(),
                ProbStopFirst = fc.GetProperty("probStopFirst").GetDouble(),
                Abstained = false,
                Validated = false, // LLM forecasts are never validated (CHECK CK_EquityForecastRecords_LlmUnvalidated)
                MethodVersion = Truncate($"llm-judge/{run.PromptVersion}", 80)!,
                ModelId = run.JudgeModel,
                PromptVersion = run.PromptVersion,
                SchemaVersion = run.SchemaVersion,
                InputSnapshotSha256 = run.InputSnapshotSha256,
                LlmRunId = run.Id,
            });
        }
    }

    /// <summary>Copy the AI run record onto the row with strict ValueKind guards (pitfall #17).</summary>
    private static void ApplyRecord(EquityLlmRun run, JsonElement? record)
    {
        if (record is not { ValueKind: JsonValueKind.Object } r)
        {
            run.Status = EquityLlmRunStatuses.Failed;
            run.Reason = "ai-record-not-json-object";
            return;
        }
        var status = Str(r, "status");
        run.Status = status is EquityLlmRunStatuses.Succeeded or EquityLlmRunStatuses.Failed or EquityLlmRunStatuses.Unavailable
            ? status : EquityLlmRunStatuses.Failed;
        if (status is null || run.Status != status) run.Reason = "ai-record-status-invalid";
        else run.Reason = Str(r, "reason");
        run.ResearchModel = Truncate(Str(r, "researchModel"), 80);
        run.JudgeModel = Truncate(Str(r, "judgeModel"), 80);
        run.PromptVersion = Truncate(Str(r, "promptVersion"), 80) ?? DefaultPromptVersion;
        run.SchemaVersion = Truncate(Str(r, "schemaVersion"), 80) ?? JudgeSchemaVersion;
        var sha = Str(r, "inputSnapshotSha256");
        if (sha is { Length: 64 } && sha.All(Uri.IsHexDigit)) run.InputSnapshotSha256 = sha.ToLowerInvariant();
        if (r.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) run.UsageJson = usage.GetRawText();
        if (r.TryGetProperty("durationMs", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var ms) && ms >= 0)
            run.DurationMs = ms;
        if (run.Status == EquityLlmRunStatuses.Succeeded)
        {
            if (r.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
                run.OutputJson = output.GetRawText();
            else
            {
                run.Status = EquityLlmRunStatuses.Failed;
                run.Reason = "ai-succeeded-without-output";
            }
        }
    }

    private static readonly HashSet<string> OutputKeys = new(StringComparer.Ordinal)
        { "schemaVersion", "symbol", "signalBarOpenTimeMs", "evidenceStatus", "scenarios", "warnings", "veto", "sources", "adjustedForecasts" };

    /// <summary>
    /// Defensive re-validation of equity-llm-judge-v1 (the AI already validated it): exact keys,
    /// identity, source references, configIds ⊆ requested configs, 0 ≤ p ≤ 1, pT + pS ≤ 1.
    /// Returns null when valid, else a short reason.
    /// </summary>
    internal static string? ValidateJudgeOutput(JsonElement? record, string symbol, long signal, IReadOnlySet<string> configIds)
    {
        if (record is not { ValueKind: JsonValueKind.Object } r || !r.TryGetProperty("output", out var o) || o.ValueKind != JsonValueKind.Object)
            return "output-not-object";
        var keys = o.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (!keys.SetEquals(OutputKeys)) return "output-keys-mismatch";
        if (Str(o, "schemaVersion") != JudgeSchemaVersion) return "schema-version-mismatch";
        if (Str(o, "symbol") != symbol) return "symbol-mismatch";
        if (o.GetProperty("signalBarOpenTimeMs") is not { ValueKind: JsonValueKind.Number } sig || !sig.TryGetInt64(out var s) || s != signal)
            return "signal-bar-mismatch";
        var evidence = Str(o, "evidenceStatus");
        if (evidence is not ("sourced" or "insufficient")) return "evidence-status-invalid";

        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (o.GetProperty("sources").ValueKind != JsonValueKind.Array) return "sources-not-list";
        foreach (var src in o.GetProperty("sources").EnumerateArray())
        {
            if (src.ValueKind != JsonValueKind.Object || Str(src, "id") is not { } id || Str(src, "url") is null || Str(src, "title") is null)
                return "source-field-type";
            if (!src.TryGetProperty("publishedAt", out var pub) || pub.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return "source-field-type";
            if (!ids.Add(id)) return "source-id-duplicate";
        }
        bool Refs(JsonElement obj, bool requireAny)
        {
            if (!obj.TryGetProperty("sourceIds", out var list) || list.ValueKind != JsonValueKind.Array) return false;
            var n = 0;
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !ids.Contains(item.GetString()!)) return false;
                n++;
            }
            return !requireAny || n > 0;
        }
        if (o.GetProperty("scenarios").ValueKind != JsonValueKind.Array) return "scenarios-not-list";
        foreach (var sc in o.GetProperty("scenarios").EnumerateArray())
        {
            if (sc.ValueKind != JsonValueKind.Object || Str(sc, "name") is null || Str(sc, "description", allowEmpty: true) is null
                || Str(sc, "direction") is not ("up" or "down" or "sideways") || !Refs(sc, false))
                return "scenario-invalid";
        }
        if (o.GetProperty("warnings").ValueKind != JsonValueKind.Array) return "warnings-not-list";
        foreach (var w in o.GetProperty("warnings").EnumerateArray())
            if (w.ValueKind != JsonValueKind.Object || Str(w, "text", allowEmpty: true) is null || !Refs(w, false))
                return "warning-invalid";
        var veto = o.GetProperty("veto");
        if (veto.ValueKind != JsonValueKind.Object || !veto.TryGetProperty("active", out var active)
            || active.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !veto.TryGetProperty("reason", out var reason) || reason.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
            || !Refs(veto, active.ValueKind == JsonValueKind.True))
            return "veto-invalid";

        if (o.GetProperty("adjustedForecasts").ValueKind != JsonValueKind.Array) return "adjusted-forecasts-not-list";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var fc in o.GetProperty("adjustedForecasts").EnumerateArray())
        {
            count++;
            if (fc.ValueKind != JsonValueKind.Object || Str(fc, "configId") is not { } cid) return "adjusted-forecast-invalid";
            if (!configIds.Contains(cid)) return "adjusted-forecast-unknown-config";
            if (!seen.Add(cid)) return "adjusted-forecast-duplicate-config";
            if (!Prob(fc, "probTargetFirst", out var pt) || !Prob(fc, "probStopFirst", out var ps))
                return "adjusted-forecast-probability-out-of-range";
            if (pt + ps > 1.0 + 1e-9) return "adjusted-forecast-probability-sum-above-one";
            if (Str(fc, "rationale") is null) return "adjusted-forecast-rationale-missing";
            if (!Refs(fc, true)) return "adjusted-forecast-without-sources";
        }
        if (evidence == "insufficient" && count > 0) return "adjusted-forecasts-with-insufficient-evidence";
        return null;
    }

    private static bool Prob(JsonElement obj, string name, out double value)
    {
        value = double.NaN;
        return obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && double.IsFinite(value = v.GetDouble()) && value is >= 0 and <= 1;
    }

    private static string? Str(JsonElement obj, string name, bool allowEmpty = false) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && (allowEmpty || !string.IsNullOrWhiteSpace(v.GetString())) ? v.GetString() : null;

    private static string? DetailMessage(JsonElement? record) =>
        record is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.Object
            ? Truncate(Str(d, "message") ?? Str(d, "code"), 300) : null;

    private static JsonElement? TryParseObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>Canonical JSON (sorted keys, compact) — same shape rule as ai/equity_llm.canonical_json.</summary>
    internal static string CanonicalJson(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();

        static void Write(JsonNode? n, StringBuilder sb)
        {
            switch (n)
            {
                case null:
                    sb.Append("null");
                    break;
                case JsonObject obj:
                    sb.Append('{');
                    var first = true;
                    foreach (var (key, value) in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(JsonSerializer.Serialize(key, CanonicalStringOptions)).Append(':');
                        Write(value, sb);
                    }
                    sb.Append('}');
                    break;
                case JsonArray arr:
                    sb.Append('[');
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Write(arr[i], sb);
                    }
                    sb.Append(']');
                    break;
                default:
                    sb.Append(n.ToJsonString(CanonicalStringOptions));
                    break;
            }
        }
    }

    private static readonly JsonSerializerOptions CanonicalStringOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
