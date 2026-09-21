using Backend.Services;
using Backend.Services.Models;

namespace Backend.Controllers;

internal static class ProductionSymbolApiError
{
    public static ApiErrorEnvelope Create(
        ProductionSymbolPolicy policy,
        string? symbol,
        string requestId) => new()
    {
        Code = "UNSUPPORTED_SYMBOL",
        Message = policy.InactiveMessage(symbol),
        Retryable = false,
        RequestId = requestId
    };
}
