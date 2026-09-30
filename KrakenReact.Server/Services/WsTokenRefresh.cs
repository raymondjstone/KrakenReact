namespace KrakenReact.Server.Services;

/// <summary>
/// Fetches a fresh websocket token after a reconnect, retrying with growing pauses. Right after a network drop the token request
/// often fails for a few seconds; subscribing with the old token instead is rejected by Kraken, and leaves a socket that looks
/// connected but receives no executions or balances.
/// </summary>
public static class WsTokenRefresh
{
    public static readonly TimeSpan[] DefaultDelays =
        [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30)];

    /// <summary>The first token obtained, or null if every attempt (one initial plus one per delay) failed.</summary>
    public static async Task<string?> TryGetAsync(Func<Task<string?>> fetch, IReadOnlyList<TimeSpan> delays, CancellationToken ct = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var token = await fetch();
                if (!string.IsNullOrEmpty(token)) return token;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* treated like a null token: try again */ }

            if (attempt >= delays.Count) return null;
            await Task.Delay(delays[attempt], ct);
        }
    }
}
