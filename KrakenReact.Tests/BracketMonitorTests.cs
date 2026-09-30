using KrakenReact.Server.Services;

namespace KrakenReact.Tests;

public class BracketMonitorTests
{
    [Theory]
    [InlineData("Buy", 94.9, 95, true)]    // long: price fell through the stop
    [InlineData("Buy", 95.0, 95, true)]    // exactly at the stop
    [InlineData("Buy", 100, 95, false)]
    [InlineData("Sell", 105.1, 105, true)] // short: price rose through the stop
    [InlineData("Sell", 100, 105, false)]
    [InlineData("Buy", 90, 0, false)]      // no stop configured
    [InlineData("Buy", 0, 95, false)]      // no price yet
    public void IsStopHit_IsSideAware(string side, double price, double stop, bool expected)
    {
        Assert.Equal(expected, BracketMonitorJob.IsStopHit(side, (decimal)price, (decimal)stop));
    }
}
