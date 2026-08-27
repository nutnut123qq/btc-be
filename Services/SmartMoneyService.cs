using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class SmartMoneyService : ISmartMoneyService
{
    private readonly AppDbContext _db;

    public SmartMoneyService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<SmartMoneyStructure>> GetSmartMoneyStructuresAsync(string symbol, string timeframe, int lookbackBars, CancellationToken ct = default)
    {
        var klines = await _db.Klines
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderByDescending(k => k.OpenTimeMs)
            .Take(lookbackBars)
            .ToListAsync(ct);

        if (klines.Count < 5) return new List<SmartMoneyStructure>();

        var orderedKlines = klines.OrderBy(k => k.OpenTimeMs).ToList();
        var structures = new List<SmartMoneyStructure>();
        
        var swingHighs = new List<(int Index, double Price, long TimeMs)>();
        var swingLows = new List<(int Index, double Price, long TimeMs)>();

        // Detect Swing Highs/Lows (5-bar pivot: 2 before, 2 after)
        for (int i = 2; i < orderedKlines.Count - 2; i++)
        {
            var k = orderedKlines[i];
            bool isSwingHigh = k.High > orderedKlines[i - 1].High &&
                               k.High > orderedKlines[i - 2].High &&
                               k.High > orderedKlines[i + 1].High &&
                               k.High > orderedKlines[i + 2].High;

            bool isSwingLow = k.Low < orderedKlines[i - 1].Low &&
                              k.Low < orderedKlines[i - 2].Low &&
                              k.Low < orderedKlines[i + 1].Low &&
                              k.Low < orderedKlines[i + 2].Low;

            if (isSwingHigh)
            {
                swingHighs.Add((i, (double)k.High, k.OpenTimeMs));
                structures.Add(new SmartMoneyStructure
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    TimeMs = k.OpenTimeMs,
                    EventType = "SWING_HIGH",
                    Price = (double)k.High,
                    Description = "Swing High"
                });
            }

            if (isSwingLow)
            {
                swingLows.Add((i, (double)k.Low, k.OpenTimeMs));
                structures.Add(new SmartMoneyStructure
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    TimeMs = k.OpenTimeMs,
                    EventType = "SWING_LOW",
                    Price = (double)k.Low,
                    Description = "Swing Low"
                });
            }
        }

        // BOS & CHOCH logic
        int currentTrend = 0; // 1 = Bullish, -1 = Bearish, 0 = neutral
        double lastSwingHigh = -1;
        double lastSwingLow = -1;

        for (int i = 0; i < orderedKlines.Count; i++)
        {
            var k = orderedKlines[i];

            // update current last swings if we pass them
            var sh = swingHighs.Where(x => x.Index <= i).LastOrDefault();
            if (sh.Index != 0) lastSwingHigh = sh.Price;

            var sl = swingLows.Where(x => x.Index <= i).LastOrDefault();
            if (sl.Index != 0) lastSwingLow = sl.Price;

            if (lastSwingHigh > 0 && (double)k.Close > lastSwingHigh)
            {
                if (currentTrend == 1)
                {
                    // BOS Bull
                    if (!structures.Any(s => s.EventType == "BOS_BULL" && s.TimeMs == k.OpenTimeMs))
                    {
                        structures.Add(new SmartMoneyStructure
                        {
                            Symbol = symbol, Timeframe = timeframe, TimeMs = k.OpenTimeMs,
                            EventType = "BOS_BULL", Price = (double)k.Close, Description = "Bullish BOS"
                        });
                        lastSwingHigh = -1; // consume it
                    }
                }
                else if (currentTrend == -1 || currentTrend == 0)
                {
                    // CHOCH Bull
                    if (!structures.Any(s => s.EventType == "CHOCH_BULL" && s.TimeMs == k.OpenTimeMs))
                    {
                        structures.Add(new SmartMoneyStructure
                        {
                            Symbol = symbol, Timeframe = timeframe, TimeMs = k.OpenTimeMs,
                            EventType = "CHOCH_BULL", Price = (double)k.Close, Description = "Bullish CHOCH"
                        });
                        currentTrend = 1;
                        lastSwingHigh = -1;
                    }
                }
            }

            if (lastSwingLow > 0 && (double)k.Close < lastSwingLow)
            {
                if (currentTrend == -1)
                {
                    // BOS Bear
                    if (!structures.Any(s => s.EventType == "BOS_BEAR" && s.TimeMs == k.OpenTimeMs))
                    {
                        structures.Add(new SmartMoneyStructure
                        {
                            Symbol = symbol, Timeframe = timeframe, TimeMs = k.OpenTimeMs,
                            EventType = "BOS_BEAR", Price = (double)k.Close, Description = "Bearish BOS"
                        });
                        lastSwingLow = -1; // consume it
                    }
                }
                else if (currentTrend == 1 || currentTrend == 0)
                {
                    // CHOCH Bear
                    if (!structures.Any(s => s.EventType == "CHOCH_BEAR" && s.TimeMs == k.OpenTimeMs))
                    {
                        structures.Add(new SmartMoneyStructure
                        {
                            Symbol = symbol, Timeframe = timeframe, TimeMs = k.OpenTimeMs,
                            EventType = "CHOCH_BEAR", Price = (double)k.Close, Description = "Bearish CHOCH"
                        });
                        currentTrend = -1;
                        lastSwingLow = -1;
                    }
                }
            }
        }

        // FVG Logic
        for (int i = 2; i < orderedKlines.Count; i++)
        {
            var k0 = orderedKlines[i - 2];
            var k1 = orderedKlines[i - 1]; // the gap candle
            var k2 = orderedKlines[i];

            // Bullish FVG
            if (k0.High < k2.Low)
            {
                var fvg = new SmartMoneyStructure
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    TimeMs = k1.OpenTimeMs, // align to the gap candle
                    EventType = "FVG_BULL",
                    Price = (double)(k0.High + k2.Low) / 2, // mid price
                    HighPrice = (double)k2.Low,
                    LowPrice = (double)k0.High,
                    IsMitigated = false,
                    Description = "Bullish FVG"
                };

                // Check mitigation
                for (int j = i + 1; j < orderedKlines.Count; j++)
                {
                    if ((double)orderedKlines[j].Low <= fvg.LowPrice)
                    {
                        fvg.IsMitigated = true;
                        break;
                    }
                }
                structures.Add(fvg);
            }

            // Bearish FVG
            if (k0.Low > k2.High)
            {
                var fvg = new SmartMoneyStructure
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    TimeMs = k1.OpenTimeMs,
                    EventType = "FVG_BEAR",
                    Price = (double)(k0.Low + k2.High) / 2,
                    HighPrice = (double)k0.Low,
                    LowPrice = (double)k2.High,
                    IsMitigated = false,
                    Description = "Bearish FVG"
                };

                // Check mitigation
                for (int j = i + 1; j < orderedKlines.Count; j++)
                {
                    if ((double)orderedKlines[j].High >= fvg.HighPrice)
                    {
                        fvg.IsMitigated = true;
                        break;
                    }
                }
                structures.Add(fvg);
            }
        }

        _db.SmartMoneyStructures.AddRange(structures);
        await _db.SaveChangesAsync(ct);

        return structures;
    }
}
