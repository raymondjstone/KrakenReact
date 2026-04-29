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
    private readonly NotificationService _notify;
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<BackgroundTaskService> _logger;
    private bool _initialLedgerSeeded = false;

    public BackgroundTaskService(
        KrakenRestService kraken,
        TradingStateService state,
        DbMethods db,
        AutoOrderService autoOrder,
        DelistedPriceService delisted,
        NotificationService notify,
        IDbContextFactory<KrakenDbContext> dbFactory,
        IHubContext<TradingHub> hub,
        ILogger<BackgroundTaskService> logger)
    {
        _kraken = kraken;
        _state = state;
        _db = db;
        _autoOrder = autoOrder;
        _delisted = delisted;
        _notify = notify;
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

            // Sync asset normalizations with code defaults (adds new, updates changed, removes stale)
            await _state.SyncAssetNormalizations(dbContext);

            // Ensure required settings exist for existing databases
            await _state.EnsureRequiredSettings(dbContext);

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
        try
        {
            await _kraken.GetInstrumentsAsync(false);
            await LoadOrders(false);
            await LoadTrades(false);
            await LoadLedger(false);
            await LoadBalances();
        }
        catch (Exception ex) { _logger.LogError(ex, "[BG] Error during initial data load — continuing to start loops"); }
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
        _ = Task.Run(async () => { try { await PriceAlertLoop(stoppingToken); } catch (Exception ex) { _logger.LogError(ex, "[BG] PriceAlertLoop crashed"); } }, stoppingToken);
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
                    var statusMsg = $"Loading prices for {d.Symbol} ({++loaded}/{total})";
                    _state.LastStatusMessage = statusMsg;
                    try { await _hub.Clients.All.SendAsync("StatusUpdate", statusMsg); }
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
        await LoadKlinesForList(snapshot.Where(p => p.KrakenNewPricesLoaded == "no" && _state.Orders.Values.Any(o => o.Symbol.Replace("/", "") == p.Base + p.CCY && o.CloseTime == null)).ToList());
        // Coins with balances
        await LoadKlinesForList(snapshot.Where(p => p.KrakenNewPricesLoaded == "no" && _state.Balances.Values.Any(b => b.Asset == p.Base)).ToList());
        // Remaining
        await LoadKlinesForList(snapshot.Where(p => p.KrakenNewPricesLoaded == "no").ToList());

        var doneMsg = $"Full price load done at {DateTime.Now:HH:mm}";
        _state.LastStatusMessage = doneMsg;
        _logger.LogInformation("[BG] {Status}", doneMsg);
        try { await _hub.Clients.All.SendAsync("StatusUpdate", doneMsg); }
        catch (Exception ex) { _logger.LogWarning(ex, "[BG] Failed to send final status update"); }
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
            _state.Orders.TryGetValue(order.Id, out var existingOrder);
            var dto = new OrderDto
            {
                Id = order.Id, Symbol = order.Symbol ?? "", Side = order.Side.ToString(), Type = order.Type.ToString(),
                Status = order.Status.ToString(), Price = order.OrderDetailsPrice != 0 ? order.OrderDetailsPrice : order.Price,
                Quantity = order.Quantity, QuantityFilled = order.QuantityFilled, Fee = order.Fee,
                AveragePrice = order.AveragePrice,
                CreateTime = order.CreateTime, CloseTime = order.CloseTime, Reason = order.Reason ?? "",
                ClientOrderId = order.ClientOrderId, SecondaryPrice = order.SecondaryPrice,
                StopPrice = order.StopPrice, Leverage = order.Leverage ?? "",
                LatestPrice = existingOrder?.LatestPrice ?? 0
            };
            // Calculate LatestPrice, Distance, DistancePercentage, OrderValue using normalized symbol lookup
            _state.RecalculateOrderFields(dto);
            _state.Orders[order.Id] = dto;
        }
    }

    private async Task LoadTrades(bool initialLoad)
    {
        var trades = await _kraken.GetTradesAsync(initialLoad);
        if (trades != null) _state.SetCachedTrades(trades);
    }

    private async Task LoadLedger(bool initialLoad)
    {
        var ledgers = await _kraken.GetLedgerAsync(initialLoad);
        if (ledgers == null) return;
        _state.SetCachedLedgers(ledgers);
        await CheckStakingRewards(ledgers);
    }

    private async Task CheckStakingRewards(List<Kraken.Net.Objects.Models.KrakenLedgerEntry> ledgers)
    {
        // On first call, seed all existing IDs so we don't notify on historical entries
        if (!_initialLedgerSeeded)
        {
            foreach (var l in ledgers)
                _state.SeenLedgerIds.Add(l.Id);
            _initialLedgerSeeded = true;
            return;
        }

        if (!_state.StakingNotifications && !_state.AutoAddStakingToOrder) return;

        var newRewards = ledgers
            .Where(l => l.Type == Kraken.Net.Enums.LedgerEntryType.Staking
                && l.Quantity > 0
                && l.SubType != "spotFromStaking"
                && l.SubType != "spotToStaking"
                && !_state.SeenLedgerIds.Contains(l.Id))
            .ToList();

        foreach (var reward in newRewards)
        {
            _state.SeenLedgerIds.Add(reward.Id);
            var asset = TradingStateService.NormalizeAsset(reward.Asset);
            var amount = reward.Quantity;
            _logger.LogInformation("[BG] Staking reward: {Asset} +{Amount}", asset, amount);

            if (_state.StakingNotifications)
            {
                try
                {
                    await _notify.Pushover("Staking Reward", $"{asset} +{amount}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BG] Failed to send staking reward notification");
                }
            }

            if (_state.AutoAddStakingToOrder && amount > 0)
            {
                // Find the newest open sell order for this asset
                var newestSellOrder = _state.Orders.Values
                    .Where(o => o.Side == "Sell"
                        && (o.Status == "Open" || o.Status == "New" || o.Status == "PendingNew")
                        && _state.NormalizeOrderSymbolBase(o.Symbol) == asset)
                    .OrderByDescending(o => o.CreateTime)
                    .FirstOrDefault();

                if (newestSellOrder != null)
                {
                    var orderId = newestSellOrder.Id;
                    var symbol = newestSellOrder.Symbol;
                    var currentQty = newestSellOrder.Quantity;
                    var price = newestSellOrder.Price;
                    var rewardAmount = amount;

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Wait 2 minutes for the balance to settle on Kraken's side
                            await Task.Delay(TimeSpan.FromMinutes(2));

                            var newQty = currentQty + rewardAmount;
                            var result = await _kraken.AmendOrderValues(orderId, symbol, price, newQty);
                            if (result.Success)
                            {
                                // Update the order in state
                                if (_state.Orders.TryGetValue(orderId, out var order))
                                {
                                    order.Quantity = newQty;
                                    _state.RecalculateOrderFields(order);
                                }
                                _logger.LogInformation("[BG] Auto-amended sell order {OrderId}: added {Amount} {Asset} staking reward (qty {OldQty} -> {NewQty})",
                                    orderId, rewardAmount, asset, currentQty, newQty);

                                // Broadcast updated orders
                                await _hub.Clients.All.SendAsync("OrderUpdate", _state.Orders.Values.ToList());

                                if (_state.StakingNotifications)
                                {
                                    try { await _notify.Pushover("Staking → Order", $"Added {rewardAmount} {asset} to sell order @ {price} (qty {currentQty} → {newQty})"); }
                                    catch { /* ignore */ }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("[BG] Failed to auto-amend sell order {OrderId} with staking reward: {Error}",
                                    orderId, result.Error?.Message);
                            }
                        }
                        catch (Exception ex) { _logger.LogError(ex, "[BG] Error auto-amending sell order {OrderId} with staking reward", orderId); }
                    });
                }
            }
        }
    }

    private async Task LoadBalances()
    {
        var bals = await _kraken.GetAvailableBalancesAsync(false);
        var grouped = bals.GroupBy(b => TradingStateService.NormalizeAsset(b.Asset))
            .Select(g => new { Asset = g.Key, Total = g.Sum(x => x.Total), Locked = g.Sum(x => x.Locked) })
            .Where(b => b.Total > 0 || b.Locked > 0)
            .ToList();

        // BalanceEx omits assets whose available balance is 0 (e.g. BTC 100% locked in sell orders).
        // Supplement with the simple Balance endpoint (always returns total per asset, XBT/XXBT included).
        var totalBals = await _kraken.GetTotalBalancesAsync();
        if (totalBals.Any())
        {
            var groupedKeys = new HashSet<string>(grouped.Select(g => g.Asset), StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in totalBals)
            {
                if (kvp.Value <= 0) continue;
                var normalizedAsset = TradingStateService.NormalizeAsset(kvp.Key);
                if (!groupedKeys.Contains(normalizedAsset))
                {
                    // Asset is in total balance but absent from BalanceEx — treat entire balance as locked
                    grouped.Add(new { Asset = normalizedAsset, Total = kvp.Value, Locked = kvp.Value });
                    groupedKeys.Add(normalizedAsset);
                    _logger.LogInformation("[BG] Balance supplement: {Asset} (raw={Raw}) total={Total} — absent from BalanceEx, treating as fully locked",
                        normalizedAsset, kvp.Key, kvp.Value);
                }
            }
        }

        var openOrders = _state.Orders.Values
            .Where(o => o.Status == "Open" || o.Status == "New" || o.Status == "PartiallyFilled")
            .ToList();

        var usdGbpRate = _state.GetUsdGbpRate();

        // First pass: compute values
        var balanceDtos = new List<BalanceDto>();
        var newAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in grouped)
        {
            newAssets.Add(b.Asset);
            var latestPrice = _state.LatestPrice(b.Asset);
            var price = latestPrice?.Close ?? 0;

            // If the live lookup returned nothing (e.g. klines not loaded yet at startup),
            // preserve the last-known price so significant holdings don't temporarily vanish.
            if (price == 0 && _state.Balances.TryGetValue(b.Asset, out var prevBal) && prevBal.LatestPrice > 0)
                price = prevBal.LatestPrice;

            var valueUsd = Math.Round(b.Total * price, 2);
            var valueGbp = usdGbpRate > 0 ? Math.Round(valueUsd * usdGbpRate, 2) : 0;

            // Order coverage calculation
            decimal coveredQty;
            decimal available;
            if (TradingStateService.Currency.Contains(b.Asset))
            {
                // Currency assets: covered = total cost of open buy orders in this currency
                coveredQty = openOrders
                    .Where(o => o.Side == "Buy" && _state.NormalizeOrderSymbolQuote(o.Symbol) == b.Asset)
                    .Sum(o => (o.Quantity - o.QuantityFilled) * o.Price);
                available = Math.Max(b.Total - coveredQty, 0);
            }
            else
            {
                // Crypto assets: covered = quantity committed to open sell orders
                coveredQty = openOrders
                    .Where(o => o.Side == "Sell" && _state.NormalizeOrderSymbolBase(o.Symbol) == b.Asset)
                    .Sum(o => o.Quantity);
                available = b.Total - b.Locked;
            }

            var uncoveredQty = Math.Max(b.Total - coveredQty, 0);
            var dto = new BalanceDto
            {
                Asset = b.Asset, Total = b.Total, Locked = b.Locked,
                Available = available, LatestPrice = price,
                LatestValue = valueUsd,
                LatestValueGbp = valueGbp,
                OrderCoveredQty = Math.Min(coveredQty, b.Total),
                OrderUncoveredQty = uncoveredQty,
                OrderCoveredValue = Math.Round(Math.Min(coveredQty, b.Total) * price, 2),
                OrderUncoveredValue = Math.Round(uncoveredQty * price, 2),
            };
            balanceDtos.Add(dto);
        }

        // Always remove un-normalized keys (e.g. XBT → BTC) to prevent duplicates.
        // If fresh API data doesn't include the normalized form, re-home the entry.
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

    private async Task PriceAlertLoop(CancellationToken ct)
    {
        // Wait 2 minutes after startup before first check so prices are loaded
        await Task.Delay(TimeSpan.FromMinutes(2), ct);

        while (!ct.IsCancellationRequested)
        {
            try { await CheckPriceAlerts(ct); }
            catch (Exception ex) { _logger.LogError(ex, "[BG] PriceAlertLoop error"); }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task CheckPriceAlerts(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var activeAlerts = await db.PriceAlerts
            .Where(a => a.Active)
            .ToListAsync(ct);
        if (!activeAlerts.Any()) return;

        foreach (var alert in activeAlerts)
        {
            if (!_state.Prices.TryGetValue(alert.Symbol, out var priceItem)) continue;
            var latestPrice = priceItem.LatestKline?.Close;
            if (latestPrice == null || latestPrice == 0) continue;

            bool triggered = alert.Direction == "below"
                ? latestPrice <= alert.TargetPrice
                : latestPrice >= alert.TargetPrice;

            if (!triggered) continue;

            alert.Active = false;
            alert.TriggeredAt = DateTime.UtcNow;

            var dir = alert.Direction == "below" ? "dropped below" : "rose above";
            var title = $"Price alert: {alert.Symbol}";
            var text = $"{alert.Symbol} {dir} ${alert.TargetPrice:F4} (current: ${latestPrice:F4}){(string.IsNullOrEmpty(alert.Note) ? "" : $" — {alert.Note}")}";
            await _notify.Pushover(title, text, Altairis.Pushover.Client.MessageSound.Falling);
            _logger.LogInformation("[PriceAlert] {Symbol} alert triggered at {Price}", alert.Symbol, latestPrice);
        }

        await db.SaveChangesAsync(ct);
    }


}
