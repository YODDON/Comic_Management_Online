using Microsoft.AspNetCore.Http;

namespace ComicMonolith.Api.Tests;

public class PathRewriteTests
{
    private static readonly string[] Rewritten =
    [
        "comics", "categories", "chapters", "comments", "favorites", "follows",
        "reading-history", "payments", "uploads", "missions", "notifications",
    ];

    private static readonly string[] Kept = ["auth", "wallets", "currency", "withdraws", "banners"];

    private static (string Path, string Query, bool Rewrote, string? Original) Run(string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);

        string? seenPath = null;
        string? seenQuery = null;
        var middleware = new PathRewriteMiddleware(ctx =>
        {
            seenPath = ctx.Request.Path.Value;
            seenQuery = ctx.Request.QueryString.Value;
            return Task.CompletedTask;
        });

        middleware.InvokeAsync(context).GetAwaiter().GetResult();

        var original = context.Items[PathRewriteRules.OriginalPathItemKey]?.ToString();
        return (seenPath!, seenQuery!, seenPath != path, original);
    }

    public static IEnumerable<object[]> RewrittenSegmentData() => Rewritten.Select(s => new object[] { s });
    public static IEnumerable<object[]> KeptSegmentData() => Kept.Select(s => new object[] { s });

    // ---- The 11 rewritten routes ----

    [Fact]
    public void The_rewritten_list_is_exactly_the_11_gateway_routes()
    {
        Assert.Equal(Rewritten.OrderBy(x => x), PathRewriteRules.RewrittenSegments.OrderBy(x => x));
    }

    [Theory]
    [MemberData(nameof(RewrittenSegmentData))]
    public void Bare_prefix_is_rewritten(string segment)
    {
        Assert.Equal($"/api/{segment}", Run($"/{segment}").Path);
    }

    [Theory]
    [MemberData(nameof(RewrittenSegmentData))]
    public void Trailing_slash_is_kept(string segment)
    {
        Assert.Equal($"/api/{segment}/", Run($"/{segment}/").Path);
    }

    [Theory]
    [MemberData(nameof(RewrittenSegmentData))]
    public void Sub_path_is_kept(string segment)
    {
        Assert.Equal($"/api/{segment}/123", Run($"/{segment}/123").Path);
    }

    [Theory]
    [MemberData(nameof(RewrittenSegmentData))]
    public void Deep_sub_path_is_kept(string segment)
    {
        Assert.Equal($"/api/{segment}/a/b/c/d", Run($"/{segment}/a/b/c/d").Path);
    }

    [Theory]
    [MemberData(nameof(RewrittenSegmentData))]
    public void Query_string_is_kept_untouched(string segment)
    {
        var result = Run($"/{segment}/x", "?page=2&pageSize=10&q=a%20b&flag");

        Assert.Equal($"/api/{segment}/x", result.Path);
        Assert.Equal("?page=2&pageSize=10&q=a%20b&flag", result.Query);
    }

    [Fact]
    public void Query_string_is_kept_on_the_bare_prefix()
    {
        var result = Run("/comics", "?keyword=one%20piece");

        Assert.Equal("/api/comics", result.Path);
        Assert.Equal("?keyword=one%20piece", result.Query);
    }

    [Theory]
    [InlineData("/comics/hot")]
    [InlineData("/comics/slug/one-piece")]
    [InlineData("/payments/sepay-webhook")]
    [InlineData("/payments/purchased-chapter")]
    [InlineData("/missions/my-missions")]
    [InlineData("/reading-history/me")]
    public void Real_frontend_paths_get_the_api_prefix(string path)
    {
        var expected = "/api" + path;

        Assert.Equal(expected, Run(path).Path);
    }

    // ---- Case ----

    [Theory]
    [InlineData("/Comics/Hot", "/api/comics/Hot")]
    [InlineData("/COMICS", "/api/comics")]
    [InlineData("/Reading-History/Me", "/api/reading-history/Me")]
    [InlineData("/comics/Slug/One-Piece", "/api/comics/Slug/One-Piece")]
    public void Prefix_is_matched_ignoring_case_and_the_rest_keeps_its_case(string path, string expected)
    {
        Assert.Equal(expected, Run(path).Path);
    }

    // ---- Things that must not change ----

    [Theory]
    [MemberData(nameof(KeptSegmentData))]
    public void Kept_routes_are_unchanged(string segment)
    {
        foreach (var path in new[] { $"/{segment}", $"/{segment}/", $"/{segment}/x/y" })
        {
            Assert.Equal(path, Run(path, "?a=1").Path);
        }
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/admin/users")]
    [InlineData("/wallets/me")]
    [InlineData("/currency/history")]
    [InlineData("/withdraws/me")]
    [InlineData("/banners/admin")]
    public void Real_kept_paths_are_unchanged(string path)
    {
        Assert.Equal(path, Run(path).Path);
    }

    [Theory]
    [InlineData("/translations")]
    [InlineData("/translations/translate")]
    [InlineData("/api/translations")]
    public void Translations_has_no_rule(string path)
    {
        Assert.Equal(path, Run(path).Path);
    }

    [Theory]
    [InlineData("/comicsX")]
    [InlineData("/comics-x")]
    [InlineData("/comicsx/1")]
    [InlineData("/apicomics")]
    [InlineData("/xcomics")]
    [InlineData("/missionsandmore")]
    [InlineData("/reading")]
    public void Lookalike_prefixes_are_not_rewritten(string path)
    {
        Assert.Equal(path, Run(path).Path);
    }

    [Theory]
    [InlineData("/api/comics")]
    [InlineData("/api/Comics/hot")]
    [InlineData("/api/reading-history/me")]
    [InlineData("/api/payments/sepay-webhook")]
    public void Paths_already_under_api_are_not_rewritten_again(string path)
    {
        Assert.Equal(path, Run(path).Path);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("/health")]
    [InlineData("/swagger")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/some/comics/path")]
    public void Other_paths_are_unchanged(string path)
    {
        Assert.Equal(path, Run(path).Path);
    }

    [Fact]
    public void Rewriting_twice_gives_the_same_result()
    {
        PathRewriteRules.TryRewrite("/comics/hot", out var once);
        var rewroteAgain = PathRewriteRules.TryRewrite(once, out var twice);

        Assert.False(rewroteAgain);
        Assert.Equal(once, twice);
    }

    // ---- Original path ----

    [Fact]
    public void Original_path_is_stored_for_a_rewritten_request()
    {
        var result = Run("/Comics/Hot");

        Assert.Equal("/Comics/Hot", result.Original);
        Assert.Equal("/api/comics/Hot", result.Path);
    }

    [Fact]
    public void Original_path_is_stored_for_an_untouched_request()
    {
        Assert.Equal("/auth/login", Run("/auth/login").Original);
    }

    [Fact]
    public async Task Next_middleware_is_always_called()
    {
        var called = 0;
        var middleware = new PathRewriteMiddleware(_ => { called++; return Task.CompletedTask; });

        foreach (var path in new[] { "/comics", "/auth/login", "/nope" })
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            await middleware.InvokeAsync(context);
        }

        Assert.Equal(3, called);
    }
}
