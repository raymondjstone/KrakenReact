using KrakenReact.Server.Hubs;
using KrakenReact.Server.Models;
using Microsoft.AspNetCore.SignalR;
using System.Text;
using System.Text.Json;
using Websocket.Client;

namespace KrakenReact.Server.Services;

public class KrakenWebSocketV1Service : BackgroundService
{
    private readonly TradingStateService _state;
    private readonly AutoOrderService _autoOrder;
    private readonly NotificationService _notifications;
    private readonly IHubContext<TradingHub> _hub;
    private readonly ILogger<KrakenWebSocketV1Service> _logger;
    private WebsocketClient? _socket;
    private volatile bool _initialSubsCompleted;
    private System.Timers.Timer? _pingTimer;
    private long _lastBalanceBroadcastTicks = DateTime.MinValue.Ticks;
    private long _lastOrderBroadcastTicks = DateTime.MinValue.Ticks;
    private volatile bool _balancesDirty;
    private volatile bool _ordersDirty;
    private string? _currentBookPair;

    public KrakenWebSocketV1Service(TradingStateService state, AutoOrderService autoOrder, NotificationService notifications, IHubContext<TradingHub> hub, ILogger<KrakenWebSocketV1Service> logger)
    {
        _state = state;
        _autoOrder = autoOrder;
        _notifications = notifications;
        _hub = hub;
        _logger = logger;

        _state.BookPairChanged += (oldPair, newPair) =>
        {
            if (_socket == null) return;
            try
            {
                var depth = _state.OrderBookDepth;
                if (!string.IsNullOrEmpty(oldPair))
                {
                    var unsub = JsonSerializer.Serialize(new { @event = "unsubscribe", pair = new[] { oldPair }, subscription = new { name = "book", depth } });
                    _socket.Send(Encoding.UTF8.GetBytes(unsub));
                }
                if (!string.IsNullOrEmpty(newPair))
                {
                    var sub = JsonSerializer.Serialize(new { @event = "subscribe", pair = new[] { newPair }, subscription = new { name = "book", depth } });
                    _socket.Send(Encoding.UTF8.GetBytes(sub));
                    _currentBookPair = newPair;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "[WS V1] Error changing book subscription"); }
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait for symbols to be loaded
            int waitAttempts = 0;
            while (!_state.Symbols.Any() && waitAttempts < 120)
            {
                waitAttempts++;
                await Task.Delay(5000, stoppingToken);
            }
            if (!_state.Symbols.Any()) { _logger.LogWarning("Symbols not loaded, WS V1 not starting"); return; }

            var uri = new Uri("wss://ws.kraken.com");
            _socket = new WebsocketClient(uri);
            _socket.LostReconnectTimeout = TimeSpan.FromMinutes(10);
            _socket.IsReconnectionEnabled = true;
            _socket.ReconnectTimeout = TimeSpan.FromSeconds(120);

            _socket.ReconnectionHappened.Subscribe(info =>
            {
                _logger.LogInformation("[WS V1] Reconnection: {Type}", info.Type);
                _ = Task.Run(async () =>
                {
                    try { await SubscribeToAssets(); }
                    catch (Exception ex) { _logger.LogError(ex, "[WS V1] Error during reconnect subscribe"); }
                });
            });

            _socket.MessageReceived.Subscribe(msg =>
            {
                try { ProcessMessage(msg.Text); }
                catch (Exception ex) { _logger.LogError(ex, "[WS V1] Error processing message"); }
            });

            await _socket.Start();

            // Start ping timer
            _pingTimer = new System.Timers.Timer(60000);
            _pingTimer.Elapsed += (s, e) => Ping();
            _pingTimer.AutoReset = true;
            _pingTimer.Enabled = true;

            // Subscribe
            await SubscribeToAssets();

            // Re-subscription timer
            _ = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try { await Task.Delay(120000, stoppingToken); }
                    catch (OperationCanceledException) { break; }
                    try
                    {
                        if (_initialSubsCompleted) await ReSubscribeToAssets();
                    }
                    catch (Exception ex) { _logger.LogError(ex, "[WS V1] Error in re-subscribe loop"); }
                }
            }, stoppingToken);

            // Keep alive
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[WS V1] Shutting down gracefully");
        }
    }

    private async Task SubscribeToAssets()
    {
        var pairs = _state.Symbols.Values
            .Where(s => TradingStateService.BaseCurrencies.Contains(s.QuoteAsset))
            .Select(s => s.WebsocketName).ToList();

        // Always include default + required pairs even if they aren't ZUSD-quoted
        foreach (var dp in TradingStateService.DefaultPairs.Concat(TradingStateService.RequiredPairs))
        {
            if (!pairs.Contains(dp)) pairs.Add(dp);
        }

        pairs = pairs.OrderBy(x => x).ToList();

        foreach (var pair in pairs)
        {
            var sub = JsonSerializer.Serialize(new { @event = "subscribe", pair = new[] { pair }, subscription = new { name = "ticker" } });
            _socket?.Send(Encoding.UTF8.GetBytes(sub));
            await Task.Delay(250);
        }
        _initialSubsCompleted = true;
    }

    private async Task ReSubscribeToAssets()
    {
        var outdated = _state.GetPriceSnapshot().Where(p => p.PriceOutdated).Select(k => k.Symbol).ToList();
        foreach (var pair in outdated)
        {
            var sub = JsonSerializer.Serialize(new { @event = "subscribe", pair = new[] { pair }, subscription = new { name = "ticker" } });
            _socket?.Send(Encoding.UTF8.GetBytes(sub));
            await Task.Delay(250);
        }
    }

    private void ProcessMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;

        // Skip non-array messages (system/control messages)
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        }
        catch { return; }

        // Process order book messages
        if (message.Contains("\"book-"))
        {
            try
            {
                var elements = JsonSerializer.Deserialize<List<object>>(message);
                if (elements != null && elements.Count >= 4)
                {
                    var pair = elements[elements.Count - 1].ToString();
                    var bookJson = elements[1].ToString();
                    using var bookDoc = JsonDocument.Parse(bookJson!);
                    var bookRoot = bookDoc.RootElement;

                    // Snapshot has "as" and "bs" keys, updates have "a" or "b"
                    bool isSnapshot = bookRoot.TryGetProperty("as", out _);

                    if (isSnapshot)
                    {
                        var asks = ParseBookLevels(bookRoot, "as");
                        var bids = ParseBookLevels(bookRoot, "bs");
                        _ = _hub.Clients.All.SendAsync("BookSnapshot", new { pair, asks, bids });
                    }
                    else
                    {
                        var asks = bookRoot.TryGetProperty("a", out _) ? ParseBookLevels(bookRoot, "a") : null;
                        var bids = bookRoot.TryGetProperty("b", out _) ? ParseBookLevels(bookRoot, "b") : null;
                        // Kraken can send two objects in the array for combined ask+bid updates
                        List<decimal[]>? asks2 = null, bids2 = null;
                        if (elements.Count >= 5)
                        {
                            var bookJson2 = elements[2].ToString();
                            using var bookDoc2 = JsonDocument.Parse(bookJson2!);
                            var bookRoot2 = bookDoc2.RootElement;
                            if (bookRoot2.TryGetProperty("a", out _)) asks2 = ParseBookLevels(bookRoot2, "a");
                            if (bookRoot2.TryGetProperty("b", out _)) bids2 = ParseBookLevels(bookRoot2, "b");
                        }
                        var finalAsks = asks2 ?? asks;
                        var finalBids = bids2 ?? bids;
                        if (asks != null && asks2 != null) finalAsks = asks2; // prefer the second if both
                        if (bids != null && bids2 != null) finalBids = bids2;
                        _ = _hub.Clients.All.SendAsync("BookUpdate", new { pair, asks = finalAsks, bids = finalBids });
                    }
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "[WS V1] Error parsing book"); }
            return;
        }

        if (!message.Contains("ticker")) return;

        try
        {
            var elements = JsonSerializer.Deserialize<List<object>>(message);
            if (elements == null || elements.Count < 4) return;

            var tickerData = JsonSerializer.Deserialize<TickerRawData>(elements[1].ToString()!, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var channelName = elements[2].ToString();
            var pair = elements[3].ToString();

            if (channelName != "ticker" || tickerData == null || pair == null) return;

            var priceItem = _state.GetOrAddPrice(pair);

            // Build a DerivedKline from ticker
            var now = DateTime.UtcNow;
            var kline = new DerivedKline
            {
                Interval = "OneMinute",
                Asset = pair,
                OpenTime = now,
                Open = tickerData.o?.FirstOrDefault() ?? 0,
                High = tickerData.h?.FirstOrDefault() ?? 0,
                Low = tickerData.l?.FirstOrDefault() ?? 0,
                Close = tickerData.c?.FirstOrDefault() ?? 0,
                VolumeWeightedAveragePrice = tickerData.p?.FirstOrDefault() ?? 0,
                Volume = tickerData.v?.FirstOrDefault() ?? 0,
                TradeCount = tickerData.t?.FirstOrDefault() ?? 0,
                Key = $"{pair}_{now.Ticks}"
            };

            priceItem.AddKline(kline);
            priceItem.TickerData = new TickerDataItem
            {
                BestAskPrice = tickerData.a?.FirstOrDefault() ?? 0,
                BestBidPrice = tickerData.b?.FirstOrDefault() ?? 0,
                LastTradePrice = tickerData.c?.FirstOrDefault() ?? 0,
                OpenPrice = tickerData.o?.FirstOrDefault() ?? 0,
                HighPrice = tickerData.h?.FirstOrDefault() ?? 0,
                LowPrice = tickerData.l?.FirstOrDefault() ?? 0,
                Volume = tickerData.v?.FirstOrDefault() ?? 0,
                VolumeWeightedAvgPrice = tickerData.p?.FirstOrDefault() ?? 0,
                TradeCount = tickerData.t?.FirstOrDefault() ?? 0
            };

            // Push to SignalR clients
            var latest = priceItem.LatestKline;
            _ = _hub.Clients.All.SendAsync("TickerUpdate", new
            {
                symbol = pair,
                closePrice = latest?.Close,
                openPrice = latest?.Open,
                highPrice = latest?.High,
                lowPrice = latest?.Low,
                volume = latest?.Volume,
                openTime = latest?.OpenTime,
                bestAsk = tickerData.a?.FirstOrDefault(),
                bestBid = tickerData.b?.FirstOrDefault()
            }).ContinueWith(t => { if (t.IsFaulted) _logger.LogWarning(t.Exception, "[WS V1] TickerUpdate broadcast failed"); }, TaskContinuationOptions.OnlyOnFaulted);

            // Run auto-order check
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _autoOrder.CheckAsync(priceItem, "Default Rule");
                    _state.AutoOrders[result.Symbol] = result;
                    await _hub.Clients.All.SendAsync("AutoTradeUpdate", result);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[WS V1] Auto-order check failed for {Symbol}", pair); }
            });

            // Update order distances in memory
            var tickerBase = TradingStateService.NormalizeAsset(priceItem.Base);
            var tickerQuote = TradingStateService.NormalizeAsset(priceItem.CCY);
            var tickerClose = priceItem.BestKline?.Close ?? 0;
            _ = Task.Run(async () =>
            {
                if (tickerClose <= 0) return;
                foreach (var order in _state.Orders.Values.Where(o =>
                    TradingStateService.IsOpenOrderStatus(o.Status) &&
                    _state.NormalizeOrderSymbolBase(o.Symbol) == tickerBase &&
                    _state.NormalizeOrderSymbolQuote(o.Symbol) == tickerQuote))
                {
                    order.LatestPrice = tickerClose;
                    order.Distance = order.Price - tickerClose;
                    order.DistancePercentage = Math.Round((order.Price - tickerClose) / (tickerClose / 100), 2);
                    _ordersDirty = true;

                    // Pushover notification when order is within configured threshold of current price
                    if (_state.OrderProximityNotifications
                        && Math.Abs(order.DistancePercentage) < _state.OrderProximityThreshold
                        && !_state.HasNotified(order.Id))
                    {
                        _state.AddNotified(order.Id);
                        _ = _notifications.Pushover(
                            $"{order.Symbol} {tickerClose} is <{_state.OrderProximityThreshold}% from order price",
                            $"{order.Symbol} {order.Side} @{order.Price} near");
                    }
                }

                // Recalculate balance values with updated price
                var usdGbpRate = _state.GetUsdGbpRate();
                foreach (var balance in _state.Balances.Values)
                {
                    var latestPrice = _state.LatestPrice(balance.Asset);
                    if (latestPrice != null)
                    {
                        balance.LatestPrice = latestPrice.Close;
                        balance.LatestValue = Math.Round(balance.Total * latestPrice.Close, 2);
                        balance.LatestValueGbp = usdGbpRate > 0 ? Math.Round(balance.LatestValue * usdGbpRate, 2) : 0;
                        _balancesDirty = true;
                    }
                }

                // Recalculate portfolio percentages
                var totalPortfolioValue = _state.Balances.Values.Sum(b => b.LatestValue);
                if (totalPortfolioValue > 0)
                {
                    foreach (var balance in _state.Balances.Values)
                    {
                        balance.PortfolioPercentage = Math.Round(balance.LatestValue / totalPortfolioValue * 100, 2);
                    }
                }

                // Throttle broadcasts to at most every 5 seconds
                var nowTicks = DateTime.UtcNow.Ticks;
                if (_ordersDirty && (nowTicks - Interlocked.Read(ref _lastOrderBroadcastTicks)) >= TimeSpan.FromSeconds(5).Ticks)
                {
                    _ordersDirty = false;
                    Interlocked.Exchange(ref _lastOrderBroadcastTicks, nowTicks);
                    try { await _hub.Clients.All.SendAsync("OrderUpdate", _state.Orders.Values.ToList()); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[WS V1] OrderUpdate broadcast failed"); }
                }
                if (_balancesDirty && (nowTicks - Interlocked.Read(ref _lastBalanceBroadcastTicks)) >= TimeSpan.FromSeconds(5).Ticks)
                {
                    _balancesDirty = false;
                    Interlocked.Exchange(ref _lastBalanceBroadcastTicks, nowTicks);
                    try { await _hub.Clients.All.SendAsync("BalanceUpdate", _state.Balances.Values.ToList()); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[WS V1] BalanceUpdate broadcast failed"); }
                }
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "[WS V1] Error parsing ticker"); }
    }

    private static List<decimal[]> ParseBookLevels(JsonElement root, string key)
    {
        var levels = new List<decimal[]>();
        if (!root.TryGetProperty(key, out var arr)) return levels;
        foreach (var entry in arr.EnumerateArray())
        {
            var items = entry.EnumerateArray().ToList();
            if (items.Count >= 2)
            {
                decimal.TryParse(items[0].GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var price);
                decimal.TryParse(items[1].GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var volume);
                levels.Add(new[] { price, volume });
            }
        }
        return levels;
    }

    private void Ping()
    {
        try { _socket?.Send(Encoding.UTF8.GetBytes("{\"event\":\"ping\"}")); }
        catch (Exception ex) { _logger.LogError(ex, "[WS V1] Ping error"); }
    }

    public override void Dispose()
    {
        _pingTimer?.Stop();
        _pingTimer?.Dispose();
        _socket?.Dispose();
        base.Dispose();
    }
}

// Helper class for deserializing Kraken ticker JSON arrays
// Kraken sends decimal values as JSON strings, so we need a custom converter
public class TickerRawData
{
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalListConverter))]
    public List<decimal>? a { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalListConverter))]
    public List<decimal>? b { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalListConverter))]
    public List<decimal>? c { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalListConverter))]
    public List<decimal>? v { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalListConverter))]
    public List<decimal>? p { get; set; }
    public List<int>? t { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalListConverter))]
    public List<decimal>? l { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalListConverter))]
    public List<decimal>? h { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(FlexibleDecimalListConverter))]
    public List<decimal>? o { get; set; }
}

public class FlexibleDecimalListConverter : System.Text.Json.Serialization.JsonConverter<List<decimal>>
{
    public override List<decimal> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<decimal>();
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                break;

            if (reader.TokenType == JsonTokenType.String)
            {
                if (decimal.TryParse(reader.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    result.Add(value);
            }
            else if (reader.TokenType == JsonTokenType.Number)
            {
                result.Add(reader.GetDecimal());
            }
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<decimal> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var v in value)
            writer.WriteNumberValue(v);
        writer.WriteEndArray();
    }
}
