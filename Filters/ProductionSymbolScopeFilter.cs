using System.Reflection;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Backend.Filters;

/// <summary>
/// Enforces the enabled production symbol scope after request model binding. Known but
/// not-yet-enabled instruments (e.g. MBB) and unknown symbols are both rejected here;
/// per-instrument capabilities open paths lane by lane.
/// </summary>
public sealed class ProductionSymbolScopeFilter(ProductionSymbolPolicy policy) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        // Equity-lane controllers resolve `symbol` themselves against catalog
        // capabilities (EquityScope); the BTC enabled-traffic gate does not apply.
        if (context.ActionDescriptor.EndpointMetadata.OfType<SkipProductionSymbolScopeAttribute>().Any()) return;

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

/// <summary>
/// Opts a controller/action out of <see cref="ProductionSymbolScopeFilter"/>. Only for
/// lanes that validate the symbol against <see cref="InstrumentCatalog"/> capabilities
/// themselves (the /api/equities lane); crypto endpoints must never carry it.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
public sealed class SkipProductionSymbolScopeAttribute : Attribute;
