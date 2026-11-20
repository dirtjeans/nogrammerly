using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nogrammerly.Core;

// The bridge lets an add-in inside another app (such as Word) use this engine. The add-in sends paragraphs
// to the tray app over a loopback connection and gets issues back. The HTTP listener lives in the Windows app;
// the settings, access rules and request handling live here so they can be tested on any machine.

/// <summary>Saved bridge settings. The bridge is off until the user turns it on.</summary>
public sealed record BridgeSettings(bool Enabled, int Port, string Token, IReadOnlyList<string> Origins)
{
    public const int DefaultPort = 47613;

    /// <summary>What the user pastes into the add-in once, so it knows where the bridge is and can prove who it is.</summary>
    public string PairingKey => $"{Port}.{Token}";

    public static BridgeSettings Create() =>
        new(false, DefaultPort, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), ["https://localhost:3000"]);

    /// <summary>Reads the settings, or makes new ones with a fresh token if the file is missing or unusable.</summary>
    public static BridgeSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path), Bridge.Json) is { Token.Length: >= 32, Port: > 1023 and < 65536, Origins: not null } saved)
                return saved;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        return Create();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Bridge.Json));
    }
}

public enum BridgeAccess { Allowed, Preflight, BadHost, BadOrigin, BadToken }

public static class Bridge
{
    public const int MaxParagraphs = 200;
    public const int MaxBodyBytes = 1_000_000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Decides whether a request may use the bridge. Every web page on the PC can reach a loopback port, so:
    /// the Host header must be ours (stops DNS rebinding), a browser's Origin must be on the list, and every
    /// real request must carry the token. A preflight (OPTIONS) carries no token; it only passes the origin check.
    /// </summary>
    public static BridgeAccess Check(BridgeSettings settings, string method, string? host, string? origin, string? authorization)
    {
        if (!string.Equals(host, $"127.0.0.1:{settings.Port}", StringComparison.Ordinal)) return BridgeAccess.BadHost;
        var preflight = string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase);
        if (origin is null ? preflight : !settings.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return BridgeAccess.BadOrigin;
        if (preflight) return BridgeAccess.Preflight;
        var expected = Encoding.UTF8.GetBytes("Bearer " + settings.Token);
        var given = Encoding.UTF8.GetBytes(authorization ?? "");
        return CryptographicOperations.FixedTimeEquals(expected, given) ? BridgeAccess.Allowed : BridgeAccess.BadToken;
    }
}

/// <summary>Set Ai to false to skip the on-device model, for a fast first pass.</summary>
public sealed record CheckRequest(IReadOnlyList<string>? Paragraphs, bool? Ai = null);

/// <summary>An issue within one paragraph. Start and Length count UTF-16 code units, as JavaScript strings do.</summary>
public sealed record BridgeIssue(int Start, int Length, string Kind, string Title, string Explanation, string Original, string? Replacement, bool IsSpelling, string IgnoreKey);

/// <summary>One list of issues per paragraph sent, in the same order.</summary>
public sealed record CheckResponse(IReadOnlyList<IReadOnlyList<BridgeIssue>> Paragraphs);

public sealed record RewriteRequest(string? Text, string? Tone);
public sealed record RewriteResponse(string Text, bool Ai);
public sealed record IgnoreRequest(string? IgnoreKey);
public sealed record StatusResponse(string Version, string Ai);
public sealed record ErrorResponse(string Error);

/// <summary>Thrown for a request the bridge understands but won't serve; the message is safe to send back.</summary>
public sealed class BridgeRequestException(string message) : Exception(message);

/// <summary>
/// Handles bridge requests with the same checks the system-wide assistant runs: the style rules, then the
/// platform's spelling pass, then the on-device model. The platform parts are passed in, so tests can leave them out.
/// </summary>
public sealed class BridgeService(
    IgnoreList ignored,
    Func<string, IEnumerable<WritingSuggestion>>? spelling = null,
    Func<string, CancellationToken, Task<IReadOnlyList<WritingSuggestion>>>? proofread = null,
    Func<string, WritingTone, Task<string?>>? rewrite = null)
{
    public async Task<CheckResponse> CheckAsync(CheckRequest request, CancellationToken token = default)
    {
        var paragraphs = request.Paragraphs ?? throw new BridgeRequestException("\"paragraphs\" is required.");
        if (paragraphs.Count > Bridge.MaxParagraphs) throw new BridgeRequestException($"Send at most {Bridge.MaxParagraphs} paragraphs at a time.");
        var results = new List<IReadOnlyList<BridgeIssue>>(paragraphs.Count);
        foreach (var paragraph in paragraphs)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(paragraph) || paragraph.Length > WritingEngine.MaxLength) { results.Add([]); continue; }
            var found = WritingEngine.Analyze(paragraph, spelling).Suggestions;
            if (request.Ai != false && proofread is not null) found = WritingEngine.Merge(found, await proofread(paragraph, token));
            results.Add(found.Where(s => !ignored.Contains(s)).Select(s => new BridgeIssue(
                s.Span.Start, s.Span.Length, s.Kind.ToString().ToLowerInvariant(), s.Title, s.Explanation, s.Original, s.Replacement, s.IsSpelling, s.IgnoreKey)).ToList());
        }
        return new CheckResponse(results);
    }

    public async Task<RewriteResponse> RewriteAsync(RewriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text)) throw new BridgeRequestException("\"text\" is required.");
        if (request.Text.Length > WritingEngine.MaxLength) throw new BridgeRequestException("The text is too long to rewrite.");
        if (!Enum.TryParse<WritingTone>(request.Tone ?? nameof(WritingTone.Professional), ignoreCase: true, out var tone) || !Enum.IsDefined(tone))
            throw new BridgeRequestException("\"tone\" must be professional, friendly, confident or concise.");
        var fromModel = rewrite is null ? null : await rewrite(request.Text, tone);
        return new RewriteResponse(fromModel ?? WritingEngine.Rewrite(request.Text, tone), fromModel is not null);
    }

    public void Ignore(IgnoreRequest request)
    {
        if (string.IsNullOrEmpty(request.IgnoreKey) || request.IgnoreKey.Length > 2 * WritingEngine.MaxLength) throw new BridgeRequestException("\"ignoreKey\" is required.");
        ignored.Add(request.IgnoreKey);
    }
}
