using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Phân tích cấu trúc thị trường (Market Structure) từ chuỗi nến.
/// Xác định Swing High/Low, HH/HL/LH/LL, BOS, CHoCH.
/// </summary>
public static class MarketStructureAnalyzer
{
    public static MarketStructureResult Analyze(IReadOnlyList<KlineDto> klines, int swingLookback = 5)
    {
        if (klines.Count < swingLookback * 2 + 1)
            return new MarketStructureResult { SummaryText = "Không đủ nến để phân tích cấu trúc." };

        var swings = new List<SwingPoint>();
        var highs = klines.Select((k, i) => (Index: i, High: k.High)).ToList();
        var lows = klines.Select((k, i) => (Index: i, Low: k.Low)).ToList();

        for (int i = swingLookback; i < klines.Count - swingLookback; i++)
        {
            var currentHigh = highs[i].High;
            var currentLow = lows[i].Low;

            bool isSwingHigh = true;
            bool isSwingLow = true;

            for (int j = 1; j <= swingLookback; j++)
            {
                if (highs[i - j].High >= currentHigh || highs[i + j].High >= currentHigh)
                    isSwingHigh = false;
                if (lows[i - j].Low <= currentLow || lows[i + j].Low <= currentLow)
                    isSwingLow = false;
            }

            if (isSwingHigh)
                swings.Add(new SwingPoint { Index = i, TimeMs = klines[i].OpenTimeMs, Price = currentHigh, Type = SwingType.High });
            if (isSwingLow)
                swings.Add(new SwingPoint { Index = i, TimeMs = klines[i].OpenTimeMs, Price = currentLow, Type = SwingType.Low });
        }

        swings = swings.OrderBy(s => s.Index).ToList();

        // Phân loại HH/HL/LH/LL
        var labeledSwings = new List<LabeledSwing>();
        for (int i = 0; i < swings.Count; i++)
        {
            var label = SwingLabel.None;
            if (i >= 2)
            {
                var prevSameType = swings.Take(i).Where(s => s.Type == swings[i].Type).LastOrDefault();
                if (prevSameType != null)
                {
                    if (swings[i].Type == SwingType.High)
                        label = swings[i].Price > prevSameType.Price ? SwingLabel.HigherHigh : SwingLabel.LowerHigh;
                    else
                        label = swings[i].Price > prevSameType.Price ? SwingLabel.HigherLow : SwingLabel.LowerLow;
                }
            }
            labeledSwings.Add(new LabeledSwing { Swing = swings[i], Label = label });
        }

        // Detect BOS và CHoCH
        var events = new List<StructureEvent>();
        for (int i = 1; i < labeledSwings.Count; i++)
        {
            var curr = labeledSwings[i];
            var prev = labeledSwings[i - 1];

            // BOS: Giá phá vỡ đỉnh/đáy trước đó theo xu hướng
            if (curr.Swing.Type == SwingType.High && curr.Label == SwingLabel.HigherHigh && prev.Swing.Type == SwingType.Low)
                events.Add(new StructureEvent { TimeMs = curr.Swing.TimeMs, Type = StructureEventType.BOS, Message = "Break of Structure: Higher High", Price = curr.Swing.Price });
            if (curr.Swing.Type == SwingType.Low && curr.Label == SwingLabel.LowerLow && prev.Swing.Type == SwingType.High)
                events.Add(new StructureEvent { TimeMs = curr.Swing.TimeMs, Type = StructureEventType.BOS, Message = "Break of Structure: Lower Low", Price = curr.Swing.Price });

            // CHoCH: Đảo chiều cấu trúc (Low sau Higher High thấp hơn Low trước, hoặc High sau Lower Low cao hơn High trước)
            if (curr.Swing.Type == SwingType.Low && curr.Label == SwingLabel.LowerLow && prev.Swing.Type == SwingType.High && prev.Label == SwingLabel.HigherHigh)
                events.Add(new StructureEvent { TimeMs = curr.Swing.TimeMs, Type = StructureEventType.CHoCH, Message = "Change of Character: Bullish to Bearish", Price = curr.Swing.Price });
            if (curr.Swing.Type == SwingType.High && curr.Label == SwingLabel.LowerHigh && prev.Swing.Type == SwingType.Low && prev.Label == SwingLabel.HigherLow)
                events.Add(new StructureEvent { TimeMs = curr.Swing.TimeMs, Type = StructureEventType.CHoCH, Message = "Change of Character: Bearish to Bullish", Price = curr.Swing.Price });
        }

        var lastSwing = labeledSwings.LastOrDefault();
        var trend = TrendDirection.Sideways;
        if (lastSwing != null)
        {
            var recentHighs = labeledSwings.Where(s => s.Swing.Type == SwingType.High).TakeLast(2).ToList();
            var recentLows = labeledSwings.Where(s => s.Swing.Type == SwingType.Low).TakeLast(2).ToList();
            if (recentHighs.Count == 2 && recentLows.Count == 2)
            {
                bool hh = recentHighs[1].Label == SwingLabel.HigherHigh;
                bool hl = recentLows[1].Label == SwingLabel.HigherLow;
                bool lh = recentHighs[1].Label == SwingLabel.LowerHigh;
                bool ll = recentLows[1].Label == SwingLabel.LowerLow;

                if (hh && hl) trend = TrendDirection.Uptrend;
                else if (lh && ll) trend = TrendDirection.Downtrend;
            }
        }

        return new MarketStructureResult
        {
            Swings = labeledSwings,
            Events = events,
            CurrentTrend = trend,
            SummaryText = BuildSummary(labeledSwings, events, trend)
        };
    }

    private static string BuildSummary(List<LabeledSwing> swings, List<StructureEvent> events, TrendDirection trend)
    {
        var lines = new List<string>();
        lines.Add($"Market Structure: {swings.Count} swing points detected. Current trend: {trend}.");
        if (events.Count > 0)
        {
            var bos = events.Count(e => e.Type == StructureEventType.BOS);
            var choch = events.Count(e => e.Type == StructureEventType.CHoCH);
            lines.Add($"Events: {bos} BOS, {choch} CHoCH.");
            foreach (var ev in events.TakeLast(3))
                lines.Add($"- [{ev.Type}] {ev.Message} @ {ev.Price:F2}");
        }
        return string.Join("\n", lines);
    }
}

public enum SwingType { High, Low }

public enum SwingLabel
{
    None,
    HigherHigh,   // HH
    LowerHigh,    // LH
    HigherLow,    // HL
    LowerLow      // LL
}

public enum StructureEventType { BOS, CHoCH }

public class SwingPoint
{
    public int Index { get; set; }
    public long TimeMs { get; set; }
    public decimal Price { get; set; }
    public SwingType Type { get; set; }
}

public class LabeledSwing
{
    public SwingPoint Swing { get; set; } = new();
    public SwingLabel Label { get; set; }
}

public class StructureEvent
{
    public long TimeMs { get; set; }
    public StructureEventType Type { get; set; }
    public string Message { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public class MarketStructureResult
{
    public IReadOnlyList<LabeledSwing> Swings { get; set; } = Array.Empty<LabeledSwing>();
    public IReadOnlyList<StructureEvent> Events { get; set; } = Array.Empty<StructureEvent>();
    public TrendDirection CurrentTrend { get; set; }
    public string SummaryText { get; set; } = string.Empty;
}
