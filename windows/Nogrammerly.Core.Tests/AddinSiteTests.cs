using System.Text;
using Nogrammerly.Core;
using Xunit;

public sealed class AddinSiteTests : IDisposable
{
    private const int Port = 47614;
    private readonly string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public AddinSiteTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        File.WriteAllText(Path.Combine(root, "src", "taskpane.html"), "<p>pane</p>");
        File.WriteAllText(Path.Combine(root, "src", "core.test.js"), "x");
        File.WriteAllText(Path.Combine(root, "src", "notes.txt"), "private");
        File.WriteAllText(Path.Combine(root, "secret.html"), "secret");
    }

    private AddinSite.Reply Get(string path, string host = "localhost:47614", string? fetchSite = null, string method = "GET") =>
        AddinSite.Respond(AddinSite.ParseHead($"{method} {path} HTTP/1.1\r\nHost: {host}\r\n" + (fetchSite is null ? "" : $"Sec-Fetch-Site: {fetchSite}\r\n")), Port, root, "47613.abc");

    [Fact]
    public void ServesTheAddInsPages()
    {
        var reply = Get("/src/taskpane.html?_host_Info=Word");
        Assert.Equal((200, "text/html; charset=utf-8", "<p>pane</p>"), (reply.Status, reply.ContentType, Encoding.UTF8.GetString(reply.Body)));
        Assert.Equal(200, Get("/src/core.test.js", host: "127.0.0.1:47614").Status);
    }

    [Theory]
    [InlineData("/secret.html")]
    [InlineData("/src/../secret.html")]
    [InlineData("/src/..%2Fsecret.html")]
    [InlineData("/src/sub/taskpane.html")]
    [InlineData("/src/notes.txt")]
    [InlineData("/src/missing.html")]
    [InlineData("/src/")]
    [InlineData("/")]
    [InlineData("/assets/..")]
    public void ServesNothingElse(string path) => Assert.Equal(404, Get(path).Status);

    [Fact]
    public void GivesTheKeyOnlyToItsOwnPage()
    {
        Assert.Equal("{\"key\":\"47613.abc\"}", Encoding.UTF8.GetString(Get("/key.json", fetchSite: "same-origin").Body));
        Assert.Equal(200, Get("/key.json").Status);
        Assert.Equal(403, Get("/key.json", fetchSite: "cross-site").Status);
        Assert.Equal(403, Get("/key.json", fetchSite: "same-site").Status);
    }

    [Fact]
    public void RefusesOtherHostNamesMethodsAndNonsense()
    {
        Assert.Equal(403, Get("/src/taskpane.html", host: "evil.example").Status);
        Assert.Equal(403, Get("/key.json", host: "localhost:1").Status);
        Assert.Equal(405, Get("/src/taskpane.html", method: "POST").Status);
        Assert.Equal(400, AddinSite.Respond(AddinSite.ParseHead("hello"), Port, root, "k").Status);
        Assert.Equal(400, AddinSite.Respond(AddinSite.ParseHead("GET / SPDY/3"), Port, root, "k").Status);
    }

    [Fact]
    public void RepliesAreWholeHttpMessagesThatBrowsersWontSniff()
    {
        var text = Encoding.UTF8.GetString(AddinSite.Serialize(Get("/src/taskpane.html")));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", text);
        Assert.Contains("Content-Length: 11\r\n", text);
        Assert.Contains("X-Content-Type-Options: nosniff\r\n", text);
        Assert.Contains("Cross-Origin-Resource-Policy: same-origin\r\n", text);
        Assert.EndsWith("\r\n\r\n<p>pane</p>", text);
    }

    [Fact]
    public void TheManifestPointsAtTheAppsOwnAddress()
    {
        var manifest = AddinSite.ManifestFor("<a>https://localhost:3000/src/x.html</a><b>https://localhost:3000</b>", AddinSite.Origin(Port));
        Assert.Equal("<a>https://localhost:47614/src/x.html</a><b>https://localhost:47614</b>", manifest);
    }

    public void Dispose() => Directory.Delete(root, true);
}
