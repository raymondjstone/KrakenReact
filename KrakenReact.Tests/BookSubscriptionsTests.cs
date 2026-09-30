using KrakenReact.Server.Hubs;
using KrakenReact.Server.Services;
using Kraken.Net.Objects.Models;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

public class BookSubscriptionsTests
{
    private static (BookSubscriptions Books, TradingStateService State) Make()
    {
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        state.Symbols["XBT/USD"] = new KrakenSymbol { WebsocketName = "XBT/USD" };
        state.Symbols["ETH/USD"] = new KrakenSymbol { WebsocketName = "ETH/USD" };
        return (new BookSubscriptions(state), state);
    }

    [Fact]
    public void AKnownPair_IsAccepted_AndUsesTheExchangeSpelling()
    {
        var (books, state) = Make();
        Assert.True(books.Subscribe("c1", "xbt/usd"));
        Assert.Equal("XBT/USD", state.BookPair);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("NOPE/USD")]
    [InlineData("XBT/USD\",\"x")]
    public void ANameThatIsNotAnExchangePair_IsRefused_AndChangesNothing(string? pair)
    {
        var (books, state) = Make();
        books.Subscribe("c1", "ETH/USD");

        Assert.False(books.Subscribe("c2", pair));
        Assert.Equal("ETH/USD", state.BookPair);
    }

    [Fact]
    public void WhenTheLastClientLeaves_TheFeedIsReleased()
    {
        var (books, state) = Make();
        books.Subscribe("c1", "XBT/USD");
        books.Release("c1");
        Assert.Null(state.BookPair);
    }

    [Fact]
    public void WhenTheClientThatSetThePairLeaves_TheFeedMovesToAnotherClientsPair()
    {
        var (books, state) = Make();
        books.Subscribe("c1", "XBT/USD");
        books.Subscribe("c2", "ETH/USD");   // newest wins
        Assert.Equal("ETH/USD", state.BookPair);

        books.Release("c2");
        Assert.Equal("XBT/USD", state.BookPair);   // c1 is still watching
    }

    [Fact]
    public void ALeavingClient_ThatWasNotWatchingTheCurrentPair_LeavesItAlone()
    {
        var (books, state) = Make();
        books.Subscribe("c1", "XBT/USD");
        books.Subscribe("c2", "ETH/USD");

        books.Release("c1");
        Assert.Equal("ETH/USD", state.BookPair);
    }

    [Fact]
    public void ReleasingAnUnknownConnection_IsHarmless()
    {
        var (books, state) = Make();
        books.Subscribe("c1", "XBT/USD");
        books.Release("ghost");
        Assert.Equal("XBT/USD", state.BookPair);
    }

    [Fact]
    public void GroupNames_IgnoreCase_SoAnySpellingReachesTheSameGroup()
    {
        Assert.Equal(BookSubscriptions.GroupName("XBT/USD"), BookSubscriptions.GroupName("xbt/usd"));
        Assert.NotEqual(BookSubscriptions.GroupName("XBT/USD"), BookSubscriptions.GroupName("ETH/USD"));
    }

    [Fact]
    public void PairOf_ReportsWhatAConnectionWatches_AndForgetsItOnRelease()
    {
        var (books, _) = Make();
        Assert.Null(books.PairOf("c1"));

        books.Subscribe("c1", "xbt/usd");
        Assert.Equal("XBT/USD", books.PairOf("c1"));

        books.Release("c1");
        Assert.Null(books.PairOf("c1"));
    }
}
