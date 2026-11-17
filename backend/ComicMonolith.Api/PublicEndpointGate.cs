using System.Text.Json;
using SharedKernel.Responses;

namespace ComicMonolith.Api;

/// <summary>
/// The old gateway's public (anonymous) endpoint list, ported verbatim from `ApiGateway/Program.cs`.
/// Every path not listed here needs an authenticated user; roles are still enforced by the controllers.
/// The rules match the path as the CLIENT sent it (before the `/api` rewrite), lowercased, with plain
/// prefix matching and no segment boundary. The quirks are intentional and pinned by tests:
/// `/banners-x` matches the banners rule, `/comics/mexyz` is excluded by the `/comics/me` prefix,
/// `/auth/login/` (trailing slash) is not public, and the `/auth` rules apply to every HTTP method.
/// </summary>
public static class PublicEndpoints
{
    /// <summary>`/health` is a host-only endpoint the old gateway never had; it is exempted by exact path.</summary>
    public const string HealthPath = "/health";

    public static bool IsPublic(string method, string path)
    {
        var p = (path ?? "").ToLowerInvariant();

        var isGet = HttpMethods.IsGet(method);
        var isPost = HttpMethods.IsPost(method);

        return
            p == "/auth/login" ||
            p == "/auth/register" ||
            p == "/auth/forgot-password" ||
            p == "/auth/reset-password" ||
            p == "/auth/refetchtoken" ||
            p == "/auth/resend-confirm" ||
            p == "/auth/google" ||
            p.StartsWith("/auth/verify", StringComparison.Ordinal) ||
            (p.StartsWith("/banners", StringComparison.Ordinal) && isGet) ||
            (p.StartsWith("/categories", StringComparison.Ordinal) && isGet) ||
            // The gateway's `POST /translations` rule is not ported: the translation feature was removed.
            (p.StartsWith("/comics", StringComparison.Ordinal) && isGet
                && !p.StartsWith("/comics/me", StringComparison.Ordinal)
                && !p.StartsWith("/comics/purchased", StringComparison.Ordinal)) ||
            (p.StartsWith("/chapters", StringComparison.Ordinal) && isGet) ||
            (p.StartsWith("/comments", StringComparison.Ordinal) && isGet) ||
            (p == "/payments/sepay-webhook" && isPost);
    }

    /// <summary>The path the client sent: the original path stored by <see cref="PathRewriteMiddleware"/>, else the current one.</summary>
    public static string ClientPath(HttpContext context) =>
        context.Items.TryGetValue(PathRewriteRules.OriginalPathItemKey, out var original) && original is PathString originalPath
            ? originalPath.Value ?? ""
            : context.Request.Path.Value ?? "";
}

/// <summary>
/// The 401 the gateway itself generated: same status, message and camelCase `ApiResponse` shape.
/// Responses produced by controllers are never touched.
/// </summary>
internal static class GatewayStyleResponse
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task WriteAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = null;

        var body = JsonSerializer.Serialize(ApiResponse<object>.ErrorResponse(message, statusCode), Options);
        await context.Response.WriteAsync(body);
    }
}

/// <summary>
/// Runs after UseRouting, UseAuthentication and UseAuthorization, so the endpoint metadata exists.
/// A request that matched no endpoint is let through (it ends as the normal 404). An endpoint marked
/// [AllowAnonymous] on a non-public path is still blocked here, as it was at the gateway.
/// </summary>
public sealed class PublicEndpointGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint() is null)
        {
            await next(context);
            return;
        }

        var path = PublicEndpoints.ClientPath(context);

        var isPublic =
            string.Equals(path, PublicEndpoints.HealthPath, StringComparison.OrdinalIgnoreCase) ||
            PublicEndpoints.IsPublic(context.Request.Method, path);

        if (!isPublic && context.User.Identity?.IsAuthenticated != true)
        {
            await GatewayStyleResponse.WriteAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized access");
            return;
        }

        await next(context);
    }
}

/// <summary>
/// The 404 the gateway itself generated for a path that matched no route. Only a 404 with no matched endpoint
/// and nothing written yet is replaced; a 404 produced by a controller keeps its own body.
/// </summary>
public sealed class NoRouteJsonMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Response.StatusCode == StatusCodes.Status404NotFound
            && context.GetEndpoint() is null
            && !context.Response.HasStarted)
        {
            await GatewayStyleResponse.WriteAsync(context, StatusCodes.Status404NotFound, "Resource not found");
        }
    }
}

public static class PublicEndpointGateExtensions
{
    /// <summary>Must run after UseRouting, UseAuthentication and UseAuthorization.</summary>
    public static IApplicationBuilder UsePublicEndpointGate(this IApplicationBuilder app) =>
        app.UseMiddleware<PublicEndpointGateMiddleware>();

    /// <summary>Wraps the whole pipeline, so put it before UseRouting.</summary>
    public static IApplicationBuilder UseNoRouteJson404(this IApplicationBuilder app) =>
        app.UseMiddleware<NoRouteJsonMiddleware>();
}
