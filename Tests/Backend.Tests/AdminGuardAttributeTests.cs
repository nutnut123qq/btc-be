using Backend.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Tests;

public class AdminGuardAttributeTests
{
    [Fact]
    public void RejectsRequestWhenAdminKeyIsNotConfigured()
    {
        var context = CreateContext(null, null);

        new AdminGuardAttribute().OnActionExecuting(context);

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public void RejectsInvalidAdminKey()
    {
        var context = CreateContext("server-secret", "wrong-secret");

        new AdminGuardAttribute().OnActionExecuting(context);

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public void AllowsMatchingAdminKey()
    {
        var context = CreateContext("server-secret", "server-secret");

        new AdminGuardAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Fact]
    public void DoesNotAcceptExecutionKeyHeaderForAdminActions()
    {
        var context = CreateContext("server-secret", null);
        context.HttpContext.Request.Headers["X-Execution-Key"] = "server-secret";

        new AdminGuardAttribute().OnActionExecuting(context);

        Assert.IsType<UnauthorizedObjectResult>(context.Result);
    }

    private static ActionExecutingContext CreateContext(string? configuredKey, string? providedKey)
    {
        var values = configuredKey is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["AdminApiKey"] = configuredKey };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        if (providedKey is not null) httpContext.Request.Headers["X-Admin-Key"] = providedKey;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
    }
}
