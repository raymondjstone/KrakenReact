namespace KrakenReact.Server.Services;

public partial class TradingStateService
{
    /// <summary>How long the live price feed may go without any tick before prices are treated as untrustworthy.</summary>
    public static readonly TimeSpan MaxFeedAge = TimeSpan.FromMinutes(5);

    private long _lastFeedTickTicks;

    /// <summary>Called by the live ticker feed on every price tick (<paramref name="at"/> is for tests).</summary>
    public void MarkFeedTick(DateTime? at = null) =>
        Interlocked.Exchange(ref _lastFeedTickTicks, (at ?? DateTime.UtcNow).ToUniversalTime().Ticks);

    /// <summary>
    /// True while the live ticker feed is delivering. Everything that trades or alerts on a price should check this
    /// first: if the websocket has silently died, the "latest price" is whatever it was when it died — possibly hours
    /// old — and a stop-loss or auto-order acting on it would be acting on a market that no longer exists.
    /// Judged across ALL pairs rather than per pair, so a thinly-traded coin that simply hasn't traded lately doesn't
    /// switch off its own protection; only the feed itself going quiet does.
    /// </summary>
    public bool IsPriceFeedAlive(TimeSpan? maxAge = null)
    {
        var last = Interlocked.Read(ref _lastFeedTickTicks);
        if (last == 0) return false; // never ticked: nothing is known yet
        return DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc) <= (maxAge ?? MaxFeedAge);
    }

    /// <summary>How long since the feed last ticked, or null if it never has.</summary>
    public TimeSpan? FeedAge
    {
        get
        {
            var last = Interlocked.Read(ref _lastFeedTickTicks);
            return last == 0 ? null : DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc);
        }
    }
}
