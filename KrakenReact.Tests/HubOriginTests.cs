using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace KrakenReact.Tests;

public class HubOriginTests
{
    private static readonly string[] Cors = ["http://localhost:5173", "https://awakethekraken"];

    private static bool Allowed(string? origin, string? host = "localhost:4567", string? forwarded = null, string[]? hosts = null) =>
        HubOriginMiddleware.IsAllowed(origin, host, forwarded, Cors, hosts ?? []);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoOrigin_IsNotACrossSiteRequest_AndPasses(string? origin) => Assert.True(Allowed(origin));

    [Fact] public void TheAppServingItsOwnPage_Passes() => Assert.True(Allowed("http://localhost:4567"));
    [Fact] public void TheViteDevServer_IsAConfiguredOrigin() => Assert.True(Allowed("http://localhost:5173"));
    [Fact] public void ConfiguredOrigins_MatchIgnoringCaseAndTrailingSlash() => Assert.True(Allowed("HTTPS://AwakeTheKraken/"));

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:9999")]            // another local app on a different port is another origin
    [InlineData("http://localhost.evil.example")]
    [InlineData("null")]                              // sandboxed iframes and file:// pages
    [InlineData("not a url")]
    [InlineData("ftp://localhost:4567")]
    public void AnyOtherWebsite_IsRefused(string origin) => Assert.False(Allowed(origin));

    [Fact]
    public void ADefaultPortIsTheSameOrigin()
    {
        Assert.True(Allowed("http://myserver", host: "myserver:80"));
        Assert.True(Allowed("https://myserver:443", host: "myserver"));
        Assert.True(Allowed("https://myserver", host: "myserver:443"));
    }

    [Fact]
    public void BehindAReverseProxy_TheForwardedHostCounts()
    {
        Assert.False(Allowed("https://app.example.com", host: "internal:4567"));
        Assert.True(Allowed("https://app.example.com", host: "internal:4567", forwarded: "app.example.com"));
        Assert.True(Allowed("https://app.example.com", host: "internal:4567", forwarded: "app.example.com, other"));
    }

    [Fact]
    public void AHostListedInAllowedHosts_IsAccepted()
    {
        Assert.False(Allowed("https://app.example.com", host: "internal:4567"));
        Assert.True(Allowed("https://app.example.com", host: "internal:4567", hosts: ["app.example.com"]));
    }

    // ── the middleware itself ──────────────────────────────────────────────

    private static async Task<(int Status, bool Reached)> Send(string path, string? origin, string host = "localhost:4567")
    {
        var reached = false;
        var mw = new HubOriginMiddleware(_ => { reached = true; return Task.CompletedTask; }, "/tradingHub", Cors, []);
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Host = new HostString(host);
        if (origin != null) ctx.Request.Headers.Origin = origin;
        await mw.InvokeAsync(ctx);
        return (ctx.Response.StatusCode, reached);
    }

    [Fact]
    public async Task AHostileOrigin_CannotReachTheHub_NegotiateOrSocket()
    {
        Assert.Equal((403, false), await Send("/tradingHub", "https://evil.example"));
        Assert.Equal((403, false), await Send("/tradingHub/negotiate", "https://evil.example"));
    }

    [Fact]
    public async Task TheAppsOwnOrigin_ReachesTheHub()
    {
        Assert.True((await Send("/tradingHub", "http://localhost:4567")).Reached);
        Assert.True((await Send("/tradingHub", null)).Reached);
    }

    [Fact]
    public async Task OtherPaths_AreNotTheseMiddlewaresBusiness()
    {
        Assert.True((await Send("/api/orders", "https://evil.example")).Reached);   // the API has its own guards (CORS + header)
    }

    [Fact]
    public void ItCanBeBuiltTheWayProgramBuildsIt()
    {
        // UseMiddleware resolves the constructor through DI with these arguments in this order
        var services = new ServiceCollection().BuildServiceProvider();
        var mw = ActivatorUtilities.CreateInstance<HubOriginMiddleware>(services, (RequestDelegate)(_ => Task.CompletedTask),
            "/tradingHub", (IEnumerable<string>)Cors, (IEnumerable<string>)new[] { "myhost" });
        Assert.NotNull(mw);
    }
}
