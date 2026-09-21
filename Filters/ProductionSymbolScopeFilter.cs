using System.Reflection;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Backend.Filters;

/// <summary>Enforces the BTC-only scope after request model binding.</summary>
public sealed class ProductionSymbolScopeFilter(ProductionSymbolPolicy policy) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        foreach (var (name, value) in context.ActionArguments.ToArray())
        {
            if (value is string text && name.Equals("symbol", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNormalize(text, context, out var normalized)) return;
                context.ActionArguments[name] = normalized;
                continue;
            }

            if (value is string list && name.Equals("symbols", StringComparison.OrdinalIgnoreCase))
            {
                try { context.ActionArguments[name] = policy.NormalizeListAndEnsureActive(list); }
                catch (ArgumentException) { Reject(context, list); return; }
                continue;
            }

            if (value is null || value is CancellationToken) continue;
            var property = value.GetType().GetProperty("Symbol", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property?.PropertyType != typeof(string) || !property.CanRead || !property.CanWrite) continue;
            var dtoSymbol = property.GetValue(value) as string;
            if (string.IsNullOrWhiteSpace(dtoSymbol)) continue;
            if (!TryNormalize(dtoSymbol, context, out var normalizedDto)) return;
            property.SetValue(value, normalizedDto);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    private bool TryNormalize(string symbol, ActionExecutingContext context, out string normalized)
    {
        normalized = ProductionSymbolPolicy.Canonicalize(symbol);
        if (policy.IsActive(normalized)) return true;
        Reject(context, symbol);
        return false;
    }

    private void Reject(ActionExecutingContext context, string? symbol)
    {
        context.Result = new BadRequestObjectResult(new ApiErrorEnvelope
        {
            Code = "UNSUPPORTED_SYMBOL",
            Message = policy.InactiveMessage(symbol),
            Retryable = false,
            RequestId = context.HttpContext.TraceIdentifier
        });
    }
}
