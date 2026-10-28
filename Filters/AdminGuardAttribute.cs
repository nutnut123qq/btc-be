using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Cryptography;
using System.Text;

namespace Backend.Filters;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class AdminGuardAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var apiKey = configuration["AdminApiKey"];

        if (string.IsNullOrEmpty(apiKey))
        {
            context.Result = new UnauthorizedObjectResult(new { Message = "Unauthorized: Admin API key is not configured on the server." });
            return;
        }

        if (!context.HttpContext.Request.Headers.ContainsKey("X-Admin-Key"))
        {
            context.Result = new UnauthorizedObjectResult(new { Message = "Unauthorized: Missing X-Admin-Key header." });
            return;
        }

        if (!HasValidAdminKey(configuration, context.HttpContext.Request))
        {
            context.Result = new UnauthorizedObjectResult(new { Message = "Unauthorized: Invalid X-Admin-Key header." });
            return;
        }
    }

    /// <summary>
    /// True only when AdminApiKey is configured (non-empty) and X-Admin-Key matches it, compared in constant time.
    /// Shared with public endpoints that reveal more to an admin caller (HealthController.Workers).
    /// </summary>
    public static bool HasValidAdminKey(IConfiguration? configuration, HttpRequest? request)
    {
        var apiKey = configuration?["AdminApiKey"];
        if (string.IsNullOrEmpty(apiKey) || request is null || !request.Headers.TryGetValue("X-Admin-Key", out var providedKey))
            return false;

        byte[] providedKeyBytes = Encoding.UTF8.GetBytes(providedKey.ToString());
        byte[] expectedKeyBytes = Encoding.UTF8.GetBytes(apiKey);
        return CryptographicOperations.FixedTimeEquals(providedKeyBytes, expectedKeyBytes);
    }
}
