using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class NotificationThrottleTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstMessage_IsSent()
    {
        Assert.True(new NotificationThrottle().ShouldSend("Fill", "1 BTC", T0));
    }

    [Fact]
    public void IdenticalRepeatWithinTheWindow_IsDropped_ThenAllowedAgain()
    {
        var t = new NotificationThrottle(TimeSpan.FromSeconds(30));
        Assert.True(t.ShouldSend("Feed down", "no ticks", T0));
        Assert.False(t.ShouldSend("Feed down", "no ticks", T0.AddSeconds(10)));
        Assert.False(t.ShouldSend("Feed down", "no ticks", T0.AddSeconds(29)));
        Assert.True(t.ShouldSend("Feed down", "no ticks", T0.AddSeconds(31)));
    }

    [Fact]
    public void DifferentMessages_AreNotTreatedAsDuplicates()
    {
        var t = new NotificationThrottle();
        Assert.True(t.ShouldSend("Order Filled — Buy XBT/USD", "0.1 @ 50000", T0));
        Assert.True(t.ShouldSend("Order Filled — Buy XBT/USD", "0.1 @ 50100", T0));   // different fill
        Assert.True(t.ShouldSend("Order Filled — Buy ETH/USD", "0.1 @ 50000", T0));   // different pair
    }

    [Fact]
    public void RateCap_LimitsAPerMinuteBurst_ThenRecovers()
    {
        var t = new NotificationThrottle(maxPerMinute: 5);
        for (var i = 0; i < 5; i++) Assert.True(t.ShouldSend("Fill", $"#{i}", T0.AddSeconds(i)));
        Assert.False(t.ShouldSend("Fill", "#6", T0.AddSeconds(6)));      // over the cap
        Assert.True(t.ShouldSend("Fill", "#7", T0.AddSeconds(61)));      // the first ones have aged out
    }

    [Fact]
    public void ADroppedMessage_DoesNotUseUpTheRateAllowance()
    {
        var t = new NotificationThrottle(TimeSpan.FromSeconds(30), maxPerMinute: 2);
        Assert.True(t.ShouldSend("A", "x", T0));
        for (var i = 0; i < 20; i++) Assert.False(t.ShouldSend("A", "x", T0.AddSeconds(1)));  // duplicates, all dropped
        Assert.True(t.ShouldSend("B", "y", T0.AddSeconds(2)));           // the one remaining slot is still there
    }

    [Fact]
    public void ManyDistinctMessages_DoNotGrowTheTableWithoutBound()
    {
        var t = new NotificationThrottle(TimeSpan.FromSeconds(1), maxPerMinute: int.MaxValue);
        for (var i = 0; i < 2000; i++) t.ShouldSend("m", i.ToString(), T0.AddSeconds(i * 2)); // must simply not throw or hang
        Assert.True(t.ShouldSend("m", "fresh", T0.AddHours(1)));
    }
}
