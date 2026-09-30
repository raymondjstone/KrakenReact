using System.Text.Json;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace KrakenReact.Server.Services;

/// <summary>What changed in a keyed collection since it was last sent.</summary>
public sealed record LiveDelta<T>(List<T> Changed, List<string> Removed)
{
    public bool IsEmpty => Changed.Count == 0 && Removed.Count == 0;
}

/// <summary>
/// Remembers what was last sent for each item (as its JSON) and reports only the items that are new, changed or gone.
/// Comparing serialized form means every field a client could see is covered without hand-maintaining an equality
/// method that would silently go stale when a property is added.
/// </summary>
public sealed class SnapshotDiffer<T>
{
    private readonly Func<T, string> _key;
    private readonly Dictionary<string, string> _last = new();
    private readonly object _lock = new();

    public SnapshotDiffer(Func<T, string> key) => _key = key;

    public LiveDelta<T> Diff(IEnumerable<T> current)
    {
        lock (_lock)
        {
            var changed = new List<T>();
            var seen = new HashSet<string>();
            foreach (var item in current)
            {
                var key = _key(item);
                seen.Add(key);
                var json = JsonSerializer.Serialize(item);
                if (!_last.TryGetValue(key, out var previous) || previous != json)
                {
                    _last[key] = json;
                    changed.Add(item);
                }
            }

            var removed = _last.Keys.Where(k => !seen.Contains(k)).ToList();
            foreach (var k in removed) _last.Remove(k);
            return new LiveDelta<T>(changed, removed);
        }
    }

    /// <summary>Records <paramref name="current"/> as the baseline without producing a delta (used after a full send).</summary>
    public void Reset(IEnumerable<T> current)
    {
        lock (_lock)
        {
            _last.Clear();
            foreach (var item in current) _last[_key(item)] = JsonSerializer.Serialize(item);
        }
    }
}

/// <summary>
/// One broadcast channel (orders or balances). Sends deltas to all clients, serialised so two overlapping broadcasts
/// can't deliver an older version of an item after a newer one, plus a full send that re-baselines everyone.
/// </summary>
public sealed class LiveStream<T>
{
    private readonly SnapshotDiffer<T> _differ;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _deltaEvent;
    private readonly string _fullEvent;

    public LiveStream(Func<T, string> key, string deltaEvent, string fullEvent)
    {
        _differ = new SnapshotDiffer<T>(key);
        _deltaEvent = deltaEvent;
        _fullEvent = fullEvent;
    }

    /// <summary>Sends only what changed since the last send; sends nothing at all if nothing did.</summary>
    public async Task BroadcastAsync(IEnumerable<T> current, IClientProxy target, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var delta = _differ.Diff(current);
            if (delta.IsEmpty) return;
            await target.SendAsync(_deltaEvent, new { changed = delta.Changed, removed = delta.Removed }, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Sends the complete list and makes it the new baseline. A safety net that heals any client that missed a delta.</summary>
    public async Task BroadcastFullAsync(IEnumerable<T> current, IClientProxy target, CancellationToken ct = default)
    {
        var list = current.ToList();
        await _gate.WaitAsync(ct);
        try
        {
            _differ.Reset(list);
            await target.SendAsync(_fullEvent, list, ct);
        }
        finally { _gate.Release(); }
    }
}

public static class LiveBroadcastExtensions
{
    /// <summary>Pushes changed/removed orders to every client.</summary>
    public static Task BroadcastOrdersAsync(this IHubContext<TradingHub> hub, TradingStateService state) =>
        state.LiveOrders.BroadcastAsync(state.Orders.Values, hub.Clients.All);

    /// <summary>Pushes changed/removed balances to every client.</summary>
    public static Task BroadcastBalancesAsync(this IHubContext<TradingHub> hub, TradingStateService state) =>
        state.LiveBalances.BroadcastAsync(state.Balances.Values, hub.Clients.All);

    /// <summary>Full orders + balances to every client, re-baselining the delta streams.</summary>
    public static async Task BroadcastFullResyncAsync(this IHubContext<TradingHub> hub, TradingStateService state)
    {
        await state.LiveOrders.BroadcastFullAsync(state.Orders.Values, hub.Clients.All);
        await state.LiveBalances.BroadcastFullAsync(state.Balances.Values, hub.Clients.All);
    }
}
