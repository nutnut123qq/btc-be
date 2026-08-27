using Backend.Data;
using Microsoft.EntityFrameworkCore;
using Skender.Stock.Indicators;
using System.Text.Json;

namespace Backend.Services;

public class RegimeDetectionService : IRegimeDetectionService
{
    private readonly AppDbContext _db;

    public RegimeDetectionService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<MarketRegime?> GetCurrentRegimeAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        return await _db.MarketRegimes
            .Where(r => r.Symbol == symbol && r.Timeframe == timeframe)
            .OrderByDescending(r => r.OpenTimeMs)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<MarketRegime>> GetRegimeHistoryAsync(string symbol, string timeframe, int limit, CancellationToken ct = default)
    {
        return await _db.MarketRegimes
            .Where(r => r.Symbol == symbol && r.Timeframe == timeframe)
            .OrderByDescending(r => r.OpenTimeMs)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task BuildRegimesAsync(string symbol, string timeframe, int lookbackBars, CancellationToken ct = default)
    {
        // Load recent klines
        var klines = await _db.Klines
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderByDescending(k => k.OpenTimeMs)
            .Take(lookbackBars + 300) // extra for indicator warmup
            .ToListAsync(ct);
        
        klines.Reverse(); // oldest first

        if (klines.Count < 200) return; // not enough data

        var quotes = klines.Select(k => new Quote
        {
            Date = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime,
            Open = k.Open,
            High = k.High,
            Low = k.Low,
            Close = k.Close,
            Volume = k.Volume
        }).ToList();

        var adxResults = quotes.GetAdx(14).ToList();
        var atrResults = quotes.GetAtr(14).ToList();
        var bbResults = quotes.GetBollingerBands(20, 2).ToList();
        var ema50Results = quotes.GetEma(50).ToList();
        var ema200Results = quotes.GetEma(200).ToList();
        var volSmaResults = quotes.Select(q => new Quote { Date = q.Date, Close = q.Volume }).GetSma(20).ToList();

        var regimes = new List<MarketRegime>();
        MarketRegime? lastRegime = null;

        for (int i = 200; i < klines.Count; i++) // start after warmup
        {
            var k = klines[i];
            
            var adx = adxResults[i].Adx ?? 0;
            var plusDi = adxResults[i].Pdi ?? 0;
            var minusDi = adxResults[i].Mdi ?? 0;
            
            var atr = atrResults[i].Atr ?? 0;
            // compute atr_sma50 manually using previous atrs
            double sumAtr = 0;
            int countAtr = 0;
            for(int j=i-49; j<=i; j++) {
                if(atrResults[j].Atr.HasValue) {
                    sumAtr += atrResults[j].Atr.Value;
                    countAtr++;
                }
            }
            double atrSma50 = countAtr > 0 ? sumAtr / countAtr : 1;
            if (atrSma50 == 0) atrSma50 = 1;
            var atrRatio = atr / atrSma50;

            var bb = bbResults[i];
            double bbWidth = 0;
            if (bb.Sma.HasValue && bb.Sma.Value != 0)
            {
                bbWidth = (double)((bb.UpperBand - bb.LowerBand) / bb.Sma);
            }

            var volSma = volSmaResults[i].Sma ?? 1;
            var volume = (double)k.Volume;

            var ema50 = ema50Results[i].Ema;
            var ema200 = ema200Results[i].Ema;

            // Classify
            string regimeType = "RangeBound";
            if (bbWidth < 0.03 || (atrRatio < 0.7 && adx < 20))
            {
                regimeType = "Compression";
            }
            else if (atrRatio > 1.5 && (adx > 20 || volume > 1.5 * (double)volSma))
            {
                regimeType = "Breakout";
            }
            else if (adx >= 25 && plusDi > minusDi && ema50 > ema200)
            {
                regimeType = "TrendingUp";
            }
            else if (adx >= 25 && minusDi > plusDi && ema50 < ema200)
            {
                regimeType = "TrendingDown";
            }
            else
            {
                regimeType = "RangeBound";
            }

            var regime = new MarketRegime
            {
                Symbol = symbol,
                Timeframe = timeframe,
                OpenTimeMs = k.OpenTimeMs,
                RegimeType = regimeType,
                TrendStrength = adx,
                VolatilityScore = atrRatio,
                Adx = adx,
                PlusDi = plusDi,
                MinusDi = minusDi,
                AtrRatio = atrRatio,
                BollingerBandwidth = bbWidth,
                CreatedAtUtc = DateTime.UtcNow
            };

            regimes.Add(regime);

            if (lastRegime != null && lastRegime.RegimeType != regimeType)
            {
                var transition = new RegimeTransition
                {
                    Symbol = symbol,
                    Timeframe = timeframe,
                    FromRegime = lastRegime.RegimeType,
                    ToRegime = regimeType,
                    TransitionTimeMs = k.OpenTimeMs,
                    DurationBars = i - klines.FindIndex(x => x.OpenTimeMs == lastRegime.OpenTimeMs), // approximate
                    CreatedAtUtc = DateTime.UtcNow
                };
                _db.RegimeTransitions.Add(transition);
            }

            // Keep track of first in series to compute duration correct?
            // Actually lastRegime can just be updated to the latest, but we need to track duration.
            // A simple way is just:
            if (lastRegime == null || lastRegime.RegimeType != regimeType) {
                lastRegime = regime;
            }
        }

        // Upsert Regimes (for simplicity just delete existing and add new in real scenario or update)
        // Since we are running this in batch, we can do a naive upsert.
        // EF Core 8: ExecuteUpdate or ExecuteDelete then Add.
        var newTimes = regimes.Select(r => r.OpenTimeMs).ToList();
        var existingTimes = await _db.MarketRegimes
            .Where(r => r.Symbol == symbol && r.Timeframe == timeframe && newTimes.Contains(r.OpenTimeMs))
            .Select(r => r.OpenTimeMs)
            .ToListAsync(ct);

        var toInsert = regimes.Where(r => !existingTimes.Contains(r.OpenTimeMs)).ToList();
        _db.MarketRegimes.AddRange(toInsert);

        await _db.SaveChangesAsync(ct);
    }

    public async Task<object> GetRegimeSummaryAsync(string symbol, string timeframe, CancellationToken ct = default)
    {
        var regimes = await _db.MarketRegimes
            .Where(r => r.Symbol == symbol && r.Timeframe == timeframe)
            .OrderByDescending(r => r.OpenTimeMs)
            .Take(100)
            .ToListAsync(ct);
            
        if (!regimes.Any()) return new { message = "No regimes found." };

        var count = regimes.Count;
        var summary = regimes.GroupBy(r => r.RegimeType)
                             .Select(g => new { RegimeType = g.Key, Percentage = Math.Round((double)g.Count() / count * 100, 2) })
                             .ToList();

        var current = regimes.First();
        return new
        {
            CurrentRegime = current.RegimeType,
            TrendStrength = current.TrendStrength,
            DistributionLast100 = summary
        };
    }
}
