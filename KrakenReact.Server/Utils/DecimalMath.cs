namespace KrakenReact.Server.Utils;

public static class DecimalMath
{
    /// <summary>Exact 10^n as a decimal (Math.Pow goes through double). Clamped to decimal's 28-digit range.</summary>
    public static decimal Pow10(int n)
    {
        var f = 1m;
        for (var i = 0; i < Math.Min(n, 28); i++) f *= 10m;
        return f;
    }

    /// <summary>Rounds toward zero at the given number of decimals — never up, so a quantity can't exceed a
    /// budget or a balance. A negative <paramref name="decimals"/> leaves the value untouched.</summary>
    public static decimal FloorToDecimals(decimal value, int decimals)
    {
        if (decimals < 0) return value;
        var factor = Pow10(decimals);
        return Math.Floor(value * factor) / factor;
    }
}
