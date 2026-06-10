namespace Backend.Services.Models;

public class PatternSearchRequest
{
    public string Symbol { get; set; } = "BTCUSDT";
    public string Timeframe { get; set; } = "15m";
    public string FeatureType { get; set; } = "returns_shape";
    public int LookbackBars { get; set; } = 2000;
    public int WindowSize { get; set; } = 10;
    public int TopK { get; set; } = 10;
    public int? MinGapBars { get; set; }
}

public class PatternSearchItem
{
    public string WindowId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public string FeatureType { get; set; } = string.Empty;
    public long StartTimeMs { get; set; }
    public long EndTimeMs { get; set; }
    public double Distance { get; set; }
    public double Similarity { get; set; }
    public int Rank { get; set; }
}

public class PatternSearchResponse
{
    public string RequestId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public string FeatureType { get; set; } = string.Empty;
    public int WindowSize { get; set; }
    public int TopK { get; set; }
    public int ScannedWindows { get; set; }
    public bool FromVectorStore { get; set; }
    public int LatencyMs { get; set; }
    public List<PatternSearchItem> Items { get; set; } = [];
}
