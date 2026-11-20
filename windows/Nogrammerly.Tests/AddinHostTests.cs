using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Nogrammerly;
using Nogrammerly.Core;
using Xunit;

/// <summary>Loads the add-in's pages from a real host over HTTPS, as Word would.</summary>
public sealed class AddinHostTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    // Made for this test only: trusted by nothing, added to no store, and its key goes when it is disposed.
    private readonly X509Certificate2 certificate = AddinSetup.NewCertificate(keepKey: false);
    private readonly int port = FreePort();
    private readonly AddinHost host;
    private readonly HttpClient client;

    public AddinHostTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "taskpane.html"), "<p>pane</p>");
        host = new AddinHost(certificate, port, root, () => "47613.testkey");
        Assert.True(host.Start());
        // Trust exactly this certificate, and check the rest of it as a browser would.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, served, _, errors) =>
                served?.Thumbprint == certificate.Thumbprint && (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) == 0,
        };
        client = new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{port}/") };
    }

    [Fact]
    public async Task ServesPagesAndTheKeyOverHttps()
    {
        var page = await client.GetAsync("src/taskpane.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("<p>pane</p>", await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"key\":\"47613.testkey\"}", await client.GetStringAsync("key.json", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnswersOnBothLoopbackAddresses()
    {
        var byNumber = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}/src/taskpane.html");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(byNumber, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task RefusesOtherFilesOtherNamesAndCrossSiteKeyRequests()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("src/../../secret.html", TestContext.Current.CancellationToken)).StatusCode);
        var otherSite = new HttpRequestMessage(HttpMethod.Get, "key.json");
        otherSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(otherSite, TestContext.Current.CancellationToken)).StatusCode);
        // A page that points its own name at this PC is refused twice over: the certificate is not for that name
        // (so a browser stops here), and the site checks the host name too (see AddinSiteTests).
        var rebound = new HttpRequestMessage(HttpMethod.Get, "key.json");
        rebound.Headers.Host = "evil.example";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(rebound, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SurvivesCallersThatDontSpeakTls()
    {
        using (var plain = new TcpClient())
        {
            await plain.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            await plain.GetStream().WriteAsync("GET /key.json HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("src/taskpane.html", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public void TheCertificateIsForLocalhostOnlyAndCantSignOthers()
    {
        Assert.True(certificate.MatchesHostname("localhost"));
        Assert.False(certificate.MatchesHostname("example.com"));
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.False(basic.CertificateAuthority);
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().Single();
        Assert.False(usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign));
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
        host.Dispose();
        certificate.Dispose();
        Directory.Delete(root, true);
    }
}
