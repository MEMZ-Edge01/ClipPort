using ClipPort.FnOS.Contracts;

namespace ClipPort.FnOS.Security;

public sealed class GatewaySecurityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        CsrfTokenStore csrfTokens,
        IHostEnvironment environment)
    {
        if (!RequiresGatewayIdentity(context.Request.Path))
        {
            await next(context);
            return;
        }

        GatewayUser? user = TryReadGatewayUser(context, environment);
        if (user is null || !user.IsAdmin)
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                new ErrorResponse("admin_required", "ClipPort for fnOS is restricted to administrators."));
            return;
        }

        context.Items[GatewayUser.ItemKey] = user;
        if (RequiresCsrf(context.Request.Method) &&
            !csrfTokens.IsValid(user.UserId, context.Request.Headers["X-ClipPort-CSRF"].FirstOrDefault()))
        {
            await WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                new ErrorResponse("csrf_invalid", "The request CSRF token is missing or invalid."));
            return;
        }

        await next(context);
    }

    private static bool RequiresGatewayIdentity(PathString path) =>
        path.StartsWithSegments("/api") || path.StartsWithSegments("/ws");

    private static bool RequiresCsrf(string method) =>
        !HttpMethods.IsGet(method) &&
        !HttpMethods.IsHead(method) &&
        !HttpMethods.IsOptions(method);

    private static GatewayUser? TryReadGatewayUser(
        HttpContext context,
        IHostEnvironment environment)
    {
        if (environment.IsDevelopment() &&
            string.Equals(
                Environment.GetEnvironmentVariable("CLIPPORT_ALLOW_LOCAL_ADMIN"),
                "1",
                StringComparison.Ordinal))
        {
            return new GatewayUser(1000, "local-admin", true);
        }

        if (!int.TryParse(context.Request.Headers["X-Trim-Userid"], out int userId) ||
            userId < 0)
        {
            return null;
        }

        bool isAdmin = string.Equals(
            context.Request.Headers["X-Trim-Isadmin"],
            "true",
            StringComparison.OrdinalIgnoreCase);
        string username = context.Request.Headers["X-Trim-Username"].FirstOrDefault() ?? string.Empty;
        return new GatewayUser(userId, username, isAdmin);
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        ErrorResponse error)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(error);
    }
}
