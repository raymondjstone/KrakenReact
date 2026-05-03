using Hangfire;
using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Hubs;
using KrakenReact.Server.Models;
using Kraken.Net.Enums;
using Microsoft.AspNetCore.SignalR;

namespace KrakenReact.Server.Services;

public class DailyPriceRefreshJob
{
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly DbMethods _db;
    private readonly AutoOrderService _autoOrder;
    private readonly DelistedPriceService _delisted;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<DailyPriceRefreshJob> _logger;

    public DailyPriceRefreshJob(
        KrakenRestService kraken,
        TradingStateService state,
        DbMethods db,
        AutoOrderService autoOrder,
        DelistedPriceService delisted,
        IHubContext<TradingHub> hub,
        ILogger<DailyPriceRefreshJob> logger)
    {
        _kraken = kraken;
        _state = state;
        _db = db;
        _autoOrder = autoOrder;
        _delisted = delisted;
        _hub = hub;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 3600)]
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _state.GetPriceSnapshot();
        _logger.LogInformation("[PriceJob] Daily kline refresh starting at {Time} — {Count} symbols",
            DateTime.Now.ToString("HH:mm"), snapshot.Count);

        _state.LastStatusMessage = $"Refreshing daily kline data ({snapshot.Count} symbols)...";
        try { await _hub.Clients.All.SendAsync("StatusUpdate", _state.LastStatusMessage, cancellationToken); }
        catch { /* non-critical */ }

        var done = 0;
        foreach (var p in snapshot)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(2));
                await LoadLatestPriceData(p).WaitAsync(cts.Token);

                var result = await _autoOrder.CheckAsync(p, "Default Rule");
                _state.AutoOrders[result.Symbol] = result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("[PriceJob] Timed out for {Symbol}, skipping", p.Symbol);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PriceJob] Failed for {Symbol}, skipping", p.Symbol);
            }
            done++;
        }

        try { await LoadBalances(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[PriceJob] Post-refresh balance update failed"); }

        var doneMsg = $"Daily price refresh done at {DateTime.Now:HH:mm}";
        _state.LastStatusMessage = doneMsg;
        try { await _hub.Clients.All.SendAsync("StatusUpdate", doneMsg); }
        catch { /* non-critical */ }
        _logger.LogInformation("[PriceJob] Complete — {Done}/{Total} symbols", done, snapshot.Count);
    }

    internal async Task LoadLatestPriceData(PriceDataItem priceItem)
    {
        var old = priceItem.GetKlineSnapshot();
        if (!old.Any())
        {
            var dbKlines = await _db.GetKlineAsync(priceItem.Symbol);
            if (dbKlines.Any()) priceItem.AddKlineHistory(dbKlines);
            old = priceItem.GetKlineSnapshot();
        }

        var minDay = old.Any(p => p.Interval == "OneDay")
            ? old.Where(p => p.Interval == "OneDay").Max(p => p.OpenTime)
            : DateTime.UtcNow.AddDays(-9999);
        var cleanSymbol = priceItem.Symbol.Replace(".F/", "/").Replace(".B/", "/");
        var result = await _kraken.GetKlinesAsync(cleanSymbol, KlineInterval.OneDay, minDay);
        var temp = result.Select(a => new DerivedKline(a, priceItem.Symbol, KlineInterval.OneDay)).ToList();

        if (temp.Any())
        {
            _ = _db.AddKlineAsync(temp);
            priceItem.AddKlineHistory(temp);
        }
        else if (!old.Any())
        {
            var pairNoSlash = cleanSymbol.Replace("/", "");
            var csvKlines = _delisted.GetKlines(pairNoSlash, priceItem.Symbol);
            if (csvKlines != null && csvKlines.Any())
            {
                priceItem.AddKlineHistory(csvKlines);
                _ = _db.AddKlineAsync(csvKlines);
                _logger.LogInformation("[PriceJob] Loaded {Count} klines from delisted CSV for {Symbol}", csvKlines.Count, priceItem.Symbol);
            }
        }

        priceItem.KrakenNewPricesLoaded = "loaded";
        priceItem.KrakenNewPricesLoadedEver = true;
        priceItem.SupportedPair = true;
        priceItem.KrakenNewPricesLoadedTime = DateTime.UtcNow;
    }

    private async Task LoadBalances()
    {
        var bals = await _kraken.GetAvailableBalancesAsync(false);
        var grouped = bals.GroupBy(b => TradingStateService.NormalizeAsset(b.Asset))
            .Select(g => new { Asset = g.Key, Total = g.Sum(x => x.Total), Locked = g.Sum(x => x.Locked) })
            .Where(b => b.Total > 0 || b.Locked > 0).ToList();

        var openOrders = _state.Orders.Values
            .Where(o => TradingStateService.IsOpenOrderStatus(o.Status))
            .ToList();

        var usdGbpRate = _state.GetUsdGbpRate();
        var balanceDtos = new List<BalanceDto>();
        var newAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var b in grouped)
        {
            newAssets.Add(b.Asset);
            var latestPrice = _state.LatestPrice(b.Asset);
            var price = latestPrice?.Close ?? 0;

            // Preserve last-known price if the live lookup returns nothing
            if (price == 0 && _state.Balances.TryGetValue(b.Asset, out var prevBal) && prevBal.LatestPrice > 0)
                price = prevBal.LatestPrice;

            var valueUsd = Math.Round(b.Total * price, 2);
            var valueGbp = usdGbpRate > 0 ? Math.Round(valueUsd * usdGbpRate, 2) : 0;

            decimal coveredQty;
            decimal available;
            if (TradingStateService.Currency.Contains(b.Asset))
            {
                coveredQty = openOrders
                    .Where(o => o.Side == "Buy" && _state.NormalizeOrderSymbolQuote(o.Symbol) == b.Asset)
                    .Sum(o => (o.Quantity - o.QuantityFilled) * o.Price);
                available = Math.Max(b.Total - coveredQty, 0);
            }
            else
            {
                coveredQty = openOrders
                    .Where(o => o.Side == "Sell" && _state.NormalizeOrderSymbolBase(o.Symbol) == b.Asset)
                    .Sum(o => o.Quantity);
                available = b.Total - b.Locked;
            }

            var uncoveredQty = Math.Max(b.Total - coveredQty, 0);
            balanceDtos.Add(new BalanceDto
            {
                Asset = b.Asset, Total = b.Total, Locked = b.Locked,
                Available = available, LatestPrice = price,
                LatestValue = valueUsd, LatestValueGbp = valueGbp,
                OrderCoveredQty = Math.Min(coveredQty, b.Total),
                OrderUncoveredQty = uncoveredQty,
                OrderCoveredValue = Math.Round(Math.Min(coveredQty, b.Total) * price, 2),
                OrderUncoveredValue = Math.Round(uncoveredQty * price, 2),
            });
        }

        foreach (var key in _state.Balances.Keys.ToList())
        {
            var normalized = TradingStateService.NormalizeAsset(key);
            if (key != normalized && _state.Balances.TryRemove(key, out var stale))
            {
                if (!newAssets.Contains(normalized))
                {
                    stale.Asset = normalized;
                    _state.Balances.TryAdd(normalized, stale);
                }
            }
        }

        var totalPortfolioValue = balanceDtos.Sum(d => d.LatestValue);
        foreach (var dto in balanceDtos)
        {
            dto.PortfolioPercentage = totalPortfolioValue > 0
                ? Math.Round(dto.LatestValue / totalPortfolioValue * 100, 2)
                : 0;
            _state.Balances[dto.Asset] = dto;
        }

        try { await _hub.Clients.All.SendAsync("BalanceUpdate", _state.Balances.Values.ToList(), usdGbpRate); }
        catch (Exception ex) { _logger.LogWarning(ex, "[PriceJob] BalanceUpdate broadcast failed"); }
    }
}
