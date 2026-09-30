using KrakenReact.Server.Controllers;
using KrakenReact.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Tests;

public class ControllerErrorsTests
{
    private sealed class Probe : ControllerBase { }

    [Fact]
    public void AnUnexpectedFailure_IsA500_WithAGenericMessage_NotTheExceptionText()
    {
        var controller = new Probe { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = controller.ServerError(new InvalidOperationException("Cannot open database \"Kraken\" requested by the login on server SQL01"));

        Assert.Equal(500, result.StatusCode);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains(ControllerErrors.GenericMessage, json);
        Assert.DoesNotContain("SQL01", json);
        Assert.Contains("\"error\"", json);   // the client reads this key
    }

    [Fact]
    public void WorksWithoutAnHttpContext()
    {
        Assert.Equal(500, new Probe().ServerError(new Exception("x")).StatusCode);
    }
}
