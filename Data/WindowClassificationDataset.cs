using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Data;

/// <summary>
/// Per-window classification dataset for supervised ML.
/// Each row represents a contiguous window of N bars flattened into one feature vector,
/// labelled by the future price direction at a given horizon.
/// </summary>
public class WindowClassificationDataset
{
    public long Id { get; set; }

    public string Symbol { get; set; } = "BTCUSDT";

    public string Timeframe { get; set; } = "1h";

    /// <summary>
    /// Number of bars in the window, e.g. 5, 10, 15, 20, 25.
    /// </summary>
    public int WindowSize { get; set; }

    /// <summary>
    /// Target horizon string: 1h, 4h, 1d.
    /// </summary>
    public string Horizon { get; set; } = "1d";

    /// <summary>
    /// Open time of the first bar in the window.
    /// </summary>
    public long WindowStartMs { get; set; }

    /// <summary>
    /// Open time of the last bar in the window.
    /// </summary>
    public long WindowEndMs { get; set; }

    /// <summary>
    /// Flattened feature vector (real[] in PostgreSQL).
    /// </summary>
    [Column(TypeName = "real[]")]
    public float[] FeatureVector { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Dimension of FeatureVector.
    /// </summary>
    public int FeatureDim { get; set; }

    /// <summary>
    /// Classification label: -1 = giảm, 0 = sideway, 1 = tăng.
    /// </summary>
    public int Label { get; set; }

    /// <summary>
    /// Future return (%) used to derive Label.
    /// </summary>
    public double? TargetReturn { get; set; }

    /// <summary>
    /// Average null ratio of the source MlFeatureStore rows inside the window.
    /// </summary>
    public double WindowNullRatio { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
