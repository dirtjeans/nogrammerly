using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nogrammerly.Core;

/// <summary>
/// The tiny web site the app serves to Word so the Word add-in needs nothing else installed: the add-in's
/// pages and icons, plus the bridge key for the page itself. Word only loads add-ins over HTTPS, so the
/// Windows app wraps this in TLS; the request handling lives here so it can be tested on any machine.
/// </summary>
public static class AddinSite
{
    public const int DefaultPort = 47614;

    /// <summary>The address in the add-in's manifest template, replaced with the real one at set-up.</summary>
    public const string TemplateAddress = "https://localhost:3000";

    /// <summary>The add-in's id in its manifest; Word lists side-loaded add-ins under this name in the registry.</summary>
    public const string ManifestId = "6f0c1f0e-6b1c-4f6e-9a57-2f1d5b7c8e41";

    public const int MaxHeadBytes = 8 * 1024;

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".png"] = "image/png",
    };

    public sealed record Request(string Method, string Path, IReadOnlyDictionary<string, string> Headers);
    public sealed record Reply(int Status, string ContentType, byte[] Body);

    public static string Origin(int port) => $"https://localhost:{port}";

    /// <summary>The manifest for an add-in served from <paramref name="address"/> (such as https://localhost:47614).</summary>
    public static string ManifestFor(string template, string address) => template.Replace(TemplateAddress, address);

    /// <summary>Reads a request's first line and headers. Null if it isn't a request this site understands.</summary>
    public static Request? ParseHead(string head)
    {
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        // Drop any query string: no page here takes one.
        var path = first[1].Split('?', '#')[0];
        return new Request(first[0], path, headers);
    }

    /// <summary>
    /// Answers a request. Only files directly inside <c>src</c> and <c>assets</c> under <paramref name="root"/> are
    /// served, by an exact pattern, so no address can reach anything else on the PC. <c>/key.json</c> gives the
    /// add-in's own page the bridge key, which saves the user pasting it.
    /// </summary>
    public static Reply Respond(Request? request, int port, string root, string pairingKey)
    {
        if (request is null) return Text(400, "Bad request.");
        // A web page that points its own name at this PC (DNS rebinding) still sends its own name as the host.
        var host = request.Headers.GetValueOrDefault("Host");
        if (host != $"localhost:{port}" && host != $"127.0.0.1:{port}") return Text(403, "Not allowed.");
        if (request.Method != "GET") return Text(405, "Not allowed.");

        if (request.Path == "/key.json")
        {
            // Browsers say where a request comes from. Only the add-in's own page may ask for the key; another
            // site couldn't read the reply anyway, because no cross-origin access is granted.
            if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "same-origin") return Text(403, "Not allowed.");
            return new Reply(200, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(new { key = pairingKey }));
        }

        var match = Regex.Match(request.Path, @"^/(src|assets)/([A-Za-z0-9_-]+(\.[A-Za-z0-9]+)+)$");
        if (!match.Success || !Types.TryGetValue(Path.GetExtension(request.Path), out var type)) return Text(404, "Not found.");
        try { return new Reply(200, type, File.ReadAllBytes(Path.Combine(root, match.Groups[1].Value, match.Groups[2].Value))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Text(404, "Not found."); }
    }

    /// <summary>The reply as HTTP/1.1 bytes. Each connection serves one request, which is plenty for a few small files.</summary>
    public static byte[] Serialize(Reply reply)
    {
        var reason = reply.Status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var head = $"HTTP/1.1 {reply.Status} {reason}\r\n" +
                   $"Content-Type: {reply.ContentType}\r\n" +
                   $"Content-Length: {reply.Body.Length}\r\n" +
                   "Cache-Control: no-cache\r\n" +
                   "X-Content-Type-Options: nosniff\r\n" +
                   "Cross-Origin-Resource-Policy: same-origin\r\n" +
                   "Connection: close\r\n\r\n";
        return [.. Encoding.ASCII.GetBytes(head), .. reply.Body];
    }

    private static Reply Text(int status, string message) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(message));
}
