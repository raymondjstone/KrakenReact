using Kraken.Net.Enums;
using Kraken.Net.Objects.Models;
using KrakenReact.Server.Services;

namespace KrakenReact.Server.Tax;

/// <summary>
/// Follows token migrations through the ledger, so a coin that changed ticker keeps one continuous
/// Section 104 pool.
/// <para>
/// A migration is not a disposal — nothing was sold and no gain arose — but in the raw data it looks
/// like the old asset vanished and a new one appeared from nowhere. Left alone that produces
/// disposals of the new ticker with no acquisition behind them, and an orphaned pool under the old
/// one. MATIC becoming POL is the case that surfaced this.
/// </para>
/// </summary>
public static class AssetMigrations
{
    /// <summary>
    /// Detects migrations and returns a map from each retired ticker to the one it became.
    /// <para>
    /// The signature is a pair of plain transfers — no subtype — where the whole of one asset leaves
    /// and the identical quantity of a different asset arrives at or after the same moment.
    /// </para>
    /// </summary>
    public static Dictionary<string, string> Detect(IEnumerable<KrakenLedgerEntry> ledgers)
    {
        var transfers = ledgers
            .Where(l => l.Type == LedgerEntryType.Transfer && string.IsNullOrEmpty(l.SubType))
            .OrderBy(l => l.Timestamp)
            .ToList();

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>();

        foreach (var from in transfers.Where(l => l.Quantity < 0))
        {
            string fromAsset = TradingStateService.NormalizeAsset(from.Asset);

            var to = transfers.FirstOrDefault(l =>
                l.Quantity == -from.Quantity &&
                l.Timestamp >= from.Timestamp &&
                !string.Equals(TradingStateService.NormalizeAsset(l.Asset), fromAsset, StringComparison.OrdinalIgnoreCase) &&
                !claimed.Contains(l.Id));

            if (to == null) continue;

            claimed.Add(to.Id);
            map.TryAdd(fromAsset, TradingStateService.NormalizeAsset(to.Asset));
        }

        return map;
    }

    /// <summary>
    /// Resolves a ticker to the one it eventually became, following a chain of renames.
    /// The visited set stops a cycle in malformed data from spinning forever.
    /// </summary>
    public static string Resolve(string asset, IReadOnlyDictionary<string, string> migrations)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { asset };
        while (migrations.TryGetValue(asset, out var next) && visited.Add(next))
            asset = next;
        return asset;
    }

    /// <summary>The ledger ids taking part in a migration, which must not be treated as income.</summary>
    public static HashSet<string> MigrationEntryIds(IEnumerable<KrakenLedgerEntry> ledgers)
    {
        var transfers = ledgers
            .Where(l => l.Type == LedgerEntryType.Transfer && string.IsNullOrEmpty(l.SubType))
            .OrderBy(l => l.Timestamp)
            .ToList();

        var ids = new HashSet<string>();
        foreach (var from in transfers.Where(l => l.Quantity < 0))
        {
            string fromAsset = TradingStateService.NormalizeAsset(from.Asset);
            var to = transfers.FirstOrDefault(l =>
                l.Quantity == -from.Quantity &&
                l.Timestamp >= from.Timestamp &&
                !string.Equals(TradingStateService.NormalizeAsset(l.Asset), fromAsset, StringComparison.OrdinalIgnoreCase) &&
                !ids.Contains(l.Id));
            if (to == null) continue;
            ids.Add(from.Id);
            ids.Add(to.Id);
        }
        return ids;
    }
}
