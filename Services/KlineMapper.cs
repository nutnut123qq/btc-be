using Backend.Data;
using Backend.Services.Models;

namespace Backend.Services;

public static class KlineMapper
{
    public static KlineDto ToDto(Kline k) => new()
    {
        OpenTimeMs = k.OpenTimeMs,
        CloseTimeMs = k.CloseTimeMs,
        TimeIso = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime.ToString("O"),
        Open = k.Open,
        High = k.High,
        Low = k.Low,
        Close = k.Close,
        Volume = k.Volume,
        QuoteVolume = k.QuoteVolume,
        TradeCount = k.TradeCount,
        TakerBuyVolume = k.TakerBuyVolume,
        TakerBuyQuoteVolume = k.TakerBuyQuoteVolume
    };

    public static IReadOnlyList<KlineDto> ToDtoList(IEnumerable<Kline> klines) => klines.Select(ToDto).ToList();
}
