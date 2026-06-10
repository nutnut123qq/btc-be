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
}
