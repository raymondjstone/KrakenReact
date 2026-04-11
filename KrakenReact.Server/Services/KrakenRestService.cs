using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using Kraken.Net.Clients;
using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using Kraken.Net.Objects.Models.Socket;
using KrakenReact.Server.Data;
using KrakenReact.Server.Models;

namespace KrakenReact.Server.Services;

public class KrakenRestService
{
    private readonly DbMethods _db;
    private readonly TradingStateService _state;
    private readonly ILogger<KrakenRestService> _logger;

    private const int MAX_RECORDS_RETURNED_PER_CALL = 50;
    private const int MAX_RECORDS_WANTED = 3501;
    private DateTime _lastUnauthCall = DateTime.Now;
    private int _sleep = 0;

    private ApiCredentials? _creds;
    private KrakenRestClient? _authClient;
    private DateTime _authClientWhen = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);
    private KrakenRestClient? _unauthClient;
    private DateTime _unauthWhen = DateTime.MinValue;
    private readonly SemaphoreSlim _unauthLock = new(1, 1);

    public KrakenRestService(DbMethods db, TradingStateService state, ILogger<KrakenRestService> logger)
    {
        _db = db;
        _state = state;
        _logger = logger;
    }

    private async Task RateLimitUnAuthenticatedCalls()
    {
        var now = DateTime.Now;
        if (now < _lastUnauthCall.AddSeconds(2))
            await Task.Delay(1000);
        _lastUnauthCall = now;
    }

    private void ResetSleep() => _sleep = 2000;
    private void MoreSleep() => _sleep += 2000;

    private async Task<bool> HandleErrors(CryptoExchange.Net.Objects.Error? error)
    {
        if (error?.Message == "EAPI:Rate limit exceeded")
        {
            await Task.Delay(_sleep);
            MoreSleep();
            return true;
        }
        if (error?.Message?.Contains("timed out") == true)
        {
            MoreSleep();
            await Task.Delay(_sleep);
            MoreSleep();
            return true;
        }
        _logger.LogError("Error fetching data: {Error}", error);
        return false;
    }

    public async Task<KrakenRestClient> AuthenticatedClient()
    {
        ResetSleep();
        await _authLock.WaitAsync();
        try
        {
            if (_creds == null)
            {
                var appcreds = await _db.GetCredentialsAsync();
                _creds = new ApiCredentials(appcreds!.appkey, appcreds.appsecret);
            }
            if (_authClientWhen < DateTime.Now.AddHours(-1))
            {
                _authClient?.Dispose();
                _authClient = new KrakenRestClient(options =>
                {
                    options.ApiCredentials = _creds;
                    options.RequestTimeout = TimeSpan.FromSeconds(180);
                });
                _authClientWhen = DateTime.Now;
            }
            return _authClient!;
        }
        finally { _authLock.Release(); }
    }

    public async Task<KrakenRestClient> UnAuthenticatedClient()
    {
        await RateLimitUnAuthenticatedCalls();
        await _unauthLock.WaitAsync();
        try
        {
            if (DateTime.UtcNow.AddMinutes(-20) > _unauthWhen || _unauthClient == null)
            {
                _unauthClient?.Dispose();
                _unauthWhen = DateTime.UtcNow;
                _unauthClient = new KrakenRestClient();
            }
            return _unauthClient;
        }
        finally { _unauthLock.Release(); }
    }

    public async Task GetInstrumentsAsync(bool initialLoad)
    {
        var dbItems = (await _db.GetSymbolsAsync()).ToList();
        foreach (var s in dbItems) _state.Symbols[s.WebsocketName] = s;

        if (initialLoad) return;

        var restClient = await UnAuthenticatedClient();
        try
        {
            var result = await restClient.SpotApi.ExchangeData.GetSymbolsAsync();
            if (result.Success)
            {
                var newrecs = new List<KrakenSymbol>();
                foreach (var r in result.Data)
                {
                    var item = r.Value;
                    _state.Symbols[item.WebsocketName] = item;
                    if (!dbItems.Any(i => i.WebsocketName == item.WebsocketName && i.LotDecimals == item.LotDecimals && i.MinValue == item.MinValue && i.Status == item.Status))
                        newrecs.Add(item);
                }
                if (newrecs.Any()) await _db.AddSymbolsAsync(newrecs);
            }
            else _logger.LogError("Error fetching symbols: {Error}", result.Error);
        }
        catch (Exception ex) { _logger.LogError(ex, "Exception fetching symbols"); }
    }

    public async Task<IEnumerable<KrakenKline>> GetKlinesAsync(string currencyPair, KlineInterval interval, DateTime? since)
    {
        if (string.IsNullOrWhiteSpace(currencyPair) || TradingStateService.BadPairs.Contains(currencyPair))
            return new List<KrakenKline>();

        // Check cache for a previously resolved API pair name
        if (_state.ApiPairNameCache.TryGetValue(currencyPair, out var cachedName))
        {
            var cached = await FetchKlinesInternal(cachedName, interval, since);
            if (cached.Any()) return cached;
            // Cache entry stale — remove and re-resolve
            _state.ApiPairNameCache.TryRemove(currencyPair, out _);
        }

        // Try the original pair name first
        var result = await FetchKlinesInternal(currencyPair, interval, since);
        if (result.Any())
        {
            _state.ApiPairNameCache[currencyPair] = currencyPair;
            return result;
        }

        // Try all candidate names (normalized, alternate, with/without slash)
        var candidates = _state.GetApiPairCandidates(currencyPair);
        foreach (var candidate in candidates)
        {
            if (candidate.Equals(currencyPair, StringComparison.OrdinalIgnoreCase)) continue; // already tried
            result = await FetchKlinesInternal(candidate, interval, since);
            if (result.Any())
            {
                _logger.LogInformation("Resolved API pair name: {Original} -> {Working}", currencyPair, candidate);
                _state.ApiPairNameCache[currencyPair] = candidate;
                return result;
            }
        }

        _logger.LogWarning("No kline data found for {Pair} after trying {Count} candidates", currencyPair, candidates.Count + 1);
        return new List<KrakenKline>();
    }

    private async Task<IEnumerable<KrakenKline>> FetchKlinesInternal(string currencyPair, KlineInterval interval, DateTime? since)
    {
        var restClient = await UnAuthenticatedClient();
        int wait = 1000;
        const int maxRetries = 3;
        int retryCount = 0;
        try
        {
            var result = await restClient.SpotApi.ExchangeData.GetKlinesAsync(currencyPair, interval, since);
            if (!result.Success && (result.Error?.Message == "EQuery:Unknown asset pair" || result.Error?.Message == "EQuery:Invalid asset pair"))
                return new List<KrakenKline>();

            while (!result.Success && retryCount < maxRetries)
            {
                retryCount++;
                await Task.Delay(wait);
                wait = Math.Min(wait + 3000, 30000);
                result = await restClient.SpotApi.ExchangeData.GetKlinesAsync(currencyPair, interval, since);
                if (!result.Success && (result.Error?.Message == "EQuery:Unknown asset pair" || result.Error?.Message == "EQuery:Invalid asset pair"))
                    return new List<KrakenKline>();
            }
            return result.Success ? result.Data.Data : new List<KrakenKline>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception fetching klines for {Pair}", currencyPair);
            return new List<KrakenKline>();
        }
    }

    /// <summary>Test a single pair name against the Kraken API, returns count of candles.</summary>
    public async Task<int> FetchKlinesInternalDirect(string currencyPair, KlineInterval interval, DateTime? since)
    {
        var result = await FetchKlinesInternal(currencyPair, interval, since);
        return result.Count();
    }

    public async Task<KrakenWebSocketToken?> GetWebSocketAsyncToken()
    {
        var krakenClient = await AuthenticatedClient();
        int wait = 500;
        for (int i = 0; i < 10; i++)
        {
            var r = await krakenClient.SpotApi.Account.GetWebsocketTokenAsync();
            if (r.Success && r.Data != null) return r.Data;
            await Task.Delay(wait);
            wait += 500;
        }
        return null;
    }

    public async Task<List<KrakenLedgerEntry>> GetLedgerAsync(bool initialLoad)
    {
        var dbItems = (await _db.GetLedgersAsync()).ToList();
        if (initialLoad) return dbItems;

        var starttime = dbItems.Any() ? dbItems.Max(a => a.Timestamp).AddHours(-4) : DateTime.Now.AddYears(-3);
        var krakenClient = await AuthenticatedClient();
        var data = new Dictionary<string, KrakenLedgerEntry>(MAX_RECORDS_WANTED);
        int recs = MAX_RECORDS_RETURNED_PER_CALL;
        while (recs >= MAX_RECORDS_RETURNED_PER_CALL && data.Count < MAX_RECORDS_WANTED)
        {
            var result = await krakenClient.SpotApi.Account.GetLedgerInfoAsync(null, null, null, starttime, null, data.Count);
            if (result.Success)
            {
                recs = result.Data.Ledger.Count;
                data = data.Concat(result.Data.Ledger).GroupBy(kvp => kvp.Key).ToDictionary(g => g.Key, g => g.Last().Value);
            }
            else
            {
                if (!(await HandleErrors(result.Error))) return dbItems;
                recs = MAX_RECORDS_RETURNED_PER_CALL;
            }
        }
        var newrecs = data.Values.Where(rec => !dbItems.Any(i => i.Id == rec.Id)).ToList();
        dbItems.AddRange(newrecs);
        if (newrecs.Any()) await _db.AddLedgersAsync(newrecs);
        return dbItems;
    }

    public async Task<List<CombinedOrder>> GetOrders(bool initialLoad)
    {
        var closed = await GetClosedOrdersAsync(initialLoad);
        var open = await GetOpenOrdersAsync(initialLoad);
        closed = closed.Where(c => !open.Any(o => o.Id == c.Id)).ToList();
        closed.AddRange(open);
        closed = closed.OrderByDescending(o => o.CreateTime).ToList();
        if (closed.Any()) await _db.AddCombinedOrdersAsync(closed);
        return closed;
    }

    public async Task<List<CombinedOrder>> GetOpenOrdersAsync(bool initialLoad)
    {
        if (initialLoad) return new List<CombinedOrder>();
        var krakenClient = await AuthenticatedClient();
        var data = new Dictionary<string, KrakenOrder>(MAX_RECORDS_WANTED);
        int recs = MAX_RECORDS_RETURNED_PER_CALL;
        while (recs >= MAX_RECORDS_RETURNED_PER_CALL && data.Count < MAX_RECORDS_WANTED)
        {
            var result = await krakenClient.SpotApi.Trading.GetOpenOrdersAsync();
            if (result.Success)
            {
                recs = result.Data.Open.Count;
                data = data.Concat(result.Data.Open).GroupBy(kvp => kvp.Key).ToDictionary(g => g.Key, g => g.Last().Value);
            }
            else
            {
                if (!(await HandleErrors(result.Error))) return new List<CombinedOrder>();
                recs = MAX_RECORDS_RETURNED_PER_CALL;
            }
        }
        return data.Values.Select(rec => new CombinedOrder(rec)).ToList();
    }

    public async Task<List<CombinedOrder>> GetClosedOrdersAsync(bool initialLoad)
    {
        var dbItems = await _db.GetCombinedOrdersAsync() ?? new List<CombinedOrder>();
        if (initialLoad) return dbItems;

        var krakenClient = await AuthenticatedClient();
        var data = new Dictionary<string, KrakenOrder>(MAX_RECORDS_WANTED);
        int recs = MAX_RECORDS_RETURNED_PER_CALL;
        while (recs >= MAX_RECORDS_RETURNED_PER_CALL && data.Count < MAX_RECORDS_WANTED)
        {
            var result = await krakenClient.SpotApi.Trading.GetClosedOrdersAsync(null, null, null, data.Count);
            if (result.Success)
            {
                recs = result.Data.Closed.Count;
                data = data.Concat(result.Data.Closed).GroupBy(kvp => kvp.Key).ToDictionary(g => g.Key, g => g.Last().Value);
            }
            else
            {
                if (!(await HandleErrors(result.Error))) return dbItems;
                recs = MAX_RECORDS_RETURNED_PER_CALL;
            }
        }
        return data.Values.Select(rec => new CombinedOrder(rec)).ToList();
    }

    public async Task<WebCallResult<KrakenPlacedOrder>> PlaceOrderAsync(string symbol, OrderSide side, OrderType orderType, decimal qty, decimal price, string clientOrderId)
    {
        var krakenClient = await AuthenticatedClient();
        return await krakenClient.SpotApi.Trading.PlaceOrderAsync(
            symbol, side, orderType, qty, price,
            null, null, null, null, false, null, clientOrderId,
            new List<OrderFlags> { OrderFlags.PostOnly },
            null, TimeInForce.GTC);
    }

    public async Task<List<KrakenUserTrade>> GetTradesAsync(bool initialLoad)
    {
        var dbItems = (await _db.GetTradesAsync()).ToList();
        if (initialLoad) return dbItems;

        var starttime = dbItems.Any() ? dbItems.Max(a => a.Timestamp).AddHours(-4) : DateTime.Now.AddYears(-3);
        var krakenClient = await AuthenticatedClient();
        var data = new Dictionary<string, KrakenUserTrade>(MAX_RECORDS_WANTED);
        int recs = MAX_RECORDS_RETURNED_PER_CALL;
        while (recs >= MAX_RECORDS_RETURNED_PER_CALL && data.Count < MAX_RECORDS_WANTED)
        {
            var result = await krakenClient.SpotApi.Trading.GetUserTradesAsync(starttime, null, data.Count);
            if (result.Success)
            {
                recs = result.Data.Trades.Count;
                data = data.Concat(result.Data.Trades).GroupBy(kvp => kvp.Key).ToDictionary(g => g.Key, g => g.Last().Value);
            }
            else
            {
                if (!(await HandleErrors(result.Error))) return dbItems;
                recs = MAX_RECORDS_RETURNED_PER_CALL;
            }
        }
        var newrecs = data.Values.Where(rec => !dbItems.Any(i => i.Id == rec.Id)).ToList();
        dbItems.AddRange(newrecs);
        if (newrecs.Any()) await _db.AddTradesAsync(newrecs);
        return dbItems;
    }

    public async Task<List<KrakenBalanceAvailable>> GetAvailableBalancesAsync(bool initialLoad)
    {
        var dbItems = (await _db.GetBalancesAsync()).ToList();
        if (initialLoad) return dbItems;

        var krakenClient = await AuthenticatedClient();
        var result = await krakenClient.SpotApi.Account.GetAvailableBalancesAsync();
        if (result.Success)
        {
            var newrecs = result.Data.Values.ToList();
            if (newrecs.Any()) await _db.AddBalancesAsync(newrecs);
            return newrecs;
        }
        _logger.LogError("Error fetching balances: {Error}", result.Error);
        return dbItems;
    }

    public async Task<WebCallResult<KrakenEditOrder>> AmendOrderValues(string orderId, string symbol, decimal newPrice, decimal newQty)
    {
        var krakenClient = await AuthenticatedClient();
        var x = await krakenClient.SpotApi.Trading.EditOrderAsync(symbol, orderId, newQty, null, newPrice);
        return x;
    }

    public async Task<bool> CancelOrderAsync(string orderId)
    {
        var krakenClient = await AuthenticatedClient();
        var result = await krakenClient.SpotApi.Trading.CancelOrderAsync(orderId);
        return result.Success;
    }
}
