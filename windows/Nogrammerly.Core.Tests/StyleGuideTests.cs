using Nogrammerly.Core;
using Xunit;

public class StyleGuideTests
{
    // An invented guide: no real company's rules are kept in this repository.
    private const string House = """
        {
          // Comments and trailing commas are allowed, since people edit these by hand.
          "name": "Acme",
          "terms": [
            { "avoid": ["web site", "web-site"], "use": "website", "note": "One word." },
            { "avoid": "zero trust", "use": "Zero Trust" },
            { "avoid": "Zero Trust Network", "note": "Old name." },
            { "avoid": "US", "use": "U.S.", "matchCase": true },
            { "avoid": ["IP tables", "iptables"], "use": "iptables", "exact": true },
            { "avoid": "internet", "use": "internet" },
            { "avoid": "per cent", "use": "%" },
          ],
          "patterns": [ { "find": "(\\d+) ?percent\\b", "use": "$1%", "note": "Use the % sign." } ],
        }
        """;

    private static StyleGuides Guides(params string[] json) => new(json.Select((j, i) => StyleGuide.Parse(j, $"guide{i}")));

    private static List<(string Original, string? Fix)> Found(StyleGuides guides, string text) =>
        guides.Find(text).Select(s => (s.Original, s.Replacement)).ToList();

    [Fact]
    public void FlagsAvoidedWordingAndOffersThePreferredOne()
    {
        var issue = Assert.Single(Guides(House).Find("Visit our web site today."));
        Assert.Equal(("Acme style", SuggestionKind.Style, "web site", "website"), (issue.Title, issue.Kind, issue.Original, issue.Replacement));
        Assert.Equal("One word. Use “website”.", issue.Explanation);
    }

    [Fact]
    public void ACapitalsRuleFlagsOnlyTheWrongForms() =>
        Assert.Equal([("zero trust", "Zero Trust"), ("Zero trust", "Zero Trust")], Found(Guides(House), "We like zero trust. Zero trust works. Zero Trust is here."));

    [Fact]
    public void ALowerCaseTermKeepsItsCapitalAtTheStartOfASentence()
    {
        Assert.Equal([("Internet", "internet")], Found(Guides(House), "Internet access is slow. The Internet is big. Use the internet."));
        Assert.Equal([("Web site", "Website")], Found(Guides(House), "Web site traffic rose."));
    }

    [Fact]
    public void AnExactTermIsWrittenAsGivenEverywhere() =>
        Assert.Equal([("Iptables", "iptables"), ("IP tables", "iptables")], Found(Guides(House), "Iptables is old. We use IP tables and iptables."));

    [Fact]
    public void MatchCaseLeavesOtherCapitalsAlone() =>
        Assert.Equal([("US", "U.S.")], Found(Guides(House), "Tell us about the US office and the U.S. team."));

    [Fact]
    public void TheLongerPhraseIsJudgedFirst()
    {
        var issue = Assert.Single(Guides(House).Find("The Zero Trust Network is gone."));
        Assert.Equal(("Zero Trust Network", null, "Old name."), (issue.Original, issue.Replacement, issue.Explanation));
    }

    [Fact]
    public void PatternsCanReuseWhatTheyMatched() =>
        Assert.Equal([("24 percent", "24%"), ("5percent", "5%")], Found(Guides(House), "A 24 percent drop and a 5percent rise, or 3% more."));

    [Fact]
    public void AHigherGuideWinsWhereALowerOneDisagrees()
    {
        const string lower = """
            { "name": "Press", "terms": [
              { "avoid": "%", "use": "percent" },
              { "avoid": "website", "use": "web site" },
              { "avoid": "zero trust", "use": "zero-trust" },
              { "avoid": "cell phone", "use": "cellphone" } ] }
            """;
        var guides = Guides(House, lower);
        Assert.Equal(["Acme", "Press"], guides.Names);
        // The lower guide may not undo "website", rule again on "zero trust", or flag wording the higher guide prefers.
        Assert.Equal([("zero trust", "Zero Trust"), ("cell phone", "cellphone")], Found(guides, "Our website covers zero trust on a cell phone."));
        Assert.Equal("Press style", guides.Find("A cell phone.")[0].Title);
    }

    [Fact]
    public void WordsAGuideAsksForAreNotSpellingMistakes()
    {
        var guides = Guides("""{ "terms": [ { "avoid": "micro-segmentation", "use": "microsegmentation" }, { "avoid": "rule set", "use": "ruleset" } ] }""");
        Assert.True(guides.Prefers("microsegmentation"));
        Assert.True(guides.Prefers("Ruleset"));
        Assert.False(guides.Prefers("segmentation"));
    }

    [Fact]
    public void ABadPatternIsSkippedAndTheRestStillWork()
    {
        var guides = Guides("""{ "terms": [ { "avoid": "web site", "use": "website" } ], "patterns": [ { "find": "(unclosed", "use": "x" } ] }""");
        Assert.Equal([("web site", "website")], Found(guides, "A web site."));
    }

    [Fact]
    public void LoadsAFolderInFileNameOrderAndReportsFilesItCantRead()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "2-press.json"), """{ "terms": [ { "avoid": "cell phone", "use": "cellphone" } ] }""");
            File.WriteAllText(Path.Combine(folder, "1-house.json"), House);
            File.WriteAllText(Path.Combine(folder, "3-broken.json"), "{ not json");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "ignored");
            var guides = StyleGuides.Load(folder);
            Assert.Equal(["Acme", "2-press"], guides.Names);
            Assert.StartsWith("3-broken.json:", Assert.Single(guides.Problems));
            Assert.Empty(StyleGuides.Load(Path.Combine(folder, "missing")).Names);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void NoGuidesMeansNoFindings() => Assert.Empty(StyleGuides.None.Find("Visit our web site."));
}
