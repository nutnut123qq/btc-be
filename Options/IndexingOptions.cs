namespace Backend.Options;

/// <summary>Cấu hình chung cho các indexer và dataset builder.</summary>
public class IndexingOptions
{
    public const string SectionName = "Indexing";

    /// <summary>Batch size mặc định cho hầu hết các indexer.</summary>
    public int DefaultBatchSize { get; set; } = 5000;

    /// <summary>Batch size cho CandlePatternIndexer.</summary>
    public int CandlePatternBatchSize { get; set; } = 5000;

    /// <summary>Batch size cho WindowVectorIndexer.</summary>
    public int WindowVectorBatchSize { get; set; } = 5000;

    /// <summary>Batch size cho CandleVolumeIndexer.</summary>
    public int VolumeStatsBatchSize { get; set; } = 5000;

    /// <summary>Batch size cho TechnicalIndicatorIndexer.</summary>
    public int TechnicalIndicatorsBatchSize { get; set; } = 5000;

    /// <summary>Batch size cho CandlePatternSequenceIndexer.</summary>
    public int PatternSequenceBatchSize { get; set; } = 5000;

    /// <summary>Batch size cho MlDatasetService feature/target inserts.</summary>
    public int MlFeatureBatchSize { get; set; } = 10000;

    /// <summary>Batch size cho WindowDatasetService.</summary>
    public int WindowDatasetBatchSize { get; set; } = 5000;

    /// <summary>Số nến warmup trước điểm bắt đầu incremental để tính indicators chính xác.</summary>
    public int TechnicalIndicatorWarmupBars { get; set; } = 250;

    /// <summary>Số nến warmup cho ML features/targets.</summary>
    public int MlDatasetWarmupBars { get; set; } = 500;

    /// <summary>Số sample tối đa mỗi window size cho window dataset.</summary>
    public int MlRebuildMaxSamplesPerWindowSize { get; set; } = 20000;

    /// <summary>Bật chạy song song các timeframe trong FullReindexService/IndexingBackgroundWorker.</summary>
    public bool EnableParallelTimeframes { get; set; } = true;

    /// <summary>Số timeframe tối đa chạy song song.</summary>
    public int MaxParallelTimeframes { get; set; } = 4;

    /// <summary>Số nến tối đa load từ DB mỗi chunk khi streaming.</summary>
    public int KlineStreamChunkSize { get; set; } = 50000;

    /// <summary>Bật chế độ streaming khi số klines vượt quá MaxInMemoryKlines.</summary>
    public bool EnableStreamingIndexing { get; set; } = true;

    /// <summary>Số nến tối đa giữ trong memory cùng lúc khi streaming.</summary>
    public int MaxInMemoryKlines { get; set; } = 100000;

    /// <summary>Số nến gần nhất dùng cho window vectors (0 = toàn bộ).</summary>
    public int WindowVectorLookbackBars { get; set; } = 5000;

    /// <summary>Số nến gần nhất dùng cho candle patterns (0 = toàn bộ).</summary>
    public int CandlePatternLookbackBars { get; set; } = 0;

    /// <summary>Số nến gần nhất dùng cho volume stats (0 = toàn bộ).</summary>
    public int VolumeStatsLookbackBars { get; set; } = 0;

    /// <summary>Số nến gần nhất dùng cho pattern sequences (0 = toàn bộ).</summary>
    public int PatternSequenceLookbackBars { get; set; } = 5000;
}
