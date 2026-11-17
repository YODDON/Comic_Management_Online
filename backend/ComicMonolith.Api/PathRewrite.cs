namespace ComicMonolith.Api;

/// <summary>
/// Replaces the YARP gateway's path transforms. The frontend calls `/comics/...`; the controllers live under
/// `/api/comics/...`. Only these 11 prefixes are rewritten. Everything else is left alone, including the
/// routes the old gateway forwarded unchanged (`/auth`, `/wallets`, `/currency`, `/withdraws`, `/banners`),
/// anything already under `/api`, `/health`, `/swagger`, and `/translations` (removed with the translation feature).
/// </summary>
public static class PathRewriteRules
{
    /// <summary>`HttpContext.Items` key that holds the path as it arrived, before any rewrite.</summary>
    public const string OriginalPathItemKey = "OriginalPath";

    /// <summary>First path segment of every route the old gateway rewrote to `/api/{segment}`.</summary>
    public static readonly IReadOnlyList<string> RewrittenSegments =
    [
        "comics",
        "categories",
        "chapters",
        "comments",
        "favorites",
        "follows",
        "reading-history",
        "payments",
        "uploads",
        "missions",
        "notifications",
    ];

    /// <summary>
    /// Rewrites `/{segment}[/rest]` to `/api/{segment}[/rest]`. The prefix match is case-insensitive and on a
    /// path-segment boundary (`/comics`, `/comics/`, `/comics/x/y` match; `/comicsX` does not), the same as the
    /// gateway's `/{segment}/{**catch-all}` route. The rest of the path keeps its case. The query string is not
    /// part of <see cref="PathString"/> and is not touched.
    /// </summary>
    public static bool TryRewrite(PathString path, out PathString rewritten)
    {
        foreach (var segment in RewrittenSegments)
        {
            if (path.StartsWithSegments("/" + segment, StringComparison.OrdinalIgnoreCase, out var remaining))
            {
                rewritten = new PathString("/api/" + segment).Add(remaining);
                return true;
            }
        }

        rewritten = path;
        return false;
    }
}

public sealed class PathRewriteMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        // Keep the incoming path: the public-endpoint rules (T14) are written against it.
        context.Items[PathRewriteRules.OriginalPathItemKey] = context.Request.Path;

        if (PathRewriteRules.TryRewrite(context.Request.Path, out var rewritten))
        {
            context.Request.Path = rewritten;
        }

        return next(context);
    }
}

public static class PathRewriteExtensions
{
    /// <summary>Must run before <c>UseRouting()</c> so endpoint matching sees the rewritten path.</summary>
    public static IApplicationBuilder UsePathRewrite(this IApplicationBuilder app) =>
        app.UseMiddleware<PathRewriteMiddleware>();
}
