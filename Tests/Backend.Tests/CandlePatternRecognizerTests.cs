using Backend.Services;
using Backend.Services.Models;

namespace Backend.Tests;

public class CandlePatternRecognizerTests
{
    private static KlineDto Bar(long i, decimal open, decimal high, decimal low, decimal close, decimal volume = 100m) => new()
    {
        OpenTimeMs = i * 3_600_000L,
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = volume,
    };

    private static List<KlineDto> DecliningTrend(int count, decimal startClose = 65000m, decimal step = 200m)
    {
        var bars = new List<KlineDto>();
        for (int i = 0; i < count; i++)
        {
            var close = startClose - i * step;
            var open = close + step;
            bars.Add(Bar(i, open, open + 50, close - 50, close));
        }
        return bars;
    }

    private static List<KlineDto> RisingTrend(int count, decimal startClose = 63000m, decimal step = 200m)
    {
        var bars = new List<KlineDto>();
        for (int i = 0; i < count; i++)
        {
            var close = startClose + i * step;
            var open = close - step;
            bars.Add(Bar(i, open, open + 50, close - 50, close));
        }
        return bars;
    }

    [Fact]
    public void Recognize_HammerInDowntrend_ReturnsHammer()
    {
        var klines = DecliningTrend(6);
        klines.Add(Bar(6, 64150m, 64180m, 63800m, 64100m)); // long lower shadow, small body, tiny upper shadow

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        var hammer = result.Candles.Last().SinglePattern;
        Assert.Equal(SingleCandlePattern.Hammer, hammer);
    }

    [Fact]
    public void Recognize_HangingManInUptrend_ReturnsHangingMan()
    {
        var klines = RisingTrend(6);
        klines.Add(Bar(6, 64150m, 64180m, 63800m, 64100m)); // same hammer shape but at top of uptrend

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        var pattern = result.Candles.Last().SinglePattern;
        Assert.Equal(SingleCandlePattern.HangingMan, pattern);
    }

    [Fact]
    public void Recognize_InvertedHammerInDowntrend_ReturnsInvertedHammer()
    {
        var klines = DecliningTrend(6);
        klines.Add(Bar(6, 63800m, 64200m, 63780m, 63850m)); // long upper shadow, small body, tiny lower shadow

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        var pattern = result.Candles.Last().SinglePattern;
        Assert.Equal(SingleCandlePattern.InvertedHammer, pattern);
    }

    [Fact]
    public void Recognize_ShootingStarInUptrend_ReturnsShootingStar()
    {
        var klines = RisingTrend(6);
        klines.Add(Bar(6, 64200m, 64600m, 64180m, 64250m)); // same inverted-hammer shape but in uptrend

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        var pattern = result.Candles.Last().SinglePattern;
        Assert.Equal(SingleCandlePattern.ShootingStar, pattern);
    }

    [Fact]
    public void Recognize_MorningStarInDowntrend_ReturnsMorningStar()
    {
        var klines = DecliningTrend(5, 65000m, 200m);
        klines.Add(Bar(5, 64050m, 64100m, 63800m, 63850m)); // red, body 200 > 50% of range 300
        klines.Add(Bar(6, 63850m, 63930m, 63830m, 63860m)); // star, body 10 < 30% of range 100
        klines.Add(Bar(7, 63860m, 64200m, 63850m, 64150m)); // green, body 290 > 100, close above mid(64050,63850)=63950

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        Assert.Contains(result.MultiPatterns, mp => mp.Pattern == MultiCandlePattern.MorningStar);
    }

    [Fact]
    public void Recognize_EveningStarInUptrend_ReturnsEveningStar()
    {
        var klines = RisingTrend(5, 63000m, 200m);
        klines.Add(Bar(5, 64050m, 64180m, 64020m, 64150m)); // green, body 100 > 50% of range 160
        klines.Add(Bar(6, 64150m, 64200m, 64120m, 64160m)); // star, body 10 < 30% of range 80
        klines.Add(Bar(7, 64140m, 64180m, 63900m, 64080m)); // red, close below mid(64050,64150)=64100, avoids bearish engulfing

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        Assert.Contains(result.MultiPatterns, mp => mp.Pattern == MultiCandlePattern.EveningStar);
    }

    [Fact]
    public void Recognize_TweezerBottoms_Valid()
    {
        var klines = DecliningTrend(5);
        klines.Add(Bar(5, 63900m, 64100m, 63800m, 63850m)); // red
        klines.Add(Bar(6, 63900m, 64050m, 63800m, 64000m)); // green, same low, opens above previous close so no engulfing

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        Assert.Contains(result.MultiPatterns, mp => mp.Pattern == MultiCandlePattern.TweezerBottoms);
    }

    [Fact]
    public void Recognize_TweezerTops_Valid()
    {
        var klines = RisingTrend(5);
        klines.Add(Bar(5, 64100m, 64300m, 64000m, 64250m)); // green
        klines.Add(Bar(6, 64250m, 64300m, 64050m, 64100m)); // red, same high

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        Assert.Contains(result.MultiPatterns, mp => mp.Pattern == MultiCandlePattern.TweezerTops);
    }

    [Fact]
    public void Recognize_TweezerBottoms_SameColor_NotDetected()
    {
        var klines = DecliningTrend(5);
        klines.Add(Bar(5, 63900m, 64100m, 63800m, 63850m)); // red
        klines.Add(Bar(6, 63800m, 64100m, 63800m, 63900m)); // also red, same low

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        Assert.DoesNotContain(result.MultiPatterns, mp => mp.Pattern == MultiCandlePattern.TweezerBottoms);
    }

    [Fact]
    public void Recognize_TrendEvaluatedPerCandle_NotGlobal()
    {
        // 6 declining bars -> hammer shape (still in downtrend) -> 7 rising bars (window ends in uptrend)
        var klines = DecliningTrend(6, 65000m, 300m);
        klines.Add(Bar(6, 62700m, 62750m, 62300m, 62650m)); // hammer shape at index 6
        for (int i = 0; i < 7; i++)
        {
            var close = 62650m + i * 300m;
            var open = close - 300m;
            klines.Add(Bar(7 + i, open, open + 50, close - 50, close));
        }

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);
        var hammerCandle = result.Candles.First(c => c.OpenTimeMs == 6 * 3_600_000L);

        // If trend were global (last 6 bars of the whole window), this would be HangingMan/Uptrend.
        // With per-candle trend it must be Hammer because index 6 is still inside the preceding downtrend.
        Assert.Equal(SingleCandlePattern.Hammer, hammerCandle.SinglePattern);
    }

    [Fact]
    public void Recognize_LongUpperShadow_NotHammer()
    {
        var klines = DecliningTrend(6);
        klines.Add(Bar(6, 64150m, 64400m, 63800m, 64100m)); // long upper shadow -> not hammer

        var result = CandlePatternRecognizer.Recognize(klines, tailCount: klines.Count);

        Assert.NotEqual(SingleCandlePattern.Hammer, result.Candles.Last().SinglePattern);
    }
}
