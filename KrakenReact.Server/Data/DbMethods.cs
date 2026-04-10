using KrakenReact.Server.Models;
using Kraken.Net.Objects.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Transactions;

namespace KrakenReact.Server.Data;

public class DbMethods
{
    private readonly IDbContextFactory<KrakenDbContext> _factory;

    public DbMethods(IDbContextFactory<KrakenDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<T> UseDbContextAsync<T>(Func<KrakenDbContext, Task<T>> dbOperation, int maxRetries = 3, int delayMs = 500)
    {
        using var scope = new TransactionScope(TransactionScopeOption.Suppress, TransactionScopeAsyncFlowOption.Enabled);
        int attempt = 0;
        while (true)
        {
            KrakenDbContext? context = null;
            try
            {
                context = await _factory.CreateDbContextAsync();
                var result = await dbOperation(context);
                scope.Complete();
                return result;
            }
            catch (SqlException ex) when (ex.Number == 1205)
            {
                if (++attempt >= maxRetries) throw;
                await Task.Delay(delayMs * attempt);
            }
            catch (SqlException ex) when (ex.Message.Contains("Timeout expired") && ex.Message.Contains("obtaining a connection from the pool"))
            {
                Console.WriteLine("Connection pool exhausted: " + ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return default;
            }
            finally
            {
                if (context is not null) await context.DisposeAsync();
            }
        }
    }

    public async Task UpsertListAsync<TEntity, TKey>(IEnumerable<TEntity> items, Func<TEntity, TKey> keySelector, Action<TEntity, TEntity>? updateValues = null) where TEntity : class
    {
        if (!items.Any()) return;
        await UseDbContextAsync(async context =>
        {
            var set = context.Set<TEntity>();
            foreach (var item in items)
            {
                var key = keySelector(item);
                var existing = await set.FindAsync(key);
                if (existing == null)
                    await set.AddAsync(item);
                else
                {
                    if (updateValues != null) updateValues(existing, item);
                    else context.Entry(existing).CurrentValues.SetValues(item);
                }
            }
            try { await context.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601)) { }
            return true;
        });
    }

    // --- Get Methods ---
    public async Task<AppCreds?> GetCredentialsAsync(string id = "KrakenDefault")
    {
        // Try new AppSettings table first
        var apiKey = await UseDbContextAsync(context => context.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "KrakenApiKey"));
        var apiSecret = await UseDbContextAsync(context => context.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "KrakenApiSecret"));

        if (apiKey != null && apiSecret != null && !string.IsNullOrEmpty(apiKey.Value) && !string.IsNullOrEmpty(apiSecret.Value))
        {
            return new AppCreds { id = "kraken", appkey = apiKey.Value, appsecret = apiSecret.Value };
        }

        // Fallback to old EFAppCreds table
        var efCreds = await UseDbContextAsync(context => context.AppCreds.AsNoTracking().FirstOrDefaultAsync(c => c.id == id));
        if (efCreds == null) return null;
        return new AppCreds { id = efCreds.id, appkey = efCreds.appkey, appsecret = efCreds.appsecret };
    }

    private AppCreds? _pushover;
    private readonly SemaphoreSlim _pushoverLock = new(1, 1);
    public async Task<AppCreds?> GetPushoverCredentialsAsync(string id = "Pushover")
    {
        if (_pushover != null) return _pushover;
        await _pushoverLock.WaitAsync();
        try
        {
            if (_pushover != null) return _pushover;

            // Try new AppSettings table first
            var userKey = await UseDbContextAsync(context => context.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "PushoverUserKey"));
            var appToken = await UseDbContextAsync(context => context.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == "PushoverAppToken"));

            if (userKey != null && appToken != null && !string.IsNullOrEmpty(userKey.Value) && !string.IsNullOrEmpty(appToken.Value))
            {
                _pushover = new AppCreds { id = "pushover", appkey = userKey.Value, appsecret = appToken.Value };
                return _pushover;
            }

            // Fallback to old EFAppCreds table
            var efCreds = await UseDbContextAsync(context => context.AppCreds.AsNoTracking().FirstOrDefaultAsync(c => c.id == id));
            if (efCreds == null) return null;
            _pushover = new AppCreds { id = efCreds.id, appkey = efCreds.appkey, appsecret = efCreds.appsecret };
            return _pushover;
        }
        finally { _pushoverLock.Release(); }
    }

    public Task<List<KrakenUserTrade>> GetTradesAsync() =>
        UseDbContextAsync(async context => await context.Trades.AsNoTracking().OrderByDescending(c => c.Timestamp).ToListAsync());

    public Task<List<KrakenLedgerEntry>> GetLedgersAsync() =>
        UseDbContextAsync(context => context.Ledgers.AsNoTracking().OrderByDescending(c => c.Timestamp).ToListAsync());

    public Task<List<DerivedKline>> GetKlineAsync(string asset) =>
        UseDbContextAsync(context => context.DerivedKlines.Where(k => k.Asset == asset).AsNoTracking().OrderBy(c => c.OpenTime).ToListAsync());

    public Task<List<CombinedOrder>> GetCombinedOrdersAsync() =>
        UseDbContextAsync(context => context.CombinedOrders.AsNoTracking().OrderByDescending(c => c.CloseTime ?? DateTime.MaxValue).ToListAsync());

    public Task<List<KrakenSymbol>> GetSymbolsAsync() =>
        UseDbContextAsync(context => context.Symbols.AsNoTracking().ToListAsync());

    public Task<List<KrakenBalanceAvailable>> GetBalancesAsync() =>
        UseDbContextAsync(context => context.Balances.AsNoTracking().OrderByDescending(c => c.Asset).ToListAsync());

    // --- Add Methods ---
    public Task AddTradesAsync(List<KrakenUserTrade> trades) => UpsertListAsync(trades, t => t.Id);
    public Task AddLedgersAsync(List<KrakenLedgerEntry> list) => UpsertListAsync(list, k => k.Id);
    public Task AddBalancesAsync(List<KrakenBalanceAvailable> list) => UpsertListAsync(list, b => b.Asset);

    public Task AddKlineAsync(List<DerivedKline> list)
    {
        var deduped = list.Where(i => i.Interval == "OneDay").OrderBy(k => k.Key).GroupBy(k => k.Key).Select(g => g.First()).ToList();
        return UpsertListAsync(deduped, k => k.Key);
    }

    public Task AddCombinedOrdersAsync(List<CombinedOrder> list)
    {
        foreach (var item in list)
        {
            item.Reason ??= string.Empty;
            item.ReferenceId ??= string.Empty;
        }
        return UpsertListAsync(list, o => o.Id);
    }

    public Task AddSymbolsAsync(List<KrakenSymbol> list) => UpsertListAsync(list, s => s.WebsocketName);
}
