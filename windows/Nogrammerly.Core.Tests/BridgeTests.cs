using Nogrammerly.Core;
using Xunit;

public class BridgeAccessTests
{
    private static readonly BridgeSettings Settings = BridgeSettings.Create() with { Origins = ["https://addin.example"] };
    private static readonly string Host = $"127.0.0.1:{Settings.Port}";
    private static readonly string Key = "Bearer " + Settings.Token;

    [Fact]
    public void AllowsTheAddInWithTheKey() =>
        Assert.Equal(BridgeAccess.Allowed, Bridge.Check(Settings, "POST", Host, "https://addin.example", Key));

    [Fact]
    public void AllowsALocalProgramWithTheKey() =>
        Assert.Equal(BridgeAccess.Allowed, Bridge.Check(Settings, "GET", Host, null, Key));

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer wrong")]
    [InlineData("bearer ")]
    public void RefusesAMissingOrWrongKey(string? authorization) =>
        Assert.Equal(BridgeAccess.BadToken, Bridge.Check(Settings, "POST", Host, "https://addin.example", authorization));

    [Fact]
    public void RefusesOtherWebPagesEvenWithTheKey() =>
        Assert.Equal(BridgeAccess.BadOrigin, Bridge.Check(Settings, "POST", Host, "https://evil.example", Key));

    [Theory]
    [InlineData("evil.example")]
    [InlineData("localhost:47613")]
    [InlineData(null)]
    public void RefusesAnotherHostName(string? host) =>
        Assert.Equal(BridgeAccess.BadHost, Bridge.Check(Settings, "POST", host, "https://addin.example", Key));

    [Fact]
    public void PreflightPassesOnOriginAlone()
    {
        Assert.Equal(BridgeAccess.Preflight, Bridge.Check(Settings, "OPTIONS", Host, "https://addin.example", null));
        Assert.Equal(BridgeAccess.BadOrigin, Bridge.Check(Settings, "OPTIONS", Host, "https://evil.example", null));
        Assert.Equal(BridgeAccess.BadOrigin, Bridge.Check(Settings, "OPTIONS", Host, null, null));
    }

    [Fact]
    public void SettingsSurviveSavingAndStartOff()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "bridge.json");
        try
        {
            var created = BridgeSettings.Load(path);
            Assert.False(created.Enabled);
            Assert.Equal(64, created.Token.Length);
            Assert.NotEqual(created.Token, BridgeSettings.Create().Token);
            (created with { Enabled = true }).Save(path);
            var loaded = BridgeSettings.Load(path);
            Assert.True(loaded.Enabled);
            Assert.Equal(created.Token, loaded.Token);
            Assert.Equal(created.Origins, loaded.Origins);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}

public class BridgeServiceTests
{
    private static Task<CheckResponse> Check(BridgeService service, CheckRequest request) => service.CheckAsync(request, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ChecksEachParagraphWithItsOwnOffsets()
    {
        var response = await Check(new BridgeService(new IgnoreList()), new CheckRequest(["All good here.", "", "We utilize it."]));
        Assert.Equal(3, response.Paragraphs.Count);
        Assert.Empty(response.Paragraphs[0]);
        Assert.Empty(response.Paragraphs[1]);
        var issue = Assert.Single(response.Paragraphs[2]);
        Assert.Equal((3, 7, "clarity", "use"), (issue.Start, issue.Length, issue.Kind, issue.Replacement));
    }

    [Fact]
    public async Task MergesTheModelPassAndCanSkipIt()
    {
        Task<IReadOnlyList<WritingSuggestion>> Model(string text, CancellationToken token) => Task.FromResult<IReadOnlyList<WritingSuggestion>>(
            [new(SuggestionKind.Correctness, "Agreement", "", "is", "are", new TextSpan(5, 2))]);
        var service = new BridgeService(new IgnoreList(), proofread: Model);
        Assert.Equal("are", Assert.Single((await Check(service, new CheckRequest(["They is here."]))).Paragraphs[0]).Replacement);
        Assert.Empty((await Check(service, new CheckRequest(["They is here."], Ai: false))).Paragraphs[0]);
    }

    [Fact]
    public async Task IgnoredIssuesStayAway()
    {
        var service = new BridgeService(new IgnoreList());
        var issue = Assert.Single((await Check(service, new CheckRequest(["We utilize it."]))).Paragraphs[0]);
        service.Ignore(new IgnoreRequest(issue.IgnoreKey));
        Assert.Empty((await Check(service, new CheckRequest(["They utilize it too."]))).Paragraphs[0]);
    }

    [Fact]
    public async Task RewriteFallsBackToRulesWithoutAModel()
    {
        var plain = await new BridgeService(new IgnoreList()).RewriteAsync(new RewriteRequest("We utilize it.", "concise"));
        Assert.Equal(("We use it.", false), (plain.Text, plain.Ai));
        var smart = await new BridgeService(new IgnoreList(), rewrite: (_, _) => Task.FromResult<string?>("Better.")).RewriteAsync(new RewriteRequest("We utilize it.", null));
        Assert.Equal(("Better.", true), (smart.Text, smart.Ai));
    }

    [Fact]
    public async Task RefusesBadRequests()
    {
        var service = new BridgeService(new IgnoreList());
        await Assert.ThrowsAsync<BridgeRequestException>(() => Check(service, new CheckRequest(null)));
        await Assert.ThrowsAsync<BridgeRequestException>(() => Check(service, new CheckRequest(Enumerable.Repeat("a", Bridge.MaxParagraphs + 1).ToList())));
        await Assert.ThrowsAsync<BridgeRequestException>(() => service.RewriteAsync(new RewriteRequest("Hi there.", "angry")));
        await Assert.ThrowsAsync<BridgeRequestException>(() => service.RewriteAsync(new RewriteRequest(" ", "concise")));
        Assert.Throws<BridgeRequestException>(() => service.Ignore(new IgnoreRequest(null)));
    }
}

public class IgnoreListTests
{
    [Fact]
    public void IsSavedAndKeyedOnTheChangeNotTheTitle()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "ignored.json");
        try
        {
            // The on-device model words its titles differently on each run.
            new IgnoreList(path).Add(new WritingSuggestion(SuggestionKind.Correctness, "Subject-verb agreement", "", "They is", "They are", new TextSpan(0, 7)));
            var again = new WritingSuggestion(SuggestionKind.Clarity, "Verb should be plural", "", "they is", "They are", new TextSpan(40, 7));
            Assert.True(new IgnoreList(path).Contains(again));
            Assert.False(new IgnoreList(path).Contains(again with { Replacement = "They were" }));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}

public class FallbackRewriteTests
{
    [Theory]
    [InlineData("I think we should go. I think so.", WritingTone.Confident, "We should go. So.")]
    [InlineData("Maybe I think it works.", WritingTone.Confident, "Maybe it works.")]
    [InlineData("Just adjust the very first one.", WritingTone.Concise, "Adjust the first one.")]
    [InlineData("We adjust it.", WritingTone.Concise, "We adjust it.")]
    public void DroppedWordsLeaveWholeWordsAndCapitals(string text, WritingTone tone, string expected) =>
        Assert.Equal(expected, WritingEngine.Rewrite(text, tone));
}
