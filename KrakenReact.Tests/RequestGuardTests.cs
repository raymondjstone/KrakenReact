using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Http;

namespace KrakenReact.Tests;

public class RequestGuardTests
{
    private static async Task<(int Status, bool NextCalled)> Run(string method, string path, string? header = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (header != null) ctx.Request.Headers[RequireClientHeaderMiddleware.HeaderName] = header;
        ctx.Response.Body = new MemoryStream();

        var called = false;
        await new RequireClientHeaderMiddleware(_ => { called = true; return Task.CompletedTask; }).InvokeAsync(ctx);
        return (ctx.Response.StatusCode, called);
    }

    [Theory]
    [InlineData("/api/orders/close/BTC")]      // market-sells a whole balance, and takes no body
    [InlineData("/api/shutdown")]
    [InlineData("/api/microtrade/3/trigger")]
    public async Task StateChangingCallWithoutTheHeader_IsRefused(string path)
    {
        var (status, next) = await Run("POST", path);
        Assert.Equal(403, status);
        Assert.False(next);
    }

    [Fact]
    public async Task WrongHeaderValue_IsRefused()
    {
        Assert.Equal(403, (await Run("POST", "/api/shutdown", "something-else")).Status);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task EveryStateChangingMethod_IsChecked(string method)
    {
        Assert.Equal(403, (await Run(method, "/api/settings")).Status);
    }

    [Fact]
    public async Task WithTheHeader_TheRequestGoesThrough()
    {
        var (status, next) = await Run("POST", "/api/orders/close/BTC", RequireClientHeaderMiddleware.HeaderValue);
        Assert.True(next);
        Assert.Equal(200, status);
    }

    [Theory]
    [InlineData("GET", "/api/orders")]
    [InlineData("HEAD", "/api/orders")]
    [InlineData("OPTIONS", "/api/orders")]   // CORS pre-flight must reach the CORS middleware
    [InlineData("POST", "/tradingHub/negotiate")] // SignalR is not under /api
    [InlineData("POST", "/hangfire/recurring/trigger")]
    public async Task ReadsPreflightsAndNonApiPaths_AreNotAffected(string method, string path)
    {
        var (_, next) = await Run(method, path);
        Assert.True(next);
    }
}

public class AllowedHostsTests
{
    private static async Task<(int Status, bool NextCalled)> Run(string[] allowed, string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Host = new HostString(host);
        ctx.Response.Body = new MemoryStream();
        var called = false;
        await new AllowedHostsMiddleware(_ => { called = true; return Task.CompletedTask; }, allowed).InvokeAsync(ctx);
        return (ctx.Response.StatusCode, called);
    }

    [Fact]
    public async Task UnlistedHost_IsRefused_ThePortDoesNotMatter()
    {
        var (status, next) = await Run(new[] { "localhost", "myserver" }, "evil.example.com:4567");
        Assert.Equal(421, status);
        Assert.False(next);
    }

    [Theory]
    [InlineData("localhost:4567")]
    [InlineData("LOCALHOST")]
    [InlineData("myserver:80")]
    public async Task ListedHost_IsAccepted(string host)
    {
        Assert.True((await Run(new[] { "localhost", "myserver" }, host)).NextCalled);
    }

    [Fact]
    public async Task EmptyList_MeansAllowAll_AsBefore()
    {
        Assert.True((await Run(Array.Empty<string>(), "anything.example.com")).NextCalled);
    }

    [Theory]
    [InlineData("localhost,myserver", 2)]
    [InlineData(" localhost ; myserver ;", 2)]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    public void Parse_AcceptsCommaAndSemicolonLists(string? configured, int expected)
    {
        Assert.Equal(expected, AllowedHostsMiddleware.Parse(configured).Length);
    }
}
