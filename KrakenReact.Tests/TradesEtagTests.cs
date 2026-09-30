using KrakenReact.Server.Controllers;
using KrakenReact.Server.Data;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace KrakenReact.Tests;

/// <summary>The trades lists are large and rebuilt on every request; an unchanged ETag lets the server answer 304 instead.</summary>
public class TradesEtagTests
{
    private static (TradesController Controller, DbMethods Db) Make(string? ifNoneMatch = null)
    {
        var db = new DbMethods(new InMemoryDbFactory($"etag-{Guid.NewGuid()}"), new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var state = new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object));
        var http = new DefaultHttpContext();
        if (ifNoneMatch != null) http.Request.Headers.IfNoneMatch = ifNoneMatch;
        return (new TradesController(db, state) { ControllerContext = new ControllerContext { HttpContext = http } }, db);
    }

    [Fact]
    public async Task FirstRequest_ReturnsDataAndAnETag()
    {
        var (controller, db) = Make();

        var result = await controller.GetAll();

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(db.TransactionEtag, controller.HttpContext.Response.Headers.ETag.ToString());
        Assert.Equal("no-cache", controller.HttpContext.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task RepeatRequestWithTheCurrentETag_Gets304_WithoutRebuilding()
    {
        var first = Make();
        var etag = first.Db.TransactionEtag;
        var controller = new TradesController(first.Db, new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object)))
        {
            ControllerContext = new ControllerContext { HttpContext = MakeHttp(etag) },
        };

        var result = await controller.GetAll();

        var status = Assert.IsType<StatusCodeResult>(result.Result);
        Assert.Equal(304, status.StatusCode);
    }

    [Fact]
    public async Task AfterTheDataChanges_TheOldETagNoLongerMatches()
    {
        var first = Make();
        var stale = first.Db.TransactionEtag;
        first.Db.InvalidateTransactionCaches(); // what happens when a fill is synced

        Assert.NotEqual(stale, first.Db.TransactionEtag);

        var controller = new TradesController(first.Db, new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object)))
        {
            ControllerContext = new ControllerContext { HttpContext = MakeHttp(stale) },
        };
        var result = await controller.GetAll();
        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GroupedTrades_ForDifferentSymbols_HaveDifferentETags()
    {
        var a = Make(); await a.Controller.GetGrouped("XBT/USD");

        // Same database, same data version: only the requested symbol differs, and that alone must change the tag
        var tagA = a.Controller.HttpContext.Response.Headers.ETag.ToString();
        var controller = new TradesController(a.Db, new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object)))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        await controller.GetGrouped("ETH/USD");
        Assert.NotEqual(tagA, controller.HttpContext.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task OutsideARequest_WorksWithoutAnETag()
    {
        var db = new DbMethods(new InMemoryDbFactory($"etag-{Guid.NewGuid()}"), new Mock<ILogger<DbMethods>>().Object, TestDiagnostics.Create());
        var controller = new TradesController(db, new TradingStateService(new DelistedPriceService(new Mock<ILogger<DelistedPriceService>>().Object)));

        var result = await controller.GetAll(); // no HttpContext, as in a plain unit test

        Assert.IsType<OkObjectResult>(result.Result);
    }

    private static DefaultHttpContext MakeHttp(string ifNoneMatch)
    {
        var http = new DefaultHttpContext();
        http.Request.Headers.IfNoneMatch = ifNoneMatch;
        return http;
    }
}
