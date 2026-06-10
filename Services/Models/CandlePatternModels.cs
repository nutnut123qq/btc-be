namespace Backend.Services.Models;

public enum SingleCandlePattern
{
    None,
    Doji,
    DragonflyDoji,
    GravestoneDoji,
    Hammer,
    HangingMan,
    InvertedHammer,
    ShootingStar,
    SpinningTop,
    BullishMarubozu,
    BearishMarubozu
}

public enum MultiCandlePattern
{
    None,
    BullishEngulfing,
    BearishEngulfing,
    PiercingLine,
    DarkCloudCover,
    BullishHarami,
    BearishHarami,
    TweezerBottoms,
    TweezerTops,
    MorningStar,
    EveningStar,
    ThreeWhiteSoldiers,
    ThreeBlackCrows,
    ThreeInsideUp,
    ThreeInsideDown
}

public enum TrendDirection
{
    Uptrend,
    Downtrend,
    Sideways
}

public class RecognizedCandle
{
    public long OpenTimeMs { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }

    public decimal BodySize => Math.Abs(Close - Open);
    public decimal Range => High - Low;
    public decimal UpperShadow => High - Math.Max(Open, Close);
    public decimal LowerShadow => Math.Min(Open, Close) - Low;
    public bool IsGreen => Close > Open;
    public bool IsRed => Close < Open;

    public SingleCandlePattern SinglePattern { get; set; } = SingleCandlePattern.None;
    public double VolumeAnomalyRatio { get; set; } = 1.0;
}

public class RecognizedMultiPattern
{
    public MultiCandlePattern Pattern { get; set; }
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
}

public class PatternRecognitionResult
{
    public IReadOnlyList<RecognizedCandle> Candles { get; set; } = Array.Empty<RecognizedCandle>();
    public IReadOnlyList<RecognizedMultiPattern> MultiPatterns { get; set; } = Array.Empty<RecognizedMultiPattern>();
    public string SummaryText { get; set; } = string.Empty;
}
