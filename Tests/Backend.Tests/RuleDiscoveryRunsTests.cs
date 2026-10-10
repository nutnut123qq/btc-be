using Backend.Controllers;
using Backend.Data;
using Backend.Options;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Backend.Tests;

public class RuleDiscoveryRunsTests
{
    [Fact]
    public async Task GetDiscoveryRuns_FiltersBySymbolAndTimeframe_AndOrdersLatestFirst()
    {
        await using var db = Db();
        var older = Run("BTCUSDT", "4h", trialCount: 90, createdAtUtc: new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc));
        var newer = Run("BTCUSDT", "4h", trialCount: 128, createdAtUtc: new DateTime(2026, 4, 24, 12, 0, 0, DateTimeKind.Utc));
        var otherTf = Run("BTCUSDT", "1h", trialCount: 128, createdAtUtc: new DateTime(2026, 4, 24, 14, 0, 0, DateTimeKind.Utc));
        db.RuleDiscoveryRuns.AddRange(older, newer, otherTf);
        await db.SaveChangesAsync();
        var controller = Controller(db);

        var filtered4h = Payload((await controller.GetDiscoveryRuns("BTCUSDT", "4h")).Result);
        Assert.Equal(2, filtered4h.RootElement.GetArrayLength());
        var first = filtered4h.RootElement[0];
        Assert.Equal(newer.Id, first.GetProperty("id").GetInt64());
        Assert.Equal("4h", first.GetProperty("timeframe").GetString());
        Assert.Equal(128, first.GetProperty("trialCount").GetInt32());
        Assert.Equal("rule-discovery-oos-v2", first.GetProperty("methodVersion").GetString());
        Assert.True(first.TryGetProperty("createdAtUtc", out _));
        Assert.True(first.TryGetProperty("evaluationStartTimeMs", out _));
        // Run ledger rows are metadata only — trials must not be embedded.
        Assert.False(first.TryGetProperty("trials", out _));

        var filtered1h = Payload((await controller.GetDiscoveryRuns("BTCUSDT", "1h")).Result);
        Assert.Single(filtered1h.RootElement.EnumerateArray());
        Assert.Equal(otherTf.Id, filtered1h.RootElement[0].GetProperty("id").GetInt64());

        var unfiltered = Payload((await controller.GetDiscoveryRuns("BTCUSDT")).Result);
        Assert.Equal(3, unfiltered.RootElement.GetArrayLength());
        Assert.Equal(otherTf.Id, unfiltered.RootElement[0].GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task GetDiscoveryRuns_EmptyTable_ReturnsEmptyArray()
    {
        await using var db = Db();
        var controller = Controller(db);

        var payload = Payload((await controller.GetDiscoveryRuns("BTCUSDT", "4h")).Result);

        Assert.Equal(JsonValueKind.Array, payload.RootElement.ValueKind);
        Assert.Empty(payload.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task GetDiscoveryRuns_TakeIsClamped()
    {
        await using var db = Db();
        for (var i = 0; i < 205; i++)
            db.RuleDiscoveryRuns.Add(Run("BTCUSDT", "4h", createdAtUtc: DateTime.UtcNow.AddMinutes(-i)));
        await db.SaveChangesAsync();
        var controller = Controller(db);

        Assert.Equal(2, Payload((await controller.GetDiscoveryRuns("BTCUSDT", "4h", take: 2)).Result).RootElement.GetArrayLength());
        Assert.Single(Payload((await controller.GetDiscoveryRuns("BTCUSDT", "4h", take: 0)).Result).RootElement.EnumerateArray());
        Assert.Single(Payload((await controller.GetDiscoveryRuns("BTCUSDT", "4h", take: -5)).Result).RootElement.EnumerateArray());
        Assert.Equal(200, Payload((await controller.GetDiscoveryRuns("BTCUSDT", "4h", take: 500)).Result).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task GetDiscoveryRuns_CanonicalizesSymbolAndTimeframe()
    {
        await using var db = Db();
        var run = Run("BTCUSDT", "4h");
        db.RuleDiscoveryRuns.Add(run);
        await db.SaveChangesAsync();
        var controller = Controller(db);

        var lowerCase = Payload((await controller.GetDiscoveryRuns("btcusdt", "4H")).Result);
        Assert.Single(lowerCase.RootElement.EnumerateArray());
        Assert.Equal(run.Id, lowerCase.RootElement[0].GetProperty("id").GetInt64());

        // "BTC" is an accepted alias of the canonical production symbol.
        var alias = Payload((await controller.GetDiscoveryRuns("BTC", "4h")).Result);
        Assert.Single(alias.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task GetDiscoveryRuns_InactiveSymbol_Returns400Envelope()
    {
        await using var db = Db();
        var controller = Controller(db);

        var result = (await controller.GetDiscoveryRuns("ETHUSDT", "4h")).Result;

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ApiErrorEnvelope>(bad.Value);
        Assert.Equal("UNSUPPORTED_SYMBOL", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task GetDiscoveryRuns_InactiveTimeframe_Returns400Envelope()
    {
        await using var db = Db();
        var controller = Controller(db);

        var result = (await controller.GetDiscoveryRuns("BTCUSDT", "15m")).Result;

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<ApiErrorEnvelope>(bad.Value);
        Assert.Equal("INACTIVE_TIMEFRAME", error.Code);
        Assert.False(error.Retryable);
    }

    private static RuleDiscoveryController Controller(AppDbContext db) =>
        new(
            new FakeBinanceKlinesService(),
            db,
            new CandleVolumeIndexer(db, NullLogger<CandleVolumeIndexer>.Instance, Microsoft.Extensions.Options.Options.Create(new IndexingOptions())),
            NullLogger<RuleDiscoveryController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static RuleDiscoveryRun Run(
        string symbol,
        string timeframe,
        int trialCount = 128,
        DateTime? createdAtUtc = null) => new()
        {
            MethodVersion = "rule-discovery-oos-v2",
            Symbol = symbol,
            Timeframe = timeframe,
            FutureBars = 3,
            CandidateBudget = 128,
            TrialCount = trialCount,
            LabelDeadZonePct = 0.30,
            RoundTripCostBps = 30,
            SelectionStartTimeMs = 1_000_000,
            SelectionEndTimeMs = 2_000_000,
            EvaluationStartTimeMs = 2_000_001,
            EvaluationEndTimeMs = 3_000_000,
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };

    private static JsonDocument Payload(IActionResult? result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonDocument.Parse(JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static AppDbContext Db()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }
}
