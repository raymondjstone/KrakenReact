using System.Globalization;
using System.Text.RegularExpressions;

namespace KrakenReact.Server.Services;

/// <summary>
/// Small, pure validators shared by the controllers. They exist so that Create and Update apply the SAME rules — several
/// Update endpoints used to skip checks their Create counterparts made, and the request was then accepted with values (a
/// negative profit-ladder trigger, an empty symbol, a side of "Sell ") that later made a job misbehave.
/// </summary>
public static class InputRules
{
    /// <summary>"Buy" or "Sell" for any casing/spacing of those words; null for anything else (never a silent default).</summary>
    public static string? NormalizeSide(string? side)
    {
        var s = side?.Trim();
        if (string.Equals(s, "Buy", StringComparison.OrdinalIgnoreCase)) return "Buy";
        if (string.Equals(s, "Sell", StringComparison.OrdinalIgnoreCase)) return "Sell";
        return null;
    }

    /// <summary>Clamps a caller-supplied row/day count into [1, max]; negative, zero or absent falls back to the default.</summary>
    public static int ClampCount(int? requested, int defaultValue, int max)
    {
        if (requested is null or <= 0) return Math.Min(defaultValue, max);
        return Math.Min(requested.Value, max);
    }

    /// <summary>Treats a time with no zone as UTC (that is what every job compares against) and converts local times to UTC.</summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    // ── Cron ────────────────────────────────────────────────────────────────

    private static readonly string[] MonthNames = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
    private static readonly string[] DayNames = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    /// <summary>
    /// True for a standard five-field cron expression (minute hour day-of-month month day-of-week) with lists, ranges and
    /// steps. An invalid one used to be saved with the rule and only then rejected by Hangfire, leaving a rule that never
    /// ran and, for rebalance schedules, aborting the startup restore of every schedule after it.
    /// </summary>
    public static bool IsValidCron(string? expression, out string error)
    {
        error = "";
        var fields = (expression ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "A cron expression needs exactly five fields: minute hour day-of-month month day-of-week (e.g. \"0 9 * * 1\").";
            return false;
        }

        var specs = new (string Name, int Min, int Max, string[]? Names, int NameBase)[]
        {
            ("minute", 0, 59, null, 0),
            ("hour", 0, 23, null, 0),
            ("day-of-month", 1, 31, null, 0),
            ("month", 1, 12, MonthNames, 1),
            ("day-of-week", 0, 7, DayNames, 0),
        };

        for (var i = 0; i < 5; i++)
        {
            if (!IsValidCronField(fields[i], specs[i].Min, specs[i].Max, specs[i].Names, specs[i].NameBase))
            {
                error = $"Invalid {specs[i].Name} field \"{fields[i]}\" in the cron expression.";
                return false;
            }
        }
        return true;
    }

    private static bool IsValidCronField(string field, int min, int max, string[]? names, int nameBase)
    {
        if (field == "?") return true;
        foreach (var part in field.Split(','))
        {
            if (part.Length == 0) return false;

            var stepSplit = part.Split('/');
            if (stepSplit.Length > 2) return false;
            if (stepSplit.Length == 2 && (!int.TryParse(stepSplit[1], NumberStyles.None, CultureInfo.InvariantCulture, out var step) || step < 1)) return false;

            var range = stepSplit[0];
            if (range == "*") continue;

            var ends = range.Split('-');
            if (ends.Length > 2) return false;
            foreach (var end in ends)
                if (!TryCronValue(end, min, max, names, nameBase, out _)) return false;

            if (ends.Length == 2 &&
                TryCronValue(ends[0], min, max, names, nameBase, out var lo) &&
                TryCronValue(ends[1], min, max, names, nameBase, out var hi) && lo > hi) return false;
        }
        return true;
    }

    private static bool TryCronValue(string text, int min, int max, string[]? names, int nameBase, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return value >= min && value <= max;
        if (names != null)
        {
            var idx = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { value = idx + nameBase; return true; }
        }
        value = 0;
        return false;
    }

    // ── Rebalance targets ───────────────────────────────────────────────────

    private static readonly Regex AssetName = new(@"^[A-Za-z0-9.]{1,20}$", RegexOptions.Compiled);

    /// <summary>
    /// Parses "BTC:40,ETH:30,USD:30". Every entry must be ASSET:PERCENT with a percent in 0-100, assets must be unique, and the
    /// total must not exceed 100. (Malformed entries used to be silently dropped, so a typo like "BTC:4O" quietly removed that
    /// asset from the plan; totals over 100 produced orders for more than the whole portfolio.)
    /// </summary>
    public static bool TryParseTargets(string? targets, out Dictionary<string, decimal> parsed, out string error)
    {
        parsed = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        error = "";

        var parts = (targets ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { error = "Targets are required, e.g. \"BTC:40,ETH:30,USD:30\"."; return false; }

        foreach (var part in parts)
        {
            var kv = part.Split(':');
            if (kv.Length != 2 || !AssetName.IsMatch(kv[0].Trim()))
            { error = $"\"{part}\" is not in ASSET:PERCENT form."; return false; }

            if (!decimal.TryParse(kv[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) || pct < 0 || pct > 100)
            { error = $"\"{part}\": the percentage must be a number from 0 to 100."; return false; }

            if (!parsed.TryAdd(kv[0].Trim(), pct))
            { error = $"{kv[0].Trim()} appears more than once."; return false; }
        }

        var total = parsed.Values.Sum();
        if (total > 100.01m) { error = $"The targets add up to {total:0.##}%, which is more than 100%."; return false; }
        return true;
    }
}
