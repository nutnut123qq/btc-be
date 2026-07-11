using Backend.Options;

namespace Backend.Services;

/// <summary>
/// Helper tính toán range và chunk cho incremental indexing.
/// Giúp các indexer chỉ load dữ liệu cần thiết thay vì toàn bộ bảng.
/// </summary>
public static class IndexingRangeHelper
{
    /// <summary>
    /// Tính startMs để load klines cho incremental indexing.
    /// Nếu chưa có dữ liệu (maxExistingMs = null), trả về 0 (load từ đầu).
    /// Nếu đã có dữ liệu, trả về maxExistingMs - warmupBars * intervalMs.
    /// </summary>
    public static long ComputeIncrementalStartMs(long? maxExistingMs, int warmupBars, long intervalMs)
    {
        if (!maxExistingMs.HasValue) return 0L;
        return Math.Max(0L, maxExistingMs.Value - warmupBars * intervalMs);
    }

    /// <summary>
    /// Giới hạn số nến trong list theo lookbackBars gần nhất.
    /// Nếu lookbackBars <= 0, trả về nguyên list.
    /// </summary>
    public static IReadOnlyList<T> ApplyLookback<T>(IReadOnlyList<T> source, int lookbackBars)
    {
        if (lookbackBars <= 0 || source.Count <= lookbackBars) return source;
        return new SliceView<T>(source, source.Count - lookbackBars, lookbackBars);
    }

    /// <summary>
    /// Chia dải [startMs, endMs] thành các chunk có kích thước cố định (tính theo số nến).
    /// Mỗi chunk overlap với chunk trước warmupBars để các chỉ báo kỹ thuật liên tục.
    /// </summary>
    public static List<(long StartMs, long EndMs)> BuildChunks(long startMs, long endMs, long intervalMs, int chunkSizeBars, int warmupBars)
    {
        if (chunkSizeBars <= 0) chunkSizeBars = 50000;
        if (warmupBars < 0) warmupBars = 0;

        var chunks = new List<(long, long)>();
        var interval = Math.Max(1L, intervalMs);
        var totalBars = (int)((endMs - startMs) / interval) + 1;

        if (totalBars <= chunkSizeBars)
        {
            chunks.Add((startMs, endMs));
            return chunks;
        }

        var chunkActualSize = chunkSizeBars - warmupBars;
        if (chunkActualSize <= 0) chunkActualSize = chunkSizeBars / 2;

        var currentEnd = endMs;
        while (currentEnd >= startMs)
        {
            var chunkStartOffset = (chunkActualSize - 1 + warmupBars) * interval;
            var currentStart = Math.Max(startMs, currentEnd - chunkStartOffset);
            chunks.Add((currentStart, currentEnd));
            if (currentStart <= startMs) break;
            currentEnd = currentStart + warmupBars * interval;
        }

        chunks.Reverse();
        return chunks;
    }

    /// <summary>
    /// Tính số nến ước tính trong dải [startMs, endMs].
    /// </summary>
    public static int EstimateBarCount(long startMs, long endMs, long intervalMs)
    {
        if (intervalMs <= 0) return 0;
        return (int)((endMs - startMs) / intervalMs) + 1;
    }
}
