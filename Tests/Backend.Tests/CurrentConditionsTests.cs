using Backend.Controllers;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Backend.Tests;

public sealed class CurrentConditionsTests
{
    private const string ContractSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string SpecSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string ItemId = "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
    private const string ReportSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const long CutoffMs = 1_789_948_799_999L;
    private const long AsOfMs = CutoffMs + 7L * 3_600_000L + 500L; // 7 full 1h bars plus a remainder after the evidence cutoff
    private const long GeneratedAtMs = 1_790_000_000_123L;

    // ------------------------------------------------------------------ tests

    [Fact]
    public async Task GetCurrentConditions_JoinsTestedHypothesisIntoEveryCell()
    {
        var (status, body, error) = await Invoke(
            AiPayload("1h", DefaultConditions), FullCatalog("1h"));

        Assert.Equal(200, status);
        Assert.Null(error);
        var root = body!.RootElement;
        Assert.Equal("1h", root.GetProperty("timeframe").GetString());
        Assert.Equal(AsOfMs, root.GetProperty("asOfMs").GetInt64());

        var condition = root.GetProperty("conditions")[0];
        Assert.Equal("technicalIndicators", condition.GetProperty("module").GetString());
        Assert.Equal("RSI_ENTER_OVERSOLD", condition.GetProperty("eventType").GetString());
        Assert.Equal("bullish", condition.GetProperty("direction").GetString());
        // Verbatim fields survive the join.
        Assert.Equal("down", condition.GetProperty("context").GetProperty("trend").GetString());
        Assert.Equal(27.4, condition.GetProperty("details").GetProperty("rsi").GetDouble());

        var cell = condition.GetProperty("evidence").GetProperty("1").GetProperty("forwardReturn");
        Assert.True(cell.GetProperty("tested").GetBoolean());
        Assert.Equal(0.01, cell.GetProperty("rawP").GetDouble());
        Assert.Equal(0.02, cell.GetProperty("adjustedQValue").GetDouble());
        Assert.True(cell.GetProperty("passesDeclaredFdr").GetBoolean());
        Assert.True(cell.GetProperty("sufficientSample").GetBoolean());
        Assert.Equal(30L, cell.GetProperty("nonOverlappingPairs").GetInt64());
        Assert.Equal(0.35, cell.GetProperty("effect").GetDouble());
        Assert.Equal(0.002, cell.GetProperty("ciLower").GetDouble());
        Assert.Equal(0.02, cell.GetProperty("ciUpper").GetDouble());
        Assert.Equal(0.012, cell.GetProperty("meanPairedDifference").GetDouble());

        // Same horizon, tested but failing FDR.
        var mfe = condition.GetProperty("evidence").GetProperty("1").GetProperty("mfe");
        Assert.True(mfe.GetProperty("tested").GetBoolean());
        Assert.False(mfe.GetProperty("passesDeclaredFdr").GetBoolean());
        Assert.Equal(0.4, mfe.GetProperty("adjustedQValue").GetDouble());
    }

    [Fact]
    public async Task GetCurrentConditions_MissingHypothesis_ReturnsUntestedCell()
    {
        var (_, body, _) = await Invoke(AiPayload("1h", DefaultConditions), FullCatalog("1h"));

        var condition = body!.RootElement.GetProperty("conditions")[0];
        // (technicalIndicators, RSI_ENTER_OVERSOLD, 3, mfe) is absent from the report.
        var cell = condition.GetProperty("evidence").GetProperty("3").GetProperty("mfe");
        Assert.False(cell.GetProperty("tested").GetBoolean());
        Assert.Equal("untested", cell.GetProperty("reason").GetString());

        // marketRegime/REGIME_BULL_NORMAL has no hypothesis at all.
        var regime = body.RootElement.GetProperty("conditions")[2];
        foreach (var horizon in new[] { "1", "3", "6" })
            foreach (var metric in new[] { "forwardReturn", "mfe", "mae" })
            {
                var regimeCell = regime.GetProperty("evidence").GetProperty(horizon).GetProperty(metric);
                Assert.False(regimeCell.GetProperty("tested").GetBoolean());
                Assert.Equal("untested", regimeCell.GetProperty("reason").GetString());
            }
    }

    [Fact]
    public async Task GetCurrentConditions_GateSemantics_FlagFalseIsLegalAndInsufficientMapsReason()
    {
        var (_, body, _) = await Invoke(AiPayload("1h", DefaultConditions), FullCatalog("1h"));

        var evidence = body!.RootElement.GetProperty("conditions")[0].GetProperty("evidence");

        // q <= alpha but non-overlapping sample below the declared gate: tested
        // cell with passesDeclaredFdr=false and sufficientSample=false.
        var gated = evidence.GetProperty("3").GetProperty("forwardReturn");
        Assert.True(gated.GetProperty("tested").GetBoolean());
        Assert.Equal(0.002, gated.GetProperty("adjustedQValue").GetDouble());
        Assert.False(gated.GetProperty("passesDeclaredFdr").GetBoolean());
        Assert.False(gated.GetProperty("sufficientSample").GetBoolean());
        Assert.Equal(5L, gated.GetProperty("nonOverlappingPairs").GetInt64());

        // Declared status insufficient_or_no_matched_sample -> untested cell.
        var insufficient = evidence.GetProperty("6").GetProperty("forwardReturn");
        Assert.False(insufficient.GetProperty("tested").GetBoolean());
        Assert.Equal("insufficient-sample", insufficient.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task GetCurrentConditions_NoVerifiedBundle_ReturnsUnavailableEvidence()
    {
        var (status, body, _) = await Invoke(
            AiPayload("1h", DefaultConditions),
            new StubCatalog([], _ => null));

        Assert.Equal(200, status);
        var evidence = body!.RootElement.GetProperty("evidence");
        Assert.False(evidence.GetProperty("available").GetBoolean());
        Assert.Equal("no-verified-evidence-bundle", evidence.GetProperty("reason").GetString());
        Assert.True(evidence.GetProperty("cutoffMs").ValueKind is JsonValueKind.Null);
        Assert.True(evidence.GetProperty("evidenceAgeBars").ValueKind is JsonValueKind.Null);

        foreach (var condition in body.RootElement.GetProperty("conditions").EnumerateArray())
        {
            var cell = condition.GetProperty("evidence").GetProperty("1").GetProperty("forwardReturn");
            Assert.False(cell.GetProperty("tested").GetBoolean());
            Assert.Equal("evidence-unavailable", cell.GetProperty("reason").GetString());
        }
        Assert.Empty(body.RootElement.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task GetCurrentConditions_ContractShaMismatch_ReturnsUnavailableEvidence()
    {
        var (status, body, _) = await Invoke(
            AiPayload("1h", DefaultConditions, definitionsSha: OtherSha), FullCatalog("1h"));

        Assert.Equal(200, status);
        var evidence = body!.RootElement.GetProperty("evidence");
        Assert.False(evidence.GetProperty("available").GetBoolean());
        Assert.Equal("contract-definitions-sha-mismatch", evidence.GetProperty("reason").GetString());
        // Identity fields are still surfaced for audit.
        Assert.Equal(ItemId, evidence.GetProperty("manifestSha256").GetString());
        Assert.Equal(SpecSha, evidence.GetProperty("specSha256").GetString());
        Assert.Equal(CutoffMs, evidence.GetProperty("cutoffMs").GetInt64());
    }

    [Fact]
    public async Task GetCurrentConditions_EmitsConflict_WhenBothDirectionsPassFdr()
    {
        var (_, body, _) = await Invoke(AiPayload("1h", DefaultConditions), FullCatalog("1h"));

        var conflict = Assert.Single(body!.RootElement.GetProperty("conflicts").EnumerateArray());
        Assert.Equal(1L, conflict.GetProperty("horizon").GetInt64());
        Assert.Equal("forwardReturn", conflict.GetProperty("metric").GetString());
        Assert.Equal(
            new[] { "technicalIndicators:RSI_ENTER_OVERSOLD" },
            conflict.GetProperty("bullish").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(
            new[] { "technicalIndicators:RSI_ENTER_OVERBOUGHT" },
            conflict.GetProperty("bearish").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public async Task GetCurrentConditions_NoConflict_WhenOnlyOneDirectionPasses()
    {
        var conditions = ConditionsJson(new[]
        {
            Condition("technicalIndicators", "RSI_ENTER_OVERSOLD", "bullish"),
            Condition("marketRegime", "REGIME_BULL_NORMAL", "bullish")
        });
        var (_, body, _) = await Invoke(AiPayload("1h", conditions), FullCatalog("1h"));

        Assert.Empty(body!.RootElement.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task GetCurrentConditions_NoConflict_WhenPasserLacksSufficientSample()
    {
        // passesDeclaredFdr=true but sufficientSample=false must not count as a
        // conflict passer — the declared sample gate applies alongside FDR.
        var (_, body, _) = await Invoke(
            AiPayload("1h", DefaultConditions),
            FullCatalog("1h", StatisticalEvidenceJson("1h", oversoldSufficient: false)));

        Assert.Empty(body!.RootElement.GetProperty("conflicts").EnumerateArray());
    }

    [Fact]
    public async Task GetCurrentConditions_EvidenceAgeBars_FloorsBarDelta()
    {
        var (_, body, _) = await Invoke(AiPayload("1h", DefaultConditions), FullCatalog("1h"));

        var evidence = body!.RootElement.GetProperty("evidence");
        Assert.True(evidence.GetProperty("available").GetBoolean());
        Assert.True(evidence.GetProperty("reason").ValueKind is JsonValueKind.Null);
        Assert.Equal(ItemId, evidence.GetProperty("runId").GetString());
        Assert.Equal(ItemId, evidence.GetProperty("manifestSha256").GetString());
        Assert.Equal(SpecSha, evidence.GetProperty("specSha256").GetString());
        Assert.Equal(CutoffMs, evidence.GetProperty("cutoffMs").GetInt64());
        Assert.Equal(7L, evidence.GetProperty("evidenceAgeBars").GetInt64());
    }

    [Fact]
    public async Task GetCurrentConditions_PassesThroughAiEnvelopeFieldsVerbatim()
    {
        var (_, body, _) = await Invoke(AiPayload("1h", DefaultConditions), FullCatalog("1h"));

        var root = body!.RootElement;
        Assert.Equal(GeneratedAtMs, root.GetProperty("generatedAtMs").GetInt64());
        Assert.Equal(120000.5, root.GetProperty("latestClosedBar").GetProperty("close").GetDouble());
        Assert.Equal(2000L, root.GetProperty("analysisWindow").GetProperty("windowBars").GetInt64());
        Assert.True(root.GetProperty("analysisWindow").GetProperty("contiguous").GetBoolean());
        Assert.Equal(ContractSha, root.GetProperty("contract").GetProperty("definitionsSha256").GetString());
        Assert.Equal("btc-technical-module-contract/v9", root.GetProperty("contract").GetProperty("contractVersion").GetString());
        Assert.Equal(
            new[] { "analysis window truncated at a kline gap" },
            root.GetProperty("warnings").EnumerateArray().Select(x => x.GetString()).ToArray());
        var unavailable = Assert.Single(root.GetProperty("unavailableModules").EnumerateArray());
        Assert.Equal("causalSmc", unavailable.GetProperty("module").GetString());
        Assert.Equal("hash verification failed", unavailable.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task GetCurrentConditions_AiServerError_Returns502SourceUnavailable()
    {
        var (status, _, error) = await Invoke(
            AiPayload("1h", DefaultConditions), FullCatalog("1h"),
            handler: _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"detail\":\"klines unavailable\"}")
            }));

        Assert.Equal(502, status);
        Assert.Equal("CONDITIONS_SOURCE_UNAVAILABLE", error!.Code);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task GetCurrentConditions_AiUnreachableOrTimeout_Returns502SourceUnavailable()
    {
        var (statusHttp, _, errorHttp) = await Invoke(
            AiPayload("1h", DefaultConditions), FullCatalog("1h"),
            handler: _ => throw new HttpRequestException("connection refused"));
        Assert.Equal(502, statusHttp);
        Assert.Equal("CONDITIONS_SOURCE_UNAVAILABLE", errorHttp!.Code);
        Assert.True(errorHttp.Retryable);

        var (statusTimeout, _, errorTimeout) = await Invoke(
            AiPayload("1h", DefaultConditions), FullCatalog("1h"),
            handler: _ => throw new TaskCanceledException("timeout"));
        Assert.Equal(502, statusTimeout);
        Assert.Equal("CONDITIONS_SOURCE_UNAVAILABLE", errorTimeout!.Code);
        Assert.True(errorTimeout.Retryable);
    }

    [Theory]
    [InlineData("wrongSchema")]
    [InlineData("missingConditions")]
    [InlineData("badConditionKind")]
    [InlineData("notJson")]
    public async Task GetCurrentConditions_InvalidPayload_Returns502PayloadInvalid(string variant)
    {
        string body = variant switch
        {
            "wrongSchema" => AiPayload("1h", DefaultConditions).Replace(
                "current-conditions-v1", "current-conditions-v0"),
            "missingConditions" => AiPayload("1h", DefaultConditions)
                .Replace("\"conditions\":", "\"removedConditions\":"),
            "badConditionKind" => AiPayload("1h", DefaultConditions)
                .Replace("\"state\"", "\"invented-kind\""),
            _ => "this is not json"
        };
        var (status, _, error) = await Invoke(body, FullCatalog("1h"));

        Assert.Equal(502, status);
        Assert.Equal("CONDITIONS_PAYLOAD_INVALID", error!.Code);
        Assert.False(error.Retryable);
    }

    [Theory]
    [InlineData("15m")]
    [InlineData("4H")]
    [InlineData(null)]
    public async Task GetCurrentConditions_BadTimeframe_Returns400(string? timeframe)
    {
        var handler = new RecordingHandler();
        var (status, _, error) = await Invoke(
            AiPayload("1h", DefaultConditions), FullCatalog("1h"), timeframe, handler);

        Assert.Equal(400, status);
        Assert.Equal("INVALID_TIMEFRAME", error!.Code);
        Assert.False(error.Retryable);
        Assert.Null(handler.LastRequest); // upstream must not be called for rejected timeframes
    }

    [Fact]
    public async Task GetCurrentConditions_RequestsAiEndpointWithTimeframe()
    {
        var handler = new RecordingHandler();
        await Invoke(AiPayload("4h", DefaultConditions), FullCatalog("4h"), "4h", handler);

        Assert.Equal("/api/current-conditions?timeframe=4h", handler.LastRequest);
    }

    // ------------------------------------------------------------- caching

    [Fact]
    public async Task GetAsync_CacheHit_DoesNotCallUpstreamAgain()
    {
        var handler = new RecordingHandler { ResponseBody = AiPayload("1h", DefaultConditions) };
        var service = CreateCachedService(handler);

        var first = await service.GetAsync("1h", CancellationToken.None);
        var second = await service.GetAsync("1h", CancellationToken.None);

        Assert.Equal(CurrentConditionsOutcome.Ok, first.Outcome);
        Assert.Equal(CurrentConditionsOutcome.Ok, second.Outcome);
        Assert.Equal(1, handler.RequestCount);
        Assert.Same(first.Payload, second.Payload);
    }

    [Fact]
    public async Task GetAsync_CacheKeySeparatesByTimeframe()
    {
        var handler = new RecordingHandler
        {
            Responder = req => Task.FromResult(Json200(
                AiPayload(req.RequestUri!.Query.Contains("4h") ? "4h" : "1h", DefaultConditions)))
        };
        var service = CreateCachedService(handler);

        await service.GetAsync("1h", CancellationToken.None);
        await service.GetAsync("4h", CancellationToken.None);
        await service.GetAsync("1h", CancellationToken.None); // served from cache

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetAsync_ConcurrentMiss_ComputesOnce()
    {
        var calls = 0;
        var handler = new RecordingHandler
        {
            Responder = async _ =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(75); // hold the fetch so the misses overlap
                return Json200(AiPayload("1h", DefaultConditions));
            }
        };
        var service = CreateCachedService(handler);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => service.GetAsync("1h", CancellationToken.None)));

        Assert.Equal(1, calls);
        Assert.All(results, r => Assert.Equal(CurrentConditionsOutcome.Ok, r.Outcome));
        Assert.All(results.Skip(1), r => Assert.Same(results[0].Payload, r.Payload));
    }

    [Fact]
    public async Task GetAsync_ErrorOutcome_IsNotCached()
    {
        var calls = 0;
        var handler = new RecordingHandler
        {
            Responder = _ => Task.FromResult(Interlocked.Increment(ref calls) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    { Content = new StringContent("{}") }
                : Json200(AiPayload("1h", DefaultConditions)))
        };
        var service = CreateCachedService(handler);

        var first = await service.GetAsync("1h", CancellationToken.None);
        var second = await service.GetAsync("1h", CancellationToken.None);

        Assert.Equal(CurrentConditionsOutcome.SourceUnavailable, first.Outcome);
        Assert.Equal(CurrentConditionsOutcome.Ok, second.Outcome);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetAsync_ExpiredEntry_Refetches()
    {
        var handler = new RecordingHandler { ResponseBody = AiPayload("1h", DefaultConditions) };
        var service = CreateCachedService(handler, TimeSpan.FromMilliseconds(1));

        await service.GetAsync("1h", CancellationToken.None);
        await Task.Delay(50);
        await service.GetAsync("1h", CancellationToken.None);

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetCurrentConditions_CrossControllerRequests_ShareInjectedCache()
    {
        // Prod topology: one controller/service per request, one singleton
        // IMemoryCache — the second request must hit the shared cache.
        var handler = new RecordingHandler { ResponseBody = AiPayload("1h", DefaultConditions) };
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") };
        var factory = new StubHttpClientFactory(client);
        var catalog = FullCatalog("1h");
        var cache = new MemoryCache(new MemoryCacheOptions());

        var first = new ResearchCurrentConditionsController(
            factory, catalog,
            NullLogger<CurrentConditionsService>.Instance,
            NullLogger<ResearchCurrentConditionsController>.Instance,
            cache: cache);
        var second = new ResearchCurrentConditionsController(
            factory, catalog,
            NullLogger<CurrentConditionsService>.Instance,
            NullLogger<ResearchCurrentConditionsController>.Instance,
            cache: cache);

        Assert.IsType<OkObjectResult>(await first.GetCurrentConditions("1h"));
        Assert.IsType<OkObjectResult>(await second.GetCurrentConditions("1h"));
        Assert.Equal(1, handler.RequestCount);
    }

    private static CurrentConditionsService CreateCachedService(
        RecordingHandler handler, TimeSpan? cacheTtl = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") };
        return new CurrentConditionsService(
            new StubHttpClientFactory(client),
            FullCatalog("1h"),
            NullLogger<CurrentConditionsService>.Instance,
            new MemoryCache(new MemoryCacheOptions()),
            cacheTtl);
    }

    private static HttpResponseMessage Json200(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    // ---------------------------------------------------------------- fixtures

    private static async Task<(int Status, JsonDocument? Body, ApiErrorEnvelope? Error)> Invoke(
        string aiBody,
        IResearchEvidenceCatalog catalog,
        string? timeframe = "1h",
        RecordingHandler? handler = null)
    {
        handler ??= new RecordingHandler();
        handler.ResponseBody ??= aiBody;
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://ai.test") };
        var controller = new ResearchCurrentConditionsController(
            new StubHttpClientFactory(client), catalog,
            NullLogger<CurrentConditionsService>.Instance,
            NullLogger<ResearchCurrentConditionsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var action = await controller.GetCurrentConditions(timeframe);
        return action switch
        {
            OkObjectResult ok => (200, JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)), null),
            BadRequestObjectResult bad => (400, null, Assert.IsType<ApiErrorEnvelope>(bad.Value)),
            ObjectResult other => (other.StatusCode ?? 500, null, Assert.IsType<ApiErrorEnvelope>(other.Value)),
            _ => throw new InvalidOperationException($"Unexpected action result {action.GetType().Name}")
        };
    }

    private static async Task<(int, JsonDocument?, ApiErrorEnvelope?)> Invoke(
        string aiBody,
        IResearchEvidenceCatalog catalog,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        string? timeframe = "1h")
        => await Invoke(aiBody, catalog, timeframe,
            new RecordingHandler { Responder = handler });

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private int _requestCount;
        public string? LastRequest { get; private set; }
        public int RequestCount => _requestCount;
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? Responder { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            LastRequest = request.RequestUri is { } uri ? uri.PathAndQuery : null;
            if (Responder is { } responder)
                return responder(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody ?? "{}", Encoding.UTF8, "application/json")
            });
        }

        public string? ResponseBody { get; set; }
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubCatalog(
        IReadOnlyList<ResearchEvidenceCatalogItemDto> items,
        Func<string, ResearchEvidenceDetailDto?> detail) : IResearchEvidenceCatalog
    {
        public ResearchEvidenceCatalogResponse GetCatalog() => new(
            "btc-research-evidence-catalog-v1",
            "BTCUSDT",
            new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc),
            items,
            new EvidenceIntegritySummaryDto(items.Count, items.Count, 0),
            null);

        public ResearchEvidenceDetailDto? GetDetail(string id) => detail(id);
    }

    private static StubCatalog FullCatalog(string timeframe, string? statisticsJson = null)
    {
        var json = statisticsJson ?? StatisticalEvidenceJson(timeframe);
        var item = new ResearchEvidenceCatalogItemDto(
            ItemId, "event", "Technical-event descriptive history", "inconclusive", "descriptive",
            "BTCUSDT", timeframe, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            ItemId, ReportSha, "summary", [],
            new EvidenceIntegrityDto(true, true, true, false, "test"));
        var element = JsonDocument.Parse(json).RootElement.Clone();
        var detail = new ResearchEvidenceDetailDto(
            ItemId, "event", "Technical-event descriptive history", "inconclusive", "descriptive",
            "BTCUSDT", timeframe, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            ItemId, ReportSha, "summary", "hypothesis",
            new ResearchEvidenceDatasetDto("fixture", null, null, CutoffMs, null),
            new ResearchEvidenceProtocolDto(null, null, null, null, null),
            [], [], [], [],
            new ResearchEvidenceCoverageDto(null, null, null, null),
            "conclusion", [],
            new ResearchEvidenceProvenanceDto(null, null, null, null, null, null),
            [],
            new EvidenceIntegrityDto(true, true, true, false, "test"),
            StatisticalEvidence: element);
        return new StubCatalog([item], id => id == ItemId ? detail : null);
    }

    private static string Condition(string module, string eventType, string direction, string kind = "state") => $$"""
        {
            "module": "{{module}}",
            "eventType": "{{eventType}}",
            "kind": "{{kind}}",
            "direction": "{{direction}}",
            "eventId": null,
            "formedTimeMs": {{AsOfMs - 3_600_000L}},
            "availableTimeMs": {{AsOfMs}},
            "context": {"trend": "down", "volatility": "normal"},
            "details": {"rsi": 27.4}
        }
        """;

    private static string ConditionsJson(IEnumerable<string> conditions) =>
        $"[{string.Join(",", conditions)}]";

    private static readonly string DefaultConditions = ConditionsJson(new[]
    {
        Condition("technicalIndicators", "RSI_ENTER_OVERSOLD", "bullish"),
        Condition("technicalIndicators", "RSI_ENTER_OVERBOUGHT", "bearish"),
        Condition("marketRegime", "REGIME_BULL_NORMAL", "bullish")
    });

    private static string AiPayload(string timeframe, string conditionsJson, string definitionsSha = ContractSha) => $$"""
        {
            "schemaVersion": "current-conditions-v1",
            "symbol": "BTCUSDT",
            "timeframe": "{{timeframe}}",
            "asOfMs": {{AsOfMs}},
            "latestClosedBar": {
                "openTimeMs": {{AsOfMs - 3_600_000L}},
                "closeTimeMs": {{AsOfMs}},
                "open": 119800.0,
                "high": 120300.0,
                "low": 119500.0,
                "close": 120000.5,
                "volume": 1234.5
            },
            "generatedAtMs": {{GeneratedAtMs}},
            "analysisWindow": {"firstOpenTimeMs": 1000, "bars": 2000, "contiguous": true, "windowBars": 2000},
            "contract": {
                "contractVersion": "btc-technical-module-contract/v9",
                "definitionsSha256": "{{definitionsSha}}",
                "rawFileSha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"
            },
            "conditions": {{conditionsJson}},
            "warnings": ["analysis window truncated at a kline gap"],
            "unavailableModules": [{"module": "causalSmc", "reason": "hash verification failed"}]
        }
        """;

    private static string StatisticalEvidenceJson(string timeframe, string definitionsSha = ContractSha, bool oversoldSufficient = true)
    {
        var hypotheses = string.Join(",", new[]
        {
            HypothesisJson("technicalIndicators", "RSI_ENTER_OVERSOLD", 1, "forwardReturn", "tested",
                rawP: 0.01, adjustedQ: 0.02, passes: true, sufficient: oversoldSufficient, nonOverlap: 30,
                effect: 0.35, ciLower: 0.002, ciUpper: 0.02, meanPaired: 0.012),
            HypothesisJson("technicalIndicators", "RSI_ENTER_OVERSOLD", 1, "mfe", "tested",
                rawP: 0.35, adjustedQ: 0.4, passes: false, sufficient: true, nonOverlap: 30,
                effect: -0.05, ciLower: -0.01, ciUpper: 0.005, meanPaired: -0.001),
            // BY q passes but the non-overlapping gate blocks the declared-FDR flag.
            HypothesisJson("technicalIndicators", "RSI_ENTER_OVERSOLD", 3, "forwardReturn", "tested",
                rawP: 0.001, adjustedQ: 0.002, passes: false, sufficient: false, nonOverlap: 5,
                effect: 0.9, ciLower: 0.1, ciUpper: 0.5, meanPaired: 0.03),
            HypothesisJson("technicalIndicators", "RSI_ENTER_OVERSOLD", 6, "forwardReturn",
                "insufficient_or_no_matched_sample",
                rawP: null, adjustedQ: null, passes: null, sufficient: false, nonOverlap: 0,
                effect: null, ciLower: null, ciUpper: null, meanPaired: null),
            HypothesisJson("technicalIndicators", "RSI_ENTER_OVERBOUGHT", 1, "forwardReturn", "tested",
                rawP: 0.02, adjustedQ: 0.03, passes: true, sufficient: true, nonOverlap: 40,
                effect: -0.2, ciLower: -0.03, ciUpper: -0.005, meanPaired: -0.015)
        });
        return $$"""
            {
                "schema": "btc-technical-evidence-statistics/v1",
                "specSha256": "{{SpecSha}}",
                "scope": {"symbol": "BTCUSDT", "timeframe": "{{timeframe}}"},
                "claimType": "descriptive_only",
                "declaredFamily": {
                    "technicalModuleContractDefinitionsSha256": "{{definitionsSha}}",
                    "moduleEventTypes": {"technicalIndicators": ["RSI_ENTER_OVERSOLD", "RSI_ENTER_OVERBOUGHT"]},
                    "moduleEventTypeIdentityCount": 2,
                    "cartesianHypothesisCount": 18,
                    "unknownObservedIdentityPolicy": "fail_closed",
                    "zeroEventPolicy": "retain_with_null_inferential_values"
                },
                "multipleTesting": {"method": "Benjamini-Yekutieli", "declaredQAlpha": 0.05},
                "hypotheses": [{{hypotheses}}]
            }
            """;
    }

    private static string HypothesisJson(
        string module, string eventType, int horizon, string metric, string status,
        double? rawP, double? adjustedQ, bool? passes, bool sufficient,
        long nonOverlap, double? effect, double? ciLower, double? ciUpper, double? meanPaired)
    {
        var tested = status == "tested";
        var count = tested ? Math.Max(nonOverlap, 2) : 0;
        var interval = ciLower is { } lower && ciUpper is { } upper
            ? $"{{\"lower\":{Num(lower)},\"upper\":{Num(upper)}}}"
            : "null";
        return $$"""
            {
                "adjustedQValue": {{Num(adjustedQ)}},
                "blockBootstrap": {
                    "blockSizeEvents": {{(tested ? "8" : "null")}},
                    "centeredTwoSidedPValue": {{Num(rawP)}},
                    "meanDifferenceInterval": {{interval}},
                    "samples": 2000
                },
                "effectSize": {
                    "count": {{count}},
                    "meanPairedDifference": {{Num(meanPaired)}},
                    "medianPairedDifference": {{Num(meanPaired)}},
                    "negativeFraction": {{(tested ? "0.4" : "null")}},
                    "pairedStandardizedMeanDifference": {{Num(effect)}},
                    "positiveFraction": {{(tested ? "0.6" : "null")}},
                    "tieFraction": {{(tested ? "0.0" : "null")}}
                },
                "eventType": "{{eventType}}",
                "horizonBars": {{horizon}},
                "hypothesisId": "{{module}}:{{eventType}}:{{horizon}}:{{metric}}",
                "metric": "{{metric}}",
                "minimumNonOverlappingPairs": 20,
                "module": "{{module}}",
                "nullBaseline": "strict-prior exact trend+volatility regime match without replacement within focal module:eventType",
                "passesDeclaredFdr": {{Bool(passes)}},
                "rawPValue": {{Num(rawP)}},
                "sampleDiagnostics": {
                    "eventOrderAutocorrelationEffectiveSampleSize": {
                        "estimate": {{(double)count}}, "maxLags": 20, "positiveAutocorrelationLagsUsed": 0
                    },
                    "independenceClaimed": false,
                    "maximumGreedyNonOverlappingOutcomeWindows": {{nonOverlap}},
                    "nominalMatchedPairs": {{count}},
                    "observationsExcludedForMaximumNonOverlappingSet": 0,
                    "uniqueDecisionTimes": {{count}}
                },
                "stability": {
                    "regime": {"availableGroupCount": 0, "groups": {}, "minimumSampleFilter": null, "pooledSignAgreementFraction": null},
                    "yearUtc": {"availableGroupCount": 0, "groups": {}, "minimumSampleFilter": null, "pooledSignAgreementFraction": null}
                },
                "status": "{{status}}",
                "sufficientSample": {{(sufficient ? "true" : "false")}}
            }
            """;
    }

    private static string Num(double? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture) ?? "null";

    private static string Bool(bool? value) =>
        value is null ? "null" : value.Value ? "true" : "false";
}
