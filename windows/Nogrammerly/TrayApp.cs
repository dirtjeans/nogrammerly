using System.Drawing;
using System.Runtime.InteropServices;
using Nogrammerly.Core;
using static Nogrammerly.NativeMethods;

namespace Nogrammerly;

/// <summary>
/// The app's UI: a notification-area icon with the field's suggestions, underlines over the field,
/// a round button in the field's corner, and a menu when the user right-clicks an underline.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly SynchronizationContext ui = SynchronizationContext.Current!;
    private static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nogrammerly");
    private static readonly string BridgeFile = Path.Combine(DataFolder, "bridge.json");
    private static readonly string WordManifest = Path.Combine(DataFolder, "word-addin.xml");
    private readonly IgnoreList ignored = new(Path.Combine(DataFolder, "ignored.json"));
    private readonly SystemWritingAssistant assistant;
    private BridgeSettings bridgeSettings = BridgeSettings.Load(BridgeFile);
    private LocalBridge? bridge;
    private AddinHost? addinHost;
    private readonly NotifyIcon tray = new();
    private readonly UnderlineOverlay overlay = new();
    private readonly FieldButton fieldButton;
    private readonly MenuHost menuHost = new();
    private readonly LowLevelMouseProc hookProc;
    private readonly IntPtr hook;
    private FieldState state = FieldState.None;
    private bool swallowRightButtonUp;

    public TrayApp(bool firstRun)
    {
        assistant = new SystemWritingAssistant(ignored);
        fieldButton = new FieldButton(() => ShowMenu(BuildFieldMenu()), Path.Combine(DataFolder, "button.txt"));
        tray.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        tray.Text = "Nogrammerly";
        tray.Visible = true;
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Opening += (_, e) => { RebuildTrayMenu(tray.ContextMenuStrip); e.Cancel = false; };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMenu(BuildFieldMenu()); };

        assistant.StateChanged += Render;
        assistant.Notice += message => tray.ShowBalloonTip(4000, "Nogrammerly", message, ToolTipIcon.None);

        // Kept in a field: the hook must not be garbage collected while Windows still calls it.
        hookProc = MouseHook;
        hook = SetWindowsHookEx(WH_MOUSE_LL, hookProc, GetModuleHandle(null), 0);

        _ = AIWriter.PrewarmAsync();
        if (bridgeSettings.Enabled) StartBridge();
        if (bridgeSettings.WordAddin) StartAddinHost(AddinSetup.FindCertificate());
        if (firstRun) Welcome();
    }

    // The bridge for add-ins in other apps. Off until the user turns it on, because it opens a port on this PC.

    private void StartBridge()
    {
        bridge = LocalBridge.ForApp(bridgeSettings, ignored);
        if (bridge.Start()) return;
        bridge.Dispose();
        bridge = null;
        tray.ShowBalloonTip(4000, "Nogrammerly", $"The add-in bridge couldn't start. Port {bridgeSettings.Port} may be in use.", ToolTipIcon.None);
    }

    private void SetBridgeEnabled(bool enabled) => Apply(bridgeSettings with { Enabled = enabled });

    /// <summary>Saves new settings and restarts the bridge with them.</summary>
    private void Apply(BridgeSettings settings)
    {
        bridge?.Dispose();
        bridge = null;
        bridgeSettings = settings;
        try { bridgeSettings.Save(BridgeFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (settings.Enabled) StartBridge();
    }

    // The Word add-in. The app serves the add-in's pages itself, so Word needs nothing else running.

    private void StartAddinHost(System.Security.Cryptography.X509Certificates.X509Certificate2? certificate)
    {
        addinHost?.Dispose();
        addinHost = null;
        if (certificate is null)
        {
            tray.ShowBalloonTip(4000, "Nogrammerly", "The Word add-in's certificate is missing or out of date. Choose Set up Word add-in again.", ToolTipIcon.None);
            return;
        }
        addinHost = new AddinHost(certificate, bridgeSettings.SitePort, AddinSetup.SiteRoot, () => bridgeSettings.PairingKey);
        if (addinHost.Start()) return;
        addinHost.Dispose();
        addinHost = null;
        tray.ShowBalloonTip(4000, "Nogrammerly", $"The Word add-in couldn't start. Port {bridgeSettings.SitePort} may be in use.", ToolTipIcon.None);
    }

    private void SetUpWordAddin()
    {
        var answer = MessageBox.Show(
            "Nogrammerly can check your documents inside Word.\n\n" +
            "Word only loads add-ins from a secure address, so Nogrammerly will make a security certificate for this PC. " +
            "It is for the address \"localhost\" only and can't be used for any other site. Windows will ask you to confirm it.\n\n" +
            "Nothing you write leaves this PC.\n\nSet up the Word add-in now?",
            "Nogrammerly", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        try
        {
            var certificate = AddinSetup.FindCertificate() ?? AddinSetup.CreateCertificate();
            if (certificate is null)
            {
                MessageBox.Show("The certificate wasn't added, so the Word add-in was not set up.", "Nogrammerly", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            AddinSetup.RegisterWithWord(bridgeSettings.SitePort, WordManifest);
            // The add-in talks to the app over the bridge, so that goes on too.
            Apply(bridgeSettings with { Enabled = true, WordAddin = true });
            StartAddinHost(certificate);
            MessageBox.Show("The Word add-in is set up.\n\nClose Word if it is open, then start it again and choose Nogrammerly on the Home tab.",
                "Nogrammerly", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show($"The Word add-in couldn't be set up: {e.Message}", "Nogrammerly", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RemoveWordAddin()
    {
        addinHost?.Dispose();
        addinHost = null;
        try { AddinSetup.RemoveFromWord(WordManifest); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        AddinSetup.RemoveCertificates();
        Apply(bridgeSettings with { WordAddin = false });
    }

    private void AddBridgeItems(ToolStripItemCollection items)
    {
        var menu = new ToolStripMenuItem("Add-ins for other apps");
        if (bridgeSettings.WordAddin) menu.DropDownItems.Add("Remove Word add-in", null, (_, _) => RemoveWordAddin());
        else menu.DropDownItems.Add("Set up Word add-in…", null, (_, _) => SetUpWordAddin());
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Let add-ins on this PC use Nogrammerly", null, (_, _) => SetBridgeEnabled(bridge is null)) { Checked = bridge is not null });
        if (bridge is not null)
            menu.DropDownItems.Add("Copy add-in key", null, (_, _) => Clipboard.SetText(bridgeSettings.PairingKey));
        items.Add(menu);
    }

    private void Render(FieldState newState)
    {
        state = newState;
        overlay.ShowMarks(state.Marks);
        if (state.Frame is { } frame) fieldButton.ShowFor(frame, state.Suggestions.Count, state.Caret); else fieldButton.Hide();
        var count = state.Suggestions.Count;
        tray.Text = !assistant.IsRunning ? "Nogrammerly (paused)" : count == 0 ? "Nogrammerly" : $"Nogrammerly: {count} suggestion{(count == 1 ? "" : "s")}";
    }

    // Right-click on an underline. Runs on the UI thread for every mouse event system-wide, so it must stay quick.

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && assistant.IsRunning)
        {
            if (message == WM_RBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
                var point = new Point(info.pt.X, info.pt.Y);
                var hit = state.Marks.FirstOrDefault(m => m.Rects.Any(r => Rectangle.Inflate(r, 2, 4).Contains(point)));
                if (hit is not null)
                {
                    swallowRightButtonUp = true;
                    ui.Post(_ => ShowMenu(BuildSuggestionMenu(hit.Suggestion)), null);
                    return 1;
                }
            }
            else if (message == WM_RBUTTONUP && swallowRightButtonUp)
            {
                swallowRightButtonUp = false;
                return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    // Menus.

    private void ShowMenu(ContextMenuStrip menu) => menuHost.Pop(menu, Cursor.Position);

    private ContextMenuStrip BuildSuggestionMenu(WritingSuggestion suggestion)
    {
        var menu = new ContextMenuStrip();
        AddSuggestionItems(menu.Items, suggestion);
        return menu;
    }

    private ContextMenuStrip BuildFieldMenu()
    {
        var menu = new ContextMenuStrip();
        var count = state.Suggestions.Count;
        Header(menu.Items, !state.IsActive ? "Click into a text field to check it" : count == 0 ? "No issues found" : $"{count} suggestion{(count == 1 ? "" : "s")}");
        if (state.IsActive)
        {
            menu.Items.Add(new ToolStripSeparator());
            AddFieldItems(menu.Items);
        }
        return menu;
    }

    private void RebuildTrayMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var count = state.Suggestions.Count;
        Header(menu.Items, !assistant.IsRunning ? "Paused" : !state.IsActive ? "Watching for a text field…" : $"{count} suggestion{(count == 1 ? "" : "s")} in this field");
        AddAIStatus(menu.Items);
        if (assistant.IsRunning && state.IsActive)
        {
            menu.Items.Add(new ToolStripSeparator());
            AddFieldItems(menu.Items);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(assistant.IsRunning ? "Pause" : "Resume", null, (_, _) => { assistant.SetRunning(!assistant.IsRunning); Render(FieldState.None); });
        if (fieldButton.IsMoved) menu.Items.Add("Put the round button back in the corner", null, (_, _) => fieldButton.ResetPosition());
        AddBridgeItems(menu.Items);
        menu.Items.Add("How to use…", null, (_, _) => Welcome());
        menu.Items.Add("Quit Nogrammerly", null, (_, _) => ExitThread());
    }

    private void AddAIStatus(ToolStripItemCollection items)
    {
        switch (AIWriter.Check())
        {
            case AIWriter.Availability.Ready:
                Header(items, "On-device AI is on");
                break;
            case AIWriter.Availability.NeedsDownload:
                items.Add("Set up on-device AI…", null, async (_, _) => await OfferDownload());
                break;
            case AIWriter.Availability.DisabledByUser:
                Header(items, "On-device AI is turned off in Windows Settings");
                break;
            case AIWriter.Availability.Unsupported:
                Header(items, "On-device AI needs a Copilot+ PC");
                break;
            case AIWriter.Availability.NeedsInstall:
                Header(items, "Install with Install.cmd for on-device AI");
                break;
        }
    }

    private async Task OfferDownload()
    {
        var answer = MessageBox.Show(
            "Nogrammerly can use the language model built into Windows for AI grammar checks and rewrites. " +
            "It runs entirely on this PC.\n\nWindows may need to download it first (it can be several GB, through Windows Update). " +
            "You can remove it later in Settings › System › AI Components.\n\nSet it up now?",
            "Nogrammerly", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        tray.ShowBalloonTip(4000, "Nogrammerly", "Setting up on-device AI. Progress is shown in Settings › Windows Update.", ToolTipIcon.None);
        var ready = await AIWriter.DownloadAsync();
        tray.ShowBalloonTip(4000, "Nogrammerly", ready ? "On-device AI is ready." : "On-device AI couldn't be set up on this PC.", ToolTipIcon.None);
    }

    /// <summary>Suggestions for the focused field plus rewrite actions; shared by the field button and the tray icon.</summary>
    private void AddFieldItems(ToolStripItemCollection items)
    {
        foreach (var suggestion in state.Suggestions.Take(15))
        {
            var original = suggestion.Original.Length > 40 ? suggestion.Original[..40] + "…" : suggestion.Original;
            var item = new ToolStripMenuItem($"{original} — {suggestion.Title}");
            AddSuggestionItems(item.DropDownItems, suggestion);
            items.Add(item);
        }
        if (state.Suggestions.Count > 0) items.Add(new ToolStripSeparator());
        if (state.HasSelection) items.Add(ToneMenu("Rewrite selection", assistant.RewriteSelection));
        items.Add(ToneMenu("Rewrite entire text", assistant.RewriteAll));
    }

    private void AddSuggestionItems(ToolStripItemCollection items, WritingSuggestion suggestion)
    {
        Header(items, $"{suggestion.Kind} · {suggestion.Title}");
        Header(items, suggestion.Explanation);
        items.Add(new ToolStripSeparator());
        if (suggestion.Replacement is { } replacement)
        {
            var bold = items.Add($"Replace with “{(replacement.Length == 0 ? "(remove)" : replacement)}”", null, (_, _) => assistant.Apply(suggestion));
            bold.Font = new Font(bold.Font, FontStyle.Bold);
            items.Add("Copy suggestion", null, (_, _) => { if (replacement.Length > 0) Clipboard.SetText(replacement); });
        }
        items.Add(ToneMenu("Rewrite sentence", tone => assistant.RewriteSentence(suggestion, tone)));
        items.Add(new ToolStripSeparator());
        items.Add("Ignore", null, (_, _) => assistant.Ignore(suggestion));
        if (suggestion.IsSpelling) items.Add($"Add “{suggestion.Original}” to dictionary", null, (_, _) => assistant.Learn(suggestion));
    }

    private static ToolStripMenuItem ToneMenu(string title, Action<WritingTone> action)
    {
        var item = new ToolStripMenuItem(title);
        foreach (var tone in Enum.GetValues<WritingTone>()) item.DropDownItems.Add(tone.ToString(), null, (_, _) => action(tone));
        return item;
    }

    private static void Header(ToolStripItemCollection items, string text) => items.Add(new ToolStripMenuItem(text) { Enabled = false });

    private void Welcome()
    {
        var ai = AIWriter.Check() switch
        {
            AIWriter.Availability.Ready => "On-device AI is on: grammar, clarity and tone checks and rewrites use the language model built into Windows.",
            AIWriter.Availability.NeedsDownload => "On-device AI is available on this PC. Choose “Set up on-device AI” from the tray icon menu to turn it on.",
            AIWriter.Availability.NeedsInstall => "To use on-device AI on a Copilot+ PC, install Nogrammerly with Install.cmd.",
            _ => "On-device AI isn't available on this PC, so Nogrammerly uses the Windows spell checker and its built-in style rules.",
        };
        MessageBox.Show(
            "Nogrammerly is running in the notification area (bottom-right, near the clock; look under ^ if you don't see ✦).\n\n" +
            "• Type in almost any app. Issues are underlined: red = correctness, blue = clarity, purple = delivery, orange = style.\n" +
            "• Right-click an underline to replace it, copy the suggestion, rewrite the sentence, or ignore it.\n" +
            "• Click the round button in the corner of the text box to see every suggestion, or rewrite your selection or the whole text.\n\n" +
            ai + "\n\nNothing you type leaves this PC.",
            "Welcome to Nogrammerly", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    protected override void ExitThreadCore()
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        bridge?.Dispose();
        addinHost?.Dispose();
        tray.Visible = false;
        tray.Dispose();
        assistant.Dispose();
        base.ExitThreadCore();
    }
}
