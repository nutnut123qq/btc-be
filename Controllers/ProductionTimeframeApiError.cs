using Backend.Services;
using Backend.Services.Models;

namespace Backend.Controllers;

internal static class ProductionTimeframeApiError
{
    public static ApiErrorEnvelope Create(
        ProductionTimeframePolicy policy,
        string? timeframe,
        string requestId) => new()
    {
        Code = "INACTIVE_TIMEFRAME",
        Message = policy.InactiveMessage(ProductionTimeframePolicy.Canonicalize(timeframe)),
        Retryable = false,
        RequestId = requestId
    };
}
