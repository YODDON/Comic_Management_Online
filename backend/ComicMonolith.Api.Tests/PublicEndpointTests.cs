using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace ComicMonolith.Api.Tests;

/// <summary>
/// Pins the old gateway's public endpoint list, including its quirks, so any future change is deliberate.
/// The expected values come from reading `ApiGateway/Program.cs`, not from the new code.
/// </summary>
public class PublicEndpointTests
{
    // ---- helpers ----

    private static Endpoint FakeEndpoint(bool allowAnonymous = false) =>
        new(_ => Task.CompletedTask,
            allowAnonymous ? new EndpointMetadataCollection(new AllowAnonymousAttribute()) : EndpointMetadataCollection.Empty,
            "test");

    private static ClaimsPrincipal LoggedIn() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user")], "test"));

    private sealed record Result(bool NextCalled, int Status, string Body, string? ContentType);

    private static async Task<Result> RunGate(
        string method,
        string path,
        bool authenticated = false,
        bool endpoint = true,
        bool allowAnonymousEndpoint = false,
        bool throughRewrite = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (authenticated) context.User = LoggedIn();
        if (endpoint) context.SetEndpoint(FakeEndpoint(allowAnonymousEndpoint));

        var nextCalled = false;
        var gate = new PublicEndpointGateMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });

        if (throughRewrite)
        {
            await new PathRewriteMiddleware(gate.InvokeAsync).InvokeAsync(context);
        }
        else
        {
            await gate.InvokeAsync(context);
        }

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return new Result(nextCalled, context.Response.StatusCode, body, context.Response.ContentType);
    }

    // ---- the rules, one by one (expected values come from the gateway source) ----

    [Theory]
    // rule 1: exact auth paths, any method, case-insensitive
    [InlineData("POST", "/auth/login", true)]
    [InlineData("POST", "/auth/register", true)]
    [InlineData("POST", "/auth/forgot-password", true)]
    [InlineData("POST", "/auth/reset-password", true)]
    [InlineData("GET", "/auth/reset-password", true)]
    [InlineData("POST", "/auth/refetchToken", true)]
    [InlineData("POST", "/auth/resend-confirm", true)]
    [InlineData("POST", "/auth/google", true)]
    [InlineData("POST", "/AUTH/LOGIN", true)]
    // rule 2: /auth/verify prefix
    [InlineData("POST", "/auth/verify", true)]
    [InlineData("GET", "/auth/verify-email", true)]
    // rule 3: GET /banners prefix
    [InlineData("GET", "/banners", true)]
    [InlineData("GET", "/banners/admin", true)]
    [InlineData("POST", "/banners", false)]
    [InlineData("PUT", "/banners/1", false)]
    // rule 4: GET /categories prefix
    [InlineData("GET", "/categories", true)]
    [InlineData("GET", "/categories/1", true)]
    [InlineData("POST", "/categories", false)]
    // rule 5: GET /comics prefix except /comics/me and /comics/purchased
    [InlineData("GET", "/comics", true)]
    [InlineData("GET", "/comics/hot", true)]
    [InlineData("GET", "/comics/slug/one-piece", true)]
    [InlineData("GET", "/comics/me", false)]
    [InlineData("GET", "/comics/purchased", false)]
    [InlineData("POST", "/comics", false)]
    [InlineData("PUT", "/comics/1", false)]
    // rule 6: GET /chapters prefix
    [InlineData("GET", "/chapters", true)]
    [InlineData("GET", "/chapters/1/pages", true)]
    [InlineData("POST", "/chapters", false)]
    [InlineData("DELETE", "/chapters/1", false)]
    // rule 7: GET /comments prefix
    [InlineData("GET", "/comments", true)]
    [InlineData("POST", "/comments", false)]
    [InlineData("DELETE", "/comments/1", false)]
    // rule 8: POST /payments/sepay-webhook, exact
    [InlineData("POST", "/payments/sepay-webhook", true)]
    [InlineData("GET", "/payments/sepay-webhook", false)]
    [InlineData("PUT", "/payments/sepay-webhook", false)]
    // everything else needs a login
    [InlineData("GET", "/missions", false)]
    [InlineData("GET", "/notifications", false)]
    [InlineData("GET", "/currency/history", false)]
    [InlineData("GET", "/withdraws/me", false)]
    [InlineData("GET", "/wallets/me", false)]
    [InlineData("GET", "/favorites/me", false)]
    [InlineData("GET", "/follows/me", false)]
    [InlineData("GET", "/reading-history/me", false)]
    [InlineData("POST", "/uploads", false)]
    [InlineData("GET", "/auth/me", false)]
    [InlineData("POST", "/auth/logout", false)]
    [InlineData("GET", "/auth/admin/users", false)]
    [InlineData("GET", "/", false)]
    public void IsPublic_matches_the_gateway_rules(string method, string path, bool expected)
    {
        Assert.Equal(expected, PublicEndpoints.IsPublic(method, path));
    }

    // ---- quirks of the gateway code, pinned on purpose ----

    [Theory]
    // plain StartsWith, no segment boundary
    [InlineData("GET", "/banners-x", true)]
    [InlineData("GET", "/bannersfoo/bar", true)]
    [InlineData("GET", "/comicsfoo", true)]
    [InlineData("GET", "/categoriesX", true)]
    [InlineData("GET", "/chaptersX", true)]
    [InlineData("GET", "/commentsX", true)]
    [InlineData("GET", "/auth/verifyfoo", true)]
    // the /comics/me and /comics/purchased exclusions are prefixes too
    [InlineData("GET", "/comics/mexyz", false)]
    [InlineData("GET", "/comics/member", false)]
    [InlineData("GET", "/comics/me/anything", false)]
    [InlineData("GET", "/comics/purchasedX", false)]
    [InlineData("GET", "/comics/purchased/1", false)]
    // `me` only excludes when it directly follows /comics/
    [InlineData("GET", "/comics/hot/me", true)]
    [InlineData("GET", "/comicsme", true)]
    // exact rules are exact: a trailing slash or suffix is not public
    [InlineData("POST", "/auth/login/", false)]
    [InlineData("POST", "/auth/loginx", false)]
    [InlineData("POST", "/auth/register/", false)]
    [InlineData("POST", "/payments/sepay-webhook/", false)]
    [InlineData("POST", "/payments/sepay-webhook-x", false)]
    // case is ignored (the gateway lowercases the path)
    [InlineData("POST", "/PAYMENTS/SEPAY-WEBHOOK", true)]
    [InlineData("GET", "/Comics/Hot", true)]
    [InlineData("GET", "/COMICS/ME", false)]
    [InlineData("GET", "/Comics/Purchased", false)]
    // the /auth rules ignore the HTTP method
    [InlineData("GET", "/auth/login", true)]
    [InlineData("DELETE", "/auth/login", true)]
    [InlineData("PUT", "/auth/verify", true)]
    // only GET counts as a read: HEAD and OPTIONS are not public on the GET rules
    [InlineData("HEAD", "/comics", false)]
    [InlineData("OPTIONS", "/comics", false)]
    [InlineData("GET", "/comics", true)]
    // the method name is compared ignoring case
    [InlineData("get", "/comics", true)]
    [InlineData("post", "/payments/sepay-webhook", true)]
    // the translation rule was not ported
    [InlineData("POST", "/translations", false)]
    [InlineData("POST", "/translations/translate", false)]
    // paths already under /api are not on the list: the rules apply to what the client sent
    [InlineData("GET", "/api/comics", false)]
    [InlineData("POST", "/api/payments/sepay-webhook", false)]
    // /health is exempted by the middleware, not by the rules
    [InlineData("GET", "/health", false)]
    public void IsPublic_pins_the_quirks(string method, string path, bool expected)
    {
        Assert.Equal(expected, PublicEndpoints.IsPublic(method, path));
    }

    [Fact]
    public void IsPublic_handles_empty_input()
    {
        Assert.False(PublicEndpoints.IsPublic("GET", ""));
        Assert.False(PublicEndpoints.IsPublic("GET", null!));
    }

    // ---- every public endpoint of the host does not return 401 (list read from the running host's endpoint metadata) ----

    public static IEnumerable<object[]> PublicEndpointsOfTheHost() =>
    [
        ["GET", "/categories"], ["GET", "/categories/1"],
        ["GET", "/chapters"], ["GET", "/chapters/1"], ["GET", "/chapters/1/pages"], ["GET", "/chapters/slug/x/pages"],
        ["GET", "/comics"], ["GET", "/comics/3fa85f64-5717-4562-b3fc-2c963f66afa6"], ["GET", "/comics/hot"],
        ["GET", "/comics/last-completed"], ["GET", "/comics/outstandings"], ["GET", "/comics/outstandings/paged"],
        ["GET", "/comics/slug/x"],
        ["GET", "/comments"],
        ["POST", "/payments/sepay-webhook"],
        ["POST", "/auth/forgot-password"], ["POST", "/auth/google"], ["POST", "/auth/login"], ["POST", "/auth/refetchToken"],
        ["POST", "/auth/register"], ["POST", "/auth/resend-confirm"], ["GET", "/auth/reset-password"],
        ["POST", "/auth/reset-password"], ["POST", "/auth/verify"], ["GET", "/auth/verify-email"],
        ["GET", "/banners"],
    ];

    [Theory]
    [MemberData(nameof(PublicEndpointsOfTheHost))]
    public async Task Public_endpoints_are_not_blocked_without_a_token(string method, string path)
    {
        var result = await RunGate(method, path, throughRewrite: true);

        Assert.True(result.NextCalled);
        Assert.NotEqual(401, result.Status);
    }

    // ---- representative protected endpoints return 401 without a token ----

    public static IEnumerable<object[]> ProtectedEndpoints() =>
    [
        ["GET", "/missions"], ["GET", "/missions/my-missions"], ["POST", "/missions"], ["GET", "/missions/admin"],
        ["GET", "/notifications"], ["POST", "/notifications"],
        ["GET", "/currency/history"], ["POST", "/currency"],
        ["GET", "/withdraws/me"], ["POST", "/withdraws"], ["GET", "/withdraws/admin"],
        ["GET", "/auth/me"], ["PUT", "/auth/me"], ["POST", "/auth/logout"], ["GET", "/auth/admin/users"],
        ["GET", "/comics/me"], ["GET", "/comics/purchased"], ["POST", "/comics"], ["PUT", "/comics/1"],
        ["POST", "/categories"], ["DELETE", "/categories/1"],
        ["POST", "/chapters"], ["DELETE", "/chapters/1"],
        ["POST", "/comments"], ["DELETE", "/comments/1"],
        ["GET", "/favorites/me"], ["POST", "/favorites/1"],
        ["GET", "/follows/me"], ["POST", "/follows/1"],
        ["GET", "/reading-history/me"], ["POST", "/reading-history"],
        ["GET", "/payments/transactions"], ["GET", "/payments/transactions/check/1"],
        ["POST", "/payments/deposit"], ["POST", "/payments/purchased-chapter"],
        ["POST", "/uploads"],
        ["POST", "/banners"], ["PUT", "/banners/1"], ["POST", "/banners/upload"],
        // the host's own /api paths are not on the public list either
        ["GET", "/api/comics"], ["GET", "/api/Comics/hot"], ["POST", "/api/payments/sepay-webhook"],
    ];

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Protected_endpoints_return_401_without_a_token(string method, string path)
    {
        var result = await RunGate(method, path, throughRewrite: true);

        Assert.False(result.NextCalled);
        Assert.Equal(401, result.Status);
    }

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Protected_endpoints_pass_for_a_logged_in_user(string method, string path)
    {
        var result = await RunGate(method, path, authenticated: true, throughRewrite: true);

        Assert.True(result.NextCalled);
        Assert.NotEqual(401, result.Status);
    }

    // ---- the 401 body is the gateway's own ----

    [Fact]
    public async Task The_401_body_is_the_gateway_ApiResponse_shape()
    {
        var result = await RunGate("GET", "/missions");

        Assert.Equal(401, result.Status);
        Assert.StartsWith("application/json", result.ContentType);

        using var json = JsonDocument.Parse(result.Body);
        var root = json.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("Unauthorized access", root.GetProperty("message").GetString());
        Assert.Equal(401, root.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("errors").ValueKind);
    }

    // ---- the [AllowAnonymous] question: the gate looks at the path, not at the endpoint's own attributes ----

    [Fact]
    public async Task AllowAnonymous_endpoint_on_a_non_public_path_is_still_blocked()
    {
        // GET /payments/sepay-webhook is [AllowAnonymous] in the controller, but the gateway's rule is POST only.
        var result = await RunGate("GET", "/payments/sepay-webhook", allowAnonymousEndpoint: true, throughRewrite: true);

        Assert.False(result.NextCalled);
        Assert.Equal(401, result.Status);
    }

    [Fact]
    public async Task Sepay_webhook_POST_is_reachable_without_a_token()
    {
        var result = await RunGate("POST", "/payments/sepay-webhook", allowAnonymousEndpoint: true, throughRewrite: true);

        Assert.True(result.NextCalled);
        Assert.NotEqual(401, result.Status);
    }

    [Fact]
    public async Task Sepay_webhook_POST_under_the_api_path_is_not_public()
    {
        // The SePay URL must keep being /payments/sepay-webhook (as it was through the gateway).
        var result = await RunGate("POST", "/api/payments/sepay-webhook", allowAnonymousEndpoint: true);

        Assert.Equal(401, result.Status);
    }

    // ---- the gate matches the path the client sent, not the rewritten one ----

    [Fact]
    public async Task The_gate_uses_the_original_path_after_the_rewrite()
    {
        // /comics/me is rewritten to /api/comics/me; the exclusion must still apply.
        var protectedResult = await RunGate("GET", "/comics/me", throughRewrite: true);
        var publicResult = await RunGate("GET", "/comics", throughRewrite: true);

        Assert.Equal(401, protectedResult.Status);
        Assert.True(publicResult.NextCalled);
    }

    [Fact]
    public async Task Without_a_stored_original_path_the_current_path_is_used()
    {
        // No rewrite middleware in front: Items has no OriginalPath.
        Assert.Equal(401, (await RunGate("GET", "/comics/me")).Status);
        Assert.True((await RunGate("GET", "/comics")).NextCalled);
    }

    // ---- /health ----

    [Theory]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/Health")]
    [InlineData("HEAD", "/HEALTH")]
    public async Task Health_is_exempt_by_exact_path(string method, string path)
    {
        var result = await RunGate(method, path);

        Assert.True(result.NextCalled);
        Assert.NotEqual(401, result.Status);
    }

    [Theory]
    [InlineData("/health/x")]
    [InlineData("/healthz")]
    [InlineData("/health/")]
    [InlineData("/api/health")]
    public async Task Health_exemption_is_not_a_prefix(string path)
    {
        var result = await RunGate("GET", path);

        Assert.Equal(401, result.Status);
    }

    // ---- paths that matched no endpoint are not gated ----

    [Theory]
    [InlineData("GET", "/nope")]
    [InlineData("GET", "/missions-typo")]
    [InlineData("POST", "/wallets/x")]
    public async Task A_request_that_matched_no_endpoint_goes_on_to_the_404(string method, string path)
    {
        var result = await RunGate(method, path, endpoint: false);

        Assert.True(result.NextCalled);
        Assert.NotEqual(401, result.Status);
    }

    // ---- the gateway's own 404 ----

    private sealed class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    private static async Task<Result> RunNoRoute(int statusFromPipeline, bool endpoint, bool startResponse = false, string? writtenBody = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/whatever";
        context.Response.Body = new MemoryStream();

        var middleware = new NoRouteJsonMiddleware(async ctx =>
        {
            // DefaultHttpContext never reports HasStarted, so simulate a response that already went out.
            if (startResponse) ctx.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
            if (endpoint) ctx.SetEndpoint(FakeEndpoint());
            ctx.Response.StatusCode = statusFromPipeline;
            if (writtenBody is not null) await ctx.Response.WriteAsync(writtenBody);
        });

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return new Result(true, context.Response.StatusCode, body, context.Response.ContentType);
    }

    [Fact]
    public async Task A_404_with_no_matched_endpoint_gets_the_gateway_body()
    {
        var result = await RunNoRoute(404, endpoint: false);

        Assert.Equal(404, result.Status);
        using var json = JsonDocument.Parse(result.Body);
        Assert.Equal("Resource not found", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(404, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task A_404_from_a_matched_endpoint_is_left_alone()
    {
        var result = await RunNoRoute(404, endpoint: true);

        Assert.Equal(404, result.Status);
        Assert.Equal("", result.Body);
    }

    [Fact]
    public async Task A_404_whose_body_was_already_written_is_left_alone()
    {
        var result = await RunNoRoute(404, endpoint: false, startResponse: true, writtenBody: "{\"message\":\"Comic not found\"}");

        Assert.Equal("{\"message\":\"Comic not found\"}", result.Body);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(500)]
    public async Task Other_statuses_are_left_alone(int status)
    {
        var result = await RunNoRoute(status, endpoint: false);

        Assert.Equal(status, result.Status);
        Assert.Equal("", result.Body);
    }
}
