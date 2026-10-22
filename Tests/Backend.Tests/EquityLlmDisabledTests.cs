using System.Net;
using System.Text.Json;
using Backend.Data;
using Backend.Options;
using Backend.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Backend.Tests;

/// <summary>Counts every request the equity LLM named HttpClient would send to the AI service.</summary>
public sealed class CountingAiHandler : HttpMessageHandler
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}

/// <summary>Test host with its own InMemory DB, a counting AI handler and an optional EquityLlm:Enabled override.</summary>
public class LlmLaneFactory : TestWebApplicationFactory
{
    public CountingAiHandler AiHandler { get; } = new();
    protected virtual bool? LlmEnabled => null;
    private readonly string _dbName = "LlmLane-" + Guid.NewGuid().ToString("N");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (LlmEnabled is { } enabled)
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EquityLlm:Enabled"] = enabled ? "true" : "false",
            }));
        builder.ConfigureServices(services =>
        {
            var db = services.Single(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            services.Remove(db);
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));
            services.AddHttpClient(EquityLlmOptions.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => AiHandler);
        });
    }
}

public sealed class LlmEnabledFactory : LlmLaneFactory
{
    protected override bool? LlmEnabled => true;
}

/// <summary>
/// EQX v2 B: the in-app LLM lane is retired. Default config ⇒ POST /llm-runs is 503 LLM_DISABLED before
/// scope resolution, the lane lock, the dossier build or any AI call, and nothing is persisted; history
/// stays readable. EquityLlm:Enabled=true keeps the legacy guard order (scope → lock) for old behaviour.
/// </summary>
[Collection(EquityLaneLockCollection.Name)]
public sealed class EquityLlmDisabledTests
{
    private static HttpClient Admin(LlmLaneFactory f)
    {
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);
        return client;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString());
    }

    private static EquityLlmRun HistoricRun(string symbol) => new()
    {
        Id = Guid.NewGuid(),
        Symbol = symbol,
        SignalBarOpenTimeMs = 1_790_000_000_000L,
        CreatedAtUtc = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
        Status = "failed",
        Reason = "ai-request-timeout",
        PromptVersion = "equity-llm-prompt-v1",
        SchemaVersion = "equity-llm-judge-v1",
        InputSnapshotSha256 = new string('a', 64),
        InputSnapshotJson = "{}",
        DurationMs = 1234,
    };

    [Fact]
    public async Task Default_PostLlmRun_Is503Disabled_NoAiCall_NoLock_NoRow()
    {
        using var factory = new LlmLaneFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EquityLlmRuns.Add(HistoricRun("BID"));
            await db.SaveChangesAsync();
        }
        var admin = Admin(factory);

        foreach (var sym in new[] { "BID", "MBB", "VCB", "NOPE" })
            await AssertErrorAsync(await admin.PostAsync($"/api/equities/{sym}/llm-runs", null),
                HttpStatusCode.ServiceUnavailable, "LLM_DISABLED");

        // Disabled check runs before the lane lock: holding the lock does not turn it into LANE_BUSY,
        // and the POST never leaves a lease behind.
        using (var lease = EquityLaneLocks.TryAcquire("BID"))
        {
            Assert.NotNull(lease);
            await AssertErrorAsync(await admin.PostAsync("/api/equities/BID/llm-runs", null),
                HttpStatusCode.ServiceUnavailable, "LLM_DISABLED");
        }
        using (var free = EquityLaneLocks.TryAcquire("BID")) Assert.NotNull(free);

        Assert.Equal(0, factory.AiHandler.Calls);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.EquityLlmRuns.CountAsync());
            Assert.Equal(0, await db.EquityForecastRecords.CountAsync());
        }

        // History stays readable (public GET).
        var list = await JsonAsync(await factory.CreateClient().GetAsync("/api/equities/BID/llm-runs"));
        var items = list.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("failed", items[0].GetProperty("status").GetString());
        Assert.Equal("ai-request-timeout", items[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Default_PostLlmRun_StillRequiresAdmin()
    {
        using var factory = new LlmLaneFactory();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostAsync("/api/equities/BID/llm-runs", null)).StatusCode);
        Assert.Equal(0, factory.AiHandler.Calls);
    }

    [Fact]
    public async Task Enabled_LegacyGuards_ScopeThenLock()
    {
        using var factory = new LlmEnabledFactory();
        var admin = Admin(factory);
        await AssertErrorAsync(await admin.PostAsync("/api/equities/VCB/llm-runs", null), HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertErrorAsync(await admin.PostAsync("/api/equities/NOPE/llm-runs", null), HttpStatusCode.NotFound, "UNKNOWN_SYMBOL");
        using (var lease = EquityLaneLocks.TryAcquire("BID"))
        {
            Assert.NotNull(lease);
            await AssertErrorAsync(await admin.PostAsync("/api/equities/BID/llm-runs", null), HttpStatusCode.Conflict, "LANE_BUSY");
        }
        Assert.Equal(0, factory.AiHandler.Calls);
    }
}
