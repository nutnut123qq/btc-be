using Backend.Services;
using Backend.Services.Models;
using Xunit;

namespace Backend.Tests;

/// <summary>EQX v2 A4: a lagging benchmark is reported "stale" with a reason, never "ok".</summary>
public class EquityRelativeStrengthStaleTests
{
    private static readonly VietnamMarketCalendar Cal = VietnamMarketCalendar.CreateDefault();

    private static Dictionary<long, decimal> Daily(DateOnly from, DateOnly to, decimal start) =>
        Cal.EnumerateTradingDates(from, to)
            .Select((d, i) => (Open: VietnamMarketCalendar.DailyOpenUtcMs(d), Close: start + i))
            .ToDictionary(x => x.Open, x => x.Close);

    [Fact]
    public void BenchmarkUpToDate_IsOk()
    {
        var inst = Daily(new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 16), 100m);
        var bench = Daily(new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 16), 1_000m);
        var b = EquityContextService.Compare("VNINDEX", "index", inst, bench, Cal);
        Assert.Equal((EquityContextStatuses.Ok, (string?)null, 0), (b.Status, b.Reason, b.LagSessions!.Value));
    }

    [Fact]
    public void BenchmarkOneSessionBehind_IsStale_AlignedWindowBehind()
    {
        var inst = Daily(new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 16), 100m);
        var bench = Daily(new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 15), 1_000m);
        var b = EquityContextService.Compare("VNINDEX", "index", inst, bench, Cal);
        Assert.Equal(EquityContextStatuses.Stale, b.Status);
        Assert.Equal(EquityContextService.AlignedWindowBehindReason, b.Reason);
        Assert.Equal(1, b.LagSessions);
        Assert.NotNull(b.Returns["20"]); // last aligned window kept for reference
        Assert.True(b.LastAlignedOpenTimeMs < b.InstrumentLatestOpenTimeMs);
    }

    [Fact]
    public void BenchmarkSeveralSessionsBehind_IsBenchmarkStale()
    {
        var inst = Daily(new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 16), 100m);
        var bench = Daily(new DateOnly(2026, 6, 1), new DateOnly(2026, 10, 9), 1_000m);
        var b = EquityContextService.Compare("CTG", "peer", inst, bench, Cal);
        Assert.Equal((EquityContextStatuses.Stale, EquityContextService.BenchmarkStaleReason, 5),
            (b.Status, b.Reason, b.LagSessions!.Value));
    }

    [Fact]
    public void LagIsCountedInTradingSessions_AcrossTheNationalDayClosure()
    {
        // Benchmark ends 2026-08-28 (Fri); instrument ends 2026-09-03 — 31/08, 01/09, 02/09 are official
        // closures, so the benchmark is exactly ONE session behind, not four weekdays.
        var inst = Daily(new DateOnly(2026, 6, 1), new DateOnly(2026, 9, 3), 100m);
        var bench = Daily(new DateOnly(2026, 6, 1), new DateOnly(2026, 8, 28), 1_000m);
        var b = EquityContextService.Compare("VNINDEX", "index", inst, bench, Cal);
        Assert.Equal((EquityContextService.AlignedWindowBehindReason, 1), (b.Reason, b.LagSessions!.Value));
    }
}
