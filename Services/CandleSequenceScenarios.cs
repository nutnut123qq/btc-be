using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Phân tích các kịch bản (scenarios) phát triển của chuỗi nến
/// dựa trên đặc trưng thống kê, không dùng tên pattern cổ điển.
/// </summary>
public static class CandleSequenceScenarios
{
    public static CandleSequenceAnalysis Analyze(IReadOnlyList<KlineDto> klines, string symbol, string timeframe)
    {
        if (klines.Count < 5)
            return new CandleSequenceAnalysis
            {
                Symbol = symbol,
                Timeframe = timeframe,
                BarsAnalyzed = klines.Count,
                SummaryText = "Không đủ nến để phân tích chuỗi (cần ít nhất 5 nến)."
            };

        var scenarios = new List<CandleScenarioResult>();

        var contraction = DetectContractionThenExpansion(klines);
        if (contraction != null) scenarios.Add(contraction);

        var progressive = DetectProgressiveCloses(klines);
        if (progressive != null) scenarios.Add(progressive);

        var rejection = DetectShadowRejectionCluster(klines);
        if (rejection != null) scenarios.Add(rejection);

        var divergence = DetectVolumePriceDivergence(klines);
        if (divergence != null) scenarios.Add(divergence);

        var reversal = DetectIntradayReversalSequence(klines);
        if (reversal != null) scenarios.Add(reversal);

        var summary = BuildSummary(scenarios);

        return new CandleSequenceAnalysis
        {
            Symbol = symbol,
            Timeframe = timeframe,
            BarsAnalyzed = klines.Count,
            Scenarios = scenarios.OrderByDescending(s => s.Strength).ToList(),
            SummaryText = summary
        };
    }

    // 1. Range thu hẹp dần rồi bùng nổ (Squeeze + Breakout)
    private static CandleScenarioResult? DetectContractionThenExpansion(IReadOnlyList<KlineDto> klines)
    {
        int n = klines.Count;
        if (n < 6) return null;

        // Lấy 5 nến trước nến cuối
        var preRanges = new List<decimal>();
        for (int i = n - 6; i < n - 1; i++)
            preRanges.Add(klines[i].High - klines[i].Low);

        if (preRanges.Count < 3) return null;

        // Kiểm tra range thu hẹp dần (mỗi nến sau nhỏ hơn hoặc bằng nến trước, cho phép lệch nhẹ)
        bool contracting = true;
        for (int i = 1; i < preRanges.Count; i++)
        {
            if (preRanges[i] > preRanges[i - 1] * 1.15m) // cho phép nhích 15%
            {
                contracting = false;
                break;
            }
        }

        if (!contracting) return null;

        var last = klines[n - 1];
        var avgPreRange = preRanges.Average();
        var lastRange = last.High - last.Low;

        if (lastRange < avgPreRange * 1.5m) return null; // chưa đủ bùng nổ

        bool bullish = last.Close > last.Open && (last.Close - last.Open) > lastRange * 0.5m;
        bool bearish = last.Close < last.Open && (last.Open - last.Close) > lastRange * 0.5m;

        if (!bullish && !bearish) return null;

        return new CandleScenarioResult
        {
            Scenario = CandleScenarioType.ContractionThenExpansion,
            Name = "Contraction Then Expansion",
            Description = "Chuỗi nến range thu hẹp dần, sau đó nến cuối bùng nổ range lớn.",
            Strength = Math.Min(1.0, (double)(lastRange / (avgPreRange + 0.0001m)) / 3.0),
            Suggestion = bullish ? "Breakout tăng tiềm năng sau thu hẹp." : "Breakout giảm tiềm năng sau thu hẹp.",
            Details = new List<string>
            {
                $"Range trung bình 5 nến trước: {avgPreRange:F2}",
                $"Range nến cuối: {lastRange:F2} ({lastRange / avgPreRange:F1}x)",
                $"Hướng breakout: {(bullish ? "Bullish" : "Bearish")}"
            }
        };
    }

    // 2. Progressive Closes — nến đóng cửa tiến dần
    private static CandleScenarioResult? DetectProgressiveCloses(IReadOnlyList<KlineDto> klines)
    {
        int n = klines.Count;
        int minConsecutive = 4;
        if (n < minConsecutive + 1) return null;

        // Đếm higher closes liên tiếp kết thúc tại nến cuối
        int higherCount = 0;
        for (int i = n - 1; i > 0 && klines[i].Close > klines[i - 1].Close; i--)
            higherCount++;

        int lowerCount = 0;
        for (int i = n - 1; i > 0 && klines[i].Close < klines[i - 1].Close; i--)
            lowerCount++;

        if (higherCount < minConsecutive && lowerCount < minConsecutive) return null;

        bool isHigher = higherCount >= lowerCount;
        int count = isHigher ? higherCount : lowerCount;

        return new CandleScenarioResult
        {
            Scenario = CandleScenarioType.ProgressiveCloses,
            Name = "Progressive Closes",
            Description = $"{count} nến đóng cửa liên tiếp {(isHigher ? "cao hơn" : "thấp hơn")} nến trước.",
            Strength = Math.Min(1.0, count / 8.0),
            Suggestion = isHigher ? "Momentum tăng đang tích lũy." : "Momentum giảm đang tích lũy.",
            Details = new List<string>
            {
                $"Số nến liên tiếp: {count}",
                $"Close đầu chuỗi: {klines[n - count - 1].Close:F2}",
                $"Close cuối chuỗi: {klines[n - 1].Close:F2}",
                $"Thay đổi: {((double)((klines[n - 1].Close - klines[n - count - 1].Close) / klines[n - count - 1].Close) * 100):F2}%"
            }
        };
    }

    // 3. Shadow Rejection Cluster — chuỗi nến có bóng dài cùng phía
    private static CandleScenarioResult? DetectShadowRejectionCluster(IReadOnlyList<KlineDto> klines)
    {
        int n = klines.Count;
        if (n < 3) return null;

        int upperCount = 0;
        int lowerCount = 0;
        var details = new List<string>();

        for (int i = Math.Max(0, n - 4); i < n; i++)
        {
            var k = klines[i];
            var range = k.High - k.Low;
            var body = Math.Abs(k.Close - k.Open);
            if (range <= 0) continue;

            var upper = k.High - Math.Max(k.Open, k.Close);
            var lower = Math.Min(k.Open, k.Close) - k.Low;

            // Bóng dài = > 1.5 lần body và > 20% range
            if (body > 0 && upper > body * 1.5m && upper > range * 0.2m)
            {
                upperCount++;
                details.Add($"Nến {i}: upper shadow {upper:F2} ({upper / range * 100:F0}% range)");
            }
            if (body > 0 && lower > body * 1.5m && lower > range * 0.2m)
            {
                lowerCount++;
                details.Add($"Nến {i}: lower shadow {lower:F2} ({lower / range * 100:F0}% range)");
            }
        }

        if (upperCount < 2 && lowerCount < 2) return null;

        bool isUpper = upperCount >= lowerCount;
        int count = isUpper ? upperCount : lowerCount;

        return new CandleScenarioResult
        {
            Scenario = CandleScenarioType.ShadowRejectionCluster,
            Name = "Shadow Rejection Cluster",
            Description = $"{count} nến gần nhất có bóng {(isUpper ? "trên" : "dưới")} dài — áp lực {(isUpper ? "bán" : "mua")} xuất hiện nhưng bị từ chối.",
            Strength = Math.Min(1.0, count / 4.0),
            Suggestion = isUpper ? "Từ chối giá cao — có thể đảo chiều giảm." : "Từ chối giá thấp — có thể đảo chiều tăng.",
            Details = details.TakeLast(3).ToList()
        };
    }

    // 4. Volume-Price Divergence
    private static CandleScenarioResult? DetectVolumePriceDivergence(IReadOnlyList<KlineDto> klines)
    {
        int n = klines.Count;
        if (n < 6) return null;

        var recent = klines.Skip(n - 5).ToList();
        var priceUp = recent[4].Close > recent[0].Close;
        var priceDown = recent[4].Close < recent[0].Close;

        if (!priceUp && !priceDown) return null;

        var volumes = recent.Select(k => k.Volume).ToList();
        bool volumeDecreasing = true;
        for (int i = 1; i < volumes.Count; i++)
        {
            if (volumes[i] > volumes[i - 1] * 1.2m) // cho phép nhích 20%
            {
                volumeDecreasing = false;
                break;
            }
        }

        if (!volumeDecreasing) return null;

        return new CandleScenarioResult
        {
            Scenario = CandleScenarioType.VolumePriceDivergence,
            Name = "Volume-Price Divergence",
            Description = $"Giá {(priceUp ? "tăng" : "giảm")} nhưng volume giảm dần qua 5 nến — momentum yếu đi.",
            Strength = 0.7,
            Suggestion = priceUp
                ? "Tăng không có volume xác nhận — đề phòng đảo chiều."
                : "Giảm không có volume xác nhận — đề phòng hồi phục.",
            Details = new List<string>
            {
                $"Volume nến đầu: {volumes[0]:F2}",
                $"Volume nến cuối: {volumes[4]:F2}",
                $"Thay đổi volume: {((double)((volumes[4] - volumes[0]) / (volumes[0] + 0.0001m)) * 100):F1}%"
            }
        };
    }

    // 5. Intraday Reversal Sequence — nến range lớn nhưng đóng cửa gần đầu kia
    private static CandleScenarioResult? DetectIntradayReversalSequence(IReadOnlyList<KlineDto> klines)
    {
        int n = klines.Count;
        if (n < 3) return null;

        int reversalCount = 0;
        var details = new List<string>();

        for (int i = Math.Max(0, n - 3); i < n; i++)
        {
            var k = klines[i];
            var range = k.High - k.Low;
            var body = Math.Abs(k.Close - k.Open);
            if (range <= 0) continue;

            var bodyRatio = body / range;
            // Nến có body nhỏ (< 30% range) nhưng range lớn (> 1.5x avg) → reversal intraday
            if (bodyRatio < 0.3m && range > 0)
            {
                reversalCount++;
                details.Add($"Nến {i}: body {bodyRatio * 100:F0}% range, wick {(1 - bodyRatio) * 100:F0}%");
            }
        }

        if (reversalCount < 2) return null;

        return new CandleScenarioResult
        {
            Scenario = CandleScenarioType.IntradayReversalSequence,
            Name = "Intraday Reversal Sequence",
            Description = $"{reversalCount} nến liên tiếp có range lớn nhưng body nhỏ — phe đối lập liên tục từ chối giá.",
            Strength = Math.Min(1.0, reversalCount / 3.0),
            Suggestion = "Thị trường đang do dự. Có thể chuẩn bị đảo chiều hoặc breakout mạnh.",
            Details = details
        };
    }

    private static string BuildSummary(List<CandleScenarioResult> scenarios)
    {
        if (scenarios.Count == 0)
            return "Không phát hiện kịch bản chuỗi nến đặc biệt trong dữ liệu gần nhất.";

        var lines = new List<string> { $"Phát hiện {scenarios.Count} kịch bản:" };
        foreach (var s in scenarios.OrderByDescending(x => x.Strength))
            lines.Add($"- [{s.Scenario}] {s.Name} (độ mạnh {s.Strength * 100:F0}%): {s.Suggestion}");

        return string.Join("\n", lines);
    }
}
