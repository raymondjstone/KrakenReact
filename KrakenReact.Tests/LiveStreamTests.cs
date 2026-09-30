using KrakenReact.Server.DTOs;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace KrakenReact.Tests;

public class SnapshotDifferTests
{
    private static OrderDto Order(string id, decimal price = 100m, string status = "Open") => new() { Id = id, Price = price, Status = status };
    private static SnapshotDiffer<OrderDto> Differ() => new(o => o.Id);

    [Fact]
    public void FirstDiff_ReportsEverythingAsChanged()
    {
        var delta = Differ().Diff([Order("a"), Order("b")]);
        Assert.Equal(2, delta.Changed.Count);
        Assert.Empty(delta.Removed);
    }

    [Fact]
    public void UnchangedItems_ProduceAnEmptyDelta()
    {
        var d = Differ();
        d.Diff([Order("a"), Order("b")]);
        Assert.True(d.Diff([Order("a"), Order("b")]).IsEmpty);
    }

    [Fact]
    public void OnlyTheItemThatChanged_IsReported()
    {
        var d = Differ();
        d.Diff([Order("a", 100m), Order("b", 200m)]);

        var delta = d.Diff([Order("a", 100m), Order("b", 250m)]);

        Assert.Equal("b", Assert.Single(delta.Changed).Id);
        Assert.Empty(delta.Removed);
    }

    [Fact]
    public void AnyVisibleFieldChangeCounts_NotJustPrice()
    {
        var d = Differ();
        d.Diff([Order("a", status: "Open")]);
        Assert.Single(d.Diff([Order("a", status: "Closed")]).Changed);
    }

    [Fact]
    public void ItemsThatDisappear_AreReportedAsRemoved_OnlyOnce()
    {
        var d = Differ();
        d.Diff([Order("a"), Order("b")]);

        var delta = d.Diff([Order("a")]);
        Assert.Equal(new[] { "b" }, delta.Removed);
        Assert.Empty(delta.Changed);

        Assert.True(d.Diff([Order("a")]).IsEmpty); // not removed again
    }

    [Fact]
    public void AnItemThatReturnsAfterBeingRemoved_IsReportedAgain()
    {
        var d = Differ();
        d.Diff([Order("a")]);
        d.Diff([]);
        Assert.Single(d.Diff([Order("a")]).Changed);
    }

    [Fact]
    public void Reset_MakesTheGivenListTheBaseline()
    {
        var d = Differ();
        d.Reset([Order("a"), Order("b")]);
        Assert.True(d.Diff([Order("a"), Order("b")]).IsEmpty);
        Assert.Single(d.Diff([Order("a"), Order("b", 999m)]).Changed);
    }
}

public class LiveStreamTests
{
    private static OrderDto Order(string id, decimal price = 100m) => new() { Id = id, Price = price, Status = "Open" };

    private static (LiveStream<OrderDto> Stream, Mock<IClientProxy> Client) Make()
    {
        var client = new Mock<IClientProxy>();
        client.Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return (new LiveStream<OrderDto>(o => o.Id, "OrdersDelta", "OrderUpdate"), client);
    }

    private static void Sent(Mock<IClientProxy> c, string method, Times times) =>
        c.Verify(x => x.SendCoreAsync(method, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task ChangedItems_AreSentAsADeltaEvent()
    {
        var (stream, client) = Make();
        await stream.BroadcastAsync([Order("a")], client.Object);
        Sent(client, "OrdersDelta", Times.Once());
        Sent(client, "OrderUpdate", Times.Never());
    }

    [Fact]
    public async Task NothingChanged_MeansNothingIsSent()
    {
        var (stream, client) = Make();
        await stream.BroadcastAsync([Order("a")], client.Object);
        await stream.BroadcastAsync([Order("a")], client.Object);
        await stream.BroadcastAsync([Order("a")], client.Object);
        Sent(client, "OrdersDelta", Times.Once());
    }

    [Fact]
    public async Task ADeltaCarriesOnlyTheChangedItemAndTheRemovedKeys()
    {
        var (stream, client) = Make();
        await stream.BroadcastAsync([Order("a"), Order("b")], client.Object);

        object?[]? args = null;
        client.Setup(c => c.SendCoreAsync("OrdersDelta", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, a, _) => args = a).Returns(Task.CompletedTask);

        await stream.BroadcastAsync([Order("a", 555m)], client.Object); // a changed, b gone

        Assert.NotNull(args);
        var json = System.Text.Json.JsonSerializer.Serialize(args![0]);
        Assert.Contains("\"a\"", json);
        Assert.Contains("555", json);
        Assert.Contains("\"removed\":[\"b\"]", json);
    }

    [Fact]
    public async Task FullResync_SendsTheWholeListAndRebaselines()
    {
        var (stream, client) = Make();
        await stream.BroadcastFullAsync([Order("a"), Order("b")], client.Object);
        Sent(client, "OrderUpdate", Times.Once());

        // The same data right after a full send is not "new" — no redundant delta
        await stream.BroadcastAsync([Order("a"), Order("b")], client.Object);
        Sent(client, "OrdersDelta", Times.Never());
    }

    [Fact]
    public async Task ConcurrentBroadcasts_NeverDeliverAnOlderVersionAfterANewerOne()
    {
        var (stream, client) = Make();
        var prices = new List<decimal>();
        client.Setup(c => c.SendCoreAsync("OrdersDelta", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, a, _) =>
            {
                var json = System.Text.Json.JsonSerializer.Serialize(a[0]);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                lock (prices) prices.Add(doc.RootElement.GetProperty("changed")[0].GetProperty("Price").GetDecimal());
            })
            .Returns(async () => await Task.Delay(5));

        var tasks = Enumerable.Range(1, 30).Select(i => Task.Run(() => stream.BroadcastAsync([Order("a", i)], client.Object)));
        await Task.WhenAll(tasks);

        // Whatever order the calls ran in, what was sent must be consistent with a single serial history:
        // each send is a genuine change from the previous one, so no price repeats back-to-back
        for (var i = 1; i < prices.Count; i++) Assert.NotEqual(prices[i - 1], prices[i]);
    }
}
