using KrakenReact.Server.Data;
using KrakenReact.Server.DTOs;
using KrakenReact.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Text;
using System.Text.Json;
using Websocket.Client;

namespace KrakenReact.Server.Services;

public class KrakenWebSocketV2Service : BackgroundService
{
    private readonly TradingStateService _state;
    private readonly KrakenRestService _kraken;
    private readonly DbMethods _db;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<KrakenWebSocketV2Service> _logger;
    private WebsocketClient? _socket;
    private string? _wsToken;
    private DateTime _lastTradeSync = DateTime.MinValue;

    public KrakenWebSocketV2Service(TradingStateService state, KrakenRestService kraken, DbMethods db, IHubContext<TradingHub> hub, ILogger<KrakenWebSocketV2Service> logger)
    {
        _state = state;
        _kraken = kraken;
        _db = db;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait for initial data to load
            await Task.Delay(15000, stoppingToken);

            var tokenResult = await _kraken.GetWebSocketAsyncToken();
            if (tokenResult == null) { _logger.LogError("[WS V2] Could not get token"); return; }
            _wsToken = tokenResult.Token;

            var uri = new Uri("wss://ws-auth.kraken.com/v2");
            _socket = new WebsocketClient(uri);
            _socket.LostReconnectTimeout = TimeSpan.FromMinutes(10);
            _socket.IsReconnectionEnabled = true;
            _socket.ReconnectTimeout = TimeSpan.FromSeconds(120);

            _socket.ReconnectionHappened.Subscribe(info =>
            {
                _logger.LogInformation("[WS V2] Reconnection: {Type}", info.Type);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var freshToken = await _kraken.GetWebSocketAsyncToken();
                        if (freshToken != null) _wsToken = freshToken.Token;
                        await Subscribe();
                    }
                    catch (Exception ex) { _logger.LogError(ex, "[WS V2] Error during reconnect subscribe"); }
                });
            });

            _socket.MessageReceived.Subscribe(msg =>
            {
                try { ProcessMessage(msg.Text); }
                catch (Exception ex) { _logger.LogError(ex, "[WS V2] Error processing message"); }
            });

            _socket.Start();
            await Subscribe();

            // Ping timer
            _ = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try { await Task.Delay(60000, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    try { _socket?.Send(Encoding.UTF8.GetBytes("{\"event\":\"ping\"}")); } catch { }
                }
            }, stoppingToken);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[WS V2] Shutting down gracefully");
        }
    }

    private async Task Subscribe()
    {
        if (_wsToken == null) return;

        // Subscribe to executions
        var execSub = JsonSerializer.Serialize(new { method = "subscribe", @params = new { channel = "executions", token = _wsToken } });
        _socket?.Send(Encoding.UTF8.GetBytes(execSub));

        // Subscribe to balances
        var balSub = JsonSerializer.Serialize(new { method = "subscribe", @params = new { channel = "balances", token = _wsToken } });
        _socket?.Send(Encoding.UTF8.GetBytes(balSub));
    }

    private void ProcessMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;

        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            // Skip control messages
            if (root.TryGetProperty("method", out _)) return;
            if (root.TryGetProperty("channel", out var channelProp))
            {
                var channel = channelProp.GetString();
                if (channel == "status" || channel == "heartbeat") return;
            }
        }
        catch { return; }

        if (message.Contains("executions"))
        {
            try
            {
                var execMsg = JsonSerializer.Deserialize<ExecutionWsMessage>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (execMsg?.Data != null)
                {
                    bool hasFill = false;
                    foreach (var exec in execMsg.Data)
                    {
                        if (string.IsNullOrEmpty(exec.OrderId)) continue;

                        // Merge into existing order if available, so we don't lose fields
                        // that the execution update doesn't include
                        if (_state.Orders.TryGetValue(exec.OrderId, out var existing))
                        {
                            if (!string.IsNullOrEmpty(exec.Symbol)) existing.Symbol = exec.Symbol;
                            if (!string.IsNullOrEmpty(exec.Side)) existing.Side = exec.Side;
                            if (!string.IsNullOrEmpty(exec.OrderType)) existing.Type = exec.OrderType;
                            if (!string.IsNullOrEmpty(exec.OrderStatus)) existing.Status = exec.OrderStatus;
                            if (exec.LimitPrice != 0) existing.Price = exec.LimitPrice;
                            if (exec.OrderQty != 0) existing.Quantity = exec.OrderQty;
                            if (exec.Timestamp != default) existing.CreateTime = exec.Timestamp;
                        }
                        else
                        {
                            _state.Orders[exec.OrderId] = new OrderDto
                            {
                                Id = exec.OrderId,
                                Symbol = exec.Symbol ?? "",
                                Side = exec.Side ?? "Buy",
                                Type = exec.OrderType ?? "Limit",
                                Status = exec.OrderStatus ?? "Open",
                                Price = exec.LimitPrice,
                                Quantity = exec.OrderQty,
                                CreateTime = exec.Timestamp
                            };
                        }
                        // Detect fills (status changes that indicate a trade occurred)
                        var status = (exec.OrderStatus ?? "").ToLower();
                        if (status == "filled" || status == "partially_filled" || status == "closed")
                            hasFill = true;
                    }
                    _ = _hub.Clients.All.SendAsync("ExecutionUpdate", execMsg.Data)
                        .ContinueWith(t => { if (t.IsFaulted) _logger.LogWarning(t.Exception, "[WS V2] ExecutionUpdate broadcast failed"); }, TaskContinuationOptions.OnlyOnFaulted);

                    // When a trade fills, fetch fresh trades+ledger from Kraken API so DB is up to date
                    if (hasFill)
                    {
                        _ = Task.Run(async () =>
                        {
                            // Throttle: don't sync more than once per 10 seconds
                            var now = DateTime.UtcNow;
                            if ((now - _lastTradeSync).TotalSeconds < 10) return;
                            _lastTradeSync = now;

                            // Brief delay to let Kraken's API reflect the trade
                            await Task.Delay(3000);
                            try
                            {
                                await _kraken.GetTradesAsync(false);
                                await _kraken.GetLedgerAsync(false);
                                _logger.LogInformation("[WS V2] Synced trades+ledger after execution fill");
                                // Notify frontend that fresh trade/ledger data is now available in DB
                                await _hub.Clients.All.SendAsync("TradesUpdated");
                            }
                            catch (Exception ex) { _logger.LogError(ex, "[WS V2] Error syncing trades after execution"); }
                        });
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "[WS V2] Error parsing execution"); }
        }

        if (message.Contains("balances"))
        {
            try
            {
                var balMsg = JsonSerializer.Deserialize<BalanceWsMessage>(message, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (balMsg?.Data != null && balMsg.Data.Any())
                {
                    var usdGbpRate = _state.GetUsdGbpRate();
                    foreach (var b in balMsg.Data)
                    {
                        var normalizedAsset = TradingStateService.NormalizeAsset(b.Asset);
                        var latestPrice = _state.LatestPrice(normalizedAsset);
                        var price = latestPrice?.Close ?? 0;
                        var total = (decimal)b.Balance;
                        var valueUsd = Math.Round(total * price, 2);
                        var valueGbp = usdGbpRate > 0 ? Math.Round(valueUsd * usdGbpRate, 2) : 0;

                        if (_state.Balances.TryGetValue(normalizedAsset, out var existing) && b.Asset != normalizedAsset)
                        {
                            // Merge into existing normalized entry
                            existing.Total += total;
                            existing.Available = existing.Total - existing.Locked;
                            existing.LatestValue = Math.Round(existing.Total * price, 2);
                            existing.LatestValueGbp = usdGbpRate > 0 ? Math.Round(existing.LatestValue * usdGbpRate, 2) : 0;
                        }
                        else
                        {
                            _state.Balances[normalizedAsset] = new BalanceDto
                            {
                                Asset = normalizedAsset,
                                Total = total,
                                Locked = 0,
                                Available = total,
                                LatestPrice = price,
                                LatestValue = valueUsd,
                                LatestValueGbp = valueGbp
                            };
                        }

                        // Remove the un-normalized key if it differs
                        if (b.Asset != normalizedAsset)
                        {
                            _state.Balances.TryRemove(b.Asset, out _);
                        }
                    }
                    // Recalculate portfolio percentages
                    var totalPortfolioValue = _state.Balances.Values.Sum(x => x.LatestValue);
                    if (totalPortfolioValue > 0)
                    {
                        foreach (var bal in _state.Balances.Values)
                            bal.PortfolioPercentage = Math.Round(bal.LatestValue / totalPortfolioValue * 100, 2);
                    }

                    _ = _hub.Clients.All.SendAsync("BalanceUpdate", _state.Balances.Values.ToList())
                        .ContinueWith(t => { if (t.IsFaulted) _logger.LogWarning(t.Exception, "[WS V2] BalanceUpdate broadcast failed"); }, TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "[WS V2] Error parsing balances"); }
        }
    }

    public override void Dispose()
    {
        _socket?.Dispose();
        base.Dispose();
    }
}

// WebSocket message models
public class ExecutionWsMessage
{
    public string? Channel { get; set; }
    public string? Type { get; set; }
    public List<ExecutionWsData>? Data { get; set; }
}

public class ExecutionWsData
{
    public string? Order_id { get; set; }
    public string? OrderId => Order_id;
    public string? Symbol { get; set; }
    public decimal Order_qty { get; set; }
    public decimal OrderQty => Order_qty;
    public string? Side { get; set; }
    public string? Order_type { get; set; }
    public string? OrderType => Order_type;
    public decimal Limit_price { get; set; }
    public decimal LimitPrice => Limit_price;
    public string? Order_status { get; set; }
    public string? OrderStatus => Order_status;
    public DateTime Timestamp { get; set; }
}

public class BalanceWsMessage
{
    public string? Channel { get; set; }
    public string? Type { get; set; }
    public List<BalanceWsData>? Data { get; set; }
}

public class BalanceWsData
{
    public string Asset { get; set; } = "";
    public double Balance { get; set; }
}
