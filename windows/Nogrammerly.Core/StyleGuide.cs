using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nogrammerly.Core;

/// <summary>
/// A house style guide, written as rules the app can check by itself. Each guide is a JSON file in a folder
/// on the user's PC; nothing in it is built into the app or sent anywhere.
/// <code>
/// {
///   "name": "Acme",
///   "terms": [
///     { "avoid": ["web site", "web-site"], "use": "website", "note": "One word." },
///     { "avoid": "zero trust", "use": "Zero Trust" },          // capitals: only the wrong forms are flagged
///     { "avoid": "US", "use": "U.S.", "matchCase": true },     // "us" is left alone
///     { "avoid": "Acme Classic", "note": "Old product name." } // no "use": flagged with the note only
///   ],
///   "patterns": [ { "find": "(\\d+) ?percent\\b", "use": "$1%", "note": "Use the % sign." } ],
///   "firstMentions": [ { "short": "Rocket", "full": "Acme Rocket" } ],   // the full name the first time
///   "acronyms": { "known": ["AI", "IT"] },                              // others are spelled out on first use
///   "serialComma": true,                                               // "a, b, and c", not "a, b and c"
///   "keep": ["lateral movement"],                                      // terms of art the AI must not reword
///   "rules": [                                                         // judgement calls, put to the on-device AI
///     { "id": "second-person", "rule": "Write to the reader as “you”.", "triggers": ["the customer", "users"] }
///   ]
/// }
/// </code>
/// </summary>
public sealed record StyleGuide(string Name, IReadOnlyList<StyleTerm> Terms, IReadOnlyList<StylePattern> Patterns)
{
    /// <summary>Names that must be given in full the first time they appear.</summary>
    public IReadOnlyList<FirstMention> FirstMentions { get; init; } = [];

    /// <summary>Set to ask for acronyms to be spelled out on first use; null to leave acronyms alone.</summary>
    public AcronymRule? Acronyms { get; init; }

    /// <summary>Terms of art that the on-device AI must leave as they are.</summary>
    public IReadOnlyList<string> Keep { get; init; } = [];

    /// <summary>True to ask for a comma before the "and" or "or" that ends a list of three or more.</summary>
    public bool SerialComma { get; init; }

    /// <summary>Rules that need judgement, which only the on-device AI can check.</summary>
    public IReadOnlyList<StyleRule> Rules { get; init; } = [];

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record File(string? Name, List<StyleTerm>? Terms, List<StylePattern>? Patterns,
        List<FirstMention>? FirstMentions, AcronymRule? Acronyms, List<StyleRule>? Rules, bool SerialComma = false, List<string>? Keep = null);

    /// <summary>Reads one guide. Throws <see cref="JsonException"/> if the text isn't a guide.</summary>
    public static StyleGuide Parse(string json, string fallbackName)
    {
        var file = JsonSerializer.Deserialize<File>(json, Json) ?? throw new JsonException("The guide is empty.");
        return new StyleGuide(string.IsNullOrWhiteSpace(file.Name) ? fallbackName : file.Name.Trim(),
            (file.Terms ?? []).Where(t => t.Avoid is { Count: > 0 }).ToList(),
            (file.Patterns ?? []).Where(p => !string.IsNullOrEmpty(p.Find)).ToList())
        {
            FirstMentions = (file.FirstMentions ?? []).Where(m => !string.IsNullOrEmpty(m.Short) && !string.IsNullOrEmpty(m.Full)).ToList(),
            Acronyms = file.Acronyms,
            SerialComma = file.SerialComma,
            Keep = (file.Keep ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).ToList(),
            Rules = (file.Rules ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Rule)).ToList(),
        };
    }
}

/// <param name="Avoid">The wording to flag: one phrase or several. Whole words, any capitals unless <paramref name="MatchCase"/>.</param>
/// <param name="Use">The preferred wording, offered as the fix. Omit it for wording that should just be avoided.</param>
/// <param name="Note">Why, shown to the user.</param>
/// <param name="MatchCase">Flag only this exact capitalisation of <paramref name="Avoid"/>.</param>
/// <param name="Exact">Always write <paramref name="Use"/> exactly so, even at the start of a sentence (such as "iptables").</param>
public sealed record StyleTerm(
    [property: JsonConverter(typeof(OneOrManyConverter))] IReadOnlyList<string>? Avoid,
    string? Use = null, string? Note = null, bool MatchCase = false, bool Exact = false);

/// <param name="Find">A regular expression to flag.</param>
/// <param name="Use">The fix, which may refer to groups in <paramref name="Find"/> as $1, $2. Omit it to flag only.</param>
public sealed record StylePattern(string Find, string? Use = null, string? Note = null);

/// <param name="Short">The short form, matched with these exact capitals, such as "Rocket".</param>
/// <param name="Full">The full name wanted on first mention, such as "Acme Rocket".</param>
public sealed record FirstMention(string Short, string Full, string? Note = null);

/// <param name="Known">Acronyms every reader knows, which need no spelling out.</param>
public sealed record AcronymRule(IReadOnlyList<string>? Known = null, string? Note = null);

/// <summary>
/// A rule that needs judgement. To spare the on-device AI, a rule is only put to it for a paragraph that
/// contains one of its <paramref name="Triggers"/> or matches <paramref name="When"/>; a rule with neither is
/// put to it for every paragraph.
/// </summary>
/// <param name="Id">A short name. A lower guide's rule with the same id as a higher guide's is dropped.</param>
/// <param name="Rule">The rule, in a sentence or two, as the AI will read it.</param>
/// <param name="Triggers">Words or phrases that make the rule worth checking.</param>
/// <param name="When">A regular expression that makes the rule worth checking.</param>
/// <param name="Wrong">A short example that breaks the rule.</param>
/// <param name="Right">The same example, corrected.</param>
public sealed record StyleRule(string Id, string Rule, IReadOnlyList<string>? Triggers = null, string? When = null, string? Wrong = null, string? Right = null);

/// <summary>Lets "avoid" be written as one string or a list of them.</summary>
internal sealed class OneOrManyConverter : JsonConverter<IReadOnlyList<string>?>
{
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString() is { Length: > 0 } one ? [one] : [],
        JsonTokenType.StartArray => (JsonSerializer.Deserialize<List<string>>(ref reader, options) ?? []).Where(s => !string.IsNullOrEmpty(s)).ToList(),
        JsonTokenType.Null => null,
        _ => throw new JsonException("\"avoid\" must be a phrase or a list of phrases."),
    };

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string>? value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, options);
}

/// <summary>
/// The user's guides, in order of authority: where two guides disagree, the earlier one wins.
/// Guides are files named so that they sort in that order, such as <c>1-house.json</c> and <c>2-microsoft.json</c>.
/// </summary>
public sealed class StyleGuides
{
    private static readonly TimeSpan PatternLimit = TimeSpan.FromMilliseconds(50);
    private readonly List<(StyleGuide Guide, List<StyleTerm> Terms)> guides = [];
    private readonly HashSet<string> preferredWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Guide, StyleRule Rule)> rules = [];
    private readonly HashSet<string> protectedTerms = new(StringComparer.OrdinalIgnoreCase);

    public static readonly StyleGuides None = new([]);

    /// <summary>Names of the guides in use, most authoritative first.</summary>
    public IReadOnlyList<string> Names => guides.Select(g => g.Guide.Name).ToList();

    /// <summary>Files that could not be read as guides, with the reason, so the user can be told.</summary>
    public IReadOnlyList<string> Problems { get; }

    public StyleGuides(IEnumerable<StyleGuide> inOrder, IReadOnlyList<string>? problems = null)
    {
        Problems = problems ?? [];
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var guide in inOrder)
        {
            // A lower guide may not flag wording a higher guide prefers, nor rule again on wording a higher guide
            // has ruled on. So "percent" in one guide can't undo "%" in a guide above it.
            var allowed = new List<StyleTerm>();
            foreach (var term in guide.Terms)
            {
                var avoid = term.Avoid!.Where(a => !settled.Contains(a)).ToList();
                if (avoid.Count > 0) allowed.Add(term with { Avoid = avoid });
            }
            guides.Add((guide, allowed));
            protectedTerms.UnionWith(guide.Keep);
            protectedTerms.UnionWith(guide.FirstMentions.Select(m => m.Full));
            // A judgement rule a higher guide already has, by id, is not asked twice.
            foreach (var rule in guide.Rules)
            {
                if (rules.All(r => !string.Equals(r.Rule.Id, rule.Id, StringComparison.OrdinalIgnoreCase))) rules.Add((guide.Name, rule));
            }
            foreach (var term in guide.Terms)
            {
                settled.UnionWith(term.Avoid!);
                if (term.Use is null) continue;
                settled.Add(term.Use);
                protectedTerms.Add(term.Use);
                // The spell checker shouldn't argue with the guide over a word the guide asks for.
                foreach (Match word in Regex.Matches(term.Use, @"[\p{L}][\p{L}\p{N}'’-]*")) preferredWords.Add(word.Value);
            }
        }
    }

    /// <summary>Loads every <c>*.json</c> guide in <paramref name="folder"/>, in file-name order. A missing folder means no guides.</summary>
    public static StyleGuides Load(string folder)
    {
        if (!Directory.Exists(folder)) return None;
        var loaded = new List<StyleGuide>();
        var problems = new List<string>();
        foreach (var path in Directory.GetFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            try { loaded.Add(StyleGuide.Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path))); }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { problems.Add($"{Path.GetFileName(path)}: {e.Message}"); }
        }
        return new StyleGuides(loaded, problems);
    }

    /// <summary>
    /// True when a suggested change would reword a term the guides settle: one they prefer, or one they say to
    /// keep. The on-device AI sometimes "improves" a term of art, and a guide outranks it.
    /// </summary>
    public bool Protects(string original, string? replacement) =>
        protectedTerms.Any(term => term.Length > 1 && WritingEngine.WordSpans(term, original).Any()
                                   && (replacement is null || !replacement.Contains(term, StringComparison.OrdinalIgnoreCase)));

    /// <summary>True when any guide has rules for the on-device AI.</summary>
    public bool HasRules => rules.Count > 0;

    /// <summary>
    /// The judgement rules worth putting to the AI for <paramref name="paragraph"/>, most authoritative guide
    /// first. Rules that named what triggers them come before rules that apply to everything.
    /// </summary>
    public IReadOnlyList<(string Guide, StyleRule Rule)> RulesFor(string paragraph)
    {
        var triggered = new List<(string, StyleRule)>();
        var always = new List<(string, StyleRule)>();
        foreach (var (guide, rule) in rules)
        {
            var conditional = rule.Triggers is { Count: > 0 } || !string.IsNullOrEmpty(rule.When);
            if (!conditional) { always.Add((guide, rule)); continue; }
            var hit = rule.Triggers?.Any(t => WritingEngine.WordSpans(t, paragraph).Any()) == true;
            if (!hit && !string.IsNullOrEmpty(rule.When))
            {
                try { hit = Regex.IsMatch(paragraph, rule.When, RegexOptions.None, PatternLimit); }
                catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { }
            }
            if (hit) triggered.Add((guide, rule));
        }
        return [.. triggered, .. always];
    }

    /// <summary>True for a word a guide asks for, which the spell checker should therefore accept.</summary>
    public bool Prefers(string word) => preferredWords.Contains(word);

    /// <summary>Everything in <paramref name="text"/> that breaks a guide. Where guides overlap, the earlier guide's finding is kept.</summary>
    public IReadOnlyList<WritingSuggestion> Find(string text)
    {
        if (guides.Count == 0) return [];
        var starts = WritingEngine.SentenceSpans(text).Select(s => s.Start).ToHashSet();
        var found = new List<WritingSuggestion>();
        foreach (var (guide, terms) in guides)
        {
            var title = $"{guide.Name} style";
            // Longer phrases first, so "Zero Trust Segmentation" is judged before "zero trust" inside it.
            foreach (var (term, avoid) in terms.SelectMany(t => t.Avoid!.Select(a => (t, a))).OrderByDescending(p => p.a.Length))
            {
                foreach (var span in WritingEngine.WordSpans(avoid, text))
                {
                    var original = text.Substring(span.Start, span.Length);
                    if (term.MatchCase && original != avoid) continue;
                    var fix = term.Use is null ? null : Cased(term, original, starts.Contains(span.Start));
                    if (fix == original) continue; // Already written the preferred way.
                    found.Add(new WritingSuggestion(SuggestionKind.Style, title, Explain(term.Note, original, fix), original, fix, span));
                }
            }
            foreach (var pattern in guide.Patterns)
            {
                try
                {
                    foreach (Match match in Regex.Matches(text, pattern.Find, RegexOptions.None, PatternLimit))
                    {
                        if (match.Length == 0) continue;
                        var fix = pattern.Use is null ? null : match.Result(pattern.Use);
                        if (fix == match.Value) continue;
                        found.Add(new WritingSuggestion(SuggestionKind.Style, title, Explain(pattern.Note, match.Value, fix), match.Value, fix, new TextSpan(match.Index, match.Length)));
                    }
                }
                catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException) { } // A bad or slow pattern is skipped, not fatal.
            }
            foreach (var mention in guide.FirstMentions)
            {
                if (FirstShortMention(text, mention, starts) is { } span)
                    found.Add(new WritingSuggestion(SuggestionKind.Style, title, mention.Note ?? $"Write “{mention.Full}” in full the first time. After that, “{mention.Short}” is fine.",
                        mention.Short, mention.Full, span));
            }
            if (guide.SerialComma)
            {
                foreach (var (span, fix) in MissingSerialCommas(text))
                    found.Add(new WritingSuggestion(SuggestionKind.Style, title, "Use the serial comma: put a comma before the “and” or “or” that ends a list.",
                        text.Substring(span.Start, span.Length), fix, span));
            }
            if (guide.Acronyms is { } acronyms)
            {
                foreach (var (acronym, span) in UnexplainedAcronyms(text, acronyms))
                    found.Add(new WritingSuggestion(SuggestionKind.Style, title,
                        acronyms.Note ?? $"Spell out “{acronym}” the first time, with the acronym after it in parentheses.", acronym, null, span));
            }
        }
        return WritingEngine.Merge(found, []);
    }

    /// <summary>
    /// Where the short name appears before the full one has been given, if it does. A short name that opens a
    /// sentence is passed over: there it may be the ordinary word, capitalised only by its position.
    /// </summary>
    private static TextSpan? FirstShortMention(string text, FirstMention mention, HashSet<int> sentenceStarts)
    {
        var full = text.IndexOf(mention.Full, StringComparison.Ordinal);
        foreach (var span in WritingEngine.WordSpans(mention.Short, text))
        {
            // Once the full name has been given, the short one is fine from there on.
            if (full >= 0 && span.Start >= full) return null;
            if (text.Substring(span.Start, span.Length) != mention.Short || sentenceStarts.Contains(span.Start)) continue;
            return span;
        }
        return null;
    }

    /// <summary>
    /// Acronyms whose first appearance has no explanation: the text never gives the acronym in parentheses
    /// after the words it stands for.
    /// </summary>
    private static IEnumerable<(string Acronym, TextSpan Span)> UnexplainedAcronyms(string text, AcronymRule rule)
    {
        var known = new HashSet<string>(rule.Known ?? [], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"(?<![\p{L}\p{N}.&/-])[A-Z][A-Z0-9]{1,5}s?(?![\p{L}\p{N}&/-]|\.[A-Z])"))
        {
            var acronym = match.Value.TrimEnd('s');
            if (acronym.Length < 2 || !seen.Add(acronym) || known.Contains(acronym)) continue;
            if (text.Contains($"({acronym})", StringComparison.Ordinal)) continue;
            // A line all in capitals is a heading or a shout, not a string of acronyms.
            var lineStart = text.LastIndexOfAny(LineBreaks, match.Index) + 1;
            var lineEnd = text.IndexOfAny(LineBreaks, match.Index);
            var letters = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Where(char.IsLetter).ToList();
            if (letters.Count > acronym.Length + 1 && letters.All(char.IsUpper)) continue;
            yield return (acronym, new TextSpan(match.Index, acronym.Length));
        }
    }

    private static readonly char[] LineBreaks = ['\r', '\n'];

    // Words that open a sentence and take a comma without starting a list: "In 2020, sales and marketing merged."
    private static readonly HashSet<string> Openers = new(StringComparer.OrdinalIgnoreCase)
    {
        "in", "on", "at", "by", "for", "with", "without", "after", "before", "during", "since", "until", "from", "to", "of", "over", "under",
        "when", "while", "if", "as", "although", "though", "because", "once", "unless", "whether", "where", "given", "despite",
        "however", "therefore", "also", "then", "finally", "first", "second", "third", "next", "meanwhile", "instead", "otherwise", "still",
        "yes", "no", "well", "so", "now", "here", "there", "indeed", "thus", "moreover", "furthermore", "today", "yesterday", "tomorrow",
        "sometimes", "often", "usually", "recently", "currently", "overall", "generally", "typically", "ultimately", "fortunately",
        "unfortunately", "similarly", "likewise", "again", "besides", "nevertheless", "nonetheless", "hi", "hello", "dear", "thanks",
    };

    private static readonly Regex ListOfThree = new(
        @"(?<=[\p{L}\p{N}]), (?<second>[\p{L}\p{N}’'-]+(?: [\p{L}\p{N}’'-]+){0,2}) (?<joiner>and|or) (?=[\p{L}\p{N}])", RegexOptions.Compiled);

    /// <summary>
    /// Lists written "a, b and c". This is a pattern, not grammar, so it looks only for the plainest case: a
    /// comma, then one to three words, then "and" or "or". A comma that only follows a sentence opener, as in
    /// "In 2020, sales and marketing merged", is passed over. Each result covers "b and", with "b, and" as the fix.
    /// </summary>
    private static IEnumerable<(TextSpan Span, string Fix)> MissingSerialCommas(string text)
    {
        foreach (var sentence in WritingEngine.SentenceSpans(text))
        {
            var body = text.Substring(sentence.Start, sentence.Length);
            foreach (Match match in ListOfThree.Matches(body))
            {
                var before = body[..match.Index];
                // An earlier comma means a list is already under way, whatever the sentence opened with.
                var opener = !before.Contains(',') && Openers.Contains(before.Split(' ', 2)[0]);
                // "a, b and c, d" is a different shape; and a number before the comma is usually a date or a figure.
                if (opener || before.Length == 0 || char.IsDigit(before[^1])) continue;
                var second = match.Groups["second"];
                var start = sentence.Start + second.Index;
                var length = match.Groups["joiner"].Index + match.Groups["joiner"].Length - second.Index;
                yield return (new TextSpan(start, length), $"{second.Value}, {match.Groups["joiner"].Value}");
            }
        }
    }

    /// <summary>
    /// The preferred wording with the right capitals. A lower-case term takes a capital at the start of a
    /// sentence, as any word does, unless the guide says to write it exactly as given.
    /// </summary>
    private static string Cased(StyleTerm term, string original, bool startsSentence)
    {
        var use = term.Use!;
        if (term.Exact || use.Length == 0 || use.Any(char.IsUpper)) return use;
        return startsSentence && char.IsUpper(original[0]) ? char.ToUpperInvariant(use[0]) + use[1..] : use;
    }

    private static string Explain(string? note, string original, string? fix) =>
        (fix, note) switch
        {
            (null, null) => $"The style guide says to avoid “{original}”.",
            (null, _) => note!,
            ("", null) => $"The style guide says to remove “{original}”.",
            (_, null) => $"The style guide says “{fix}”, not “{original}”.",
            _ => $"{note} Use “{fix}”.",
        };
}
