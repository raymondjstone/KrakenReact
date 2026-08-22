using System.Linq.Expressions;
using KrakenReact.Server.Models;
using KrakenReact.Server.Services;
using Kraken.Net.Objects.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Transactions;

namespace KrakenReact.Server.Data;

public class DbMethods
{
    private readonly IDbContextFactory<KrakenDbContext> _factory;
    private readonly ILogger<DbMethods> _logger;
    private readonly SqlTimeoutDiagnostics _sqlDiag;

    public DbMethods(IDbContextFactory<KrakenDbContext> factory, ILogger<DbMethods> logger, SqlTimeoutDiagnostics sqlDiag)
    {
        _factory = factory;
        _logger = logger;
        _sqlDiag = sqlDiag;
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
                _logger.LogError(ex, "Connection pool exhausted");
                throw;
            }
            catch (Exception ex) when (SqlTimeoutDiagnostics.IsSqlTimeout(ex))
            {
                _sqlDiag.CaptureIfTimeout("DbMethods.UseDbContext", ex);
                throw;
            }
            finally
            {
                if (context is not null) await context.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Inserts or updates a list of entities, keyed by the given property.
    /// <para>
    /// The batch's existing rows are fetched in a single <c>WHERE key IN (...)</c> query rather than
    /// one lookup per item. The per-item form issued a round trip for every row on every sync, which
    /// against the closed-order history alone had accumulated some 794,000 index seeks — for a table
    /// holding under two thousand rows.
    /// </para>
    /// <para>
    /// The key selector is an expression rather than a delegate so the same lambda can both build
    /// that query and be compiled for matching in memory.
    /// </para>
    /// </summary>
    public async Task UpsertListAsync<TEntity, TKey>(
        IEnumerable<TEntity> items,
        Expression<Func<TEntity, TKey>> keySelector,
        Action<TEntity, TEntity>? updateValues = null) where TEntity : class
    {
        var list = items.ToList();
        if (list.Count == 0) return;

        var readKey = keySelector.Compile();

        // Process in small batches so each SaveChangesAsync transaction touches at most
        // 50 rows. A single large transaction over hundreds of rows caused SQL Server
        // lock escalation to a table lock, blocking all concurrent readers.
        const int batchSize = 50;
        for (int i = 0; i < list.Count; i += batchSize)
        {
            var batch = list.Skip(i).Take(batchSize).ToList();
            await UseDbContextAsync(async context =>
            {
                var set = context.Set<TEntity>();

                // One query for the whole batch: WHERE key IN (...). The rows come back tracked, so
                // assigning to them below still produces an UPDATE exactly as the per-item form did.
                var keys = batch.Select(readKey).Distinct().ToList();
                var existingRows = await set.Where(BuildKeyInPredicate(keySelector, keys)).ToListAsync();

                var existingByKey = new Dictionary<TKey, TEntity>();
                foreach (var row in existingRows) existingByKey[readKey(row)] = row;

                foreach (var item in batch)
                {
                    if (existingByKey.TryGetValue(readKey(item), out var existing))
                    {
                        if (updateValues != null) updateValues(existing, item);
                        else context.Entry(existing).CurrentValues.SetValues(item);
                    }
                    else
                    {
                        await set.AddAsync(item);
                        // A batch containing the same key twice would otherwise insert it twice and
                        // trip the unique-violation catch below, silently losing the whole batch.
                        existingByKey[readKey(item)] = item;
                    }
                }

                try { await context.SaveChangesAsync(); }
                catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601)) { }
                return true;
            });
        }
    }

    /// <summary>Builds <c>e =&gt; keys.Contains(e.Key)</c> from a key selector, for a batched lookup.</summary>
    private static Expression<Func<TEntity, bool>> BuildKeyInPredicate<TEntity, TKey>(
        Expression<Func<TEntity, TKey>> keySelector, List<TKey> keys)
    {
        var contains = Expression.Call(
            typeof(Enumerable), nameof(Enumerable.Contains), [typeof(TKey)],
            Expression.Constant(keys), keySelector.Body);
        return Expression.Lambda<Func<TEntity, bool>>(contains, keySelector.Parameters[0]);
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

    // ── Transaction history cache ──────────────────────────────────────────
    //
    // Trades, ledgers and closed orders are read constantly — several endpoints load the whole lot
    // on every request, and the hourly sync reads all of them again just to find its high-water
    // mark. Measured against the live database that was over 17,000 full scans of each table.
    //
    // They are also tiny: under six megabytes for all three together, against a database of 625 MB.
    // Holding them in memory costs almost nothing and removes those scans entirely.
    //
    // Every write to these tables goes through this class, so each Add method drops the matching
    // cache. That is what keeps the copy honest: nothing else can change the table behind its back.
    private List<KrakenUserTrade>? _tradesCache;
    private List<KrakenLedgerEntry>? _ledgersCache;
    private List<CombinedOrder>? _combinedOrdersCache;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    /// <summary>
    /// Serves a cached table, loading it once on first use.
    /// <para>
    /// The lock is held across the load so a burst of concurrent callers on a cold cache produces one
    /// query rather than one each — which is precisely the moment this matters, at startup.
    /// </para>
    /// </summary>
    private async Task<List<T>> GetCachedAsync<T>(Func<List<T>?> read, Action<List<T>> write, Func<Task<List<T>>> load)
    {
        var cached = read();
        if (cached is not null) return cached;

        await _cacheLock.WaitAsync();
        try
        {
            cached = read();
            if (cached is not null) return cached;

            var loaded = await load();
            write(loaded);
            return loaded;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>Drops the cached copies, so the next read reloads from the database.</summary>
    public void InvalidateTransactionCaches()
    {
        _tradesCache = null;
        _ledgersCache = null;
        _combinedOrdersCache = null;
    }

    public Task<List<KrakenUserTrade>> GetTradesAsync() =>
        GetCachedAsync(() => _tradesCache, v => _tradesCache = v,
            () => UseDbContextAsync(async context => await context.Trades.AsNoTracking().OrderByDescending(c => c.Timestamp).ToListAsync()));

    public Task<List<KrakenLedgerEntry>> GetLedgersAsync() =>
        GetCachedAsync(() => _ledgersCache, v => _ledgersCache = v,
            () => UseDbContextAsync(context => context.Ledgers.AsNoTracking().OrderByDescending(c => c.Timestamp).ToListAsync()));


    public Task<List<DerivedKline>> GetKlineAsync(string asset) =>
        UseDbContextAsync(context => context.DerivedKlines.Where(k => k.Asset == asset).AsNoTracking().OrderBy(c => c.OpenTime).ToListAsync());

    public Task<List<CombinedOrder>> GetCombinedOrdersAsync() =>
        GetCachedAsync(() => _combinedOrdersCache, v => _combinedOrdersCache = v,
            () => UseDbContextAsync(context => context.CombinedOrders.AsNoTracking().OrderByDescending(c => c.CloseTime ?? DateTime.MaxValue).ToListAsync()));

    public Task<List<KrakenSymbol>> GetSymbolsAsync() =>
        UseDbContextAsync(context => context.Symbols.AsNoTracking().ToListAsync());

    public Task<List<KrakenBalanceAvailable>> GetBalancesAsync() =>
        UseDbContextAsync(context => context.Balances.AsNoTracking().OrderByDescending(c => c.Asset).ToListAsync());

    // --- Add Methods ---
    public async Task AddTradesAsync(List<KrakenUserTrade> trades)
    {
        await UpsertListAsync(trades, t => t.Id);
        _tradesCache = null;
    }

    public async Task AddLedgersAsync(List<KrakenLedgerEntry> list)
    {
        await UpsertListAsync(list, k => k.Id);
        _ledgersCache = null;
    }

    public Task AddBalancesAsync(List<KrakenBalanceAvailable> list) => UpsertListAsync(list, b => b.Asset);

    public async Task AddKlineAsync(List<DerivedKline> list)
    {
        var deduped = list
            .Where(i => i.Interval == "OneDay")
            .GroupBy(k => k.Key)
            .Select(g => g.First())
            .ToList();
        if (deduped.Count == 0) return;

        // One round-trip to load existing keys instead of N FindAsync calls.
        // Daily klines are immutable once stored, so INSERT-only is correct here.
        await using var db = await _factory.CreateDbContextAsync();
        var keys = deduped.Select(k => k.Key).ToList();
        var existing = await db.DerivedKlines
            .Where(k => keys.Contains(k.Key))
            .Select(k => k.Key)
            .ToHashSetAsync();

        var toAdd = deduped.Where(d => !existing.Contains(d.Key)).ToList();
        if (toAdd.Count == 0) return;

        const int batchSize = 100;
        for (int i = 0; i < toAdd.Count; i += batchSize)
        {
            await using var batchDb = await _factory.CreateDbContextAsync();
            batchDb.DerivedKlines.AddRange(toAdd.Skip(i).Take(batchSize));
            try { await batchDb.SaveChangesAsync(); }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx &&
                (sqlEx.Number == 2627 || sqlEx.Number == 2601)) { }
        }
    }

    public Task AddCombinedOrdersAsync(List<CombinedOrder> list)
    {
        foreach (var item in list)
        {
            item.Reason ??= string.Empty;
            item.ReferenceId ??= string.Empty;
        }
        return UpsertCombinedOrdersAsync(list);
    }

    private async Task UpsertCombinedOrdersAsync(List<CombinedOrder> list)
    {
        await UpsertListAsync(list, o => o.Id);
        _combinedOrdersCache = null;
    }

    public Task AddSymbolsAsync(List<KrakenSymbol> list) => UpsertListAsync(list, s => s.WebsocketName);
}
