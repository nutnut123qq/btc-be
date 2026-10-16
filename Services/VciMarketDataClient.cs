using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Backend.Options;
using Backend.Services.Models;

namespace Backend.Services;

/// <summary>Primary MBB provider: VCI gap-chart endpoint (adjusted VND bars).</summary>
public interface IVciOhlcvClient
{
    /// <summary>
    /// Fetches up to <paramref name="countBack"/> bars ending at or before
    /// <paramref name="toUnixSeconds"/>, ascending open time. May include a still-forming
    /// latest bar; finalization is applied by the ingestion layer.
    /// </summary>
    Task<IReadOnlyList<ProviderOhlcvBar>> GetBarsAsync(
        string symbol,
        string timeframe,
        long toUnixSeconds,
        int countBack,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// VCI OHLC fetch for equity instruments.
/// POST {base}/api/chart/OHLCChart/gap-chart with
/// {"timeFrame":"ONE_DAY"|"ONE_HOUR","symbols":[symbol],"to":unixSec,"countBack":n}
/// and a response of [{ "t":[], "o":[], "h":[], "l":[], "c":[], "v":[] }] where t is
/// unix SECONDS (probe-verified). Parsing is fail-closed: every JsonElement access is
/// ValueKind-guarded because TryGetProperty/GetString on a wrong kind throws
/// InvalidOperationException, not JsonException (repo pitfall #17).
/// </summary>
public sealed class VciMarketDataClient : IVciOhlcvClient
{
    internal const string ProviderName = "VCI";
    internal const string ChartPath = "/api/chart/OHLCChart/gap-chart";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly MbbIngestionOptions _options;
    private readonly ILogger<VciMarketDataClient> _logger;

    public VciMarketDataClient(HttpClient http, MbbIngestionOptions? options = null,
        ILogger<VciMarketDataClient>? logger = null)
    {
        _http = http;
        _options = options ?? new MbbIngestionOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<VciMarketDataClient>.Instance;
    }

    internal static string TimeFrameParam(string timeframe) => timeframe switch
    {
        EquityCandleTimeframes.Day => "ONE_DAY",
        EquityCandleTimeframes.Hour => "ONE_HOUR",
        _ => throw new ArgumentException(
            $"VCI does not serve a usable '{timeframe}' frame for MBB (contract: 1h/1d only, no synthetic 4h).",
            nameof(timeframe))
    };

    public async Task<IReadOnlyList<ProviderOhlcvBar>> GetBarsAsync(
        string symbol,
        string timeframe,
        long toUnixSeconds,
        int countBack,
        CancellationToken cancellationToken = default)
    {
        var url = $"{_options.VciBaseUrl.TrimEnd('/')}{ChartPath}";
        var payload = new
        {
            timeFrame = TimeFrameParam(timeframe),
            symbols = new[] { symbol },
            to = toUnixSeconds,
            countBack
        };

        var maxAttempts = Math.Max(1, _options.MaxRetries + 1);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds)));
                using var response = await _http.PostAsJsonAsync(url, payload, JsonOptions, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var transient = (int)response.StatusCode >= 500
                        || response.StatusCode == HttpStatusCode.TooManyRequests;
                    throw new MarketDataProviderException(
                        ProviderName,
                        $"VCI gap-chart returned {(int)response.StatusCode} {response.StatusCode} for {symbol}/{timeframe}.",
                        response.StatusCode, transient);
                }
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
                return ParseBars(doc.RootElement, symbol);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var ex = new MarketDataProviderException(
                    ProviderName, $"VCI request timed out for {symbol}/{timeframe}.", isTransient: true);
                if (attempt >= maxAttempts) throw ex;
                await DelayRetryAsync(attempt, ex, symbol, timeframe, cancellationToken);
            }
            catch (MarketDataProviderException ex) when (ex.IsTransient)
            {
                if (attempt >= maxAttempts) throw;
                await DelayRetryAsync(attempt, ex, symbol, timeframe, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                var wrapped = new MarketDataProviderException(
                    ProviderName, $"VCI request failed for {symbol}/{timeframe}: {ex.Message}",
                    isTransient: true, inner: ex);
                if (attempt >= maxAttempts) throw wrapped;
                await DelayRetryAsync(attempt, wrapped, symbol, timeframe, cancellationToken);
            }
            catch (JsonException ex)
            {
                // Payload contract violation: fail closed, never retry.
                throw new MarketDataPayloadException(ProviderName,
                    $"Gap-chart payload is not valid JSON or is truncated: {ex.Message}");
            }
        }
    }

    private async Task DelayRetryAsync(int attempt, Exception ex, string symbol, string timeframe,
        CancellationToken cancellationToken)
    {
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        _logger.LogWarning(ex,
            "VCI fetch failed for {Symbol} {Timeframe}; retry {Attempt} after {BackoffMs}ms",
            symbol, timeframe, attempt, backoff.TotalMilliseconds);
        await Task.Delay(backoff, cancellationToken);
    }

    /// <summary>
    /// Strict parse of the gap-chart response. Expected: array whose first element is an
    /// object with parallel numeric arrays t/o/h/l/c/v of equal length. Any deviation
    /// throws <see cref="MarketDataPayloadException"/> — partial/ambiguous payloads must
    /// never become stored bars. Bars are deduplicated (last wins) and sorted ascending.
    /// </summary>
    internal static IReadOnlyList<ProviderOhlcvBar> ParseBars(JsonElement root, string symbol)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new MarketDataPayloadException(ProviderName,
                $"gap-chart root is {root.ValueKind}, expected an array.");
        if (root.GetArrayLength() == 0)
            return [];
        var first = root[0];
        if (first.ValueKind != JsonValueKind.Object)
            throw new MarketDataPayloadException(ProviderName,
                $"gap-chart first element is {first.ValueKind}, expected an object.");

        var arrays = new JsonElement[6];
        var names = new[] { "t", "o", "h", "l", "c", "v" };
        for (var i = 0; i < names.Length; i++)
        {
            if (!first.TryGetProperty(names[i], out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new MarketDataPayloadException(ProviderName,
                    $"gap-chart element is missing array '{names[i]}' for {symbol}.");
            arrays[i] = arr;
        }
        var n = arrays[0].GetArrayLength();
        for (var i = 1; i < arrays.Length; i++)
        {
            if (arrays[i].GetArrayLength() != n)
                throw new MarketDataPayloadException(ProviderName,
                    $"gap-chart arrays length mismatch: '{names[0]}'={n} vs '{names[i]}'={arrays[i].GetArrayLength()}.");
        }

        var bars = new List<ProviderOhlcvBar>(n);
        for (var i = 0; i < n; i++)
        {
            var fields = new decimal[arrays.Length];
            for (var k = 0; k < arrays.Length; k++)
            {
                fields[k] = NumericField(arrays[k][i], names[k], i);
            }
            // t is unix SECONDS per the probe-verified contract (numeric string
            // or number; anything else fails closed above).
            var openUtcMs = checked((long)Math.Round((double)fields[0])) * 1000L;
            bars.Add(new ProviderOhlcvBar(
                openUtcMs,
                fields[1], fields[2], fields[3], fields[4], fields[5]));
        }

        return bars
            .GroupBy(b => b.OpenTimeMs)
            .Select(g => g.Last())
            .OrderBy(b => b.OpenTimeMs)
            .ToArray();
    }

    /// <summary>
    /// VCI gap-chart fields arrive as JSON numbers or numeric strings (probe saw
    /// both representations; `t` in particular comes back as a string). Anything
    /// else still fails closed — no implicit coercion of non-numeric payloads.
    /// </summary>
    private static decimal NumericField(JsonElement value, string name, int index)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDecimal();
        if (value.ValueKind == JsonValueKind.String
            && decimal.TryParse(value.GetString(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new MarketDataPayloadException(ProviderName,
            $"gap-chart '{name}[{index}]' is {value.ValueKind}, expected a number or numeric string.");
    }
}
