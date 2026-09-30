using System.Collections.Concurrent;
using KrakenReact.Server.Services;

namespace KrakenReact.Server.Hubs;

/// <summary>
/// Which order-book pair the connected clients want. The exchange feed carries one book at a time, so the newest request wins;
/// but the feed is released when nobody is left listening, and when the last client that wanted the current pair goes, the feed
/// moves to another client's pair instead of streaming for nobody. Requests for names that are not exchange pairs are refused
/// rather than forwarded to Kraken.
/// </summary>
public sealed class BookSubscriptions
{
    private readonly TradingStateService _state;
    private readonly ConcurrentDictionary<string, string> _byConnection = new();
    private readonly object _gate = new();

    public BookSubscriptions(TradingStateService state) => _state = state;

    /// <summary>Returns false (and changes nothing) when the pair is not a known exchange pair.</summary>
    public bool Subscribe(string connectionId, string? pair)
    {
        var canonical = Canonical(pair);
        if (canonical == null) return false;
        lock (_gate)
        {
            _byConnection[connectionId] = canonical;
            _state.BookPair = canonical;
        }
        return true;
    }

    /// <summary>The client stopped watching (or disconnected).</summary>
    public void Release(string connectionId)
    {
        lock (_gate)
        {
            if (!_byConnection.TryRemove(connectionId, out _)) return;
            if (_byConnection.IsEmpty) { _state.BookPair = null; return; }
            // Someone else is still watching: make sure the feed shows a pair somebody wants
            var current = _state.BookPair;
            if (current == null || !_byConnection.Values.Contains(current, StringComparer.OrdinalIgnoreCase))
                _state.BookPair = _byConnection.Values.First();
        }
    }

    private string? Canonical(string? pair)
    {
        if (string.IsNullOrWhiteSpace(pair) || pair.Length > 30) return null;
        var match = _state.Symbols.Keys.FirstOrDefault(k => k.Equals(pair.Trim(), StringComparison.OrdinalIgnoreCase));
        return match;
    }
}
