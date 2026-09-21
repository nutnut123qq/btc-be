using Backend.Services.Models;

namespace Backend.Services;

/// <summary>
/// Versioned candle representation shared by the Historical Analog API and the
/// Python walk-forward evaluator. Keep this implementation in numerical parity
/// with ai/historical_analog_walkforward.py::build_returns_shape_v2_vectors.
/// </summary>
internal static class HistoricalAnalogV2Representation
{
    private const double Epsilon = 1e-12;

    public const string Version = "returns_shape_v2_signed";
    public const string RankingMethod = "cosine-similarity-desc-point-in-time";

    public static float[]? BuildVector(IReadOnlyList<KlineDto> source, int start, int count)
    {
        if (start < 0 || count <= 0 || start + count > source.Count) return null;

        var vector = new float[count * 4];
        var offset = 0;
        var previousClose = (double)source[start].Close;
        if (!IsFinitePositive(previousClose)) return null;

        for (var index = 0; index < count; index++)
        {
            var candle = source[start + index];
            var open = (double)candle.Open;
            var high = (double)candle.High;
            var low = (double)candle.Low;
            var close = (double)candle.Close;
            if (!IsValidOhlc(open, high, low, close)) return null;

            var candleRange = high - low;
            var candleReturn = index == 0 ? 0.0 : (close / previousClose) - 1.0;
            var rangeFraction = candleRange / previousClose;
            var scaledReturn = Math.Clamp(candleReturn / Math.Max(rangeFraction, Epsilon), -1.0, 1.0);

            var signedBody = 0.0;
            var upperWick = 0.0;
            var lowerWick = 0.0;
            if (candleRange > Epsilon)
            {
                signedBody = (close - open) / candleRange;
                upperWick = (high - Math.Max(open, close)) / candleRange;
                lowerWick = (Math.Min(open, close) - low) / candleRange;
            }

            vector[offset++] = (float)scaledReturn;
            vector[offset++] = (float)signedBody;
            vector[offset++] = (float)upperWick;
            vector[offset++] = (float)lowerWick;
            previousClose = close;
        }

        var norm = Math.Sqrt(vector.Sum(value => value * value));
        if (!double.IsFinite(norm) || norm <= Epsilon) return null;
        for (var index = 0; index < vector.Length; index++)
            vector[index] = (float)(vector[index] / norm);
        return vector;
    }

    private static bool IsValidOhlc(double open, double high, double low, double close) =>
        IsFinitePositive(open) && IsFinitePositive(high) && IsFinitePositive(low) && IsFinitePositive(close) &&
        high >= Math.Max(open, close) && low <= Math.Min(open, close) && high >= low;

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;
}
