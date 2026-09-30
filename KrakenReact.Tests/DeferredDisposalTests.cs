using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class DeferredDisposalTests
{
    private sealed class Probe : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task NotDisposedUntilTheDelayHasPassed()
    {
        var p = new Probe();
        var task = DeferredDisposal.DisposeAfter(p, TimeSpan.FromMilliseconds(300));

        Assert.False(p.Disposed);   // still usable by a call that started before the swap
        await task;
        Assert.True(p.Disposed);
    }

    [Fact]
    public async Task NullIsIgnored() => await DeferredDisposal.DisposeAfter(null, TimeSpan.FromSeconds(10));

    [Fact]
    public async Task ADisposeThatThrows_DoesNotFaultTheTask()
    {
        var task = DeferredDisposal.DisposeAfter(new Thrower(), TimeSpan.Zero);
        await task;
    }

    private sealed class Thrower : IDisposable { public void Dispose() => throw new ObjectDisposedException("x"); }
}
