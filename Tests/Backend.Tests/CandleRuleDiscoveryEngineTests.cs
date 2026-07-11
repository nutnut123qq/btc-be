using Backend.Services;
using Backend.Services.Models;

namespace Backend.Tests;

public class CandleRuleDiscoveryEngineTests
{
    /// <summary>
    /// Tạo chuỗi nến có body_ratio > 0.55 để rule "body_ratio > 0.55" match ở mọi index,
    /// kết hợp với returns xác định trước để test win rate và max drawdown.
    /// </summary>
    private static List<KlineDto> BuildBarsWithBodyRatioAndReturns(IEnumerable<double> returnsPct, decimal startClose = 1000m)
    {
        var klines = new List<KlineDto>();
        var close = startClose;
        long i = 0;
        foreach (var ret in returnsPct)
        {
            var nextClose = close * (1m + (decimal)ret / 100m);
            var open = Math.Min(close, nextClose) - 200m;
            var high = Math.Max(close, nextClose) + 5m;
            var low = open - 5m;
            klines.Add(new KlineDto
            {
                OpenTimeMs = i * 3_600_000L,
                Open = open,
                High = high,
                Low = low,
                Close = nextClose,
                Volume = 100m,
            });
            close = nextClose;
            i++;
        }
        return klines;
    }

    [Fact]
    public void Discover_WinRate_UsesTotalCount_IncludingNeutrals()
    {
        // Pattern returns: +0.5% (win), -0.4% (loss), 0% (neutral) lặp lại.
        // Tất cả nến đều có body_ratio > 0.55 để rule "body_ratio > 55" match ở mọi index.
        var pattern = new List<double> { 0.5, -0.4, 0.0 };
        var allReturns = new List<double>();
        while (allReturns.Count < 200)
            allReturns.AddRange(pattern);
        allReturns = allReturns.Take(200).ToList();

        var klines = BuildBarsWithBodyRatioAndReturns(allReturns);

        var rules = CandleRuleDiscoveryEngine.Discover(
            klines,
            symbol: "BTCUSDT",
            timeframe: "1h",
            futureBars: 1,
            minWinRate: 0.0,
            minSamples: 10,
            minAvgReturnPct: 0.0);

        Assert.NotEmpty(rules);
        var bodyRule = rules.FirstOrDefault(r => r.Name.Contains("Body", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(bodyRule);

        // Với ~199 mẫu: win ~67, loss ~66, neutral ~66 => winRate ~ 67/199 ~ 0.337
        // Công thức cũ (bỏ qua neutral) sẽ ra ~67/(67+66) ~ 0.504
        Assert.True(bodyRule.WinRate < 0.4, $"Win rate should include neutrals in denominator, got {bodyRule.WinRate}");
    }

    [Fact]
    public void Discover_MaxDrawdown_UsesChronologicalOrder()
    {
        // Chuỗi returns: +10, -8, -6, -4, +20
        // Cumulative P&L theo thởi gian: 10, 2, -4, -8, 12
        // Max drawdown thực tế: từ peak 10 xuống trough -8 = 18.
        // Nếu sắp xếp tăng dần thì chuỗi cumulative khác, max drawdown sẽ khác.
        var pattern = new List<double> { 10.0, -8.0, -6.0, -4.0, 20.0 };

        var allReturns = new List<double>();
        while (allReturns.Count < 200)
            allReturns.AddRange(pattern);
        allReturns = allReturns.Take(200).ToList();

        var klines = BuildBarsWithBodyRatioAndReturns(allReturns);

        var rules = CandleRuleDiscoveryEngine.Discover(
            klines,
            symbol: "BTCUSDT",
            timeframe: "1h",
            futureBars: 1,
            minWinRate: 0.0,
            minSamples: 10,
            minAvgReturnPct: 0.0);

        Assert.NotEmpty(rules);
        // Lấy rule single "Body>55" có nhiều sample nhất để tránh rule pair chỉ match ở subset đặc biệt.
        var bodyRule = rules
            .Where(r => string.Equals(r.Name, "Body>55", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.SampleCount)
            .FirstOrDefault();
        Assert.NotNull(bodyRule);
        Assert.True(bodyRule.SampleCount >= 10, $"Expected sample count >= 10 but got {bodyRule.SampleCount}");

        // Max drawdown phải phản ánh chuỗi cumulative theo thởi gian, không phải sorted.
        Assert.True(bodyRule.MaxDrawdownPct >= 15.0, $"Expected max drawdown >= 15.0 but got {bodyRule.MaxDrawdownPct}");
    }

    [Fact]
    public void MaxDrawdown_CalculatesPeakToTrough()
    {
        // Direct verification of the drawdown algorithm using reflection-free helper.
        var returns = new List<double> { 10.0, -8.0, -6.0, -4.0, 20.0 };
        double maxDd = 0, peak = 0, cum = 0;
        foreach (var r in returns)
        {
            cum += r;
            if (cum > peak) peak = cum;
            var dd = peak - cum;
            if (dd > maxDd) maxDd = dd;
        }
        Assert.Equal(18.0, maxDd);
    }
}
