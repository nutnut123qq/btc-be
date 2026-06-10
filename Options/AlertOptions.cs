namespace Backend.Options;

/// <summary>Worker-only options. Alert rules live in <see cref="Backend.Data.PriceAlertSettings"/> (DB / FE).</summary>
public class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>When false, <see cref="Services.PriceAlertWorker"/> sleeps without calling Binance.</summary>
    public bool WorkerEnabled { get; set; } = true;

    /// <summary>Seconds between Binance checks.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>User id whose <see cref="Data.PriceAlertSettings"/> row the worker evaluates.</summary>
    public string DefaultUserId { get; set; } = "default";
}
