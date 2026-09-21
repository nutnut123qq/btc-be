using Backend.Filters;
using Backend.Services;
using Backend.Services.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Backend.Tests;

public class ProductionSymbolScopeFilterTests
{
    [Fact]
    public void OnActionExecuting_RejectsNonBtcQuerySymbol()
    {
        var context = CreateContext(new Dictionary<string, object?> { ["symbol"] = "ETHUSDT" });

        new ProductionSymbolScopeFilter(new ProductionSymbolPolicy()).OnActionExecuting(context);

        var badRequest = Assert.IsType<BadRequestObjectResult>(context.Result);
        var error = Assert.IsType<ApiErrorEnvelope>(badRequest.Value);
        Assert.Equal("UNSUPPORTED_SYMBOL", error.Code);
    }

    [Fact]
    public void OnActionExecuting_NormalizesBtcAliasInsideRequestDto()
    {
        var request = new SymbolRequest { Symbol = " btc " };
        var context = CreateContext(new Dictionary<string, object?> { ["request"] = request });

        new ProductionSymbolScopeFilter(new ProductionSymbolPolicy()).OnActionExecuting(context);

        Assert.Null(context.Result);
        Assert.Equal("BTCUSDT", request.Symbol);
    }

    private static ActionExecutingContext CreateContext(IDictionary<string, object?> arguments)
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], arguments, new object());
    }

    private sealed class SymbolRequest
    {
        public string Symbol { get; set; } = string.Empty;
    }
}
