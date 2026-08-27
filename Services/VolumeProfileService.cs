using System.Text.Json;
using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class VolumeProfileService : IVolumeProfileService
{
    private readonly AppDbContext _db;

    public VolumeProfileService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<VolumeProfileSnapshot?> GetVolumeProfileAsync(string symbol, string timeframe, int lookbackBars, CancellationToken ct = default)
    {
        var klines = await _db.Klines
            .Where(k => k.Symbol == symbol && k.Timeframe == timeframe)
            .OrderByDescending(k => k.OpenTimeMs)
            .Take(lookbackBars)
            .ToListAsync(ct);

        if (klines.Count == 0) return null;

        var orderedKlines = klines.OrderBy(k => k.OpenTimeMs).ToList();
        double minPrice = (double)orderedKlines.Min(k => k.Low);
        double maxPrice = (double)orderedKlines.Max(k => k.High);

        if (minPrice == maxPrice) return null;

        int numBins = 30;
        double binSize = (maxPrice - minPrice) / numBins;
        var bins = new double[numBins];

        double totalVolume = 0;

        foreach (var k in orderedKlines)
        {
            totalVolume += (double)k.Volume;
            if (k.High == k.Low) continue;

            int startBin = Math.Clamp((int)(((double)k.Low - minPrice) / binSize), 0, numBins - 1);
            int endBin = Math.Clamp((int)(((double)k.High - minPrice) / binSize), 0, numBins - 1);

            int binsSpanned = endBin - startBin + 1;
            double volPerBin = (double)k.Volume / binsSpanned;

            for (int i = startBin; i <= endBin; i++)
            {
                bins[i] += volPerBin;
            }
        }

        int pocBin = 0;
        double maxBinVol = 0;
        for (int i = 0; i < numBins; i++)
        {
            if (bins[i] > maxBinVol)
            {
                maxBinVol = bins[i];
                pocBin = i;
            }
        }

        double pocPrice = minPrice + (pocBin * binSize) + (binSize / 2);

        double targetVol = totalVolume * 0.70;
        double currentVol = bins[pocBin];
        int lowerBin = pocBin - 1;
        int upperBin = pocBin + 1;

        while (currentVol < targetVol && (lowerBin >= 0 || upperBin < numBins))
        {
            double lowerVol = lowerBin >= 0 ? bins[lowerBin] : -1;
            double upperVol = upperBin < numBins ? bins[upperBin] : -1;

            if (lowerVol >= upperVol && lowerVol != -1)
            {
                currentVol += lowerVol;
                lowerBin--;
            }
            else if (upperVol != -1)
            {
                currentVol += upperVol;
                upperBin++;
            }
        }

        double valPrice = minPrice + ((lowerBin + 1) * binSize);
        double vahPrice = minPrice + (upperBin * binSize);

        var snapshot = new VolumeProfileSnapshot
        {
            Symbol = symbol,
            Timeframe = timeframe,
            WindowStartMs = orderedKlines.First().OpenTimeMs,
            WindowEndMs = orderedKlines.Last().OpenTimeMs,
            PocPrice = pocPrice,
            VahPrice = vahPrice,
            ValPrice = valPrice,
            ProfileBinsJson = JsonSerializer.Serialize(bins),
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.VolumeProfileSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(ct);

        return snapshot;
    }
}
