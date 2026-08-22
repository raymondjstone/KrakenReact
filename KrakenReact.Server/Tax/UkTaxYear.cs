namespace KrakenReact.Server.Tax;

/// <summary>
/// A UK tax year, which runs 6 April to 5 April inclusive. Named by its starting calendar year, so
/// <c>2024</c> is 6 April 2024 to 5 April 2025 — the convention HMRC's own "2024-25" label follows.
/// </summary>
public sealed record UkTaxYear(int StartYear)
{
    public DateTime InclusiveStart => new(StartYear, 4, 6, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The first instant of the following tax year, so a date belongs when start ≤ d &lt; end.</summary>
    public DateTime ExclusiveEnd => new(StartYear + 1, 4, 6, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>HMRC's own label for the year, e.g. "2024-25".</summary>
    public string Label => $"{StartYear}-{(StartYear + 1) % 100:D2}";

    public bool Contains(DateTime ukDate) => ukDate >= InclusiveStart && ukDate < ExclusiveEnd;

    /// <summary>The tax year a UK-local date falls in.</summary>
    public static UkTaxYear ContainingDate(DateTime ukDate) =>
        new(ukDate.Month > 4 || (ukDate.Month == 4 && ukDate.Day >= 6) ? ukDate.Year : ukDate.Year - 1);

    public override string ToString() => Label;
}

/// <summary>
/// Converts stored timestamps to UK local time. The tax year boundary and the same-day and 30-day
/// rules are all defined on UK calendar days, so a trade at 00:30 UTC on 6 April in British Summer
/// Time belongs to the previous day — and, on that date, to the previous tax year.
/// </summary>
public static class UkTime
{
    private static readonly TimeZoneInfo UkZone = ResolveUkZone();

    private static TimeZoneInfo ResolveUkZone()
    {
        // Windows and Linux name the same zone differently; try both before giving up.
        foreach (var id in new[] { "GMT Standard Time", "Europe/London" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Converts a stored (UTC) timestamp to UK local time.</summary>
    public static DateTime ToUk(DateTime timestamp)
    {
        var utc = timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
        };
        return TimeZoneInfo.ConvertTimeFromUtc(utc, UkZone);
    }

    /// <summary>The UK calendar day a stored timestamp falls on.</summary>
    public static DateTime ToUkDate(DateTime timestamp) => ToUk(timestamp).Date;
}
