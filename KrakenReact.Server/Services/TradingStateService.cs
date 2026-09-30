using KrakenReact.Server.DTOs;
using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

/// <summary>
/// Shared in-memory trading state (prices, orders, balances, symbols) plus the settings that drive it.
/// Split across partial files by concern: <c>.Assets</c> (asset-name normalization and static config),
/// <c>.Symbols</c> (symbol/price resolution), <c>.Settings</c> (database-backed configuration) and
/// <c>.Orders</c> (order/balance recalculation). This file holds the state and settings fields themselves.
/// </summary>
public partial class TradingStateService
{
    private readonly DelistedPriceService _delisted;

    public TradingStateService(DelistedPriceService delisted)
    {
        _delisted = delisted;
    }

    public bool InitialDataLoad { get; set; } = true;
    public string LastStatusMessage { get; set; } = "";
    public bool StakingNotifications { get; set; }
    public bool HideAlmostZeroBalances { get; set; }
    public bool OrderProximityNotifications { get; set; } = true;
    public decimal OrderProximityThreshold { get; set; } = 2.0m;
    // Bounded ledger-ID dedup set with FIFO eviction. Clear-on-overflow (used by
    // _notifiedOrders) is wrong here — wiping the set would trigger a re-notification
    // storm on the next Kraken ledger poll because every recently-returned ledger
    // entry would suddenly look "unseen". FIFO keeps the oldest dedup state aging
    // out one at a time so the set never empties.
    private readonly HashSet<string> _seenLedgerIds = new();
    private readonly Queue<string> _seenLedgerOrder = new();
    private readonly object _seenLedgerLock = new();
    private const int MaxSeenLedgerIds = 5000;

    public bool HasSeenLedger(string id)
    {
        lock (_seenLedgerLock) { return _seenLedgerIds.Contains(id); }
    }

    public void AddSeenLedger(string id)
    {
        lock (_seenLedgerLock)
        {
            if (!_seenLedgerIds.Add(id)) return; // already present
            _seenLedgerOrder.Enqueue(id);
            while (_seenLedgerOrder.Count > MaxSeenLedgerIds)
                _seenLedgerIds.Remove(_seenLedgerOrder.Dequeue());
        }
    }

    public ConcurrentDictionary<string, PriceDataItem> Prices { get; } = new();
    public ConcurrentDictionary<string, OrderDto> Orders { get; } = new();
    public ConcurrentDictionary<string, BalanceDto> Balances { get; } = new();
    public ConcurrentDictionary<string, AutoTradeDto> AutoOrders { get; } = new();
    public ConcurrentDictionary<string, KrakenSymbol> Symbols { get; } = new();

    // Snapshot caches refreshed by BackgroundTaskService so request handlers don't hit the DB.
    public IReadOnlyList<KrakenUserTrade> CachedTrades { get; private set; } = Array.Empty<KrakenUserTrade>();
    public IReadOnlyList<KrakenLedgerEntry> CachedLedgers { get; private set; } = Array.Empty<KrakenLedgerEntry>();
    public void SetCachedTrades(IEnumerable<KrakenUserTrade> trades) => CachedTrades = trades.ToList();
    public void SetCachedLedgers(IEnumerable<KrakenLedgerEntry> ledgers) => CachedLedgers = ledgers.ToList();

    // ML prediction settings
    public string PredictionSymbols { get; set; } = "XBT/USD,ETH/USD,SOL/USD";
    public string PredictionInterval { get; set; } = "OneHour";
    public string PredictionMode { get; set; } = "specific";   // "specific" | "all" | "existing"
    public string PredictionCurrency { get; set; } = "USD";    // quote currency when mode="all"
    public int PredictionAutoRefreshIntervalMinutes { get; set; } = 15;

    // Order book state
    public static readonly int[] ValidBookDepths = { 10, 25, 100, 500, 1000 };
    public int OrderBookDepth { get; set; } = 25;
    private string? _bookPair;
    private readonly object _bookLock = new();
    public event Action<string?, string?>? BookPairChanged; // (oldPair, newPair)

    public string? BookPair
    {
        get { lock (_bookLock) return _bookPair; }
        set
        {
            string? old;
            lock (_bookLock) { old = _bookPair; _bookPair = value; }
            if (old != value) BookPairChanged?.Invoke(old, value);
        }
    }

    public static readonly List<decimal> DefaultOrderPriceOffsets = new() { 2, 5, 10, 15 };
    public static readonly List<decimal> DefaultOrderQtyPercentages = new() { 5, 10, 20, 25, 50, 75, 100 };
    public List<decimal> OrderPriceOffsets { get; set; } = new(DefaultOrderPriceOffsets);
    public List<decimal> OrderQtyPercentages { get; set; } = new(DefaultOrderQtyPercentages);

    public bool AutoSellOnBuyFill { get; set; }
    public decimal AutoSellPercentage { get; set; } = 10m;
    public bool AutoAddStakingToOrder { get; set; }
    public bool StopLossEnabled { get; set; }
    public decimal StopLossPct { get; set; } = 5m;
    public bool TakeProfitEnabled { get; set; }
    public decimal TakeProfitPct { get; set; } = 15m;
    public bool DrawdownAlertEnabled { get; set; }
    public decimal DrawdownAlertThreshold { get; set; } = 10m;
    public bool DryRunJobs { get; set; }

    // Trailing stop-loss
    public bool TrailingStopEnabled { get; set; }
    public decimal TrailingStopPct { get; set; } = 5m;
    public ConcurrentDictionary<string, decimal> TrailingHighPrices { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Auto-cancel stale open orders
    public bool AutoCancelEnabled { get; set; }
    public int AutoCancelDays { get; set; } = 30;
    public bool AutoCancelBuys { get; set; } = true;
    public bool AutoCancelSells { get; set; }

    /// <summary>Cache of working Kraken API pair names. Key = internal symbol (e.g. "XBT/USD"), Value = API-accepted name (e.g. "BTCUSD").</summary>
    public ConcurrentDictionary<string, string> ApiPairNameCache { get; } = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notifiedOrders = new();
    private readonly object _notifiedLock = new();
    private const int MaxNotifiedOrders = 5000;

    public bool HasNotified(string orderId)
    {
        lock (_notifiedLock) { return _notifiedOrders.Contains(orderId); }
    }

    public void AddNotified(string orderId)
    {
        lock (_notifiedLock)
        {
            if (_notifiedOrders.Count >= MaxNotifiedOrders)
                _notifiedOrders.Clear();
            _notifiedOrders.Add(orderId);
        }
    }

    // Proximity alerts: atomic check-and-mark, FIFO eviction (no wholesale clear → no re-alert storm),
    // and re-arming once the order has moved away from the price again.
    private readonly HashSet<string> _proximityAlerted = new();
    private readonly Queue<string> _proximityOrder = new();
    private readonly object _proximityLock = new();

    /// <summary>True exactly once per approach: marks the order alerted and returns true if it wasn't already.</summary>
    public bool TryMarkProximityAlerted(string orderId)
    {
        lock (_proximityLock)
        {
            if (!_proximityAlerted.Add(orderId)) return false;
            _proximityOrder.Enqueue(orderId);
            while (_proximityOrder.Count > MaxNotifiedOrders)
                _proximityAlerted.Remove(_proximityOrder.Dequeue());
            return true;
        }
    }

    /// <summary>Re-arms an order's proximity alert (call once it has moved back out of the zone).</summary>
    public void ClearProximityAlerted(string orderId)
    {
        lock (_proximityLock) { _proximityAlerted.Remove(orderId); }
    }
}
