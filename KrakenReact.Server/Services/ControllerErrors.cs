using Microsoft.AspNetCore.Mvc;

namespace KrakenReact.Server.Services;

public static class ControllerErrors
{
    /// <summary>What a caller is told when a request fails unexpectedly. The detail (which can name servers, tables or file paths)
    /// goes to the log, not to whoever made the request.</summary>
    public const string GenericMessage = "The request failed. See the server log for details.";

    /// <summary>Logs the exception and returns a 500 with a generic message under the "error" key the client already reads.</summary>
    public static ObjectResult ServerError(this ControllerBase controller, Exception ex)
    {
        Serilog.Log.Error(ex, "{Controller} request failed: {Path}", controller.GetType().Name, controller.HttpContext?.Request.Path.Value);
        return controller.StatusCode(500, new { error = GenericMessage });
    }
}
