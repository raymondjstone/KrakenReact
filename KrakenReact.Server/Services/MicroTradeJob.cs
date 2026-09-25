using CryptoExchange.Net.Objects;
using Hangfire;
using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

/// <summary>
/// Micro trading: watches the price change over each rule's configured interval (1h/4h/6h/12h/24h)
/// on configured pairs and, on a large enough drop, places a small limit buy just under market.
/// Once that buy fills, an automatic limit sell is placed above the fill price. A per-rule rolling
/// window caps how many buys can fire in a given period to avoid over-trading a fast-dropping pair.
/// </summary>
public class MicroTradeJob
{
    /// <summary>AppSettings key for the Micro Trading emergency stop toggle — see MicroTradeController.</summary>
    public const string EmergencyStopKey = "MicroTradeEmergencyStop";

    /// <summary>Least-significant-digit shrink retries a sell goes through before giving up if Kraken
    /// rejects it (typically a rounding mismatch between what we think we hold and Kraken's ledger).</summary>
    private const int MaxSellAttempts = 5;

    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly PriceChangeService _priceChange;
    private readonly NotificationService _notify;
    private readonly ILogger<MicroTradeJob> _logger;
    private readonly SqlTimeoutDiagnostics _sqlDiag;

    public MicroTradeJob(IDbContextFactory<KrakenDbContext> dbFactory, KrakenRestService kraken, TradingStateService state, PriceChangeService priceChange, NotificationService notify, ILogger<MicroTradeJob> logger, SqlTimeoutDiagnostics sqlDiag)
    {
        _dbFactory = dbFactory;
        _kraken = kraken;
        _state = state;
        _priceChange = priceChange;
        _notify = notify;
        _logger = logger;
        _sqlDiag = sqlDiag;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (_sqlDiag.RecentTimeout(SqlTimeoutDiagnostics.RecentTimeoutBackoff))
        {
            _logger.LogWarning("[MicroTrade] Skipping tick — recent SQL timeout elsewhere, backing off");
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        List<MicroTradeRule> rules;
        try
        {
            rules = await db.MicroTradeRules.Where(r => r.Active).ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _sqlDiag.CaptureIfTimeout("MicroTradeJob.LoadRules", ex);
            throw;
        }

        var emergencyStop = await IsEmergencyStopActiveAsync(db, ct);

        foreach (var rule in rules)
        {
            try { await CheckRuleAsync(db, rule, emergencyStop, ct); }
            catch (Exception ex)
            {
                rule.LastResult = $"Exception: {ex.Message}";
                _logger.LogError(ex, "[MicroTrade] Exception checking rule {Id}", rule.Id);
            }
        }

        await MonitorFillsAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteRuleAsync(int ruleId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rule = await db.MicroTradeRules.FindAsync([ruleId], ct);
        if (rule == null) return;

        var emergencyStop = await IsEmergencyStopActiveAsync(db, ct);

        try { await CheckRuleAsync(db, rule, emergencyStop, ct); }
        catch (Exception ex)
        {
            rule.LastResult = $"Exception: {ex.Message}";
            _logger.LogError(ex, "[MicroTrade] Exception checking rule {Id}", rule.Id);
        }

        await MonitorFillsAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> IsEmergencyStopActiveAsync(KrakenDbContext db, CancellationToken ct)
    {
        var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == EmergencyStopKey, ct);
        return setting != null && string.Equals(setting.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a rule's symbol to the PriceDataItem holding live ticker data for it.
    /// <para>
    /// Different collectors key the same pair under different spellings — e.g. the V2 ticker feed
    /// may create "BTC/USD" while the REST kline loader later creates a separate "XBT/USD" entry
    /// for the same market. TradingStateService.ResolveSymbolKey short-circuits on an exact match,
    /// so once a second entry spelled exactly like the rule's own Symbol exists, it wins even though
    /// it has no TickerData — which looks like the 24h change "disappearing" after having worked.
    /// If the directly-resolved entry has no ticker data, fall back to scanning for a sibling entry
    /// with the same normalized base/quote that does.
    /// </para>
    /// </summary>
    private PriceDataItem? ResolvePriceItem(string symbol)
    {
        var key = _state.ResolveSymbolKey(symbol);
        _state.Prices.TryGetValue(key, out var priceItem);
        if (priceItem?.TickerData?.ChangePct24h != null) return priceItem;

        var parts = symbol.Split('/');
        if (parts.Length == 2)
        {
            var normBase = TradingStateService.NormalizeAsset(parts[0]);
            var normCcy = TradingStateService.NormalizeAsset(parts[1]);
            var sibling = _state.Prices.Values.FirstOrDefault(p =>
                p.TickerData?.ChangePct24h != null &&
                TradingStateService.NormalizeAsset(p.Base) == normBase &&
                TradingStateService.NormalizeAsset(p.CCY) == normCcy);
            if (sibling != null) return sibling;
        }

        return priceItem;
    }

    private async Task CheckRuleAsync(KrakenDbContext db, MicroTradeRule rule, bool emergencyStopActive, CancellationToken ct)
    {
        rule.LastCheckedAt = DateTime.UtcNow;

        var priceItem = ResolvePriceItem(rule.Symbol);
        if (priceItem == null)
        {
            rule.LastResult = "No price data available";
            return;
        }

        var intervalHours = rule.DropIntervalHours <= 0 ? 24 : rule.DropIntervalHours;
        var changePct = await _priceChange.GetChangeAsync(rule.Symbol, intervalHours, priceItem);
        if (changePct == null)
        {
            rule.LastResult = intervalHours == 24
                ? "No live 24h change data yet — waiting for ticker"
                : $"No {intervalHours}h change data yet — insufficient kline history";
            return;
        }

        if (changePct > -rule.DropPct)
        {
            rule.LastResult = $"No trigger — {intervalHours}h change {changePct:F2}% (need <= -{rule.DropPct}%)";
            return;
        }

        // Cooldown — no order on this pair (from any rule) within the last CooldownHours,
        // regardless of the window/max-orders limit below.
        if (rule.CooldownHours > 0)
        {
            var cooldownCutoff = DateTime.UtcNow.AddHours(-rule.CooldownHours);
            var lastOrder = await db.MicroTradeOrders
                .Where(o => o.Symbol == rule.Symbol && o.Status != "Cancelled")
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (lastOrder != null && lastOrder.CreatedAt > cooldownCutoff)
            {
                var hoursAgo = (DateTime.UtcNow - lastOrder.CreatedAt).TotalHours;
                rule.LastResult = $"Skip — cooldown active, last order {hoursAgo:F1}h ago (need >= {rule.CooldownHours}h)";
                return;
            }
        }

        var windowStart = DateTime.UtcNow.AddHours(-rule.WindowHours);
        var recentCount = await db.MicroTradeOrders.CountAsync(
            o => o.RuleId == rule.Id && o.CreatedAt >= windowStart && o.Status != "Cancelled", ct);
        if (recentCount >= rule.MaxOrdersPerWindow)
        {
            rule.LastResult = $"Skip — rate limit {recentCount}/{rule.MaxOrdersPerWindow} orders in last {rule.WindowHours}h";
            return;
        }

        var currentPrice = priceItem.BestKline?.Close ?? 0;
        if (currentPrice <= 0)
        {
            rule.LastResult = "No current price available";
            return;
        }

        var sym = _state.Symbols.Values.FirstOrDefault(s =>
            s.WebsocketName.Equals(rule.Symbol, StringComparison.OrdinalIgnoreCase));
        var priceDecimals = sym?.PriceDecimals > 0 ? sym.PriceDecimals : 8;
        var lotDecimals = sym?.LotDecimals > 0 ? sym.LotDecimals : 8;

        var buyPrice = Math.Round(currentPrice * 0.999m, priceDecimals);
        // Floor (never round up) to the pair's real lot precision — Kraken rejects a quantity with
        // more decimal places than the pair allows outright, which was the root cause behind most
        // rounding failures, and flooring also guarantees we never spend more than BuyOrderTotal.
        var qty = FloorToDecimals(rule.BuyOrderTotal / buyPrice, lotDecimals);

        var minQty = sym?.OrderMin ?? 0.0001m;
        if (qty < minQty)
        {
            rule.LastResult = $"Qty {qty} below min {minQty}";
            return;
        }

        var minValue = sym?.MinValue ?? 0m;
        if (minValue > 0 && qty * buyPrice < minValue)
        {
            rule.LastResult = $"Order value {qty * buyPrice:F2} below min value {minValue}";
            return;
        }

        // Emergency stop — blocks all new buy creation (including dry-run simulated ones) until
        // switched off from the screen. Existing open positions are still monitored/sold as normal;
        // this only stops new buys from being created.
        if (emergencyStopActive)
        {
            rule.LastResult = $"Skip — EMERGENCY STOP active (would buy {qty} @ {buyPrice})";
            return;
        }

        // Deliberately not OR'd with the global DryRunJobs setting — this rule's own Dry run
        // checkbox is the only control shown on this screen, so it must be the sole authority.
        // Falling back to the global flag meant unchecking it here did nothing while the
        // Settings-page toggle (used for DCA etc.) was still on, which looked like a bug.
        var dryRun = rule.DryRun;

        // Confirm the quote currency actually has enough available balance right now before
        // placing a real order — a rule can trigger off a price move faster than the balance
        // cache updates, or funds can already be tied up in other open orders.
        if (!dryRun)
        {
            var quoteCcy = _state.NormalizeOrderSymbolQuote(rule.Symbol);
            var orderCost = qty * buyPrice;
            _state.Balances.TryGetValue(quoteCcy, out var quoteBalance);
            var available = quoteBalance?.Available ?? 0m;
            if (quoteBalance == null || available < orderCost)
            {
                rule.LastResult = $"Skip — insufficient {quoteCcy} balance ({available:F2} available, need {orderCost:F2})";
                _logger.LogWarning("[MicroTrade] Skip buy for rule {Id} — insufficient {Ccy} balance ({Available} < {Cost})",
                    rule.Id, quoteCcy, available, orderCost);
                return;
            }
        }

        var userRef = NewUserRef();
        var order = new MicroTradeOrder
        {
            RuleId = rule.Id,
            Symbol = rule.Symbol,
            BuyPrice = buyPrice,
            Quantity = qty,
            CreatedAt = DateTime.UtcNow,
            DryRun = dryRun,
            BuyUserRef = dryRun ? null : userRef,
        };

        if (dryRun)
        {
            order.Status = "DryRun";
            order.Note = $"DRY RUN — would buy {qty} @ {buyPrice}";
            db.MicroTradeOrders.Add(order);
            rule.LastResult = order.Note;
            _logger.LogInformation("[MicroTrade] DRY RUN — would place buy: {Symbol} {Qty} @ {Price}", rule.Symbol, qty, buyPrice);
            await NotifySafe($"DRY RUN — Micro Trade {rule.Symbol}",
                $"{intervalHours}h {changePct:F2}% — would buy {qty} @ {buyPrice:F4} (${rule.BuyOrderTotal})");
            return;
        }

        // Write-ahead: persist the intent BEFORE talking to Kraken. Previously the row was only saved at the
        // very end of the whole tick, so a request timeout (Kraken accepted the order but the response never
        // arrived), a SQL timeout or any exception later in the tick left a REAL order on Kraken with no record
        // — and with no record the cooldown/rate-limit couldn't see it, so the next tick bought again.
        order.Status = "Placing";
        order.Note = "Sending buy to Kraken";
        db.MicroTradeOrders.Add(order);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            db.Entry(order).State = EntityState.Detached;
            _sqlDiag.CaptureIfTimeout("MicroTradeJob.SaveBuyIntent", ex);
            rule.LastResult = $"Skip — could not record the buy before placing it: {ex.GetBaseException().Message}";
            _logger.LogError(ex, "[MicroTrade] Could not persist buy intent for rule {Id} — NOT placing the order", rule.Id);
            return;
        }

        // Kraken gets no clientOrderId (id-format validation errors); a numeric userref tags the order instead
        var outcome = await PlaceAndConfirmAsync(rule.Symbol, OrderSide.Buy, qty, buyPrice, userRef);

        if (outcome.Success)
        {
            order.BuyOrderId = outcome.OrderId;
            order.Status = "Buying";
            order.Note = outcome.Recovered ? "Kraken response timed out; order found on Kraken by userref" : "";
            rule.LastResult = $"OK — buy {qty} @ {buyPrice} (orderId={order.BuyOrderId})";
            await NotifySafe($"Micro Trade Buy — {rule.Symbol}",
                $"{intervalHours}h {changePct:F2}% drop — bought {qty} @ {buyPrice:F4} (${rule.BuyOrderTotal})");
        }
        else if (outcome.Unknown)
        {
            // Couldn't tell whether Kraken has it. Keep the row ("Placing") so it counts toward cooldown /
            // rate limit and gets resolved by userref on a later tick — never assume "not placed".
            order.Note = $"Buy placement unconfirmed ({outcome.Error}) — will resolve by userref {userRef}";
            rule.LastResult = $"UNCONFIRMED — {outcome.Error}; checking Kraken again next tick";
            _logger.LogError("[MicroTrade] Buy placement unconfirmed for rule {Id} (userref {UserRef}): {Error}", rule.Id, userRef, outcome.Error);
            await NotifySafe($"Micro Trade Unconfirmed — {rule.Symbol}", order.Note);
        }
        else
        {
            db.MicroTradeOrders.Remove(order); // Kraken definitively rejected it — nothing exists to track
            rule.LastResult = $"Error: {outcome.Error}";
            _logger.LogError("[MicroTrade] Buy failed for rule {Id}: {Error}", rule.Id, outcome.Error);
        }

        await PersistAsync(db, order, "buy");
    }

    // ── Placement helpers ─────────────────────────────────────────────────────────────────────────

    private sealed record PlaceOutcome(bool Success, string? OrderId, string? Error, bool Unknown = false, bool Recovered = false);

    private static uint NewUserRef() => (uint)Random.Shared.Next(1, int.MaxValue);

    /// <summary>Kraken API rejections carry a code such as "EOrder:Insufficient funds" — the order
    /// definitely does not exist. Anything else ("Request timed out", connection errors…) is ambiguous.</summary>
    private static bool IsDefiniteRejection(string? message) =>
        !string.IsNullOrEmpty(message) && System.Text.RegularExpressions.Regex.IsMatch(message, @"\bE(Order|General|Service|Trade|Funding|Query|API|Auth)\w*:");

    /// <summary>
    /// Places a limit order tagged with <paramref name="userRef"/>. If the call fails ambiguously (timeout etc.)
    /// Kraken is asked for that userref before anything is called a failure, because the order is often accepted
    /// even though the response is lost. Not found twice in a row => genuinely not placed; Kraken unreachable =>
    /// Unknown (caller must keep a record and re-check, not retry blindly).
    /// </summary>
    private async Task<PlaceOutcome> PlaceAndConfirmAsync(string symbol, OrderSide side, decimal qty, decimal price, uint userRef)
    {
        var result = await _kraken.PlaceOrderAsync(symbol, side, OrderType.Limit, qty, price, null, userRef);
        if (result.Success)
            return new PlaceOutcome(true, result.Data?.OrderIds?.FirstOrDefault(), null);

        var error = result.Error?.Message;
        if (IsDefiniteRejection(error))
            return new PlaceOutcome(false, null, error);

        _logger.LogWarning("[MicroTrade] {Side} {Symbol} placement failed ambiguously ({Error}) — checking Kraken for userref {UserRef}", side, symbol, error, userRef);
        var notFoundChecks = 0;
        foreach (var delayMs in new[] { 3000, 6000 })
        {
            await Task.Delay(delayMs);
            var (isChecked, found) = await _kraken.FindOrderByUserRefAsync(userRef);
            if (found != null)
                return new PlaceOutcome(true, found.Id, null, Recovered: true);
            if (isChecked) notFoundChecks++;
        }

        return notFoundChecks >= 2
            ? new PlaceOutcome(false, null, error)
            : new PlaceOutcome(false, null, error, Unknown: true);
    }

    /// <summary>Saves the current changes after an order was actually sent to Kraken. Never throws: on failure the
    /// intent row already exists with its userref, so the monitor can still reconcile it.</summary>
    private async Task PersistAsync(KrakenDbContext db, MicroTradeOrder order, string what)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _sqlDiag.CaptureIfTimeout($"MicroTradeJob.Persist.{what}", ex);
            _logger.LogCritical(ex, "[MicroTrade] FAILED to save {What} for micro order {Id} {Symbol} (buyOrderId={Buy}, sellOrderId={Sell}, buyUserRef={BRef}, sellUserRef={SRef})",
                what, order.Id, order.Symbol, order.BuyOrderId, order.SellOrderId, order.BuyUserRef, order.SellUserRef);
            await NotifySafe($"Micro Trade DB save failed — {order.Symbol}",
                $"The {what} order was sent to Kraken but the record could not be saved (buy={order.BuyOrderId}, sell={order.SellOrderId}). It will be reconciled automatically.");
        }
    }

    private async Task NotifySafe(string title, string message)
    {
        try { await _notify.Pushover(title, message); }
        catch (Exception ex) { _logger.LogWarning(ex, "[MicroTrade] Notification failed: {Title}", title); }
    }

    // ── Fill monitoring ───────────────────────────────────────────────────────────────────────────

    private async Task MonitorFillsAsync(KrakenDbContext db, CancellationToken ct)
    {
        List<MicroTradeOrder> pending;
        try
        {
            pending = await db.MicroTradeOrders
                .Where(o => o.Status == "Placing" || o.Status == "Buying" || o.Status == "Selling")
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // Don't let a failed monitor query abort the tick before its saves (the rule results still need saving)
            _sqlDiag.CaptureIfTimeout("MicroTradeJob.LoadPending", ex);
            _logger.LogError(ex, "[MicroTrade] Could not load pending orders to monitor");
            return;
        }

        if (pending.Count == 0) return;

        var ruleIds = pending.Select(o => o.RuleId).Distinct().ToList();
        var rules = await db.MicroTradeRules.Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);

        foreach (var order in pending)
        {
            try
            {
                rules.TryGetValue(order.RuleId, out var rule);
                if (order.Status == "Placing")
                    await HandlePlacing(db, order);
                else if (order.Status == "Buying")
                    await HandleBuying(db, order, rule);
                else
                    await HandleSelling(db, order, rule);
                await PersistAsync(db, order, order.Status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MicroTrade] Error monitoring order {Id}", order.Id);
            }
        }
    }

    /// <summary>
    /// True if the order is still resting on the book according to the websocket cache. The feed updates an
    /// order's Status in place as it fills/closes but never removes it from _state.Orders (that only happens
    /// ~30 days later via REST reconciliation), so a plain ContainsKey would treat a closed order as open forever.
    /// NOTE: "not open in the cache" does NOT mean "filled" — it can equally be cancelled, expired, or simply
    /// unknown to the cache — so callers must confirm with Kraken before acting on a fill.
    /// </summary>
    private bool IsOrderStillOpen(string orderId) =>
        _state.Orders.TryGetValue(orderId, out var o) && TradingStateService.IsOpenOrderStatus(o.Status);

    /// <summary>A buy whose placement was never confirmed: resolve it by userref.</summary>
    private async Task HandlePlacing(KrakenDbContext db, MicroTradeOrder order)
    {
        if ((DateTime.UtcNow - order.CreatedAt).TotalSeconds < 90) return;

        if (order.BuyUserRef is not > 0)
        {
            db.MicroTradeOrders.Remove(order);
            return;
        }

        var (isChecked, found) = await _kraken.FindOrderByUserRefAsync((uint)order.BuyUserRef.Value);
        if (!isChecked) return; // Kraken unreachable — try again next tick, don't guess

        if (found != null)
        {
            order.BuyOrderId = found.Id;
            order.Status = "Buying";
            order.Note = "Buy placement was unconfirmed; found on Kraken by userref";
            _logger.LogInformation("[MicroTrade] Resolved unconfirmed buy for order {Id} -> {KrakenId}", order.Id, found.Id);
            await NotifySafe($"Micro Trade Buy Confirmed — {order.Symbol}", $"Unconfirmed buy found on Kraken ({found.Id})");
        }
        else if ((DateTime.UtcNow - order.CreatedAt).TotalMinutes >= 3)
        {
            // Kraken confirms it was never placed — it must not be shown or counted as an order at all
            db.MicroTradeOrders.Remove(order);
            _logger.LogWarning("[MicroTrade] Unconfirmed buy {Id} confirmed absent on Kraken — record removed", order.Id);
        }
    }

    private async Task HandleBuying(KrakenDbContext db, MicroTradeOrder order, MicroTradeRule? rule)
    {
        if (string.IsNullOrEmpty(order.BuyOrderId)) return;
        // Give the websocket order feed time to catch up before assuming anything
        if ((DateTime.UtcNow - order.CreatedAt).TotalSeconds < 60) return;

        // A previous pass may have sent the sell but failed to record it (timeout / SQL failure). Adopt it
        // rather than placing a second sell; if Kraken can't be asked, wait rather than risk a duplicate.
        if (order.SellUserRef is > 0)
        {
            var (isChecked, adopted) = await TryAdoptExistingSellAsync(order);
            if (adopted || !isChecked) return;
        }

        if (IsOrderStillOpen(order.BuyOrderId)) return; // still resting

        // Ask Kraken what actually happened. Absent from the websocket cache != filled: that assumption produced a
        // phantom fill (and a doomed sell of coins never bought) for an order that was cancelled/never rested.
        var filledOrder = await _kraken.GetOrderInfoAsync(order.BuyOrderId!);
        if (filledOrder == null)
        {
            order.Note = "Could not verify buy order with Kraken — will retry";
            return;
        }
        if (filledOrder.Status is Kraken.Net.Enums.OrderStatus.Open or Kraken.Net.Enums.OrderStatus.Pending)
            return; // cache lagging; still live on Kraken

        if (filledOrder.QuantityFilled <= 0)
        {
            order.Status = "Cancelled";
            order.Note = $"Buy {filledOrder.Status} on Kraken with nothing filled" + (string.IsNullOrEmpty(filledOrder.Reason) ? "" : $" ({filledOrder.Reason})");
            _logger.LogWarning("[MicroTrade] Buy {Id} ended {Status} with no fill — cancelled, no sell placed", order.Id, filledOrder.Status);
            await NotifySafe($"Micro Trade Buy Not Filled — {order.Symbol}", order.Note);
            return;
        }

        order.BuyFilledAt ??= DateTime.UtcNow;
        var risePct = rule?.RisePct ?? 10m;
        var sellPrice = Math.Round(order.BuyPrice * (1 + risePct / 100m), GetPriceDecimals(order.Symbol));

        // Sell exactly what Kraken says was bought (vol_exec) — includes partial fills of a since-cancelled buy
        var actualQty = filledOrder.QuantityFilled;
        order.Quantity = actualQty;

        var (sellOutcome, soldQty) = await PlaceSellWithRetryAsync(db, order, actualQty, sellPrice);

        if (sellOutcome.Success)
        {
            order.Quantity = soldQty;
            order.SellOrderId = sellOutcome.OrderId;
            order.SellPrice = sellPrice;
            order.Status = "Selling";
            order.Note = soldQty != actualQty
                ? $"Sell quantity reduced {actualQty} -> {soldQty} after Kraken rejected the exact fill quantity"
                : "";
            _logger.LogInformation("[MicroTrade] Buy filled for order {Id} — placed sell of {Qty} @ {Price}", order.Id, soldQty, sellPrice);
            await NotifySafe($"Micro Trade Filled — {order.Symbol}",
                $"Buy filled @ {order.BuyPrice:F4}. Sell of {soldQty} placed @ {sellPrice:F4}");
        }
        else if (sellOutcome.Unknown)
        {
            order.Note = $"Sell placement unconfirmed ({sellOutcome.Error}) — will re-check userref {order.SellUserRef}";
            _logger.LogError("[MicroTrade] Sell placement unconfirmed for order {Id}: {Error}", order.Id, sellOutcome.Error);
            await NotifySafe($"Micro Trade Sell Unconfirmed — {order.Symbol}", order.Note);
        }
        else
        {
            order.Note = $"Sell placement failed after {MaxSellAttempts} attempts (last tried qty {soldQty}): {sellOutcome.Error}";
            _logger.LogError("[MicroTrade] Failed to place sell for order {Id}: {Error}", order.Id, sellOutcome.Error);
            await NotifySafe($"Micro Trade Sell Failed — {order.Symbol}", order.Note);
        }
    }

    /// <summary>If an earlier sell placement for this order (tagged SellUserRef) actually exists on Kraken, adopt it
    /// as the order's sell. IsChecked=false means Kraken couldn't be asked.</summary>
    private async Task<(bool IsChecked, bool Adopted)> TryAdoptExistingSellAsync(MicroTradeOrder order)
    {
        var (isChecked, found) = await _kraken.FindOrderByUserRefAsync((uint)order.SellUserRef!.Value);
        if (found == null || found.Side != OrderSide.Sell) return (isChecked, false);

        order.SellOrderId = found.Id;
        if (found.Price > 0) order.SellPrice = found.Price;
        if (found.Quantity > 0) order.Quantity = found.Quantity;
        order.BuyFilledAt ??= DateTime.UtcNow;
        order.Status = "Selling";
        order.Note = "Sell had been placed but not recorded; adopted from Kraken by userref";
        _logger.LogInformation("[MicroTrade] Adopted existing sell {SellId} for order {Id}", found.Id, order.Id);
        return (true, true);
    }

    /// <summary>
    /// Places a sell for <paramref name="quantity"/>, first floored to the pair's actual lot precision — both
    /// Kraken's fill quantities and our budget-derived quantities can carry more decimals than the pair allows,
    /// which Kraken rejects outright. If still rejected (small balance/ledger mismatch) the quantity is shrunk
    /// and retried. One userref is persisted BEFORE the first attempt so a sell that Kraken accepted but whose
    /// response/record was lost can be found again (see <see cref="TryAdoptExistingSellAsync"/>).
    /// </summary>
    private async Task<(PlaceOutcome Outcome, decimal Quantity)> PlaceSellWithRetryAsync(KrakenDbContext db, MicroTradeOrder order, decimal quantity, decimal price)
    {
        var symbol = order.Symbol;
        var lotDecimals = GetLotDecimals(symbol);
        var qty = FloorToDecimals(quantity, lotDecimals);
        if (qty <= 0) qty = quantity; // flooring collapsed it to zero (quantity smaller than the pair's lot step) — fall back and let Kraken's own error surface

        var userRef = NewUserRef();
        order.SellUserRef = userRef;
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _sqlDiag.CaptureIfTimeout("MicroTradeJob.SaveSellIntent", ex);
            _logger.LogError(ex, "[MicroTrade] Could not persist sell intent for order {Id} — NOT placing the sell this tick", order.Id);
            return (new PlaceOutcome(false, null, $"could not record sell before placing: {ex.GetBaseException().Message}", Unknown: true), qty);
        }

        PlaceOutcome outcome;
        for (var attempt = 1; ; attempt++)
        {
            outcome = await PlaceAndConfirmAsync(symbol, OrderSide.Sell, qty, price, userRef);
            if (outcome.Success || outcome.Unknown || attempt >= MaxSellAttempts) return (outcome, qty);

            var shrunk = ShrinkQuantitySlightly(qty, lotDecimals);
            _logger.LogWarning("[MicroTrade] Sell of {Qty} {Symbol} failed ({Error}) — retrying with {Shrunk}",
                qty, symbol, outcome.Error, shrunk);
            qty = shrunk;
        }
    }

    /// <summary>Trims a rejected sell quantity so the next retry has a real chance of succeeding.
    /// If the value still carries more decimal places than the pair's lot precision allows, it's
    /// floored straight down to that precision (the usual cause of a rejection — trimming just the
    /// least-significant digit of an over-precise value can land back on a numerically identical
    /// value when trailing digits are zero, which never actually reduces precision and just burns
    /// retries). Once it's already at the pair's precision, one unit at that precision is subtracted
    /// instead, which is the right move for a small balance/ledger mismatch.</summary>
    private static decimal ShrinkQuantitySlightly(decimal qty, int lotDecimals)
    {
        var str = qty.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dotIdx = str.IndexOf('.');
        var currentDecimals = dotIdx < 0 ? 0 : str.Length - dotIdx - 1;

        if (currentDecimals > lotDecimals)
            return FloorToDecimals(qty, lotDecimals);

        var step = 1m / (decimal)Math.Pow(10, Math.Max(lotDecimals, 0));
        var shrunk = qty - step;
        return shrunk > 0 ? shrunk : 0;
    }

    private int GetLotDecimals(string symbol)
    {
        var sym = _state.Symbols.Values.FirstOrDefault(s => s.WebsocketName.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        return sym?.LotDecimals > 0 ? sym.LotDecimals : 8;
    }

    private int GetPriceDecimals(string symbol)
    {
        var sym = _state.Symbols.Values.FirstOrDefault(s => s.WebsocketName.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        return sym?.PriceDecimals > 0 ? sym.PriceDecimals : 8;
    }

    private static decimal FloorToDecimals(decimal value, int decimals)
    {
        if (decimals < 0) return value;
        var factor = (decimal)Math.Pow(10, decimals);
        return Math.Floor(value * factor) / factor;
    }

    private async Task HandleSelling(KrakenDbContext db, MicroTradeOrder order, MicroTradeRule? rule)
    {
        if (string.IsNullOrEmpty(order.SellOrderId)) return;

        // Stop loss — if enabled and not already triggered for this order, reprice the resting
        // profit-target sell down to near the current (lower) price so it can actually fill,
        // instead of sitting forever above a market that has since dropped further.
        if (rule is { StopLossEnabled: true } && !order.StopLossTriggered && IsOrderStillOpen(order.SellOrderId))
        {
            var stopPrice = order.BuyPrice * (rule.StopLossPct / 100m);
            var priceItem = ResolvePriceItem(order.Symbol);
            var currentPrice = priceItem?.BestKline?.Close ?? 0;

            if (currentPrice > 0 && currentPrice <= stopPrice)
            {
                var cancelled = await _kraken.CancelOrderAsync(order.SellOrderId);
                if (cancelled)
                {
                    var newSellPrice = Math.Round(currentPrice * 1.001m, GetPriceDecimals(order.Symbol));
                    var (outcome, soldQty) = await PlaceSellWithRetryAsync(db, order, order.Quantity, newSellPrice);
                    order.StopLossTriggered = true;

                    if (outcome.Success)
                    {
                        var oldSellPrice = order.SellPrice;
                        var oldQty = order.Quantity;
                        order.Quantity = soldQty;
                        order.SellOrderId = outcome.OrderId;
                        order.SellPrice = newSellPrice;
                        order.Note = $"Stop-loss: price fell to {currentPrice:F4} (<= {stopPrice:F4}) — repriced sell {oldSellPrice:F4} -> {newSellPrice:F4}"
                            + (soldQty != oldQty ? $" (qty reduced {oldQty} -> {soldQty} after rejection)" : "");
                        _logger.LogInformation("[MicroTrade] Stop-loss repriced sell for order {Id}: {Note}", order.Id, order.Note);
                        await NotifySafe($"Micro Trade Stop-Loss — {order.Symbol}", order.Note);
                    }
                    else
                    {
                        order.Note = $"Stop-loss: sell cancelled but reprice failed: {outcome.Error}";
                        _logger.LogError("[MicroTrade] Stop-loss reprice failed for order {Id}: {Error}", order.Id, outcome.Error);
                        await NotifySafe($"Micro Trade Stop-Loss Failed — {order.Symbol}", order.Note);
                    }
                }
                return; // don't also check for a fill this same pass
            }
        }

        if (IsOrderStillOpen(order.SellOrderId)) return; // still resting

        // Not open in the websocket cache — confirm with Kraken before calling it sold
        var info = await _kraken.GetOrderInfoAsync(order.SellOrderId);
        if (info == null) return; // can't verify — retry next tick
        if (info.Status is Kraken.Net.Enums.OrderStatus.Open or Kraken.Net.Enums.OrderStatus.Pending) return;

        if (info.Status == Kraken.Net.Enums.OrderStatus.Closed && info.QuantityFilled > 0)
        {
            order.Status = "Sold";
            order.SoldAt = DateTime.UtcNow;
            _logger.LogInformation("[MicroTrade] Sell filled for order {Id}", order.Id);
            await NotifySafe($"Micro Trade Sold — {order.Symbol}",
                $"Sell filled @ {order.SellPrice:F4} (bought @ {order.BuyPrice:F4})");
        }
        else
        {
            // Cancelled/expired without filling (e.g. cancelled by hand on Kraken) — the coins are still held
            order.Status = "Cancelled";
            order.Note = $"Sell {info.Status} on Kraken without filling ({info.QuantityFilled} of {info.Quantity}) — position still held";
            _logger.LogWarning("[MicroTrade] Sell for order {Id} ended {Status} unfilled", order.Id, info.Status);
            await NotifySafe($"Micro Trade Sell Cancelled — {order.Symbol}", order.Note);
        }
    }
}
