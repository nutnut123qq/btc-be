using System.Net;
using System.Reflection;
using System.Text.Json;
using Backend.Data;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

/// <summary>
/// Seeds shared by the EQTECH-1 lane S tests (EF InMemory, explicit calendar, fixed clock). Only types that
/// already exist on the base commit are used here, so the HTTP tests compile there and fail with 404.
/// </summary>
internal static class DirectionScoreFixture
{
    /// <summary>Explicit HOSE calendar: weekends + 2025-09-01/02 (National Day); unbounded coverage.</summary>
    public static readonly VietnamMarketCalendar Cal = new([new DateOnly(2025, 9, 1), new DateOnly(2025, 9, 2)]);

    public static readonly DateTimeOffset Now = Local(2026, 1, 5, 10);
    private static readonly DateTime Seeded = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static DateTimeOffset Local(int y, int m, int d, int hh, int mm = 0) => new(y, m, d, hh, mm, 0, TimeSpan.FromHours(7));

    public static long LocalMs(DateOnly day, int hh, int mm = 0) => VietnamMarketCalendar.ToUtcMs(day, new TimeOnly(hh, mm));

    public static long Open(DateOnly day) => VietnamMarketCalendar.DailyOpenUtcMs(day);

    public static List<DateOnly> TradingDays(VietnamMarketCalendar cal, DateOnly from, int count)
    {
        var days = new List<DateOnly>();
        for (var d = from; days.Count < count; d = d.AddDays(1))
            if (cal.IsTradingDay(d)) days.Add(d);
        return days;
    }

    /// <summary>One adjusted 1d bar per day (OHLC = close), except the indices in <paramref name="skip"/>.</summary>
    public static void SeedBars(AppDbContext db, string symbol, VietnamMarketCalendar cal, IReadOnlyList<DateOnly> days,
        Func<int, decimal> close, params int[] skip)
    {
        for (var i = 0; i < days.Count; i++)
        {
            if (skip.Contains(i)) continue;
            var open = Open(days[i]);
            var px = close(i);
            db.InstrumentCandles.Add(new InstrumentCandle
            {
                Symbol = symbol, Timeframe = "1d", OpenTimeMs = open, CloseTimeMs = cal.SessionCloseUtcMs("1d", open),
                Open = px, High = px, Low = px, Close = px, Volume = 1_000, Provider = "VCI", PriceBasis = "adjusted",
                Venue = "HOSE", SourceTimezone = "Asia/Ho_Chi_Minh", FetchedAtUtc = Seeded, UpdatedAtUtc = Seeded,
            });
        }
    }

    /// <summary>A note as the writer stores it; by default available at 15:00 local of the signal day (in window).</summary>
    public static EquityResearchNote Note(string symbol, DateOnly signalDay, int horizon, string direction, string tag,
        string mode = "forward", long? availableMs = null, DateTime? createdUtc = null)
    {
        var open = Open(signalDay);
        var available = availableMs ?? LocalMs(signalDay, 15);
        var availableUtc = DateTimeOffset.FromUnixTimeMilliseconds(available).UtcDateTime;
        return new EquityResearchNote
        {
            Id = Guid.NewGuid(),
            NoteId = $"session/{symbol}/{open}/{tag}",
            Symbol = symbol,
            Mode = mode,
            RequestedAsOfUtc = availableUtc,
            SignalBarOpenTimeMs = open,
            SignalBarCloseTimeMs = VietnamMarketCalendar.ToUtcMs(signalDay, VietnamMarketCalendar.AfternoonCloseLocal),
            HorizonSessions = horizon,
            SnapshotId = $"snapshot-{tag}",
            SnapshotSha256 = new string('a', 64),
            SkillVersion = "test-skill",
            MethodVersion = "test-method",
            Direction = direction,
            ActionView = "none",
            ActionBasis = "market-only",
            BodyJson = "{}",
            AvailableTimeMs = available,
            CreatedAtUtc = createdUtc ?? availableUtc,
        };
    }

    // --- main fixture (H = 20) ----------------------------------------------------------------------------
    // 100 trading days from 2025-06-02; close = 110 on rows 10..29, else 100. Over 20 stored rows that gives
    // R_t = +10% (up) for t in 0..9, -1/11 (down) for t in 10..29 and 0 (sideways) for t >= 30.

    public static readonly IReadOnlyList<DateOnly> MainDays = TradingDays(Cal, new DateOnly(2025, 6, 2), 100);

    public static decimal MainClose(int i) => i is >= 10 and <= 29 ? 110m : 100m;

    public static readonly (int Row, string Direction, string Tag)[] MainNotes =
    [
        (5, "up", "n05"), (25, "up", "n25"), (39, "sideways", "n39"), (40, "down", "n40"),
        (69, "sideways", "n69"), (79, "no-view", "n79"),
    ];

    public static void SeedMain(AppDbContext db, string symbol = "MBB")
    {
        SeedBars(db, symbol, Cal, MainDays, MainClose);
        foreach (var (row, direction, tag) in MainNotes)
            db.EquityResearchNotes.Add(Note(symbol, MainDays[row], 20, direction, tag));
        db.SaveChanges();
    }
}

/// <summary>
/// HTTP surface of GET /api/equities/{symbol}/research-notes/direction-scores: AdminGuard, equity scope, take
/// clamp, contract fields and pinned numbers, and no database writes. Compiles on base (fails there with 404).
/// </summary>
public class EquityDirectionScoringHttpTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private sealed class Factory(string dbName) : TestWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>)).ToList())
                    services.Remove(d);
                services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
                services.AddSingleton<TimeProvider>(new FixedTime(DirectionScoreFixture.Now));
                services.AddSingleton(DirectionScoreFixture.Cal);
            });
        }
    }

    private const string MbbUrl = "/api/equities/MBB/research-notes/direction-scores";

    private static HttpClient Admin(Factory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Key", TestWebApplicationFactory.AdminApiKey);
        return client;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static int CountOf<T>(AppDbContext db) where T : class => db.Set<T>().Count();

    /// <summary>Row count of every keyed entity type, read through a fresh scope.</summary>
    private static Dictionary<string, int> CountAllRows(Factory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var countOf = typeof(EquityDirectionScoringHttpTests).GetMethod(nameof(CountOf), BindingFlags.NonPublic | BindingFlags.Static)!;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in db.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.FindPrimaryKey() is not null && !t.HasSharedClrType))
            counts[type.Name] = (int)countOf.MakeGenericMethod(type.ClrType).Invoke(null, [db])!;
        return counts;
    }

    [Fact]
    public async Task Http_RequiresAdminKey_ResolvesScope_AndServesThePinnedScores()
    {
        await using var factory = new Factory("eqtech1-s-http-main");
        using (var scope = factory.Services.CreateScope())
            DirectionScoreFixture.SeedMain(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync(MbbUrl)).StatusCode);
        var wrong = factory.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Admin-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync(MbbUrl)).StatusCode);

        var admin = Admin(factory);
        var response = await admin.GetAsync(MbbUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await Json(response);
        Assert.Equal("equity-direction-scores-v1", root.GetProperty("contractVersion").GetString());
        Assert.Equal("direction-score-v1", root.GetProperty("ruleVersion").GetString());
        Assert.Equal("MBB", root.GetProperty("symbol").GetString());
        Assert.Contains("ties broken sideways > up > down", root.GetProperty("rule").GetString());
        Assert.Equal(6, root.GetProperty("totalNotes").GetInt32());

        var horizons = root.GetProperty("horizons");
        Assert.Equal(new[] { 20, 60 }, horizons.EnumerateArray().Select(h => h.GetProperty("horizonSessions").GetInt32()));
        var h20 = horizons[0];
        Assert.Equal("insufficient-sample", h20.GetProperty("status").GetString());
        Assert.Equal((5, 3, 3, 30), (h20.GetProperty("resolved").GetInt32(), h20.GetProperty("hits").GetInt32(),
            h20.GetProperty("nEffective").GetInt32(), h20.GetProperty("minEffectiveForScoring").GetInt32()));
        Assert.Equal(0.6, h20.GetProperty("hitRate").GetDouble(), 12);
        Assert.Equal(5.0 / 6, h20.GetProperty("coverage").GetDouble(), 12);
        Assert.Equal(1, h20.GetProperty("baselinePrior").GetProperty("hits").GetInt32());
        Assert.Equal("sideways", h20.GetProperty("bestConstantInHindsight").GetProperty("label").GetString());
        Assert.Equal(3, h20.GetProperty("bestConstantInHindsight").GetProperty("hits").GetInt32());
        Assert.Equal("no-data", horizons[1].GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, horizons[1].GetProperty("hitRate").ValueKind);

        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "n79", "n69", "n40", "n39", "n25", "n05" },
            items.Select(i => i.GetProperty("noteId").GetString()!.Split('/')[^1]));
        Assert.Equal("no-view", items[0].GetProperty("status").GetString());
        var n25 = items[4];
        Assert.Equal(("resolved", "down", "up", false), (n25.GetProperty("status").GetString()!, n25.GetProperty("realized").GetString()!,
            n25.GetProperty("baselineCall").GetString()!, n25.GetProperty("hit").GetBoolean()));
        Assert.Equal(-1.0 / 11, n25.GetProperty("returnPct").GetDouble(), 12);
        Assert.Equal((110m, 100m), (n25.GetProperty("signalClose").GetDecimal(), n25.GetProperty("targetClose").GetDecimal()));

        async Task AssertError(string url, HttpStatusCode status, string code)
        {
            var r = await admin.GetAsync(url);
            Assert.Equal(status, r.StatusCode);
            Assert.Equal(code, (await Json(r)).GetProperty("code").GetString());
        }
        await AssertError("/api/equities/VCB/research-notes/direction-scores", HttpStatusCode.BadRequest, "UNSUPPORTED_CAPABILITY");
        await AssertError("/api/equities/NOPE/research-notes/direction-scores", HttpStatusCode.NotFound, "UNKNOWN_SYMBOL");
    }

    [Fact]
    public async Task Http_TakeClampsItemsOnly_AndTheGetWritesNothing()
    {
        await using var factory = new Factory("eqtech1-s-http-take");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            DirectionScoreFixture.SeedMain(db);
            var days = DirectionScoreFixture.TradingDays(DirectionScoreFixture.Cal, new DateOnly(2023, 1, 2), 501);
            for (var i = 0; i < days.Count; i++)
                db.EquityResearchNotes.Add(DirectionScoreFixture.Note("BID", days[i], 20, "up", $"r{i:000}", mode: "retrospective"));
            db.SaveChanges();
        }
        var before = CountAllRows(factory);
        Assert.Equal(100, before[typeof(InstrumentCandle).FullName!]);
        Assert.Equal(507, before[typeof(EquityResearchNote).FullName!]);

        var admin = Admin(factory);
        async Task<(int Items, int Total, int Retrospective)> Get(string query)
        {
            var response = await admin.GetAsync("/api/equities/BID/research-notes/direction-scores" + query);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var root = await Json(response);
            return (root.GetProperty("items").GetArrayLength(), root.GetProperty("totalNotes").GetInt32(),
                root.GetProperty("retrospectiveNotes").GetInt32());
        }
        Assert.Equal((200, 501, 501), await Get(""));
        Assert.Equal((1, 501, 501), await Get("?take=0"));
        Assert.Equal((1, 501, 501), await Get("?take=-3"));
        Assert.Equal((500, 501, 501), await Get("?take=500"));
        Assert.Equal((500, 501, 501), await Get("?take=9999"));
        var bidItem = (await Json(await admin.GetAsync("/api/equities/BID/research-notes/direction-scores?take=1")))
            .GetProperty("items")[0];
        Assert.Equal(("retrospective-not-scored", "r500"),
            (bidItem.GetProperty("status").GetString()!, bidItem.GetProperty("noteId").GetString()!.Split('/')[^1]));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(MbbUrl)).StatusCode);

        Assert.Equal(before, CountAllRows(factory));
    }
}

/// <summary>
/// Rule direction-score-v1 at service level (EF InMemory, explicit calendar, fixed clock): exact R, class, hit,
/// baselines, earliest-per-bar, window, retrospective, undeclared horizon, pending reasons, the nEffective = 30
/// flip and read-only behaviour.
/// </summary>
public class EquityDirectionScoringTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static AppDbContext NewDb(string name) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name).Options);

    private static EquityDirectionScoringService Service(AppDbContext db, VietnamMarketCalendar? cal = null, DateTimeOffset? now = null) =>
        new(db, cal ?? DirectionScoreFixture.Cal, new FixedTime(now ?? DirectionScoreFixture.Now));

    private static EquityDirectionScoreItemDto Item(EquityDirectionScoresResponse r, string tag) =>
        r.Items.Single(i => i.NoteId.EndsWith("/" + tag, StringComparison.Ordinal));

    private static EquityDirectionHorizonScoreDto H(EquityDirectionScoresResponse r, int horizon) =>
        r.Horizons.Single(h => h.HorizonSessions == horizon);

    private static long Open(DateOnly day) => DirectionScoreFixture.Open(day);

    [Fact]
    public void MostFrequent_BreaksTiesSidewaysThenUpThenDown()
    {
        // (up, down, sideways)
        Assert.Equal("sideways", EquityDirectionScoringService.MostFrequent(0, 0, 0));
        Assert.Equal("sideways", EquityDirectionScoringService.MostFrequent(2, 2, 2));
        Assert.Equal("up", EquityDirectionScoringService.MostFrequent(3, 3, 1));
        Assert.Equal("sideways", EquityDirectionScoringService.MostFrequent(4, 1, 4));
        Assert.Equal("sideways", EquityDirectionScoringService.MostFrequent(1, 3, 3));
        Assert.Equal("down", EquityDirectionScoringService.MostFrequent(1, 4, 3));
    }

    [Fact]
    public async Task MainFixture_PinsReturnClassHitAndBothBaselines()
    {
        using var db = NewDb(nameof(MainFixture_PinsReturnClassHitAndBothBaselines));
        DirectionScoreFixture.SeedMain(db);
        var r = await Service(db).GetAsync("MBB");

        Assert.Equal(("equity-direction-scores-v1", "direction-score-v1", "MBB"), (r.ContractVersion, r.RuleVersion, r.Symbol));
        Assert.Equal(EquityDirectionScoringService.RuleVersion, r.RuleVersion);
        Assert.Equal(EquityDirectionScoringService.Rule, r.Rule);
        Assert.Contains("H=20 (tb-p5-s4-h20): up if R >= +5%, down if R <= -4%", r.Rule);
        Assert.Contains("H=60 (tb-p10-s6-h60): up if R >= +10%, down if R <= -6%", r.Rule);
        Assert.Contains("ties broken sideways > up > down", r.Rule);
        Assert.Equal(DirectionScoreFixture.Now.UtcDateTime, r.GeneratedAtUtc);
        Assert.Null(r.CalendarCoverageTo); // explicit test calendar: unbounded
        Assert.Equal((6, 0, 0), (r.TotalNotes, r.RetrospectiveNotes, r.NoDeclaredThresholdNotes));

        var days = DirectionScoreFixture.MainDays;
        void AssertItem(string tag, int row, string status, decimal signalClose, decimal targetClose, double ret,
            string? realized, string? baseline, bool? hit)
        {
            var it = Item(r, tag);
            Assert.Equal((status, (string?)null), (it.Status, it.PendingReason));
            Assert.Equal((signalClose, targetClose), (it.SignalClose!.Value, it.TargetClose!.Value));
            Assert.Equal(Open(days[row + 20]), it.TargetBarOpenTimeMs);
            Assert.Equal(ret, it.ReturnPct!.Value, 12);
            Assert.Equal((realized, baseline, hit), (it.Realized, it.BaselineCall, it.Hit));
        }
        AssertItem("n05", 5, "resolved", 100m, 110m, 0.10, "up", "sideways", true);           // no prior history ⇒ sideways
        AssertItem("n25", 25, "resolved", 110m, 100m, -1.0 / 11, "down", "up", false);       // prior rows 0..5: 6 × up
        AssertItem("n39", 39, "resolved", 100m, 100m, 0.0, "sideways", "up", true);           // rows 0..19: up 10 = down 10 ⇒ up
        AssertItem("n40", 40, "resolved", 100m, 100m, 0.0, "sideways", "down", false);        // rows 0..20: down 11 > up 10
        AssertItem("n69", 69, "resolved", 100m, 100m, 0.0, "sideways", "sideways", true);     // rows 0..49: down 20 = sideways 20
        AssertItem("n79", 79, "no-view", 100m, 100m, 0.0, "sideways", null, null);            // coverage only

        Assert.Equal(new[] { "n79", "n69", "n40", "n39", "n25", "n05" }, r.Items.Select(i => i.NoteId.Split('/')[^1]));

        var h20 = H(r, 20);
        Assert.Equal(("tb-p5-s4-h20", 0.05, -0.04, "insufficient-sample"),
            (h20.ConfigId, h20.UpThresholdPct, h20.DownThresholdPct, h20.Status));
        Assert.Equal((6, 0, 0, 6, 0, 6), (h20.ForwardNotes, h20.OutOfWindow, h20.WindowUndetermined, h20.Eligible, h20.Superseded, h20.Scored));
        Assert.Equal((1, 5, 5, 0), (h20.NoView, h20.Directional, h20.Resolved, h20.Pending));
        Assert.Equal(new EquityDirectionPendingCounts(0, 0, 0, 0), h20.PendingReasons);
        Assert.Equal(3, h20.Hits);
        Assert.Equal(0.6, h20.HitRate!.Value, 12);
        Assert.Equal(5.0 / 6, h20.Coverage!.Value, 12);
        Assert.Equal((3, 30), (h20.NEffective, h20.MinEffectiveForScoring)); // rows 5, 25, 69 (39 and 40 overlap 25)
        Assert.Equal(1, h20.BaselinePrior.Hits);
        Assert.Equal(0.2, h20.BaselinePrior.HitRate!.Value, 12);
        Assert.Equal(("sideways", 3), (h20.BestConstantInHindsight.Label!, h20.BestConstantInHindsight.Hits));
        Assert.Equal(0.6, h20.BestConstantInHindsight.HitRate!.Value, 12);

        var h60 = H(r, 60);
        Assert.Equal(("tb-p10-s6-h60", 0.10, -0.06, "no-data"), (h60.ConfigId, h60.UpThresholdPct, h60.DownThresholdPct, h60.Status));
        Assert.Equal((0, 0, 0), (h60.ForwardNotes, h60.Scored, h60.NEffective));
        Assert.Null(h60.HitRate);
        Assert.Null(h60.Coverage);
        Assert.Equal(new EquityDirectionPriorBaselineDto(0, null), h60.BaselinePrior);
        Assert.Equal(new EquityDirectionConstantBaselineDto(null, 0, null), h60.BestConstantInHindsight);
    }

    [Fact]
    public async Task Thresholds_AreInclusive_AndH60UsesItsOwnConfigAndSixtyCalendarSessions()
    {
        using var db = NewDb(nameof(Thresholds_AreInclusive_AndH60UsesItsOwnConfigAndSixtyCalendarSessions));
        var days = DirectionScoreFixture.TradingDays(DirectionScoreFixture.Cal, new DateOnly(2025, 8, 4), 70);
        var overrides = new Dictionary<int, decimal>
        {
            [20] = 21_000m, [21] = 19_200m, [22] = 20_999m, [23] = 19_201m,
            [64] = 22_000m, [65] = 18_800m, [66] = 21_000m, [67] = 19_000m,
        };
        DirectionScoreFixture.SeedBars(db, "BID", DirectionScoreFixture.Cal, days, i => overrides.GetValueOrDefault(i, 20_000m));
        for (var s = 0; s < 8; s++)
            db.EquityResearchNotes.Add(DirectionScoreFixture.Note("BID", days[s], s < 4 ? 20 : 60, "up", $"b{s}"));
        await db.SaveChangesAsync();

        var r = await Service(db).GetAsync("BID");
        void AssertItem(string tag, double ret, string realized)
        {
            var it = Item(r, tag);
            Assert.Equal("resolved", it.Status);
            Assert.Equal(ret, it.ReturnPct!.Value, 12);
            Assert.Equal((realized, realized == "up"), (it.Realized!, it.Hit!.Value));
        }
        AssertItem("b0", 0.05, "up");          // exactly +5% ⇒ up
        AssertItem("b1", -0.04, "down");       // exactly -4% ⇒ down
        AssertItem("b2", 0.04995, "sideways");
        AssertItem("b3", -0.03995, "sideways");
        AssertItem("b4", 0.10, "up");          // H=60: exactly +10% ⇒ up
        AssertItem("b5", -0.06, "down");       // H=60: exactly -6% ⇒ down
        AssertItem("b6", 0.05, "sideways");    // +5% is up at H=20 but sideways at H=60
        AssertItem("b7", -0.05, "sideways");   // -5% is down at H=20 but sideways at H=60

        // Calendar sessions across the 2025-09-01/02 holidays: 2025-08-04 + 20 = 2025-09-03, 2025-08-08 + 60 = 2025-11-04.
        Assert.Equal(Open(new DateOnly(2025, 9, 3)), Item(r, "b0").TargetBarOpenTimeMs);
        Assert.Equal(Open(new DateOnly(2025, 11, 4)), Item(r, "b4").TargetBarOpenTimeMs);
        Assert.Equal((4, 1), (H(r, 20).Resolved, H(r, 20).Hits));
        Assert.Equal((4, 1), (H(r, 60).Resolved, H(r, 60).Hits));
    }

    [Fact]
    public async Task Target_IsTheCalendarSession_NotTheNthStoredRow_AndAMissingTargetIsNeverShifted()
    {
        using var db = NewDb(nameof(Target_IsTheCalendarSession_NotTheNthStoredRow_AndAMissingTargetIsNeverShifted));
        var days = DirectionScoreFixture.TradingDays(DirectionScoreFixture.Cal, new DateOnly(2025, 6, 2), 60);
        // Rows 15 and 45 are not stored. Row 30 (target of p) and row 46 (the bar after q's missing target) close at 106.
        DirectionScoreFixture.SeedBars(db, "MBB", DirectionScoreFixture.Cal, days, i => i is 30 or 46 ? 106m : 100m, 15, 45);
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", days[10], 20, "up", "p"));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", days[25], 20, "up", "q"));
        await db.SaveChangesAsync();

        var r = await Service(db).GetAsync("MBB");
        var p = Item(r, "p"); // the 20th stored row after days[10] is days[31] (close 100 ⇒ sideways)
        Assert.Equal(("resolved", Open(days[30]), 106m, "up", true), (p.Status, p.TargetBarOpenTimeMs!.Value, p.TargetClose!.Value, p.Realized!, p.Hit!.Value));
        Assert.Equal(0.06, p.ReturnPct!.Value, 12);

        var q = Item(r, "q");
        Assert.Equal(("pending", "target-bar-missing", Open(days[45])), (q.Status, q.PendingReason!, q.TargetBarOpenTimeMs!.Value));
        Assert.Equal(100m, q.SignalClose);
        Assert.Null(q.TargetClose);
        Assert.Null(q.ReturnPct);
        Assert.Null(q.Realized);
        Assert.Null(q.Hit);
        Assert.Null(q.BaselineCall);

        var h20 = H(r, 20);
        Assert.Equal((2, 1, 1, 1), (h20.Directional, h20.Resolved, h20.Pending, h20.Hits));
        Assert.Equal(new EquityDirectionPendingCounts(0, 1, 0, 0), h20.PendingReasons);
    }

    [Fact]
    public async Task OnlyTheEarliestInWindowForwardNotePerBarIsScored()
    {
        using var db = NewDb(nameof(OnlyTheEarliestInWindowForwardNotePerBarIsScored));
        var days = DirectionScoreFixture.TradingDays(DirectionScoreFixture.Cal, new DateOnly(2025, 6, 2), 40);
        DirectionScoreFixture.SeedBars(db, "MBB", DirectionScoreFixture.Cal, days, i => i == 25 ? 110m : 100m);
        var d5 = days[5];
        var at15 = DirectionScoreFixture.LocalMs(d5, 15);
        var created = DateTimeOffset.FromUnixTimeMilliseconds(at15).UtcDateTime;
        // Inserted before the earliest note on purpose: the order must come from the data, not from insertion.
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 20, "up", "late", availableMs: DirectionScoreFixture.LocalMs(d5, 16)));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 20, "up", "mm", availableMs: at15, createdUtc: created.AddSeconds(1)));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 20, "up", "zz", availableMs: at15, createdUtc: created));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 20, "down", "aa", availableMs: at15, createdUtc: created));
        // At the 09:00 open of the next session ⇒ out of window; 1 ms before it ⇒ in window.
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 20, "up", "out", availableMs: DirectionScoreFixture.LocalMs(days[6], 9)));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", days[7], 20, "up", "edge", availableMs: DirectionScoreFixture.LocalMs(days[8], 9) - 1));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 20, "up", "retro", mode: "retrospective"));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 30, "up", "h30"));
        await db.SaveChangesAsync();

        var r = await Service(db).GetAsync("MBB");
        var earliest = Item(r, "aa");
        Assert.Equal(("resolved", (string?)"up", (bool?)false), (earliest.Status, earliest.Realized, earliest.Hit)); // R = +10% but it said down
        foreach (var tag in new[] { "late", "mm", "zz" })
        {
            var later = Item(r, tag); // all of them said up: never counted
            Assert.Equal("superseded-not-scored", later.Status);
            Assert.Null(later.Hit);
            Assert.Null(later.Realized);
            Assert.Null(later.ReturnPct);
        }
        Assert.Equal("out-of-window-not-scored", Item(r, "out").Status);
        Assert.Null(Item(r, "out").Hit);
        var edge = Item(r, "edge");
        Assert.Equal(("resolved", (string?)"sideways", (bool?)false), (edge.Status, edge.Realized, edge.Hit));
        Assert.Equal("retrospective-not-scored", Item(r, "retro").Status);
        Assert.Null(Item(r, "retro").Realized);
        Assert.Equal("no-declared-thresholds", Item(r, "h30").Status);
        Assert.Null(Item(r, "h30").Realized);

        Assert.Equal((8, 1, 1), (r.TotalNotes, r.RetrospectiveNotes, r.NoDeclaredThresholdNotes));
        var h20 = H(r, 20);
        Assert.Equal((6, 1, 0, 5, 3, 2), (h20.ForwardNotes, h20.OutOfWindow, h20.WindowUndetermined, h20.Eligible, h20.Superseded, h20.Scored));
        Assert.Equal((0, 2, 2, 0), (h20.NoView, h20.Directional, h20.Resolved, h20.Hits));
        Assert.Equal(0.0, h20.HitRate!.Value, 12);
        Assert.Equal(1.0, h20.Coverage!.Value, 12);
        Assert.Equal(0, H(r, 60).ForwardNotes);
    }

    [Fact]
    public async Task TheScoredSlotIsPerSignalBarAndHorizon_H20AndH60OnOneBarAreBothScored()
    {
        using var db = NewDb(nameof(TheScoredSlotIsPerSignalBarAndHorizon_H20AndH60OnOneBarAreBothScored));
        var days = DirectionScoreFixture.TradingDays(DirectionScoreFixture.Cal, new DateOnly(2025, 6, 2), 80);
        DirectionScoreFixture.SeedBars(db, "MBB", DirectionScoreFixture.Cal, days, _ => 100m);
        var d5 = days[5];
        // h60 first, h20 five minutes later: a slot keyed on the signal bar alone would supersede the h20 note.
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 60, "sideways", "h60", availableMs: DirectionScoreFixture.LocalMs(d5, 15)));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", d5, 20, "sideways", "h20", availableMs: DirectionScoreFixture.LocalMs(d5, 15, 5)));
        await db.SaveChangesAsync();

        var r = await Service(db).GetAsync("MBB");
        foreach (var tag in new[] { "h20", "h60" })
            Assert.Equal(("resolved", (string?)"sideways", (bool?)true), (Item(r, tag).Status, Item(r, tag).Realized, Item(r, tag).Hit));
        foreach (var h in new[] { 20, 60 })
            Assert.Equal((1, 0, 1, 1), (H(r, h).Scored, H(r, h).Superseded, H(r, h).Resolved, H(r, h).Hits));
    }

    [Fact]
    public async Task PendingReasons_AndWindowUndetermined_OnACoverageBoundedCalendar()
    {
        var cal = VietnamMarketCalendar.FromSource(new MarketCalendarSource(
            "test-2025", new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), [], [], null));
        using var db = NewDb(nameof(PendingReasons_AndWindowUndetermined_OnACoverageBoundedCalendar));
        var days = cal.EnumerateTradingDates(new DateOnly(2025, 10, 1), new DateOnly(2025, 12, 30));
        var nov3 = days.ToList().IndexOf(new DateOnly(2025, 11, 3));
        DirectionScoreFixture.SeedBars(db, "MBB", cal, days, _ => 100m, nov3);
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", new DateOnly(2025, 12, 3), 20, "up", "immature"));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", new DateOnly(2025, 12, 10), 20, "down", "expired"));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", new DateOnly(2025, 11, 3), 20, "up", "nosignal"));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", new DateOnly(2025, 12, 4), 20, "no-view", "noview"));
        db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", new DateOnly(2025, 12, 31), 20, "up", "window"));
        await db.SaveChangesAsync();

        var r = await Service(db, cal, DirectionScoreFixture.Local(2025, 12, 30, 16)).GetAsync("MBB");
        Assert.Equal("2025-12-31", r.CalendarCoverageTo);
        var immature = Item(r, "immature"); // 2025-12-03 + 20 sessions = 2025-12-31, not finalized at 12-30 16:00
        Assert.Equal(("pending", "immature", Open(new DateOnly(2025, 12, 31))),
            (immature.Status, immature.PendingReason!, immature.TargetBarOpenTimeMs!.Value));
        var expired = Item(r, "expired"); // 2025-12-10 + 20 sessions lands in 2026 (holidays unknown)
        Assert.Equal(("pending", "calendar-coverage-expired"), (expired.Status, expired.PendingReason!));
        Assert.Null(expired.TargetBarOpenTimeMs);
        var noSignal = Item(r, "nosignal"); // 2025-11-03 not stored; its target 2025-12-01 is
        Assert.Equal(("pending", "signal-bar-missing", Open(new DateOnly(2025, 12, 1))),
            (noSignal.Status, noSignal.PendingReason!, noSignal.TargetBarOpenTimeMs!.Value));
        Assert.Null(noSignal.SignalClose);
        var noView = Item(r, "noview");
        Assert.Equal(("no-view", "calendar-coverage-expired"), (noView.Status, noView.PendingReason!));
        Assert.Null(noView.Hit);
        Assert.Equal("window-undetermined-not-scored", Item(r, "window").Status);

        var h20 = H(r, 20);
        Assert.Equal("no-data", h20.Status);
        Assert.Equal((5, 0, 1, 4, 0, 4), (h20.ForwardNotes, h20.OutOfWindow, h20.WindowUndetermined, h20.Eligible, h20.Superseded, h20.Scored));
        Assert.Equal((1, 3, 0, 3), (h20.NoView, h20.Directional, h20.Resolved, h20.Pending));
        Assert.Equal(new EquityDirectionPendingCounts(1, 0, 1, 1), h20.PendingReasons);
        Assert.Equal((0, 0), (h20.Hits, h20.NEffective));
        Assert.Null(h20.HitRate);
        Assert.Equal(0.75, h20.Coverage!.Value, 12);
        Assert.Equal(new EquityDirectionPriorBaselineDto(0, null), h20.BaselinePrior);
        Assert.Equal(new EquityDirectionConstantBaselineDto(null, 0, null), h20.BestConstantInHindsight);
    }

    [Theory]
    [InlineData(570, 29, "insufficient-sample")] // the 30th resolved note overlaps row 560: nEffective 29
    [InlineData(580, 30, "ok")]
    public async Task Status_FlipsToOk_ExactlyAtThirtyEffectiveNotes(int lastRow, int expectedEffective, string expectedStatus)
    {
        var cal = new VietnamMarketCalendar([]);
        using var db = NewDb($"{nameof(Status_FlipsToOk_ExactlyAtThirtyEffectiveNotes)}-{lastRow}");
        var days = DirectionScoreFixture.TradingDays(cal, new DateOnly(2023, 1, 2), 620);
        DirectionScoreFixture.SeedBars(db, "MBB", cal, days, _ => 100m);
        var rows = Enumerable.Range(0, 29).Select(k => k * 20).Append(lastRow).ToArray();
        foreach (var row in rows)
            db.EquityResearchNotes.Add(DirectionScoreFixture.Note("MBB", days[row], 20, "sideways", $"s{row:000}"));
        await db.SaveChangesAsync();

        var h20 = H(await Service(db, cal).GetAsync("MBB"), 20);
        Assert.Equal((30, 30, expectedEffective, expectedStatus), (h20.Resolved, h20.Hits, h20.NEffective, h20.Status));
        Assert.Equal(30, h20.BaselinePrior.Hits); // flat series: every prior call is sideways
    }

    [Fact]
    public async Task ReadOnly_NoRowsAddedAndNothingTracked()
    {
        using var db = NewDb(nameof(ReadOnly_NoRowsAddedAndNothingTracked));
        DirectionScoreFixture.SeedMain(db);
        db.ChangeTracker.Clear();
        (int, int, int) Counts() => (db.InstrumentCandles.Count(), db.EquityResearchNotes.Count(), db.EquityForecastRecords.Count());
        var before = Counts();

        await Service(db).GetAsync("MBB");

        Assert.Empty(db.ChangeTracker.Entries());
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(before, Counts());
        Assert.Equal((100, 6, 0), before);
    }
}
