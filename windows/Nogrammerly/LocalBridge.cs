using System.Net;
using System.Reflection;
using System.Text.Json;
using Nogrammerly.Core;

namespace Nogrammerly;

/// <summary>
/// Serves the engine to add-ins in other apps (such as Word) over HTTP on 127.0.0.1 only, so nothing leaves the PC.
/// Add-ins can't run the on-device model themselves: Windows only gives it to this installed app.
/// Who may call is decided by <see cref="Bridge.Check"/>.
/// </summary>
internal sealed class LocalBridge : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly BridgeSettings settings;
    private readonly BridgeService service;
    private readonly Func<string> aiStatus;

    public LocalBridge(BridgeSettings settings, BridgeService service, Func<string> aiStatus)
    {
        this.settings = settings; this.service = service; this.aiStatus = aiStatus;
        // A numeric loopback prefix needs no admin rights and is never reachable from another machine.
        listener.Prefixes.Add($"http://127.0.0.1:{settings.Port}/");
    }

    /// <summary>The bridge with the app's own spell checker and on-device model behind it.</summary>
    public static LocalBridge ForApp(BridgeSettings settings, IgnoreList ignored)
    {
        var spellChecker = new SpellChecker(System.Globalization.CultureInfo.CurrentUICulture.Name);
        var service = new BridgeService(ignored, spellChecker.Check, AIWriter.ProofreadAsync, AIWriter.RewriteAsync);
        return new LocalBridge(settings, service, () => AIWriter.Check().ToString());
    }

    /// <summary>False if the port is taken or Windows refuses the listener.</summary>
    public bool Start()
    {
        try { listener.Start(); }
        catch (HttpListenerException) { return false; }
        _ = Task.Run(AcceptLoop);
        return true;
    }

    private async Task AcceptLoop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Handle(context));
        }
    }

    private async Task Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var origin = request.Headers["Origin"];
            var access = Bridge.Check(settings, request.HttpMethod, request.UserHostName, origin, request.Headers["Authorization"]);
            if (access is BridgeAccess.BadHost or BridgeAccess.BadOrigin) { await Reply(response, 403, new ErrorResponse("Not allowed.")); return; }
            if (origin is not null)
            {
                // Only reached for an origin on the list, so it is safe to echo back.
                response.Headers["Access-Control-Allow-Origin"] = origin;
                response.Headers["Vary"] = "Origin";
            }
            if (access == BridgeAccess.Preflight)
            {
                response.Headers["Access-Control-Allow-Methods"] = "GET, POST";
                response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
                // Chromium asks for this before it lets a public page call a loopback address.
                response.Headers["Access-Control-Allow-Private-Network"] = "true";
                response.Headers["Access-Control-Max-Age"] = "600";
                response.StatusCode = 204;
                response.Close();
                return;
            }
            if (access == BridgeAccess.BadToken) { await Reply(response, 401, new ErrorResponse("Missing or wrong key.")); return; }

            switch (request.HttpMethod, request.Url?.AbsolutePath)
            {
                case ("GET", "/status"):
                    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
                    await Reply(response, 200, new StatusResponse(version, aiStatus()));
                    break;
                case ("POST", "/check"):
                    await Reply(response, 200, await service.CheckAsync(await Read<CheckRequest>(request)));
                    break;
                case ("POST", "/rewrite"):
                    await Reply(response, 200, await service.RewriteAsync(await Read<RewriteRequest>(request)));
                    break;
                case ("POST", "/ignore"):
                    service.Ignore(await Read<IgnoreRequest>(request));
                    await Reply(response, 200, new { });
                    break;
                default:
                    await Reply(response, 404, new ErrorResponse("Unknown request."));
                    break;
            }
        }
        catch (BridgeRequestException e) { await Reply(response, 400, new ErrorResponse(e.Message)); }
        catch (JsonException) { await Reply(response, 400, new ErrorResponse("The request body isn't valid JSON.")); }
        catch (Exception) { await Reply(response, 500, new ErrorResponse("Nogrammerly couldn't handle that request.")); }
    }

    private static async Task<T> Read<T>(HttpListenerRequest request)
    {
        if (request.ContentLength64 > Bridge.MaxBodyBytes) throw new BridgeRequestException("The request is too large.");
        // The length header may be absent or wrong, so count what actually arrives.
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer)) > 0)
        {
            if (body.Length + read > Bridge.MaxBodyBytes) throw new BridgeRequestException("The request is too large.");
            body.Write(buffer, 0, read);
        }
        return JsonSerializer.Deserialize<T>(body.ToArray(), Bridge.Json) ?? throw new BridgeRequestException("The request body is empty.");
    }

    private static async Task Reply(HttpListenerResponse response, int status, object body)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), Bridge.Json);
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { } // The caller went away.
    }

    public void Dispose()
    {
        try { listener.Close(); }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
    }
}
