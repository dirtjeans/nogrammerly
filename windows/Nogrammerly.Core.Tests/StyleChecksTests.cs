using Nogrammerly.Core;
using Xunit;

public class StyleChecksTests
{
    private static List<WritingSuggestion> Check(string text, WritingChecks checks = WritingChecks.All) =>
        WritingEngine.Analyze(text, null, checks).Suggestions.ToList();

    [Fact]
    public void FlagsACliche()
    {
        var issue = Assert.Single(Check("We should think outside the box."));
        Assert.Equal(("Cliché", SuggestionKind.Style, "think outside the box", null), (issue.Title, issue.Kind, issue.Original, issue.Replacement));
    }

    [Fact]
    public void OffersAPlainWordWhereOneAlwaysFits()
    {
        var issue = Assert.Single(Check("At the end of the day, it works."));
        Assert.Equal(("At the end of the day", "In the end"), (issue.Original, issue.Replacement));
    }

    [Theory]
    [InlineData("We met prior to lunch.", "prior to", "before")]
    [InlineData("The end result was good.", "end result", "result")]
    [InlineData("She is able to help.", "is able to", "can")]
    [InlineData("He basically agreed.", "basically", null)]
    public void FlagsWordiness(string text, string original, string? replacement)
    {
        var issue = Assert.Single(Check(text));
        Assert.Equal(("Wordy", original, replacement), (issue.Title, issue.Original, issue.Replacement));
    }

    [Theory]
    [InlineData("The fix was shipped on Monday.", "was shipped")]
    [InlineData("The report has been written.", "been written")]
    [InlineData("Mistakes were quietly made.", "were quietly made")]
    public void FlagsPassiveVoice(string text, string original)
    {
        var issue = Assert.Single(Check(text));
        Assert.Equal(("Passive voice", original), (issue.Title, issue.Original));
    }

    [Theory]
    [InlineData("I was tired after the trip.")]
    [InlineData("She is interested in the role.")]
    [InlineData("They were at the office.")]
    [InlineData("We need speed and we will succeed.")]
    [InlineData("The team shipped the fix on Monday.")]
    public void LeavesActiveSentencesAndDescribingWordsAlone(string text) => Assert.Empty(Check(text));

    [Fact]
    public void FlagsADenseSentenceAsHardToRead()
    {
        var issue = Assert.Single(Check("The implementation of organizational restructuring initiatives necessitates comprehensive stakeholder consultation processes immediately."));
        Assert.Equal(("Hard to read", SuggestionKind.Clarity, true), (issue.Title, issue.Kind, issue.IsSentenceLevel));
        Assert.Contains("out of 100", issue.Explanation);
    }

    [Theory]
    [InlineData("The cat sat on the mat and looked at the dog for a long time.")]
    [InlineData("Extraordinary.")]
    [InlineData("Organizational restructuring necessitates consultation.")]
    public void LeavesPlainOrShortSentencesAlone(string text) => Assert.Empty(Check(text));

    [Fact]
    public void ASentenceIssueSitsOverTheWordIssuesInsideIt()
    {
        var text = "The implementation of organizational restructuring initiatives necessitates comprehensive stakeholder consultation prior to finalization.";
        var issues = Check(text);
        Assert.Equal(["Hard to read", "Wordy"], issues.Select(i => i.Title));
        Assert.True(issues[0].Span.Overlaps(issues[1].Span));
    }

    [Fact]
    public void ALongSentenceIsNotAlsoCalledHardToRead()
    {
        var text = string.Join(' ', Enumerable.Repeat("organizational", 30)) + ".";
        Assert.Equal(["Long sentence"], Check(text).Select(i => i.Title));
    }

    [Fact]
    public void EachKindCanBeSwitchedOff()
    {
        const string text = "At the end of the day, the fix was shipped prior to lunch. " +
                            "The implementation of organizational restructuring initiatives necessitates comprehensive stakeholder consultation processes.";
        Assert.Equal(4, Check(text).Count);
        Assert.Empty(Check(text, WritingChecks.None));
        Assert.Equal(["Cliché"], Check(text, WritingChecks.Cliches).Select(i => i.Title));
        Assert.Equal(["Wordy"], Check(text, WritingChecks.Wordiness).Select(i => i.Title));
        Assert.Equal(["Passive voice"], Check(text, WritingChecks.PassiveVoice).Select(i => i.Title));
        Assert.Equal(["Hard to read"], Check(text, WritingChecks.Readability).Select(i => i.Title));
    }

    [Fact]
    public void TheBasicsStayOnWhenEverythingElseIsOff() =>
        Assert.Equal(["Prefer simpler wording"], Check("We utilize it.", WritingChecks.None).Select(i => i.Title));

    [Theory]
    [InlineData("cat", 1)]
    [InlineData("make", 1)]
    [InlineData("table", 2)]
    [InlineData("shipped", 1)]
    [InlineData("wanted", 2)]
    [InlineData("reading", 2)]
    [InlineData("organization", 5)]
    [InlineData("comprehensive", 4)]
    public void CountsSyllablesCloselyEnough(string word, int expected) => Assert.Equal(expected, StyleChecks.Syllables(word));

    [Fact]
    public void ReadingEaseFallsAsWordsGetLonger()
    {
        var plain = StyleChecks.ReadingEase("the cat sat on the mat and looked at the dog".Split(' '));
        var dense = StyleChecks.ReadingEase("organizational restructuring initiatives necessitate comprehensive stakeholder consultation processes immediately henceforth".Split(' '));
        Assert.True(plain > 80, $"plain {plain}");
        Assert.True(dense < 0, $"dense {dense}");
    }
}
