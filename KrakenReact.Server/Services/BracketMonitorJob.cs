using Hangfire;
using Kraken.Net.Enums;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace KrakenReact.Server.Services;

public class BracketMonitorJob
{
    private readonly IDbContextFactory<KrakenDbContext> _dbFactory;
    private readonly KrakenRestService _kraken;
    private readonly TradingStateService _state;
    private readonly NotificationService _notify;
    private readonly ILogger<BracketMonitorJob> _logger;

    public BracketMonitorJob(
        IDbContextFactory<KrakenDbContext> dbFactory,
        KrakenRestService kraken,
        TradingStateService state,
        NotificationService notify,
        ILogger<BracketMonitorJob> logger)
    {
        _dbFactory = dbFactory;
        _kraken = kraken;
        _state = state;
        _notify = notify;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var active = await db.BracketOrders
            .Where(b => b.Status == "Watching" || b.Status == "Active")
            .ToListAsync(ct);

        if (active.Count == 0) return;

        foreach (var bracket in active)
        {
            try
            {
                if (bracket.Status == "Watching")
                    await HandleWatching(bracket);
                else
                    await HandleActive(bracket);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Bracket] Error processing bracket {Id}", bracket.Id);
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task HandleWatching(BracketOrder bracket)
    {
        // Wait at least 90 seconds before assuming the parent order filled
        if ((DateTime.UtcNow - bracket.CreatedAt).TotalSeconds < 90) return;

        // If parent still in open orders, nothing to do
        if (_state.Orders.ContainsKey(bracket.KrakenOrderId)) return;

        _logger.LogInformation("[Bracket] Parent {OrderId} gone — placing SL @ {Stop} TP @ {TP}",
            bracket.KrakenOrderId, bracket.StopPrice, bracket.TakeProfitPrice);

        var oppSide = bracket.Side.Equals("Buy", StringComparison.OrdinalIgnoreCase)
            ? OrderSide.Sell : OrderSide.Buy;
        var sym = bracket.Symbol.Replace("/", "");

        var slResult = await _kraken.PlaceOrderAsync(sym, oppSide, OrderType.Limit,
            bracket.Quantity, bracket.StopPrice,
            $"brk-sl-{bracket.Id}-{DateTime.UtcNow:HHmm}");

        var tpResult = await _kraken.PlaceOrderAsync(sym, oppSide, OrderType.Limit,
            bracket.Quantity, bracket.TakeProfitPrice,
            $"brk-tp-{bracket.Id}-{DateTime.UtcNow:HHmm}");

        if (slResult.Success && tpResult.Success)
        {
            bracket.StopOrderId = slResult.Data?.OrderIds?.FirstOrDefault();
            bracket.TakeProfitOrderId = tpResult.Data?.OrderIds?.FirstOrDefault();
            bracket.Status = "Active";
            bracket.ActivatedAt = DateTime.UtcNow;
            await _notify.Pushover(
                $"Bracket Active — {bracket.Symbol}",
                $"SL @ {bracket.StopPrice:F4}  TP @ {bracket.TakeProfitPrice:F4}");
        }
        else
        {
            bracket.Status = "Cancelled";
            var err = $"SL={slResult.Error?.Message} TP={tpResult.Error?.Message}";
            _logger.LogError("[Bracket] Failed to place SL/TP for {Id}: {Err}", bracket.Id, err);
            await _notify.Pushover($"Bracket Failed — {bracket.Symbol}", err);
        }
    }

    private async Task HandleActive(BracketOrder bracket)
    {
        if (bracket.ActivatedAt == null ||
            (DateTime.UtcNow - bracket.ActivatedAt.Value).TotalSeconds < 90) return;

        if (bracket.StopOrderId == null || bracket.TakeProfitOrderId == null) return;

        var slGone = !_state.Orders.ContainsKey(bracket.StopOrderId);
        var tpGone = !_state.Orders.ContainsKey(bracket.TakeProfitOrderId);

        if (tpGone && !slGone)
        {
            await _kraken.CancelOrderAsync(bracket.StopOrderId);
            bracket.Status = "TookProfit";
            await _notify.Pushover($"Bracket TP — {bracket.Symbol}",
                $"Take-profit @ {bracket.TakeProfitPrice:F4} filled. SL cancelled.");
            _logger.LogInformation("[Bracket] TP hit for bracket {Id}", bracket.Id);
        }
        else if (slGone && !tpGone)
        {
            await _kraken.CancelOrderAsync(bracket.TakeProfitOrderId);
            bracket.Status = "Stopped";
            await _notify.Pushover($"Bracket SL — {bracket.Symbol}",
                $"Stop-loss @ {bracket.StopPrice:F4} filled. TP cancelled.");
            _logger.LogInformation("[Bracket] SL hit for bracket {Id}", bracket.Id);
        }
        else if (slGone && tpGone)
        {
            bracket.Status = "TookProfit";
            _logger.LogWarning("[Bracket] Both legs gone for bracket {Id}", bracket.Id);
        }
    }
}
