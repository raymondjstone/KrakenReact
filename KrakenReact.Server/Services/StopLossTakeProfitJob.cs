using Kraken.Net.Enums;

namespace KrakenReact.Server.Services;

public class StopLossTakeProfitJob
{
    private readonly TradingStateService _state;
    private readonly KrakenRestService _kraken;
    private readonly NotificationService _notify;
    private readonly ILogger<StopLossTakeProfitJob> _logger;
    private static readonly HashSet<string> FIAT = new(StringComparer.OrdinalIgnoreCase)
        { "USD", "USDT", "USDC", "GBP", "EUR", "CAD", "AUD", "JPY", "CHF" };

    public StopLossTakeProfitJob(TradingStateService state, KrakenRestService kraken, NotificationService notify, ILogger<StopLossTakeProfitJob> logger)
    {
        _state = state;
        _kraken = kraken;
        _notify = notify;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        if (!_state.StopLossEnabled && !_state.TakeProfitEnabled) return;

        foreach (var bal in _state.Balances.Values.ToList())
        {
            if (FIAT.Contains(bal.Asset)) continue;
            if (bal.Available <= 0 || bal.LatestValue < 5m) continue;
            if (!bal.TotalCostBasis.HasValue || bal.TotalCostBasis.Value <= 0 || bal.Total <= 0) continue;

            var avgCost = bal.TotalCostBasis.Value / bal.Total;
            var currentPrice = bal.LatestPrice;
            if (currentPrice <= 0) continue;

            var changePct = (currentPrice - avgCost) / avgCost * 100m;

            if (_state.StopLossEnabled && changePct <= -_state.StopLossPct)
            {
                _logger.LogWarning("[StopLoss] {Asset} at {Price:F4}, down {Pct:F1}% from avg {Avg:F4}",
                    bal.Asset, currentPrice, changePct, avgCost);

                // Find the websocket symbol to place the order
                var sym = FindSymbol(bal.Asset);
                if (sym == null) continue;

                var clientId = $"SL{DateTime.Now:yyyyMMddHHmmss}";
                var result = await _kraken.PlaceOrderAsync(sym, OrderSide.Sell, OrderType.Market, bal.Available, 0, clientId);
                if (result.Success)
                {
                    await _notify.Pushover($"Stop-Loss Triggered — {bal.Asset}",
                        $"Sold {bal.Available:F4} {bal.Asset} at market price (down {Math.Abs(changePct):F1}% from avg cost {avgCost:F4})");
                    _logger.LogInformation("[StopLoss] Stop-loss order placed for {Asset}", bal.Asset);
                }
                else
                {
                    _logger.LogError("[StopLoss] Failed to place stop-loss for {Asset}: {Error}", bal.Asset, result.Error?.Message);
                }
            }
            else if (_state.TakeProfitEnabled && changePct >= _state.TakeProfitPct)
            {
                _logger.LogInformation("[TakeProfit] {Asset} at {Price:F4}, up {Pct:F1}% from avg {Avg:F4}",
                    bal.Asset, currentPrice, changePct, avgCost);

                var sym = FindSymbol(bal.Asset);
                if (sym == null) continue;

                // Place limit sell at current price
                var clientId = $"TP{DateTime.Now:yyyyMMddHHmmss}";
                var result = await _kraken.PlaceOrderAsync(sym, OrderSide.Sell, OrderType.Limit, bal.Available, currentPrice, clientId);
                if (result.Success)
                {
                    await _notify.Pushover($"Take-Profit Triggered — {bal.Asset}",
                        $"Limit sell {bal.Available:F4} {bal.Asset} @ {currentPrice:F4} (up {changePct:F1}% from avg {avgCost:F4})");
                    _logger.LogInformation("[TakeProfit] Take-profit order placed for {Asset}", bal.Asset);
                }
                else
                {
                    _logger.LogError("[TakeProfit] Failed to place take-profit for {Asset}: {Error}", bal.Asset, result.Error?.Message);
                }
            }
        }
    }

    private string? FindSymbol(string asset)
    {
        var key = _state.Symbols.Values.FirstOrDefault(s =>
            TradingStateService.NormalizeAsset(s.BaseAsset) == asset && s.WebsocketName.EndsWith("/USD"))
            ?.WebsocketName;
        return key?.Replace("/", "");
    }
}
