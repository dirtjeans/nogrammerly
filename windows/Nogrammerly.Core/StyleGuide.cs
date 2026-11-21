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
///   "patterns": [ { "find": "(\\d+) ?percent\\b", "use": "$1%", "note": "Use the % sign." } ]
/// }
/// </code>
/// </summary>
public sealed record StyleGuide(string Name, IReadOnlyList<StyleTerm> Terms, IReadOnlyList<StylePattern> Patterns)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record File(string? Name, List<StyleTerm>? Terms, List<StylePattern>? Patterns);

    /// <summary>Reads one guide. Throws <see cref="JsonException"/> if the text isn't a guide.</summary>
    public static StyleGuide Parse(string json, string fallbackName)
    {
        var file = JsonSerializer.Deserialize<File>(json, Json) ?? throw new JsonException("The guide is empty.");
        return new StyleGuide(string.IsNullOrWhiteSpace(file.Name) ? fallbackName : file.Name.Trim(),
            (file.Terms ?? []).Where(t => t.Avoid is { Count: > 0 }).ToList(),
            (file.Patterns ?? []).Where(p => !string.IsNullOrEmpty(p.Find)).ToList());
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
            foreach (var term in guide.Terms)
            {
                settled.UnionWith(term.Avoid!);
                if (term.Use is null) continue;
                settled.Add(term.Use);
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
        }
        return WritingEngine.Merge(found, []);
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
