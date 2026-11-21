using System.Text.RegularExpressions;

namespace Nogrammerly.Core;

/// <summary>The optional kinds of check. The basics (spelling, spacing, long sentences) are always on.</summary>
[Flags]
public enum WritingChecks
{
    None = 0,
    Cliches = 1,
    Wordiness = 2,
    Readability = 4,
    PassiveVoice = 8,
    All = Cliches | Wordiness | Readability | PassiveVoice,
}

/// <summary>
/// Checks beyond the basics: tired phrases, words that add nothing, passive verbs and sentences that are hard
/// to read. All are simple pattern rules, so they run at once and on any machine. They will sometimes be
/// wrong, which is why each kind can be switched off and every flag is only a suggestion.
/// </summary>
public static class StyleChecks
{
    // Phrases so worn that they no longer say much. A replacement is offered only where one always fits.
    private static readonly (string Phrase, string? Replacement)[] Cliches =
    [
        ("at the end of the day", "in the end"), ("think outside the box", null), ("low-hanging fruit", null),
        ("low hanging fruit", null), ("move the needle", null), ("circle back", null), ("touch base", null),
        ("going forward", null), ("moving forward", null), ("take it to the next level", null), ("game changer", null),
        ("game-changer", null), ("win-win", null), ("paradigm shift", null), ("synergy", null), ("synergies", null),
        ("deep dive", null), ("best of breed", null), ("boil the ocean", null), ("on the same page", null),
        ("hit the ground running", null), ("push the envelope", null), ("the bottom line", null), ("par for the course", null),
        ("tip of the iceberg", null), ("needle in a haystack", null), ("few and far between", "rare"),
        ("in this day and age", "today"), ("only time will tell", null), ("when all is said and done", "in the end"),
        ("last but not least", "finally"), ("needless to say", null), ("it goes without saying", null),
        ("easier said than done", null), ("the fact of the matter is", null), ("at this moment in time", "now"),
        ("each and every one", "each one"), ("tried and true", "proven"), ("time and time again", "repeatedly"),
        ("the powers that be", null), ("think big", null), ("raise the bar", null), ("back to square one", null),
        ("the elephant in the room", null), ("a perfect storm", null), ("level playing field", null),
        ("reinvent the wheel", null), ("cutting edge", null), ("cutting-edge", null), ("state of the art", null),
        ("state-of-the-art", null), ("world class", null), ("world-class", null), ("mission critical", null),
        ("leverage", "use"), ("reach out to", "contact"), ("give 110%", null), ("bring to the table", null),
        ("drill down", null), ("run it up the flagpole", null), ("open the kimono", null), ("peel back the onion", null),
    ];

    // Longer ways of saying something short, and pairs of words where one already means the other.
    private static readonly (string Phrase, string? Replacement)[] Wordy =
    [
        ("in spite of the fact that", "although"), ("despite the fact that", "although"), ("with regard to", "about"),
        ("with regards to", "about"), ("in regard to", "about"), ("with respect to", "about"), ("in relation to", "about"),
        ("in the near future", "soon"), ("a large number of", "many"), ("a majority of", "most"), ("the majority of", "most"),
        ("has the ability to", "can"), ("have the ability to", "can"), ("is able to", "can"), ("are able to", "can"),
        ("for the purpose of", "to"), ("on a daily basis", "daily"), ("on a regular basis", "regularly"),
        ("on a weekly basis", "weekly"), ("at the present time", "now"), ("at this time", "now"), ("make a decision", "decide"),
        ("come to a conclusion", "conclude"), ("take into consideration", "consider"), ("give consideration to", "consider"),
        ("prior to", "before"), ("subsequent to", "after"), ("in close proximity to", "near"), ("in the vicinity of", "near"),
        ("in the amount of", "for"), ("in the course of", "during"), ("until such time as", "until"), ("in most cases", "usually"),
        ("it is important to note that", null), ("it should be noted that", null), ("please be advised that", null),
        ("as a matter of fact", "in fact"), ("for all intents and purposes", "in effect"), ("in the final analysis", "finally"),
        ("is in a position to", "can"), ("with the exception of", "except"), ("in light of the fact that", "because"),
        ("on account of the fact that", "because"), ("for the reason that", "because"), ("by means of", "by"),
        ("whether or not", "whether"), ("a number of", "several"), ("the reason why is that", "because"),
        // Pairs that repeat themselves.
        ("absolutely essential", "essential"), ("advance planning", "planning"), ("advance warning", "warning"),
        ("end result", "result"), ("final outcome", "outcome"), ("past history", "history"), ("past experience", "experience"),
        ("free gift", "gift"), ("each and every", "each"), ("first and foremost", "first"), ("close proximity", "near"),
        ("revert back", "revert"), ("return back", "return"), ("combine together", "combine"), ("join together", "join"),
        ("unexpected surprise", "surprise"), ("basic fundamentals", "fundamentals"), ("new innovation", "innovation"),
        ("added bonus", "bonus"), ("completely finished", "finished"), ("completely eliminate", "eliminate"),
        ("future plans", "plans"), ("general consensus", "consensus"), ("exact same", "same"), ("true fact", "fact"),
        ("still remains", "remains"), ("personal opinion", "opinion"), ("brief summary", "summary"), ("repeat again", "repeat"),
        // Words that soften a sentence without adding to it.
        ("basically", null), ("actually", null), ("literally", null), ("sort of", null), ("kind of", null),
        ("in my opinion", null), ("to be honest", null), ("as you know", null), ("quite", null), ("somewhat", null),
    ];

    private const string Irregular = "written|done|made|given|taken|seen|known|sent|built|held|found|told|shown|kept|left|lost|paid|sold|bought|" +
                                     "brought|chosen|broken|driven|eaten|forgotten|hidden|spoken|stolen|thrown|worn|begun|drawn|grown|run|set|put|cut|read|led|met|won|" +
                                     "caught|taught|thought|felt|heard|laid|said|spent|understood|withdrawn|overseen|undertaken";

    // Words ending in -ed that usually describe a state, not something done to the subject.
    private static readonly HashSet<string> NotPassive = new(StringComparer.OrdinalIgnoreCase)
    {
        "tired", "excited", "interested", "pleased", "worried", "concerned", "surprised", "married", "located", "based", "supposed",
        "used", "involved", "related", "delighted", "thrilled", "bored", "scared", "confused", "satisfied", "disappointed", "embarrassed",
        "committed", "dedicated", "experienced", "qualified", "prepared", "allowed", "inclined", "determined", "ashamed", "depressed",
        "relaxed", "exhausted", "frustrated", "annoyed", "amazed", "impressed", "retired", "stressed", "skilled", "talented", "limited",
        "advanced", "complicated", "detailed", "need", "indeed", "speed", "proceed", "succeed", "exceed", "feed", "bleed", "breed", "seed",
    };

    private static readonly Regex Passive = new(
        $@"(?<![\p{{L}}\p{{N}}'])(?:am|is|are|was|were|be|been|being|get|gets|got)\s+(?:(?:not|also|never|always|often|still|just|\w+ly)\s+){{0,2}}(?<verb>\w+ed|{Irregular})(?![\p{{L}}\p{{N}}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Phrase, passive and readability suggestions for <paramref name="text"/>, for the kinds switched on.</summary>
    public static IEnumerable<WritingSuggestion> Find(string text, WritingChecks checks)
    {
        if (checks.HasFlag(WritingChecks.Cliches))
        {
            foreach (var issue in Phrases(text, Cliches, SuggestionKind.Style, "Cliché", "is a worn phrase. Say what you mean in plain words."))
                yield return issue;
        }
        if (checks.HasFlag(WritingChecks.Wordiness))
        {
            foreach (var issue in Phrases(text, Wordy, SuggestionKind.Clarity, "Wordy", "adds words without adding meaning."))
                yield return issue;
        }
        if (checks.HasFlag(WritingChecks.PassiveVoice))
        {
            foreach (Match match in Passive.Matches(text))
            {
                if (NotPassive.Contains(match.Groups["verb"].Value)) continue;
                yield return new WritingSuggestion(SuggestionKind.Clarity, "Passive voice",
                    "Consider saying who does it: “The team shipped the fix”, not “The fix was shipped”.", match.Value, null, new TextSpan(match.Index, match.Length));
            }
        }
    }

    /// <summary>A sentence-wide readability suggestion, or null if the sentence reads easily enough.</summary>
    public static WritingSuggestion? HardToRead(string text, TextSpan sentence)
    {
        var words = Regex.Matches(text.Substring(sentence.Start, sentence.Length), @"[\p{L}][\p{L}'’-]*").Select(m => m.Value).ToList();
        // Short sentences swing wildly on one long word, so they are left alone.
        if (words.Count < 10) return null;
        var ease = ReadingEase(words);
        if (ease >= 30) return null;
        return new WritingSuggestion(SuggestionKind.Clarity, "Hard to read",
            $"Reading ease is {Math.Max(0, Math.Round(ease))} out of 100; 40 or more reads comfortably. Try shorter words, or split the sentence.",
            text.Substring(sentence.Start, sentence.Length), null, sentence, IsSentenceLevel: true);
    }

    /// <summary>The Flesch reading-ease score of one sentence: higher is easier. It weighs sentence length and syllables per word.</summary>
    public static double ReadingEase(IReadOnlyList<string> words) =>
        words.Count == 0 ? 100 : 206.835 - 1.015 * words.Count - 84.6 * words.Sum(Syllables) / words.Count;

    /// <summary>A close estimate of the syllables in an English word: groups of vowels, less a silent final "e".</summary>
    public static int Syllables(string word)
    {
        var w = new string(word.ToLowerInvariant().Where(char.IsLetter).ToArray());
        if (w.Length <= 3) return 1;
        var count = Regex.Matches(w, "[aeiouy]+").Count;
        // "make" has one syllable, but "table" and "recipe" keep theirs.
        if (w.EndsWith('e') && !w.EndsWith("le") && !Regex.IsMatch(w, "[aeiouy]{2}$") && count > 1) count--;
        if (w.EndsWith("ed") && w.Length > 4 && !Regex.IsMatch(w, "[td]ed$") && count > 1) count--;
        return Math.Max(1, count);
    }

    private static IEnumerable<WritingSuggestion> Phrases(string text, (string Phrase, string? Replacement)[] list, SuggestionKind kind, string title, string why)
    {
        foreach (var (phrase, replacement) in list)
        {
            foreach (var span in WritingEngine.WordSpans(phrase, text))
            {
                var original = text.Substring(span.Start, span.Length);
                var better = replacement is null ? null : WritingEngine.MatchCase(original, replacement);
                yield return new WritingSuggestion(kind, title,
                    better is null ? $"“{original}” {why}" : $"“{original}” {why} Try “{better}”.", original, better, span);
            }
        }
    }
}
