using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class InputRulesTests
{
    [Theory]
    [InlineData("Buy", "Buy")]
    [InlineData("buy", "Buy")]
    [InlineData("  SELL ", "Sell")]
    [InlineData("Sell", "Sell")]
    public void NormalizeSide_AcceptsTheTwoWords_InAnyCasingOrSpacing(string input, string expected) =>
        Assert.Equal(expected, InputRules.NormalizeSide(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Banana")]
    [InlineData("Selll")]
    [InlineData("Buy Sell")]
    public void NormalizeSide_NeverGuesses(string? input) => Assert.Null(InputRules.NormalizeSide(input));

    [Theory]
    [InlineData(null, 100, 500, 100)]
    [InlineData(0, 100, 500, 100)]
    [InlineData(-5, 100, 500, 100)]      // a negative Take() used to be a SQL error
    [InlineData(50, 100, 500, 50)]
    [InlineData(int.MaxValue, 100, 500, 500)]
    public void ClampCount_KeepsRequestsInARange(int? requested, int def, int max, int expected) =>
        Assert.Equal(expected, InputRules.ClampCount(requested, def, max));

    [Fact]
    public void ToUtc_HandlesEveryKind()
    {
        var utc = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, InputRules.ToUtc(utc));
        Assert.Equal(DateTimeKind.Utc, InputRules.ToUtc(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Unspecified)).Kind);
        Assert.Equal(12, InputRules.ToUtc(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Unspecified)).Hour);   // assumed to be UTC already
        Assert.Equal(DateTimeKind.Utc, InputRules.ToUtc(DateTime.Now).Kind);
    }

    // ── cron ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0 9 * * 1")]
    [InlineData("*/5 * * * *")]
    [InlineData("0 0 1 1 *")]
    [InlineData("15,45 8-17 * * MON-FRI")]
    [InlineData("0 12 1 JAN,JUL *")]
    [InlineData("30 4 * * 0")]
    [InlineData("30 4 * * 7")]
    [InlineData("0-30/10 * * * *")]
    [InlineData("  0   9   *   *   1  ")]
    public void ValidCron_IsAccepted(string cron)
    {
        Assert.True(InputRules.IsValidCron(cron, out var error), error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("every monday")]
    [InlineData("0 9 * *")]                 // four fields
    [InlineData("0 9 * * * *")]             // six fields
    [InlineData("60 * * * *")]              // minute out of range
    [InlineData("* 24 * * *")]              // hour out of range
    [InlineData("* * 0 * *")]               // day-of-month starts at 1
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]             // zero step
    [InlineData("5-1 * * * *")]             // reversed range
    [InlineData("1,,2 * * * *")]            // empty list item
    [InlineData("a * * * *")]
    [InlineData("1-2-3 * * * *")]
    public void InvalidCron_IsRejected_WithAReason(string? cron)
    {
        Assert.False(InputRules.IsValidCron(cron, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TheErrorNamesTheFieldThatIsWrong()
    {
        InputRules.IsValidCron("0 99 * * *", out var error);
        Assert.Contains("hour", error);
    }

    // ── rebalance targets ───────────────────────────────────────────────────

    [Fact]
    public void GoodTargets_AreParsed()
    {
        Assert.True(InputRules.TryParseTargets("BTC:40, ETH:30 ,USD:30", out var t, out _));
        Assert.Equal(40m, t["BTC"]);
        Assert.Equal(30m, t["eth"]);           // case-insensitive lookup
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void TargetsMayLeaveSomeUnallocated() =>
        Assert.True(InputRules.TryParseTargets("BTC:40,ETH:30", out _, out _));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("BTC")]                     // no percentage
    [InlineData("BTC:4O")]                  // letter O for zero: used to be silently dropped
    [InlineData("BTC:-5")]
    [InlineData("BTC:150")]
    [InlineData("BTC:60,ETH:60")]           // 120% in total
    [InlineData("BTC:50,BTC:30")]           // duplicate
    [InlineData("BTC:1:2")]
    [InlineData(":50")]
    [InlineData("BT C:50")]
    public void BadTargets_AreRejected_WithAReason(string? targets)
    {
        Assert.False(InputRules.TryParseTargets(targets, out _, out var error));
        Assert.NotEmpty(error);
    }
}
