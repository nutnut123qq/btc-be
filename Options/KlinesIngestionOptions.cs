namespace Backend.Options;

/// <summary>Cấu hình cho <see cref="Services.KlinesIngestionWorker"/>.</summary>
public class KlinesIngestionOptions
{
    public const string SectionName = "KlinesIngestion";

    /// <summary>Ngày bắt đầu (UTC) để kiểm tra và backfill gaps trong bảng Klines.</summary>
    public DateTime BackfillStartDate { get; set; } = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Số request Binance tối đa trong một chu kỳ worker (latest + backfill).</summary>
    public int MaxRequestsPerCycle { get; set; } = 60;

    /// <summary>Số lượng gaps tối đa được xử lý cho mỗi timeframe trong một chu kỳ.</summary>
    public int MaxGapsPerTimeframe { get; set; } = 50;
}
