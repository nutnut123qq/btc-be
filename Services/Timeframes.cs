namespace Backend.Services;

/// <summary>
/// Quy đổi khung thởi gian Binance sang mili-giây. Nguồn duy nhất cho toàn backend
/// (trước đây bị lặp ở MarketController, CandleSequenceValidator, WindowDatasetService).
/// Phân biệt hoa/thường theo Binance: "1m" = phút, "1M" = tháng. Trả 0 nếu không hợp lệ.
/// </summary>
public static class Timeframes
{
    public static long IntervalToMs(string? interval) => interval switch
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
        _ => 0L,
    };
}
