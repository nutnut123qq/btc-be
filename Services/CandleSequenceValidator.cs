using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Kiểm tra tính hợp lệ (integrity) của chuỗi nến OHLC.
/// Đảm bảo dữ liệu từ exchange không bị lỗi, miss nến, hoặc bất thường.
/// </summary>
public static class CandleSequenceValidator
{
    public static CandleValidationResult Validate(IReadOnlyList<KlineDto> klines, string interval)
    {
        var issues = new List<CandleValidationIssue>();
        if (klines.Count == 0)
        {
            issues.Add(new CandleValidationIssue(0, "EMPTY", "Chuỗi nến rỗng."));
            return new CandleValidationResult { Issues = issues };
        }

        var intervalMs = ResolveIntervalMs(interval);

        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];

            // 1. Giá không âm
            if (k.Open < 0 || k.High < 0 || k.Low < 0 || k.Close < 0)
                issues.Add(new CandleValidationIssue(i, "NEGATIVE_PRICE", $"Open={k.Open}, High={k.High}, Low={k.Low}, Close={k.Close} có giá trị âm."));

            // 2. High >= Low
            if (k.High < k.Low)
                issues.Add(new CandleValidationIssue(i, "HIGH_LT_LOW", $"High {k.High} < Low {k.Low}."));

            // 3. High >= max(Open, Close)
            var maxOc = Math.Max(k.Open, k.Close);
            if (k.High < maxOc)
                issues.Add(new CandleValidationIssue(i, "HIGH_LT_MAX_OC", $"High {k.High} < max(Open,Close) {maxOc}."));

            // 4. Low <= min(Open, Close)
            var minOc = Math.Min(k.Open, k.Close);
            if (k.Low > minOc)
                issues.Add(new CandleValidationIssue(i, "LOW_GT_MIN_OC", $"Low {k.Low} > min(Open,Close) {minOc}."));

            // 5. Volume không âm
            if (k.Volume < 0)
                issues.Add(new CandleValidationIssue(i, "NEGATIVE_VOLUME", $"Volume {k.Volume} âm."));

            // 6. Kiểm tra miss nến (so với nến trước)
            if (i > 0)
            {
                var prev = klines[i - 1];
                var expectedDiff = intervalMs;
                var actualDiff = k.OpenTimeMs - prev.OpenTimeMs;

                if (actualDiff != expectedDiff)
                {
                    var missingCount = expectedDiff > 0 ? (actualDiff / expectedDiff) - 1 : 0;
                    issues.Add(new CandleValidationIssue(
                        i,
                        "MISSING_CANDLE",
                        $"Thiếu {missingCount} nến giữa {prev.OpenTimeMs} và {k.OpenTimeMs} (khoảng cách {actualDiff}ms, mong đợi {expectedDiff}ms)."));
                }
            }
        }

        // 7. Kiểm tra duplicate OpenTimeMs
        var dupGroups = klines.GroupBy(k => k.OpenTimeMs).Where(g => g.Count() > 1).ToList();
        foreach (var g in dupGroups)
        {
            issues.Add(new CandleValidationIssue(
                -1,
                "DUPLICATE_TIME",
                $"OpenTimeMs {g.Key} xuất hiện {g.Count()} lần."));
        }

        return new CandleValidationResult
        {
            TotalBars = klines.Count,
            ValidBars = klines.Count - issues.Where(x => x.BarIndex >= 0).Select(x => x.BarIndex).Distinct().Count(),
            Issues = issues,
            IsValid = issues.Count == 0
        };
    }

    private static long ResolveIntervalMs(string interval)
    {
        return interval?.ToLowerInvariant() switch
        {
            "1m" => 60_000L,
            "3m" => 180_000L,
            "5m" => 300_000L,
            "15m" => 900_000L,
            "30m" => 1_800_000L,
            "1h" => 3_600_000L,
            "2h" => 7_200_000L,
            "4h" => 14_400_000L,
            "6h" => 21_600_000L,
            "8h" => 28_800_000L,
            "12h" => 43_200_000L,
            "1d" => 86_400_000L,
            "3d" => 259_200_000L,
            "1w" => 604_800_000L,
            "1M" => 2_592_000_000L,
            _ => 0L
        };
    }
}

public class CandleValidationIssue
{
    public int BarIndex { get; set; }
    public string Code { get; set; }
    public string Message { get; set; }

    public CandleValidationIssue(int barIndex, string code, string message)
    {
        BarIndex = barIndex;
        Code = code;
        Message = message;
    }
}

public class CandleValidationResult
{
    public int TotalBars { get; set; }
    public int ValidBars { get; set; }
    public bool IsValid { get; set; }
    public IReadOnlyList<CandleValidationIssue> Issues { get; set; } = Array.Empty<CandleValidationIssue>();

    public string SummaryText =>
        IsValid
            ? $"✅ Chuỗi nến hợp lệ ({TotalBars} nến)."
            : $"⚠️ Phát hiện {Issues.Count} vấn đề trong {TotalBars} nến ({ValidBars} nến hợp lệ).";
}
