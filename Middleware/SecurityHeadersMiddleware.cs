namespace Backend.Middleware;

/// <summary>
/// Sets security headers on every response, including error and 404 responses.
/// Headers are applied in <see cref="HttpResponse.OnStarting"/> so they survive
/// the exception-handler pipeline re-execution, and via indexer assignment
/// (not Append) so an upstream proxy/header cannot produce duplicate values.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static ctx =>
        {
            var headers = ((HttpContext)ctx).Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}
