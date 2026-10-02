using Backend.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Backend.Tests;

public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public async Task AddsSecurityHeadersToEveryResponse()
    {
        var (context, responseFeature) = CreateContext();
        var middleware = new SecurityHeadersMiddleware(async ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            await ctx.Response.WriteAsync("ok");
        });

        await middleware.InvokeAsync(context);
        await FireOnStartingAsync(responseFeature);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Opener-Policy"]);
    }

    [Fact]
    public async Task AddsHeadersWhenNextWritesNoBody()
    {
        // Covers error/404-style responses where no body is produced downstream.
        var (context, responseFeature) = CreateContext();
        var middleware = new SecurityHeadersMiddleware(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);
        await FireOnStartingAsync(responseFeature);

        Assert.Equal("nosniff", context.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", context.Response.Headers["Referrer-Policy"]);
        Assert.Equal("same-origin", context.Response.Headers["Cross-Origin-Opener-Policy"]);
    }

    [Fact]
    public async Task OverwritesPreExistingHeaderWithoutDuplicating()
    {
        // Simulates a header already present (e.g. set by an upstream proxy):
        // indexer assignment must replace it, not append a second value.
        var (context, responseFeature) = CreateContext();
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        var middleware = new SecurityHeadersMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(context);
        await FireOnStartingAsync(responseFeature);

        Assert.Equal("DENY", context.Response.Headers["X-Frame-Options"].ToString());
        Assert.Single(context.Response.Headers["X-Frame-Options"].AsEnumerable());
    }

    private static (DefaultHttpContext Context, CapturingResponseFeature ResponseFeature) CreateContext()
    {
        var context = new DefaultHttpContext();
        // DefaultHttpContext's StreamResponseBodyFeature never runs OnStarting
        // callbacks (only a real server does), so capture them explicitly and
        // fire them the way Kestrel would when the response begins.
        var responseFeature = new CapturingResponseFeature();
        context.Features.Set<IHttpResponseFeature>(responseFeature);
        return (context, responseFeature);
    }

    private static async Task FireOnStartingAsync(CapturingResponseFeature responseFeature)
    {
        foreach (var (callback, state) in responseFeature.StartingCallbacks)
        {
            await callback(state);
        }
    }

    private sealed class CapturingResponseFeature : HttpResponseFeature
    {
        public List<(Func<object, Task> Callback, object State)> StartingCallbacks { get; } = new();

        public override void OnStarting(Func<object, Task> callback, object state)
        {
            StartingCallbacks.Add((callback, state));
        }
    }
}
