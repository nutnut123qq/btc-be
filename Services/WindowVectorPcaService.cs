using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

/// <summary>
/// Tính PCA đơn giản trên window vectors và lưu components vào MlFeatureStore.
/// </summary>
public class WindowVectorPcaService
{
    private readonly AppDbContext _db;
    private readonly ILogger<WindowVectorPcaService> _logger;

    public WindowVectorPcaService(
        AppDbContext db,
        ILogger<WindowVectorPcaService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> ComputeAndStoreAsync(
        string symbol,
        string timeframe,
        string featureType = "returns_shape",
        int windowSize = 25,
        int components = 5,
        CancellationToken cancellationToken = default)
    {
        var vectors = await _db.WindowVectors
            .AsNoTracking()
            .Where(v => v.Symbol == symbol && v.Timeframe == timeframe && v.FeatureType == featureType && v.WindowSize == windowSize && v.Version == WindowVectorIndexer.VectorVersion)
            .OrderBy(v => v.EndTimeMs)
            .ToListAsync(cancellationToken);

        if (vectors.Count < 100)
        {
            _logger.LogWarning("Not enough vectors ({Count}) for PCA on {Symbol} {Timeframe}", vectors.Count, symbol, timeframe);
            return 0;
        }

        // Prepare matrix
        var data = vectors.Select(v => v.Vector.Select(x => (double)x).ToArray()).ToList();
        var dim = data[0].Length;

        // Center data
        var mean = new double[dim];
        for (int d = 0; d < dim; d++)
            mean[d] = data.Average(row => row[d]);
        var centered = data.Select(row => row.Select((x, i) => x - mean[i]).ToArray()).ToList();

        // Find top K principal components using power iteration
        var pcs = new List<double[]>();
        var current = centered.Select(row => row.ToArray()).ToList();
        for (int k = 0; k < components; k++)
        {
            var pc = FindTopEigenvector(current);
            pcs.Add(pc);

            // Deflate
            for (int i = 0; i < current.Count; i++)
            {
                var projection = Dot(current[i], pc);
                for (int d = 0; d < dim; d++)
                    current[i][d] -= projection * pc[d];
            }
        }

        // Project each vector onto PCs
        var projections = new List<(long EndTimeMs, double[] Components)>();
        foreach (var row in centered)
        {
            var comps = pcs.Select(pc => Dot(row, pc)).ToArray();
            // Map by EndTimeMs
            projections.Add((vectors[projections.Count].EndTimeMs, comps));
        }

        // Update MlFeatureStore
        var existing = await _db.MlFeatureStores
            .Where(f => f.Symbol == symbol && f.Timeframe == timeframe)
            .ToListAsync(cancellationToken);
        var existingByTime = existing.ToDictionary(f => f.OpenTimeMs);

        int updated = 0;
        foreach (var proj in projections)
        {
            if (!existingByTime.TryGetValue(proj.EndTimeMs, out var feature)) continue;
            feature.PcaComponent1 = proj.Components.Length > 0 ? proj.Components[0] : null;
            feature.PcaComponent2 = proj.Components.Length > 1 ? proj.Components[1] : null;
            feature.PcaComponent3 = proj.Components.Length > 2 ? proj.Components[2] : null;
            feature.PcaComponent4 = proj.Components.Length > 3 ? proj.Components[3] : null;
            feature.PcaComponent5 = proj.Components.Length > 4 ? proj.Components[4] : null;
            updated++;
        }

        if (updated > 0)
            await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Stored PCA components for {Updated} feature rows on {Symbol} {Timeframe}", updated, symbol, timeframe);
        return updated;
    }

    private static double[] FindTopEigenvector(List<double[]> matrix)
    {
        var dim = matrix[0].Length;
        var vec = new double[dim];
        var rand = new Random(42);
        for (int d = 0; d < dim; d++)
            vec[d] = rand.NextDouble() - 0.5;
        vec = Normalize(vec);

        for (int iter = 0; iter < 100; iter++)
        {
            var next = new double[dim];
            foreach (var row in matrix)
            {
                var dot = Dot(row, vec);
                for (int d = 0; d < dim; d++)
                    next[d] += dot * row[d];
            }
            next = Normalize(next);
            if (Dot(vec, next) > 0.9999)
                return next;
            vec = next;
        }
        return vec;
    }

    private static double Dot(double[] a, double[] b)
    {
        double sum = 0;
        var n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            sum += a[i] * b[i];
        return sum;
    }

    private static double[] Normalize(double[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => x * x));
        if (norm < 1e-10) return v;
        return v.Select(x => x / norm).ToArray();
    }
}
