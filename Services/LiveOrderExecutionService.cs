using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Backend.Services;

public class LiveOrderExecutionService : ILiveOrderExecutionService
{
    private readonly HttpClient _http;
    private readonly ILogger<LiveOrderExecutionService> _logger;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _baseUrl;
    private readonly string _tradingMode;

    public string TradingMode => _tradingMode;
    public string BaseUrl => _baseUrl;

    public LiveOrderExecutionService(
        HttpClient http,
        IConfiguration config,
        ILogger<LiveOrderExecutionService> logger)
    {
        _http = http;
        _logger = logger;

        _baseUrl = config["BinanceTestnet:BaseUrl"] ?? "https://testnet.binancefuture.com";
        _apiKey = config["BinanceTestnet:ApiKey"] ?? "";
        _apiSecret = config["BinanceTestnet:ApiSecret"] ?? "";

        var rawMode = config["BinanceTestnet:TradingMode"] ?? "Paper";
        _tradingMode = string.Equals(rawMode, "Live", StringComparison.OrdinalIgnoreCase) ? "Live"
            : (string.Equals(rawMode, "Testnet", StringComparison.OrdinalIgnoreCase) || string.Equals(rawMode, "LiveTestnet", StringComparison.OrdinalIgnoreCase)) ? "Testnet"
            : "Paper";

        // Fail-closed safety check: block production exchange endpoints unless explicitly permitted
        var isProdUrl = _baseUrl.Contains("fapi.binance.com", StringComparison.OrdinalIgnoreCase) ||
                        _baseUrl.Contains("api.binance.com", StringComparison.OrdinalIgnoreCase);
        var allowProd = config.GetValue<bool>("BinanceTestnet:AllowProductionTrading", false);
        if (isProdUrl && !allowProd)
        {
            _logger.LogError("[TradingSafety] Production Binance URL detected ({Url}) without explicit AllowProductionTrading=true flag.", _baseUrl);
            throw new InvalidOperationException($"Production Binance Futures URL ({_baseUrl}) blocked by fail-closed safety guardrail. Only testnet.binancefuture.com is allowed.");
        }

        _http.BaseAddress = new Uri(_baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<BinanceOrderResult> PlaceMarketOrderAsync(
        string symbol,
        string side,
        decimal quantity,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["side"] = side.ToUpperInvariant(),
            ["type"] = "MARKET",
            ["quantity"] = quantity.ToString("F3", CultureInfo.InvariantCulture),
        };

        return await ExecuteSignedRequestAsync("/fapi/v1/order", HttpMethod.Post, parameters, cancellationToken);
    }

    public async Task<BinanceOrderResult> PlaceStopLossOrderAsync(
        string symbol,
        string side,
        decimal stopPrice,
        decimal quantity,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["side"] = side.ToUpperInvariant(),
            ["type"] = "STOP_MARKET",
            ["stopPrice"] = stopPrice.ToString("F2", CultureInfo.InvariantCulture),
            ["quantity"] = quantity.ToString("F3", CultureInfo.InvariantCulture),
            ["reduceOnly"] = "true",
        };

        return await ExecuteSignedRequestAsync("/fapi/v1/order", HttpMethod.Post, parameters, cancellationToken);
    }

    public async Task<BinanceOrderResult> PlaceTakeProfitOrderAsync(
        string symbol,
        string side,
        decimal takeProfitPrice,
        decimal quantity,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["side"] = side.ToUpperInvariant(),
            ["type"] = "TAKE_PROFIT_MARKET",
            ["stopPrice"] = takeProfitPrice.ToString("F2", CultureInfo.InvariantCulture),
            ["quantity"] = quantity.ToString("F3", CultureInfo.InvariantCulture),
            ["reduceOnly"] = "true",
        };

        return await ExecuteSignedRequestAsync("/fapi/v1/order", HttpMethod.Post, parameters, cancellationToken);
    }

    public async Task<BinanceAccountBalanceResult> GetAccountBalanceAsync(
        CancellationToken cancellationToken = default)
    {
        if (_tradingMode == "Paper" || string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_apiSecret))
        {
            _logger.LogInformation("[BinanceTestnet] Paper mode: returning simulated account balance ($10,000 USDT)");
            return new BinanceAccountBalanceResult
            {
                Success = true,
                TotalWalletBalance = 10000.00m,
                AvailableBalance = 10000.00m,
                TotalUnrealizedProfit = 0.00m,
                TradingMode = _tradingMode,
                RawResponseJson = "{\"simulated\": true, \"balance\": 10000.0}",
            };
        }

        try
        {
            var parameters = new Dictionary<string, string>();
            var queryString = BuildSignedQueryString(parameters);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/fapi/v2/account?{queryString}");
            req.Headers.Add("X-MBX-APIKEY", _apiKey);

            using var resp = await _http.SendAsync(req, cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[BinanceTestnet] Account balance failed: {Status} {Json}", resp.StatusCode, json);
                return new BinanceAccountBalanceResult
                {
                    Success = false,
                    ErrorMessage = $"HTTP {resp.StatusCode}: {json}",
                    TradingMode = _tradingMode,
                    RawResponseJson = json,
                };
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            decimal totalWallet = 0;
            decimal totalAvailable = 0;
            decimal unrealized = 0;

            if (root.TryGetProperty("totalWalletBalance", out var twb) && decimal.TryParse(twb.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedTwb))
                totalWallet = parsedTwb;
            if (root.TryGetProperty("availableBalance", out var ab) && decimal.TryParse(ab.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAb))
                totalAvailable = parsedAb;
            if (root.TryGetProperty("totalUnrealizedProfit", out var up) && decimal.TryParse(up.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedUp))
                unrealized = parsedUp;

            return new BinanceAccountBalanceResult
            {
                Success = true,
                TotalWalletBalance = totalWallet,
                AvailableBalance = totalAvailable,
                TotalUnrealizedProfit = unrealized,
                TradingMode = _tradingMode,
                RawResponseJson = json,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BinanceTestnet] Exception getting account balance");
            return new BinanceAccountBalanceResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                TradingMode = _tradingMode,
            };
        }
    }

    public async Task<BinanceOrderResult> CancelAllOpenOrdersAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
        };

        return await ExecuteSignedRequestAsync("/fapi/v1/allOpenOrders", HttpMethod.Delete, parameters, cancellationToken);
    }

    private async Task<BinanceOrderResult> ExecuteSignedRequestAsync(
        string endpoint,
        HttpMethod method,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var queryString = BuildSignedQueryString(parameters);
        var symbol = parameters.GetValueOrDefault("symbol", "");
        var side = parameters.GetValueOrDefault("side", "");

        if (_tradingMode == "Paper" || string.IsNullOrEmpty(_apiKey) || string.IsNullOrEmpty(_apiSecret))
        {
            var simulatedOrderId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _logger.LogInformation(
                "[BinanceTestnet] Paper Mode Simulated Execution: {Method} {Endpoint}?{QueryString}",
                method, endpoint, queryString);

            return new BinanceOrderResult
            {
                Success = true,
                OrderId = simulatedOrderId,
                ClientOrderId = $"sim_{simulatedOrderId}",
                Symbol = symbol,
                Side = side,
                Status = "FILLED",
                ExecutedQty = parameters.TryGetValue("quantity", out var qStr) && decimal.TryParse(qStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var q) ? q : 0,
                AvgPrice = parameters.TryGetValue("stopPrice", out var spStr) && decimal.TryParse(spStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var sp) ? sp : 0,
                TradingMode = "Paper (Simulated HMAC)",
                RawResponseJson = $"{{\"simulated\": true, \"endpoint\": \"{endpoint}\", \"query\": \"{queryString}\"}}",
            };
        }

        try
        {
            HttpRequestMessage req;
            if (method == HttpMethod.Post)
            {
                req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                req.Content = new StringContent(queryString, Encoding.UTF8, "application/x-www-form-urlencoded");
            }
            else if (method == HttpMethod.Delete)
            {
                req = new HttpRequestMessage(HttpMethod.Delete, $"{endpoint}?{queryString}");
            }
            else
            {
                req = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}?{queryString}");
            }

            req.Headers.Add("X-MBX-APIKEY", _apiKey);

            using var resp = await _http.SendAsync(req, cancellationToken);
            var json = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[BinanceTestnet] Order request failed: {Status} {Json}", resp.StatusCode, json);
                return new BinanceOrderResult
                {
                    Success = false,
                    Symbol = symbol,
                    Side = side,
                    Status = "REJECTED",
                    ErrorMessage = $"HTTP {resp.StatusCode}: {json}",
                    TradingMode = _tradingMode,
                    RawResponseJson = json,
                };
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            long orderId = root.TryGetProperty("orderId", out var oid) ? oid.GetInt64() : 0;
            string clientOid = root.TryGetProperty("clientOrderId", out var coid) ? coid.GetString() ?? "" : "";
            string status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "NEW" : "NEW";

            decimal execQty = 0;
            if (root.TryGetProperty("executedQty", out var eq) && decimal.TryParse(eq.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var peq))
                execQty = peq;

            decimal avgP = 0;
            if (root.TryGetProperty("avgPrice", out var ap) && decimal.TryParse(ap.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pap))
                avgP = pap;

            return new BinanceOrderResult
            {
                Success = true,
                OrderId = orderId,
                ClientOrderId = clientOid,
                Symbol = symbol,
                Side = side,
                Status = status,
                ExecutedQty = execQty,
                AvgPrice = avgP,
                TradingMode = _tradingMode,
                RawResponseJson = json,
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[BinanceTestnet] Exception executing order {Symbol} {Side}", symbol, side);
            return new BinanceOrderResult
            {
                Success = false,
                Symbol = symbol,
                Side = side,
                Status = "ERROR",
                ErrorMessage = ex.Message,
                TradingMode = _tradingMode,
            };
        }
    }

    private string BuildSignedQueryString(Dictionary<string, string> parameters)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        parameters["timestamp"] = timestamp.ToString();
        parameters["recvWindow"] = "5000";

        var sorted = parameters.OrderBy(p => p.Key);
        var query = string.Join("&", sorted.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

        if (!string.IsNullOrEmpty(_apiSecret))
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
            var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(query));
            var signature = Convert.ToHexString(hashBytes).ToLowerInvariant();
            query += $"&signature={signature}";
        }

        return query;
    }
}
