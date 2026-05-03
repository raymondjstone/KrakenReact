using KrakenReact.Server.DTOs;
using Kraken.Net.Enums;
using System.Collections.Concurrent;

namespace KrakenReact.Server.Services;

public class AutoOrderService
{
    private readonly TradingStateService _state;
    private readonly NotificationService _notifications;
    private readonly KrakenRestService _kraken;
    private readonly ILogger<AutoOrderService> _logger;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instrumentLocks = new();

    public AutoOrderService(TradingStateService state, NotificationService notifications, KrakenRestService kraken, ILogger<AutoOrderService> logger)
    {
        _state = state;
        _notifications = notifications;
        _kraken = kraken;
        _logger = logger;
    }

    public async Task<AutoTradeDto> CheckAsync(PriceDataItem instrument, string rulename, bool allowOrderAdd = false)
    {
        string key = instrument.Symbol;
        var semaphore = _instrumentLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync();
        try { return await CheckAsyncInternal(instrument, rulename, allowOrderAdd); }
        finally { semaphore.Release(); }
    }

    private async Task<AutoTradeDto> CheckAsyncInternal(PriceDataItem instrument, string rulename, bool allowOrderAdd)
    {
        var ao = new AutoTradeDto
        {
            Symbol = instrument.Symbol,
            Base = instrument.Base,
            CCY = instrument.CCY,
            CoinType = instrument.CoinType,
            ClosePriceMovement = instrument.CloseMovementDiff(1),
            ClosePriceMovementWeek = instrument.CloseMovementDiff(7),
            ClosePriceMovementMonth = instrument.CloseMovementDiff(31)
        };

        if (!instrument.KrakenNewPricesLoadedEver)
        {
            ao.Reason = $"{rulename} Historic Prices not loaded";
            return ao;
        }

        var klines = instrument.GetKlineSnapshot();
        if (!klines.Any() || klines.Count < 60)
        {
            ao.Reason = $"{rulename} Too few Historic Prices";
            return ao;
        }

        var monthend = DateTime.Now.AddDays(-32);
        var weekend = DateTime.Now.AddDays(-7);
        var closePrice = instrument.LatestKline?.Close ?? 0;
        var avgDay = instrument.ClosePriceAverage(1);
        var avgWeek = instrument.ClosePriceAverage(7);

        var weekDayDiff = avgDay.HasValue && avgWeek.HasValue && avgWeek.Value != 0
            ? Math.Round(avgDay.Value * 100 / avgWeek.Value, 1)
            : 99999m;
        ao.OrderRanking = (int)(1000 * weekDayDiff);

        if (closePrice == 0) { ao.Reason = $"{rulename} Zero close price"; return ao; }
        if (instrument.CoinType == "Currency" || instrument.CoinType == "Blacklist") { ao.Reason = $"{rulename} Currency or blacklisted"; return ao; }

        if (!klines.Any(o => o.OpenTime < DateTime.UtcNow.AddDays(-365)))
        {
            ao.Reason = $"{rulename} No prices older than a year";
            return ao;
        }

        var lastDay = klines.LastOrDefault(o => o.Interval == "OneDay");
        if (lastDay == null || (lastDay.Volume * lastDay.Close) < 20000)
        {
            ao.Reason = $"{rulename} Low Volume yesterday under 20k";
            return ao;
        }

        if (avgWeek == null) { ao.Reason = $"{rulename} no average for week"; return ao; }
        if (avgDay == null) { ao.Reason = $"{rulename} no average for day"; return ao; }
        if (avgWeek >= avgDay) { ao.Reason = $"{rulename} Average week price is not under day price"; return ao; }
        if (weekDayDiff < 2) { ao.Reason = $"{rulename} Week/Day difference under 2%"; return ao; }

        // Check symbol status
        if (_state.Symbols.TryGetValue(instrument.Symbol + "/USD", out var symbolInfo))
        {
            if (symbolInfo.Status == SymbolStatus.CancelOnly) { ao.Reason = $"{rulename} Cancel only SELL IT!!!"; return ao; }
            if (symbolInfo.Status == SymbolStatus.Delisted) { ao.Reason = $"{rulename} Delisted SELL IT!!!"; return ao; }
            if (symbolInfo.Status == SymbolStatus.ReduceOnly) { ao.Reason = $"{rulename} Reduce Only SELL IT!!!"; return ao; }
            if (symbolInfo.Status == SymbolStatus.WorkInProcess || symbolInfo.Status == SymbolStatus.Maintenance)
            {
                ao.Reason = $"{rulename} asset state of {symbolInfo.Status}";
                return ao;
            }
        }

        if (lastDay == null || (lastDay.Volume * lastDay.Close) < 100000)
        {
            ao.Reason = $"{rulename} Ok but Low Volume yesterday under 100k";
            return ao;
        }

        // Check if buy order already exists
        var hasOpenBuyOrder = _state.Orders.Values.Any(o =>
            o.Symbol == instrument.SymbolNoSlash && TradingStateService.IsOpenOrderStatus(o.Status) && o.Side == "Buy");
        if (hasOpenBuyOrder) { ao.Reason = $"{rulename} Buy Order already in queue"; return ao; }

        ao.OrderWanted = true;

        var hasOpenSellOrder = _state.Orders.Values.Any(o =>
            o.Symbol == instrument.SymbolNoSlash && TradingStateService.IsOpenOrderStatus(o.Status) && o.Side == "Sell");
        if (hasOpenSellOrder) { ao.Reason = $"{rulename} ok, but Sell Order already in queue"; return ao; }

        ao.Reason = ao.OrderRanking < 4400 ? $"{rulename} OK but low score" : $"{rulename} OK";
        return ao;
    }

    public static decimal RoundDown(decimal value, int decimals)
    {
        decimal factor = (decimal)Math.Pow(10, decimals);
        return Math.Floor(value * factor) / factor;
    }
}
