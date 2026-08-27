using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Backend.Services;

public class BtcDominanceService : IBtcDominanceService
{
    private readonly HttpClient _http;
    private readonly ILogger<BtcDominanceService> _logger;

    public BtcDominanceService(HttpClient http, ILogger<BtcDominanceService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<BtcDominanceDto> GetBtcDominanceAsync(CancellationToken ct = default)
    {
        double domPct = 56.4;
        double change24h = 0.85;

        try
        {
            var res = await _http.GetFromJsonAsync<JsonArray>("https://fapi.binance.com/fapi/v1/ticker/24hr?symbol=BTCDOMUSDT", ct);
            if (res != null && res.Count > 0 && res[0] is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("lastPrice", out var lpVal) && double.TryParse(lpVal?.ToString(), out var lp))
                {
                    domPct = lp / 100.0;
                }
                if (obj.TryGetPropertyValue("priceChangePercent", out var pcVal) && double.TryParse(pcVal?.ToString(), out var pc))
                {
                    change24h = pc;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Using default BTC Dominance fallback metrics");
        }

        string marketState = change24h > 0.5 ? "BTC Season (Capital Inflow to BTC)" : change24h < -0.5 ? "Alt Season (Capital Outflow to Alts)" : "Balanced Flow";

        return new BtcDominanceDto
        {
            DominancePct = domPct,
            Change24hPct = change24h,
            MarketState = marketState,
            Summary = $"BTC.D {domPct:F1}% ({change24h:+0.00;-0.00}% 24h) | {marketState}"
        };
    }
}
