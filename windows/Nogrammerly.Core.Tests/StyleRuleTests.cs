using Nogrammerly.Core;
using Xunit;

public class StyleRuleTests
{
    // An invented guide: no real company's rules are kept in this repository.
    private const string House = """
        {
          "name": "Acme",
          "firstMentions": [ { "short": "Rocket", "full": "Acme Rocket" } ],
          "acronyms": { "known": ["AI", "IT", "US"] },
          "rules": [
            { "id": "second-person", "rule": "Write to the reader as “you”.", "triggers": ["the customer", "users"], "wrong": "Users can sign in.", "right": "You can sign in." },
            { "id": "serial-comma", "rule": "Use a comma before “and” in a list of three.", "when": "\\w+, \\w+ and " },
            { "id": "plain", "rule": "Avoid idioms." }
          ]
        }
        """;

    private static StyleGuides Guides(params string[] json) => new(json.Select((j, i) => StyleGuide.Parse(j, $"guide{i}")));

    private static List<string> Ids(StyleGuides guides, string paragraph) => guides.RulesFor(paragraph).Select(r => r.Rule.Id).ToList();

    [Fact]
    public void ARuleIsAskedOnlyWhereItsTriggersAppear()
    {
        var guides = Guides(House);
        Assert.Equal(["plain"], Ids(guides, "The weather is nice today."));
        Assert.Equal(["second-person", "plain"], Ids(guides, "Users can do this."));
        Assert.Equal(["serial-comma", "plain"], Ids(guides, "We sell apples, pears and plums."));
        Assert.Equal(["second-person", "serial-comma", "plain"], Ids(guides, "The customer gets apples, pears and plums."));
    }

    [Fact]
    public void ATriggerIsAWholeWord() => Assert.Equal(["plain"], Ids(Guides(House), "Superusers rejoice."));

    [Fact]
    public void AHigherGuidesRuleReplacesALowerOneWithTheSameId()
    {
        var guides = Guides(House, """{ "name": "Press", "rules": [ { "id": "plain", "rule": "Lower version." }, { "id": "numbers", "rule": "Spell out one to nine." } ] }""");
        var rules = guides.RulesFor("Nothing special here at all.");
        Assert.Equal([("Acme", "plain"), ("Press", "numbers")], rules.Select(r => (r.Guide, r.Rule.Id)));
        Assert.Equal("Avoid idioms.", rules[0].Rule.Rule);
    }

    [Fact]
    public void GuidesWithoutRulesAskNothing()
    {
        Assert.False(StyleGuides.None.HasRules);
        Assert.False(Guides("""{ "terms": [ { "avoid": "web site", "use": "website" } ] }""").HasRules);
        Assert.True(Guides(House).HasRules);
    }

    [Fact]
    public void ThePromptCarriesOneRuleItsExampleAndTheText()
    {
        var rule = Guides(House).RulesFor("Users can do this.")[0].Rule;
        var prompt = Proofreading.RulePrompt(rule, "Users can do this.");
        Assert.Contains("Rule: Write to the reader as “you”.\nWrong: Users can sign in.\nRight: You can sign in.", prompt.ReplaceLineEndings("\n"));
        Assert.EndsWith("Text:\nUsers can do this.", prompt.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("idioms", prompt);
    }

    [Fact]
    public void TheModelsAnswerBecomesSuggestionsNamedAfterTheGuide()
    {
        const string reply = """[{"original": "Users", "replacement": "You", "explanation": "Speak to the reader directly."}]""";
        var issue = Assert.Single(Proofreading.ParseRuleIssues(reply, "Users can do this.", "Acme"));
        Assert.Equal(("Acme style", SuggestionKind.Style, "Users", "You", new TextSpan(0, 5)), (issue.Title, issue.Kind, issue.Original, issue.Replacement, issue.Span));
        Assert.Equal("Speak to the reader directly. Use “You”.", issue.Explanation);
        Assert.Empty(Proofreading.ParseRuleIssues("[]", "Users can do this.", "Acme"));
        Assert.Empty(Proofreading.ParseRuleIssues("Looks fine to me!", "Users can do this.", "Acme"));
    }

    [Fact]
    public void AShortNameIsFlaggedOnlyBeforeTheFullNameHasBeenGiven()
    {
        var guides = Guides(House);
        var issue = Assert.Single(guides.Find("We launched Rocket today. Then Rocket flew."));
        Assert.Equal(("Rocket", "Acme Rocket", new TextSpan(12, 6)), (issue.Original, issue.Replacement, issue.Span));
        Assert.Empty(guides.Find("We launched Acme Rocket today. Then Rocket flew."));
        Assert.Empty(guides.Find("Acme Rocket is here."));
    }

    [Fact]
    public void AShortNameThatOpensASentenceOrIsLowercaseIsLeftAlone()
    {
        var guides = Guides(House);
        Assert.Empty(guides.Find("Rocket science is hard."));
        Assert.Empty(guides.Find("We built a rocket."));
    }

    [Fact]
    public void AnAcronymIsFlaggedOnceUnlessTheTextExplainsItOrEveryoneKnowsIt()
    {
        var guides = Guides(House);
        var issue = Assert.Single(guides.Find("Track MTTR weekly. MTTR matters to IT and AI teams in the US."));
        Assert.Equal(("MTTR", null, new TextSpan(6, 4)), (issue.Original, issue.Replacement, issue.Span));
        Assert.Empty(guides.Find("Track mean time to recovery (MTTR) weekly. MTTR matters."));
        Assert.Equal(["APIs"[..3]], guides.Find("Our APIs are fast.").Select(i => i.Original));
    }

    [Fact]
    public void HeadingsInCapitalsAndOrdinaryCapitalsAreNotAcronyms()
    {
        var guides = Guides(House);
        Assert.Empty(guides.Find("IMPORTANT NOTICE FOR STAFF\nPlease read this."));
        Assert.Empty(guides.Find("I went to the U.S.A. with Sam. A plan B exists."));
    }

    private static List<(string Original, string? Fix)> Commas(string text) =>
        Guides("""{ "serialComma": true }""").Find(text).Select(i => (i.Original, i.Replacement)).ToList();

    [Theory]
    [InlineData("We support endpoints, servers and containers in one console.", "servers and", "servers, and")]
    [InlineData("John made a cake: sugar, flour or fruit.", "flour or", "flour, or")]
    [InlineData("It covers data centers, public clouds and remote offices.", "public clouds and", "public clouds, and")]
    [InlineData("In 2020, we added clouds, containers and endpoints.", "containers and", "containers, and")]
    public void FlagsAListWithoutTheSerialComma(string text, string original, string fix) =>
        Assert.Equal([(original, fix)], Commas(text));

    [Theory]
    [InlineData("We support endpoints, servers, and containers.")]
    [InlineData("In 2020, sales and marketing merged.")]
    [InlineData("However, sales and marketing agreed.")]
    [InlineData("Yesterday, Tom and Ann left early.")]
    [InlineData("Sales and marketing merged.")]
    [InlineData("On January 5, 2024 and after, it works.")]
    public void LeavesCorrectListsAndSentenceOpenersAlone(string text) => Assert.Empty(Commas(text));

    [Fact]
    public void TheModelsReasonStartsWithACapital()
    {
        const string reply = """[{"original": "Users", "replacement": "You", "explanation": "uses third person."}]""";
        Assert.Equal("Uses third person. Use “You”.", Proofreading.ParseRuleIssues(reply, "Users can do this.", "Acme")[0].Explanation);
    }

    [Fact]
    public void TermsTheGuidesSettleCantBeRewordedByTheAi()
    {
        var guides = Guides("""{ "keep": ["lateral movement"], "terms": [ { "avoid": "zero trust", "use": "Zero Trust" } ], "firstMentions": [ { "short": "Rocket", "full": "Acme Rocket" } ] }""");
        Assert.True(guides.Protects("lateral movement", "horizontal movement"));
        Assert.True(guides.Protects("stops Lateral Movement fast", "stops sideways motion fast"));
        Assert.True(guides.Protects("a Zero Trust strategy", "a no-trust strategy"));
        Assert.True(guides.Protects("Acme Rocket", null));
        // A change that leaves the term in place is fine, and so is a change to other words.
        Assert.False(guides.Protects("stops lateral movement", "helps stop lateral movement"));
        Assert.False(guides.Protects("hit it out of the park", "do it well"));
        Assert.False(StyleGuides.None.Protects("lateral movement", "x"));
    }

    [Fact]
    public void GuidesWithoutTheseSettingsDontRunTheseChecks() =>
        Assert.Empty(Guides("""{ "terms": [] }""").Find("We launched Rocket, tracked MTTR and went home."));
}
