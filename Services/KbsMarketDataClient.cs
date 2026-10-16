using System.Globalization;
using System.Net;
using System.Text.Json;
using Backend.Options;
using Backend.Services.Models;

namespace Backend.Services;

/// <summary>Cross-check provider: KBS buddy-wts endpoints. Diagnostic only — never a write source.</summary>
public interface IKbsOhlcvClient
{
    Task<IReadOnlyList<ProviderOhlcvBar>> GetDailyBarsAsync(
        string symbol, DateOnly startLocal, DateOnly endLocal, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProviderOhlcvBar>> GetHourlyBarsAsync(
        string symbol, DateOnly startLocal, DateOnly endLocal, CancellationToken cancellationToken = default);
}

/// <summary>
/// KBS OHLC fetch for cross-checking VCI canonical MBB data.
/// GET {base}/iis-server/investment/stocks/{symbol}/data_day|data_60P?sdate=DD-MM-YYYY&edate=DD-MM-YYYY
/// returns { "data_day": [ { "t":"yyyy-MM-dd HH:mm" (local VN), "o":..,"h":..,"l":..,"c":..,"v":.. } ] }.
/// KBS daily history is shorter (2020+) and integer-rounded, so it validates VCI rather
/// than feeding the store; provenance separation is enforced by never writing its bars.
/// </summary>
public sealed class KbsMarketDataClient : IKbsOhlcvClient
{
    internal const string ProviderName = "KBS";
    private const string PathPrefix = "/iis-server/investment/stocks";

    private readonly HttpClient _http;
    private readonly MbbIngestionOptions _options;
    private readonly ILogger<KbsMarketDataClient> _logger;

    public KbsMarketDataClient(HttpClient http, MbbIngestionOptions? options = null,
        ILogger<KbsMarketDataClient>? logger = null)
    {
        _http = http;
        _options = options ?? new MbbIngestionOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<KbsMarketDataClient>.Instance;
    }

    public Task<IReadOnlyList<ProviderOhlcvBar>> GetDailyBarsAsync(
        string symbol, DateOnly startLocal, DateOnly endLocal, CancellationToken cancellationToken = default) =>
        FetchAsync(symbol, "data_day", startLocal, endLocal, cancellationToken);

    public Task<IReadOnlyList<ProviderOhlcvBar>> GetHourlyBarsAsync(
        string symbol, DateOnly startLocal, DateOnly endLocal, CancellationToken cancellationToken = default) =>
        FetchAsync(symbol, "data_60P", startLocal, endLocal, cancellationToken);

    private async Task<IReadOnlyList<ProviderOhlcvBar>> FetchAsync(
        string symbol, string key, DateOnly startLocal, DateOnly endLocal, CancellationToken cancellationToken)
    {
        var url = $"{_options.KbsBaseUrl.TrimEnd('/')}{PathPrefix}/{Uri.EscapeDataString(symbol)}/{key}"
            + $"?sdate={startLocal:dd-MM-yyyy}&edate={endLocal:dd-MM-yyyy}";

        var maxAttempts = Math.Max(1, _options.MaxRetries + 1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds)));
                using var response = await _http.GetAsync(url, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var transient = (int)response.StatusCode >= 500
                        || response.StatusCode == HttpStatusCode.TooManyRequests;
                    throw new MarketDataProviderException(
                        ProviderName,
                        $"KBS {key} returned {(int)response.StatusCode} {response.StatusCode} for {symbol}.",
                        response.StatusCode, transient);
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                return ParseBars(doc.RootElement, key, symbol);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var ex = new MarketDataProviderException(
                    ProviderName, $"KBS {key} request timed out for {symbol}.", isTransient: true);
                if (attempt >= maxAttempts) throw ex;
                await DelayRetryAsync(attempt, ex, symbol, cancellationToken);
            }
            catch (MarketDataProviderException ex) when (ex.IsTransient)
            {
                if (attempt >= maxAttempts) throw;
                await DelayRetryAsync(attempt, ex, symbol, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                var wrapped = new MarketDataProviderException(
                    ProviderName, $"KBS {key} request failed for {symbol}: {ex.Message}",
                    isTransient: true, inner: ex);
                if (attempt >= maxAttempts) throw wrapped;
                await DelayRetryAsync(attempt, wrapped, symbol, cancellationToken);
            }
            catch (JsonException ex)
            {
                throw new MarketDataPayloadException(ProviderName,
                    $"{key} payload is not valid JSON or is truncated: {ex.Message}");
            }
        }
    }

    private async Task DelayRetryAsync(int attempt, Exception ex, string symbol, CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        _logger.LogWarning(ex, "KBS fetch failed for {Symbol}; retry {Attempt} after {BackoffMs}ms",
            symbol, attempt, backoff.TotalMilliseconds);
        await Task.Delay(backoff, cancellationToken);
    }

    /// <summary>
    /// Strict parse of a KBS response: root object containing array property
    /// <paramref name="key"/> of objects {t,o,h,l,c,v}. Row timestamps are local
    /// ("yyyy-MM-dd HH:mm" or with seconds) and converted via the fixed +07:00 offset.
    /// </summary>
    internal static IReadOnlyList<ProviderOhlcvBar> ParseBars(JsonElement root, string key, string symbol)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new MarketDataPayloadException(ProviderName,
                $"{key} root is {root.ValueKind}, expected an object.");
        if (!root.TryGetProperty(key, out var rows))
            throw new MarketDataPayloadException(ProviderName,
                $"{key} response has no '{key}' property for {symbol}.");
        if (rows.ValueKind == JsonValueKind.Null)
            return [];
        if (rows.ValueKind != JsonValueKind.Array)
            throw new MarketDataPayloadException(ProviderName,
                $"{key} is {rows.ValueKind}, expected an array.");

        var fields = new[] { "o", "h", "l", "c", "v" };
        var bars = new List<ProviderOhlcvBar>(rows.GetArrayLength());
        var index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new MarketDataPayloadException(ProviderName,
                    $"{key}[{index}] is {row.ValueKind}, expected an object.");
            if (!row.TryGetProperty("t", out var tProp) || tProp.ValueKind != JsonValueKind.String)
                throw new MarketDataPayloadException(ProviderName,
                    $"{key}[{index}] has no string 't' timestamp.");
            if (!TryParseLocalTimestamp(tProp.GetString()!, out var openUtcMs))
                throw new MarketDataPayloadException(ProviderName,
                    $"{key}[{index}] timestamp '{tProp.GetString()}' is not yyyy-MM-dd HH:mm.");

            var values = new decimal[5];
            for (var f = 0; f < fields.Length; f++)
            {
                if (!row.TryGetProperty(fields[f], out var vProp)
                    || (vProp.ValueKind != JsonValueKind.Number && vProp.ValueKind != JsonValueKind.String))
                    throw new MarketDataPayloadException(ProviderName,
                        $"{key}[{index}] field '{fields[f]}' missing or not a number.");
                if (vProp.ValueKind == JsonValueKind.Number)
                {
                    values[f] = vProp.GetDecimal();
                }
                else if (!decimal.TryParse(vProp.GetString(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out values[f]))
                {
                    throw new MarketDataPayloadException(ProviderName,
                        $"{key}[{index}] field '{fields[f]}' is not parseable: '{vProp.GetString()}'.");
                }
            }

            bars.Add(new ProviderOhlcvBar(openUtcMs, values[0], values[1], values[2], values[3], values[4]));
            index++;
        }

        return bars
            .GroupBy(b => b.OpenTimeMs)
            .Select(g => g.Last())
            .OrderBy(b => b.OpenTimeMs)
            .ToArray();
    }

    internal static bool TryParseLocalTimestamp(string raw, out long openUtcMs)
    {
        openUtcMs = 0;
        if (!DateTime.TryParseExact(raw.Trim(),
                new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return false;
        openUtcMs = new DateTimeOffset(local, VietnamMarketCalendar.VietnamOffset).ToUnixTimeMilliseconds();
        return true;
    }
}
