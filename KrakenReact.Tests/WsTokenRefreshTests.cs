using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class WsTokenRefreshTests
{
    private static readonly TimeSpan[] Fast = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];

    [Fact]
    public async Task AnImmediateToken_IsReturned_WithoutRetrying()
    {
        var calls = 0;
        var t = await WsTokenRefresh.TryGetAsync(() => { calls++; return Task.FromResult<string?>("tok"); }, Fast);
        Assert.Equal("tok", t);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NullsAndErrors_AreRetried_UntilATokenArrives()
    {
        var calls = 0;
        var t = await WsTokenRefresh.TryGetAsync(() =>
        {
            calls++;
            if (calls == 1) return Task.FromResult<string?>(null);
            if (calls == 2) throw new HttpRequestException("network down");
            return Task.FromResult<string?>("fresh");
        }, Fast);

        Assert.Equal("fresh", t);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task WhenEveryAttemptFails_ReturnsNull_AfterOnePlusOnePerDelay()
    {
        var calls = 0;
        var t = await WsTokenRefresh.TryGetAsync(() => { calls++; return Task.FromResult<string?>(null); }, Fast);
        Assert.Null(t);
        Assert.Equal(Fast.Length + 1, calls);
    }

    [Fact]
    public async Task AnEmptyToken_CountsAsAFailure()
    {
        var t = await WsTokenRefresh.TryGetAsync(() => Task.FromResult<string?>(""), []);
        Assert.Null(t);
    }

    [Fact]
    public async Task Cancellation_StopsTheRetrying()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WsTokenRefresh.TryGetAsync(
            () => { cts.Cancel(); return Task.FromResult<string?>(null); }, [TimeSpan.FromSeconds(30)], cts.Token));
    }
}
