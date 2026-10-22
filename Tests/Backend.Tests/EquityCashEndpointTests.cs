using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Backend.Tests;

/// <summary>
/// EQX v2 A3 over HTTP: PUT /api/equities/cash (AdminGuard), additive position DTO fields, budget error
/// codes on both route families (/api/equities/{symbol}/position and the /api/mbb alias), and admin
/// corporate-action import carrying shareRatio/verificationStatus into the public context.
/// </summary>
[Collection(EquityLaneLockCollection.Name)]
public sealed class EquityCashEndpointTests
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

    [Fact]
    public async Task PutCash_IsAdminGuarded_AndPositionsShareOnePool()
    {
        using var factory = new LlmLaneFactory();
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PutAsJsonAsync("/api/equities/cash", new { availableCashVnd = 1m })).StatusCode);

        var admin = Admin(factory);
        var cash = await JsonAsync(await admin.PutAsJsonAsync("/api/equities/cash", new { availableCashVnd = 10_000_000m }));
        Assert.Equal("default", cash.GetProperty("accountId").GetString());
        Assert.Equal(10_000_000m, cash.GetProperty("availableCashVnd").GetDecimal());
        Assert.False(cash.GetProperty("budgetsExceedCash").GetBoolean());
        await AssertErrorAsync(await admin.PutAsJsonAsync("/api/equities/cash", new { availableCashVnd = -5m }),
            HttpStatusCode.BadRequest, "INVALID_CASH_INPUT");

        var mbb = await admin.PutAsJsonAsync("/api/equities/MBB/position", new { lots = Array.Empty<object>(), buyBudgetVnd = 7_000_000m });
        Assert.Equal(HttpStatusCode.OK, mbb.StatusCode);
        var mbbBody = await JsonAsync(mbb);
        Assert.Equal(10_000_000m, mbbBody.GetProperty("sharedCashVnd").GetDecimal());
        Assert.Equal(7_000_000m, mbbBody.GetProperty("buyBudgetVnd").GetDecimal());
        Assert.Equal("shared-pool", mbbBody.GetProperty("cashSource").GetString());
        Assert.Equal(10_000_000m, mbbBody.GetProperty("availableCashVnd").GetDecimal()); // legacy mirror of the pool

        await AssertErrorAsync(
            await admin.PutAsJsonAsync("/api/equities/BID/position", new { lots = Array.Empty<object>(), buyBudgetVnd = 4_000_000m }),
            HttpStatusCode.BadRequest, "BUDGET_EXCEEDS_CASH");

        // /api/mbb alias (legacy body with availableCashVnd) writes the SAME shared pool and says so.
        var alias = await JsonAsync(await admin.PutAsJsonAsync("/api/mbb/position",
            new { lots = Array.Empty<object>(), availableCashVnd = 12_000_000m, buyBudgetVnd = 7_000_000m }));
        Assert.Equal(12_000_000m, alias.GetProperty("sharedCashVnd").GetDecimal());
        Assert.Contains(alias.GetProperty("cashNotes").EnumerateArray(),
            n => n.GetString() == MbbDecisionService.LegacyCashWriteNote);
        var bid = await JsonAsync(await admin.GetAsync("/api/equities/BID/position"));
        Assert.Equal(12_000_000m, bid.GetProperty("sharedCashVnd").GetDecimal());
        Assert.Equal(JsonValueKind.Null, bid.GetProperty("buyBudgetVnd").ValueKind);
        Assert.Equal(7_000_000m, bid.GetProperty("totalDeclaredBudgetsVnd").GetDecimal());

        await AssertErrorAsync(
            await admin.PutAsJsonAsync("/api/mbb/position", new { lots = Array.Empty<object>(), buyBudgetVnd = 20_000_000m }),
            HttpStatusCode.BadRequest, "BUDGET_EXCEEDS_CASH");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(12_000_000m, (await db.EquityCashAccounts.SingleAsync()).AvailableCashVnd);
        }

        // availableCashVnd: null resets the pool to unknown.
        var cleared = await JsonAsync(await admin.PutAsJsonAsync("/api/equities/cash", new { availableCashVnd = (decimal?)null }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("availableCashVnd").ValueKind);
        var mbbAfter = await JsonAsync(await admin.GetAsync("/api/equities/MBB/position"));
        Assert.Equal("unknown", mbbAfter.GetProperty("cashSource").GetString());
    }

    [Fact]
    public async Task PutPosition_BudgetWithoutPool_IsCashUnknown()
    {
        using var factory = new LlmLaneFactory();
        await AssertErrorAsync(
            await Admin(factory).PutAsJsonAsync("/api/equities/MBB/position", new { lots = Array.Empty<object>(), buyBudgetVnd = 1_000_000m }),
            HttpStatusCode.BadRequest, "CASH_UNKNOWN");
        var pos = await JsonAsync(await Admin(factory).GetAsync("/api/equities/MBB/position"));
        Assert.Equal("unknown", pos.GetProperty("cashSource").GetString());
        Assert.Equal(JsonValueKind.Null, pos.GetProperty("sharedCashVnd").ValueKind);
    }

    [Fact]
    public async Task AdminCorporateActionImport_AcceptsShareRatio_AndContextExposesIt()
    {
        using var factory = new LlmLaneFactory();
        var admin = Admin(factory);
        var ok = await admin.PostAsJsonAsync("/api/equities/BID/admin/corporate-actions", new[]
        {
            new { exDate = "2026-06-10", actionType = "stock-dividend", ratioText = "100:15", shareRatio = 0.15m,
                  sourceUrl = "https://example.com/bid-ca", verificationStatus = "verified" },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        await AssertErrorAsync(await admin.PostAsJsonAsync("/api/equities/BID/admin/corporate-actions", new[]
        {
            new { exDate = "2026-06-11", actionType = "cash-dividend", shareRatio = 0.1m, sourceUrl = "https://example.com/x" },
        }), HttpStatusCode.BadRequest, "INVALID_CONTEXT_INPUT");

        // ShareRatio is EXTRA shares per old share; > 10 is rejected as implausible (e.g. a 1:20 typed as 20).
        await AssertErrorAsync(await admin.PostAsJsonAsync("/api/equities/BID/admin/corporate-actions", new[]
        {
            new { exDate = "2026-06-12", actionType = "split", shareRatio = 20m, sourceUrl = "https://example.com/x", verificationStatus = "verified" },
        }), HttpStatusCode.BadRequest, "INVALID_CONTEXT_INPUT");

        var context = await JsonAsync(await factory.CreateClient().GetAsync("/api/equities/BID/context"));
        var item = context.GetProperty("corporateActions").GetProperty("items").EnumerateArray().Single();
        Assert.Equal(0.15m, item.GetProperty("shareRatio").GetDecimal());
        Assert.Equal("verified", item.GetProperty("verificationStatus").GetString());
    }
}
