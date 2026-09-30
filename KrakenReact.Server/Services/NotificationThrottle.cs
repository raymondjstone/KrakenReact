namespace KrakenReact.Server.Services;

/// <summary>
/// Decides whether a push notification should actually be sent. Two protections against a burst — a flapping feed, a
/// run of fills, a job failing every tick — turning into dozens of identical phone alerts (and eating Pushover's monthly
/// message allowance): an identical title+text is dropped if it was sent within the duplicate window, and no more than
/// a fixed number go out per minute. Suppressed messages are still recorded in the app's own alert log by the caller.
/// </summary>
public sealed class NotificationThrottle
{
    private readonly TimeSpan _duplicateWindow;
    private readonly int _maxPerMinute;
    private readonly Dictionary<string, DateTime> _lastSent = new();
    private readonly Queue<DateTime> _recent = new();
    private readonly object _lock = new();

    public NotificationThrottle(TimeSpan? duplicateWindow = null, int maxPerMinute = 20)
    {
        _duplicateWindow = duplicateWindow ?? TimeSpan.FromSeconds(30);
        _maxPerMinute = maxPerMinute;
    }

    public bool ShouldSend(string title, string text, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var key = title + "\n" + text;

        lock (_lock)
        {
            if (_lastSent.TryGetValue(key, out var previous) && now - previous < _duplicateWindow)
                return false;

            while (_recent.Count > 0 && now - _recent.Peek() >= TimeSpan.FromMinutes(1))
                _recent.Dequeue();
            if (_recent.Count >= _maxPerMinute)
                return false;

            _lastSent[key] = now;
            _recent.Enqueue(now);

            if (_lastSent.Count > 500) // keep the duplicate table small
                foreach (var stale in _lastSent.Where(kv => now - kv.Value >= _duplicateWindow).Select(kv => kv.Key).ToList())
                    _lastSent.Remove(stale);

            return true;
        }
    }
}
