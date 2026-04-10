using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Hubs;
using KrakenReact.Server.Models;
using Kraken.Net.Enums;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class BackgroundTaskService : BackgroundService
{
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly DbMethods _db;
    private readonly AutoOrderService _autoOrder;
    private readonly DelistedPriceService _delisted;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<BackgroundTaskService> _logger;
    private volatile bool _klinesDone = false;

    public BackgroundTaskService(
        KrakenRestService kraken, 
        TradingStateService state, 
        DbMethods db, 
        AutoOrderService autoOrder, 
        DelistedPriceService delisted,
        IDbContextFactory<KrakenDbContext> dbFactory,
        IHubContext<TradingHub> hub, 
        ILogger<BackgroundTaskService> logger)
    {
        _kraken = kraken;
        _state = state;
        _db = db;
        _autoOrder = autoOrder;
        _delisted = delisted;
        _dbFactory = dbFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[BG] Starting background tasks...");

        // Phase 0: Load configuration from database
        _logger.LogInformation("[BG] Loading configuration from database...");
        try
        {
            await using var dbContext = await _dbFactory.CreateDbContextAsync(stoppingToken);

            // Initialize default settings on first run
            _logger.LogInformation("[BG] Initializing default settings...");
            await _state.InitializeDefaultSettings(dbContext);

            // Migrate old API credentials from EFAppCreds to AppSettings if needed
            _logger.LogInformation("[BG] Checking for credentials migration...");
            await _state.MigrateApiCredentials(dbContext);

            // Load current configuration
            await _state.ReloadConfiguration(dbContext);
            _logger.LogInformation("[BG] Configuration loaded");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[BG] Failed to load configuration from database, using defaults");
        }

        // Phase 1: Load symbols from DB so the app can start
        await _kraken.GetInstrumentsAsync(true);

        // Add all symbol prices
        foreach (var s in _state.Symbols.Values.Where(s => TradingStateService.BaseCurrencies.Contains(s.QuoteAsset)))
            _state.GetOrAddPrice(s.WebsocketName);

        // Ensure default pairs are loaded
        foreach (var pair in TradingStateService.DefaultPairs)
            _state.GetOrAddPrice(pair);

        // Ensure required pairs (e.g. GBP/USD for currency conversion) are always loaded
        foreach (var pair in TradingStateService.RequiredPairs)
            _state.GetOrAddPrice(pair);

        // Phase 2: Fetch fresh trades, ledger, orders, balances from Kraken API immediately
        // This is fast and must happen before the slow kline loading
        _logger.LogInformation("[BG] Fetching fresh orders, trades, ledger, balances from API...");
        await _kraken.GetInstrumentsAsync(false);
        await LoadOrders(false);
        await LoadTrades(false);
        await LoadLedger(false);
        await LoadBalances();
        _state.InitialDataLoad = false;
        _logger.LogInformation("[BG] Fresh transaction data loaded");

        // Notify frontend that fresh trade/ledger data is available
        try { await _hub.Clients.All.SendAsync("TradesUpdated"); }
        catch (Exception ex) { _logger.LogWarning(ex, "[BG] TradesUpdated broadcast failed"); }

        // Phase 3: Slow kline loading in background (doesn't block trades/orders)
        _ = Task.Run(async () => { try { await LoadKlinesBackground(stoppingToken); } catch (Exception ex) { _logger.LogError(ex, "[BG] LoadKlinesBackground crashed"); } }, stoppingToken);

        // Phase 4: Start periodic timers
        _ = Task.Run(async () => { try { await OrderRefreshLoop(stoppingToken); } catch (Exception ex) { _logger.LogError(ex, "[BG] OrderRefreshLoop crashed"); } }, stoppingToken);
        _ = Task.Run(async () => { try { await TransactionRefreshLoop(stoppingToken); } catch (Exception ex) { _logger.LogError(ex, "[BG] TransactionRefreshLoop crashed"); } }, stoppingToken);
        _ = Task.Run(async () => { try { await DailyTaskLoop(stoppingToken); } catch (Exception ex) { _logger.LogError(ex, "[BG] DailyTaskLoop crashed"); } }, stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[BG] Shutting down gracefully");
        }
    }

    private async Task LoadKlinesBackground(CancellationToken ct)
    {
        try
        {
            await LoadKlines(ct);
            _klinesDone = true;

            // Add prices for balance assets that may not have been in the symbol list
            foreach (var bal in _state.Balances.Values.Where(b => !b.Asset.Contains("USD")))
            {
                var k = TradingStateService.NormalizeAsset(bal.Asset);
                var symbol = _state.Symbols.Values.FirstOrDefault(a => TradingStateService.NormalizeAsset(a.BaseAsset) == k && a.QuoteAsset == "ZUSD");
                var key = symbol != null ? symbol.WebsocketName : $"{k}/USD";
                _state.GetOrAddPrice(key);
            }
            _logger.LogInformation("[BG] All klines loaded");
        }
        catch (Exception ex) { _logger.LogError(ex, "[BG] Error in LoadKlinesBackground"); }
    }

    private async Task RefreshTransactions()
    {
        await LoadLedger(false);
        await LoadTrades(false);
        await LoadOrders(false);
        await LoadBalances();
        try { await _hub.Clients.All.SendAsync("TradesUpdated"); }
        catch (Exception ex) { _logger.LogWarning(ex, "[BG] TradesUpdated broadcast failed"); }
    }

    private async Task LoadKlines(CancellationToken ct)
    {
        int loaded = 0;
        var allToLoad = _state.GetPriceSnapshot().Where(p => p.KrakenNewPricesLoaded == "no" && !TradingStateService.Blacklist.Contains(p.Base)).ToList();
        int total = allToLoad.Count;

        async Task LoadKlinesForList(List<PriceDataItem> items)
        {
            foreach (var d in items)
            {
                if (ct.IsCancellationRequested) break;
                if (TradingStateService.Blacklist.Contains(d.Base)) continue;
                try
                {
                    try { await _hub.Clients.All.SendAsync("StatusUpdate", $"Loading prices for {d.Symbol} ({++loaded}/{total})"); }
                    catch { /* non-critical */ }
                    await LoadLatestPriceData(d);
                    var result = await _autoOrder.CheckAsync(d, "Default Rule");
                    _state.AutoOrders[result.Symbol] = result;
                }
                catch (Exception ex) { _logger.LogError(ex, "[BG] Error loading klines for {Symbol}", d.Symbol); }
            }
        }

        var snapshot = _state.GetPriceSnapshot();

        // Main coins first
        await LoadKlinesForList(snapshot.Where(p => p.KrakenNewPricesLoaded == "no" && p.CoinType == "Main Coin").ToList());
        // Coins with open orders
        await LoadKlinesForList(snapshot.Where(p => p.KrakenNewPricesLoaded == "no" && _state.Orders.Values.Any(o => o.Symbol == p.Base + p.CCY && o.CloseTime == null)).ToList());
        // Coins with balances
        await LoadKlinesForList(snapshot.Where(p => p.KrakenNewPricesLoaded == "no" && _state.Balances.Values.Any(b => b.Asset == p.Base)).ToList());
        // Remaining
        await LoadKlinesForList(snapshot.Where(p => p.KrakenNewPricesLoaded == "no").ToList());

        try { await _hub.Clients.All.SendAsync("StatusUpdate", $"All prices loaded ({loaded} symbols)"); }
        catch { /* non-critical */ }
        _logger.LogInformation("[BG] All klines loaded");
    }

    private async Task LoadLatestPriceData(PriceDataItem priceItem)
    {
        var old = priceItem.GetKlineSnapshot();
        // Load from DB first
        if (!old.Any())
        {
            var dbKlines = await _db.GetKlineAsync(priceItem.Symbol);
            if (dbKlines.Any()) priceItem.AddKlineHistory(dbKlines);
            old = priceItem.GetKlineSnapshot();
        }

        var minDay = old.Any(p => p.Interval == "OneDay") ? old.Where(p => p.Interval == "OneDay").Max(p => p.OpenTime) : DateTime.UtcNow.AddDays(-9999);
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
            // Kraken API returned no data and we have nothing in DB — try delisted CSV fallback
            var pairNoSlash = cleanSymbol.Replace("/", "");
            var csvKlines = _delisted.GetKlines(pairNoSlash, priceItem.Symbol);
            if (csvKlines != null && csvKlines.Any())
            {
                priceItem.AddKlineHistory(csvKlines);
                _ = _db.AddKlineAsync(csvKlines);
                _logger.LogInformation("[BG] Loaded {Count} klines from delisted CSV for {Symbol}", csvKlines.Count, priceItem.Symbol);
            }
        }
        priceItem.KrakenNewPricesLoaded = "loaded";
        priceItem.KrakenNewPricesLoadedEver = true;
        priceItem.SupportedPair = true;
        priceItem.KrakenNewPricesLoadedTime = DateTime.UtcNow;
    }

    private async Task LoadOrders(bool initialLoad)
    {
        var result = await _kraken.GetOrders(initialLoad);
        foreach (var order in result)
        {
            var latestPrice = _state.LatestPrice(order.Symbol);
            var price = latestPrice?.Close ?? 0;
            var dto = new OrderDto
            {
                Id = order.Id, Symbol = order.Symbol ?? "", Side = order.Side.ToString(), Type = order.Type.ToString(),
                Status = order.Status.ToString(), Price = order.OrderDetailsPrice != 0 ? order.OrderDetailsPrice : order.Price,
                Quantity = order.Quantity, QuantityFilled = order.QuantityFilled, Fee = order.Fee,
                AveragePrice = order.AveragePrice, OrderValue = Math.Round((order.OrderDetailsPrice != 0 ? order.OrderDetailsPrice : order.Price) * order.Quantity, 2),
                LatestPrice = price, Distance = (order.OrderDetailsPrice != 0 ? order.OrderDetailsPrice : order.Price) - price,
                DistancePercentage = price != 0 ? Math.Round(((order.OrderDetailsPrice != 0 ? order.OrderDetailsPrice : order.Price) - price) / (price / 100), 2) : 100,
                CreateTime = order.CreateTime, CloseTime = order.CloseTime, Reason = order.Reason ?? "",
                ClientOrderId = order.ClientOrderId, SecondaryPrice = order.SecondaryPrice,
                StopPrice = order.StopPrice, Leverage = order.Leverage ?? ""
            };
            _state.Orders[order.Id] = dto;
        }
    }

    private async Task LoadTrades(bool initialLoad) => await _kraken.GetTradesAsync(initialLoad);
    private async Task LoadLedger(bool initialLoad) => await _kraken.GetLedgerAsync(initialLoad);

    private async Task LoadBalances()
    {
        var bals = await _kraken.GetAvailableBalancesAsync(false);
        var grouped = bals.GroupBy(b => TradingStateService.NormalizeAsset(b.Asset))
            .Select(g => new { Asset = g.Key, Total = g.Sum(x => x.Total), Locked = g.Sum(x => x.Locked) })
            .Where(b => b.Total > 0 || b.Locked > 0).ToList();

        var openOrders = _state.Orders.Values
            .Where(o => o.Status == "Open" || o.Status == "New" || o.Status == "PartiallyFilled")
            .ToList();

        var usdGbpRate = _state.GetUsdGbpRate();

        // First pass: compute values
        var balanceDtos = new List<BalanceDto>();
        foreach (var b in grouped)
        {
            var latestPrice = _state.LatestPrice(b.Asset);
            var price = latestPrice?.Close ?? 0;
            var valueUsd = Math.Round(b.Total * price, 2);
            var valueGbp = usdGbpRate > 0 ? Math.Round(valueUsd * usdGbpRate, 2) : 0;

            // Order coverage: for non-USD assets, sum sell order quantities; for USD, sum buy order values
            decimal coveredQty;
            if (TradingStateService.Currency.Contains(b.Asset))
            {
                coveredQty = openOrders
                    .Where(o => o.Side == "Buy")
                    .Sum(o => o.OrderValue);
            }
            else
            {
                coveredQty = openOrders
                    .Where(o => o.Side == "Sell" && TradingStateService.NormalizeAsset(o.Symbol.Replace("/", "").Replace("USD", "")) == b.Asset)
                    .Sum(o => o.Quantity);
            }

            var uncoveredQty = Math.Max(b.Total - coveredQty, 0);
            var dto = new BalanceDto
            {
                Asset = b.Asset, Total = b.Total, Locked = b.Locked,
                Available = b.Total - b.Locked, LatestPrice = price,
                LatestValue = valueUsd,
                LatestValueGbp = valueGbp,
                OrderCoveredQty = Math.Min(coveredQty, b.Total),
                OrderUncoveredQty = uncoveredQty,
                OrderCoveredValue = Math.Round(Math.Min(coveredQty, b.Total) * price, 2),
                OrderUncoveredValue = Math.Round(uncoveredQty * price, 2),
            };
            balanceDtos.Add(dto);
        }

        // Second pass: compute portfolio percentages
        var totalPortfolioValue = balanceDtos.Sum(d => d.LatestValue);
        foreach (var dto in balanceDtos)
        {
            dto.PortfolioPercentage = totalPortfolioValue > 0
                ? Math.Round(dto.LatestValue / totalPortfolioValue * 100, 2)
                : 0;
            _state.Balances[dto.Asset] = dto;
        }

        try { await _hub.Clients.All.SendAsync("BalanceUpdate", _state.Balances.Values.ToList(), usdGbpRate); }
        catch (Exception ex) { _logger.LogWarning(ex, "[BG] BalanceUpdate broadcast failed"); }
    }

    private async Task OrderRefreshLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(8), ct);
            try { await LoadOrders(false); } catch (Exception ex) { _logger.LogError(ex, "[BG] Order refresh error"); }
        }
    }

    private async Task TransactionRefreshLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromHours(1), ct);
            try { await RefreshTransactions(); } catch (Exception ex) { _logger.LogError(ex, "[BG] Transaction refresh error"); }
        }
    }

    private async Task DailyTaskLoop(CancellationToken ct)
    {
        // Run at 04:00 each day
        var scheduleTimes = new[] { (4, 0) };

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.Now;
            DateTime? nextRun = null;
            foreach (var (hour, minute) in scheduleTimes)
            {
                var candidate = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
                if (candidate <= now) candidate = candidate.AddDays(1);
                if (nextRun == null || candidate < nextRun) nextRun = candidate;
            }

            var delay = nextRun!.Value - now;
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }

            try
            {
                _logger.LogInformation("[BG] Daily kline refresh starting at {Time}", DateTime.Now.ToString("HH:mm"));
                try { await _hub.Clients.All.SendAsync("StatusUpdate", "Refreshing daily kline data..."); }
                catch { /* non-critical */ }

                foreach (var p in _state.GetPriceSnapshot())
                    await LoadLatestPriceData(p);

                try { await _hub.Clients.All.SendAsync("StatusUpdate", "Daily kline refresh complete"); }
                catch { /* non-critical */ }
                _logger.LogInformation("[BG] Daily kline refresh complete");
            }
            catch (Exception ex) { _logger.LogError(ex, "[BG] Daily task error"); }
        }
    }
}
