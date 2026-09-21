namespace Backend.Data;

public class AppAlert
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "default";
    /// <summary>price_above, price_below</summary>
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public decimal? PriceSnapshot { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsRead { get; set; }
    public string? SourceKey { get; set; }
    /// <summary>observed-event or validated-predictive; never inferred from the title.</summary>
    public string EvidenceKind { get; set; } = "observed-event";
    public string Provenance { get; set; } = "price-alert-worker";
    public long? AvailableTimeMs { get; set; }
    /// <summary>At-most-once external delivery state. The database alert is the canonical delivery.</summary>
    public string DeliveryStatus { get; set; } = "not-configured";
    public DateTime? DeliveryAttemptedAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public string? DeliveryError { get; set; }
    public DateTime? ArchivedAtUtc { get; set; }
}
