using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using Nogrammerly;
using Nogrammerly.Core;
using Xunit;

/// <summary>Talks to a real bridge over loopback HTTP, as an add-in would.</summary>
public sealed class LocalBridgeTests : IDisposable
{
    private const string AddIn = "https://addin.example";
    private readonly BridgeSettings settings = BridgeSettings.Create() with { Port = FreePort(), Origins = [AddIn] };
    private readonly LocalBridge bridge;
    private readonly HttpClient client = new();

    public LocalBridgeTests()
    {
        var spelling = new SpellChecker("en-US");
        bridge = new LocalBridge(settings, new BridgeService(new IgnoreList(), spelling.Check), () => "Unsupported");
        Assert.True(bridge.Start());
        client.BaseAddress = new Uri($"http://127.0.0.1:{settings.Port}/");
    }

    [Fact]
    public async Task ChecksParagraphsForAnAddInWithTheKey()
    {
        var request = Request(HttpMethod.Post, "check", AddIn, settings.Token);
        request.Content = JsonContent.Create(new { paragraphs = new[] { "We recieve mail.", "We utilize it." } });
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AddIn, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var body = await response.Content.ReadFromJsonAsync<CheckResponse>(Bridge.Json, TestContext.Current.CancellationToken);
        Assert.Equal(["receive", "use"], body!.Paragraphs.Select(p => Assert.Single(p).Replacement));
        Assert.Equal((3, 7, true), (body.Paragraphs[0][0].Start, body.Paragraphs[0][0].Length, body.Paragraphs[0][0].IsSpelling));
    }

    [Fact]
    public async Task ReportsStatusAndRewrites()
    {
        var status = await client.SendAsync(Request(HttpMethod.Get, "status", null, settings.Token), TestContext.Current.CancellationToken);
        Assert.Equal("Unsupported", (await status.Content.ReadFromJsonAsync<StatusResponse>(Bridge.Json, TestContext.Current.CancellationToken))!.Ai);

        var request = Request(HttpMethod.Post, "rewrite", AddIn, settings.Token);
        request.Content = JsonContent.Create(new { text = "We just utilize it.", tone = "concise" });
        var rewrite = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(new RewriteResponse("We use it.", false), await rewrite.Content.ReadFromJsonAsync<RewriteResponse>(Bridge.Json, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RefusesCallersWithoutTheKeyOrFromOtherPages()
    {
        async Task<HttpStatusCode> Status(HttpRequestMessage request) => (await client.SendAsync(request, TestContext.Current.CancellationToken)).StatusCode;
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(Request(HttpMethod.Get, "status", AddIn, null)));
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(Request(HttpMethod.Get, "status", AddIn, "wrong")));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(Request(HttpMethod.Get, "status", "https://evil.example", settings.Token)));

        var otherPage = await client.SendAsync(Request(HttpMethod.Options, "check", "https://evil.example", null), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, otherPage.StatusCode);
        Assert.False(otherPage.Headers.Contains("Access-Control-Allow-Origin"));

        // A page that points its own domain name at 127.0.0.1 (DNS rebinding) still sends its own name as the host.
        var rebound = Request(HttpMethod.Get, "status", null, settings.Token);
        rebound.Headers.Host = "evil.example";
        Assert.NotEqual(HttpStatusCode.OK, await Status(rebound));
    }

    [Fact]
    public async Task AnswersThePreflightForTheAddIn()
    {
        var response = await client.SendAsync(Request(HttpMethod.Options, "check", AddIn, null), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(AddIn, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("Authorization", response.Headers.GetValues("Access-Control-Allow-Headers").Single());
    }

    [Fact]
    public async Task RefusesBadAndOversizedBodies()
    {
        var bad = Request(HttpMethod.Post, "check", AddIn, settings.Token);
        bad.Content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(bad, TestContext.Current.CancellationToken)).StatusCode);

        var huge = Request(HttpMethod.Post, "check", AddIn, settings.Token);
        huge.Content = new StringContent(new string('a', Bridge.MaxBodyBytes + 1), System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(huge, TestContext.Current.CancellationToken)).StatusCode);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? origin, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        client.Dispose();
        bridge.Dispose();
    }
}
