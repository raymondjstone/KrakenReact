namespace KrakenReact.Server.Services;

/// <summary>
/// Disposes a replaced object after a delay instead of at once. A client that is swapped out may still be serving a request that
/// started a moment earlier; disposing it under that request fails the call (on the order path, that means an ambiguous placement).
/// The delay only needs to outlast the longest request.
/// </summary>
public static class DeferredDisposal
{
    public static Task DisposeAfter(IDisposable? item, TimeSpan delay)
    {
        if (item == null) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try { await Task.Delay(delay); }
            finally { try { item.Dispose(); } catch { /* already gone - nothing to do */ } }
        });
    }
}
