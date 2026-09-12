namespace Backend.Options;

/// <summary>Khung nến được phép ghi/cập nhật trong production.</summary>
public sealed class ProductionTimeframeOptions
{
    public const string SectionName = "ProductionTimeframes";

    public List<string> Active { get; set; } = ["1h", "4h", "1d"];

    public string Default { get; set; } = "4h";
}
