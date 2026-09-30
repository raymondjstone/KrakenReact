namespace KrakenReact.Server.Services;

/// <summary>
/// A fat-finger check for manually entered orders. A limit order priced THROUGH the market (a buy above it, a sell below
/// it) executes immediately at the far end of that range, so a slipped decimal or a stale price on screen can cost a lot in
/// a single click. Orders resting on the right side of the market (a buy below, a sell above) are the normal case and are
/// never questioned.
/// </summary>
public static class OrderPriceGuard
{
    /// <summary>How far through the market an order may be before it must be confirmed (config: Orders:PriceGuardPct; 0 disables).</summary>
    public static decimal MaxDeviationPct { get; set; } = 5m;

    /// <summary>
    /// How many percent through the market the price is: positive when the order would cross it (buy above / sell below),
    /// zero or negative when it rests on the right side. Null when there is no usable market price to compare with.
    /// </summary>
    public static decimal? ThroughMarketPct(string side, decimal price, decimal market)
    {
        if (market <= 0 || price <= 0) return null;
        return side.Equals("Buy", StringComparison.OrdinalIgnoreCase)
            ? (price - market) / market * 100m
            : (market - price) / market * 100m;
    }

    public static bool IsSuspicious(string side, decimal price, decimal market, decimal? maxPct = null)
    {
        var limit = maxPct ?? MaxDeviationPct;
        if (limit <= 0) return false; // disabled
        var through = ThroughMarketPct(side, price, market);
        return through != null && through.Value > limit;
    }
}
