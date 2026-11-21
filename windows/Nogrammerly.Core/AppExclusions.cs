using System.Text.Json;

namespace Nogrammerly.Core;

/// <summary>
/// Apps whose text is never read. Some are left alone from the start, because what is typed in them is
/// secret or isn't prose: password managers, remote-desktop windows and terminals. The user can add any app,
/// and can also switch one of the built-in ones back on. Apps are known by their program name without ".exe".
/// </summary>
public sealed class AppExclusions
{
    public static readonly IReadOnlyList<string> BuiltIn =
    [
        // Password managers.
        "1Password", "Bitwarden", "KeePass", "KeePassXC", "LastPass", "Dashlane", "Keeper", "NordPass", "RoboForm", "Enpass", "ProtonPass",
        // Remote screens: the text belongs to another computer.
        "mstsc", "msrdc", "vmconnect", "TeamViewer", "AnyDesk", "vncviewer",
        // Terminals.
        "WindowsTerminal", "cmd", "powershell", "pwsh", "conhost", "OpenConsole", "putty", "wezterm-gui", "alacritty",
    ];

    private sealed record Saved(string[]? Excluded, string[]? Allowed);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HashSet<string> excluded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> builtIn = new(BuiltIn, StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private readonly string? path;

    public AppExclusions(string? path = null)
    {
        this.path = path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json);
            excluded.UnionWith(saved?.Excluded ?? []);
            allowed.UnionWith(saved?.Allowed ?? []);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { } // Fall back to the built-in list.
    }

    public bool IsExcluded(string app)
    {
        lock (gate) return excluded.Contains(app) || (builtIn.Contains(app) && !allowed.Contains(app));
    }

    /// <summary>The apps the user chose to leave alone, in name order.</summary>
    public IReadOnlyList<string> ChosenByUser
    {
        get { lock (gate) return excluded.Order(StringComparer.OrdinalIgnoreCase).ToList(); }
    }

    public void Set(string app, bool isExcluded)
    {
        lock (gate)
        {
            if (isExcluded) { allowed.Remove(app); if (!builtIn.Contains(app)) excluded.Add(app); }
            else { excluded.Remove(app); if (builtIn.Contains(app)) allowed.Add(app); }
            if (path is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new Saved([.. excluded.Order()], [.. allowed.Order()]), Json));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // Applies until the app quits.
        }
    }
}
