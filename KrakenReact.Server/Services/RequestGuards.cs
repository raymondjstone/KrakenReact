namespace KrakenReact.Server.Services;

/// <summary>
/// Rejects state-changing API calls that don't carry the app's own header.
/// <para>
/// This is what stops another website driving the API through the user's browser (cross-site request forgery). A page
/// on any origin can POST to this server without a CORS pre-check as long as the request is "simple" — and several
/// endpoints, including the one that market-sells an entire balance, take no body, so they are. A request that adds a
/// custom header is never simple: the browser must first ask permission via a CORS pre-flight, and the CORS policy
/// only grants that to this app's own origins. The app's client sends the header on every call; a hostile page can't.
/// </para>
/// No login is involved. GET, HEAD and OPTIONS (pre-flight) pass through untouched.
/// </summary>
public sealed class RequireClientHeaderMiddleware
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "KrakenReact";

    private readonly RequestDelegate _next;
    public RequireClientHeaderMiddleware(RequestDelegate next) => _next = next;

    public static bool NeedsHeader(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
        !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);

    public async Task InvokeAsync(HttpContext context)
    {
        if (NeedsHeader(context.Request) &&
            !string.Equals(context.Request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { message = $"Missing {HeaderName} header — request refused." });
            return;
        }
        await _next(context);
    }
}

/// <summary>
/// Optionally restricts which Host names the server answers to. Without it, a hostile site can use DNS rebinding — a
/// name it controls that resolves to this machine — to make the browser treat this server as the hostile site's own
/// origin and so bypass CORS entirely. Enabled by listing names in <c>Security:AllowedHosts</c>
/// (env <c>Security__AllowedHosts=localhost,myhost</c>); empty means allow all, as before.
/// </summary>
public sealed class AllowedHostsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowed;

    public AllowedHostsMiddleware(RequestDelegate next, IEnumerable<string> allowedHosts)
    {
        _next = next;
        _allowed = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
    }

    public static string[] Parse(string? configured) =>
        (configured ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task InvokeAsync(HttpContext context)
    {
        if (_allowed.Count > 0 && !_allowed.Contains(context.Request.Host.Host))
        {
            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            await context.Response.WriteAsync("Host not allowed.");
            return;
        }
        await _next(context);
    }
}

/// <summary>
/// Refuses SignalR connections that come from another website.
/// <para>
/// CORS does not apply to WebSockets, so any page open in the user's browser could connect to the live-updates hub and receive
/// balances and orders (the hub sends them the moment a client connects). Browsers always send an Origin header on a WebSocket
/// handshake, and a page cannot forge it - so a connection whose origin is not this app is turned away. Requests with no Origin
/// (scripts, tools, servers) are not a cross-site risk and pass. No login is involved.
/// </para>
/// </summary>
public sealed class HubOriginMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string[] _allowedOrigins;
    private readonly string[] _allowedHosts;
    private readonly string _hubPath;

    public HubOriginMiddleware(RequestDelegate next, string hubPath, IEnumerable<string> allowedOrigins, IEnumerable<string> allowedHosts)
    {
        _next = next;
        _hubPath = hubPath;
        _allowedOrigins = allowedOrigins.Select(o => o.TrimEnd('/')).ToArray();
        _allowedHosts = allowedHosts.ToArray();
    }

    /// <summary>
    /// True if a request with this Origin may use the hub: no Origin; the same origin as the Host it was sent to (the normal
    /// case, the app serving its own page); the same as X-Forwarded-Host (a reverse proxy in front); a configured CORS origin
    /// (the Vite dev server); or a host named in Security:AllowedHosts.
    /// </summary>
    public static bool IsAllowed(string? origin, string? requestHost, string? forwardedHost, IEnumerable<string> allowedOrigins, IEnumerable<string> allowedHosts)
    {
        if (string.IsNullOrEmpty(origin)) return true;
        // "null" is what sandboxed pages and file:// documents send - never this app
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https")) return false;

        var trimmed = origin.TrimEnd('/');
        if (allowedOrigins.Any(o => string.Equals(o.TrimEnd('/'), trimmed, StringComparison.OrdinalIgnoreCase))) return true;

        var originHost = Normalize(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}", uri.Scheme);
        if (requestHost != null && string.Equals(Normalize(requestHost, uri.Scheme), originHost, StringComparison.OrdinalIgnoreCase)) return true;
        var forwarded = forwardedHost?.Split(',')[0].Trim();
        if (!string.IsNullOrEmpty(forwarded) && string.Equals(Normalize(forwarded, uri.Scheme), originHost, StringComparison.OrdinalIgnoreCase)) return true;

        return allowedHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A host[:port] with the scheme's default port removed, so "myhost:80" and "myhost" compare equal on http.</summary>
    private static string Normalize(string hostPort, string scheme)
    {
        var defaultPort = scheme == "https" ? ":443" : ":80";
        return hostPort.EndsWith(defaultPort, StringComparison.Ordinal) ? hostPort[..^defaultPort.Length] : hostPort;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments(_hubPath, StringComparison.OrdinalIgnoreCase) &&
            !IsAllowed(context.Request.Headers.Origin.ToString(), context.Request.Host.Value,
                       context.Request.Headers["X-Forwarded-Host"].ToString(), _allowedOrigins, _allowedHosts))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Origin not allowed.");
            return;
        }
        await _next(context);
    }
}
