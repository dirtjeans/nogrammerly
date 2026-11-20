using System.Drawing;
using System.Drawing.Drawing2D;
using Nogrammerly.Core;
using static Nogrammerly.NativeMethods;

namespace Nogrammerly;

/// <summary>
/// A borderless, always-on-top window that never takes focus, so the app being typed in keeps its cursor and selection.
/// Coordinates are physical pixels; the window ignores DPI changes so positions from UI Automation stay exact.
/// </summary>
internal class FloatingWindow : Form
{
    protected FloatingWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED) return;
        base.WndProc(ref m);
    }

    protected void ShowAt(Rectangle bounds)
    {
        Bounds = bounds;
        if (!Visible) Show();
    }
}

/// <summary>Transparent, click-through window that draws coloured underlines over another app's text.</summary>
internal sealed class UnderlineOverlay : FloatingWindow
{
    private static readonly Color Key = Color.FromArgb(255, 0, 255);
    private (Rectangle Rect, Color Color)[] lines = [];

    public UnderlineOverlay()
    {
        BackColor = Key;
        TransparencyKey = Key;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void ShowMarks(IReadOnlyList<Mark> marks)
    {
        var all = marks.SelectMany(m => m.Rects.Select(r => (Rect: r, Color: Theme.Color(m.Suggestion.Kind)))).ToArray();
        if (all.Length == 0) { Hide(); return; }
        var area = all.Select(l => l.Rect).Aggregate(Rectangle.Union);
        area.Inflate(4, 4);
        lines = all.Select(l => (new Rectangle(l.Rect.X - area.X, l.Rect.Y - area.Y, l.Rect.Width, l.Rect.Height), l.Color)).ToArray();
        ShowAt(area);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // No anti-aliasing: blended edge pixels would not match the transparency key and show as a pink fringe.
        foreach (var (rect, color) in lines)
        {
            using var pen = new Pen(color, Math.Max(2, rect.Height / 9));
            e.Graphics.DrawLine(pen, rect.Left, rect.Bottom, rect.Right, rect.Bottom);
        }
    }
}

/// <summary>
/// Grammarly-style round button in the bottom-right corner of the field being edited. The user can drag it
/// somewhere else in the field; it remembers that place, and it steps aside when the text cursor comes close.
/// The window is a little larger than the circle, to leave room for its soft shadow. It is drawn with
/// per-pixel transparency, so the shadow blends with whatever is behind it and clear pixels let clicks through.
/// </summary>
internal sealed class FieldButton : FloatingWindow
{
    private const int BaseSize = 26;
    private readonly string? savePath;
    private int issues;
    private int size = BaseSize;
    private (int Size, int Issues) drawn;
    private Point offset;
    private Rectangle field;
    private Rectangle screen;
    private int dpi = 96;
    private Point pressedAt;
    private Point windowAt;
    private bool pressed;
    private bool dragging;

    /// <param name="savePath">File that remembers where the user dragged the button; null to not remember.</param>
    public FieldButton(Action onClick, string? savePath = null)
    {
        this.savePath = savePath;
        try { if (savePath is not null && File.Exists(savePath)) offset = ButtonPlacement.Parse(File.ReadAllText(savePath)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Cursor = Cursors.Hand;
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            pressed = true; pressedAt = Cursor.Position; windowAt = Location;
        };
        MouseMove += (_, _) =>
        {
            if (!pressed) return;
            var now = Cursor.Position;
            // A small wobble while clicking is still a click.
            var slack = SystemInformation.DragSize;
            if (!dragging && Math.Abs(now.X - pressedAt.X) <= slack.Width && Math.Abs(now.Y - pressedAt.Y) <= slack.Height) return;
            dragging = true;
            Location = new Point(windowAt.X + now.X - pressedAt.X, windowAt.Y + now.Y - pressedAt.Y);
        };
        MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !pressed) return;
            pressed = false;
            if (!dragging) { onClick(); return; }
            dragging = false;
            MoveTo(ButtonPlacement.OffsetFor(field, screen, size, new Point(Left + Pad, Top + Pad), dpi));
        };
        var tip = new ToolTip();
        tip.SetToolTip(this, "Nogrammerly: suggestions and rewrites. Drag to move.");
    }

    public bool IsMoved => offset != Point.Empty;

    public void ResetPosition() => MoveTo(Point.Empty);

    private void MoveTo(Point newOffset)
    {
        offset = newOffset;
        if (Visible) Location = WindowSpot(ButtonPlacement.Place(field, screen, size, offset, dpi));
        if (savePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
            File.WriteAllText(savePath, ButtonPlacement.Format(offset));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // Remembered until the app quits.
    }

    /// <summary>Room around the circle for its shadow.</summary>
    private int Pad => Math.Max(4, size / 3);

    private Point WindowSpot(Point circle) => new(circle.X - Pad, circle.Y - Pad);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED;
            return cp;
        }
    }

    /// <summary><paramref name="fieldFrame"/> and <paramref name="caret"/> are in physical screen pixels.</summary>
    public void ShowFor(Rectangle fieldFrame, int issueCount, Rectangle? caret = null)
    {
        issues = issueCount;
        // While the user is dragging, the button goes where the mouse goes.
        if (!pressed)
        {
            var windowDpi = GetDpiForWindow(GetForegroundWindow());
            dpi = windowDpi == 0 ? 96 : (int)windowDpi;
            size = BaseSize * dpi / 96;
            field = fieldFrame;
            screen = Screen.FromRectangle(field).WorkingArea;
            var side = size + 2 * Pad;
            ShowAt(new Rectangle(WindowSpot(ButtonPlacement.Place(field, screen, size, offset, dpi, caret)), new Size(side, side)));
        }
        if (drawn != (size, issues)) Draw();
    }

    /// <summary>Paints the shadow, circle and label, and hands the picture to Windows with its transparency.</summary>
    private void Draw()
    {
        if (!IsHandleCreated) return;
        using var bitmap = Render(size, Pad, issues);
        if (SetLayeredBitmap(Handle, bitmap, Location)) drawn = (size, issues);
    }

    internal static Bitmap Render(int size, int pad, int issues)
    {
        var side = size + 2 * pad;
        var bitmap = new Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Grey-scale smoothing: ClearType's coloured fringes don't survive a transparent background.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            // The shadow is a stack of faint discs, each a pixel larger, sitting slightly below the circle.
            // Where they overlap they add up, so it is darkest at the circle's edge and fades outwards.
            var drop = Math.Max(1, size / 13);
            // Tight but clear: it reaches only an eighth of the circle's width, and is dark enough to see.
            var spread = Math.Clamp(size / 8, 2, pad - drop);
            using (var shade = new SolidBrush(Color.FromArgb(110 / spread, 0, 0, 0)))
            {
                for (var i = spread; i >= 1; i--) g.FillEllipse(shade, pad - i, pad - i + drop, size + 2 * i - 1, size + 2 * i - 1);
            }
            using var fill = new SolidBrush(issues == 0 ? Theme.Green : Theme.Red);
            g.FillEllipse(fill, pad, pad, size - 1, size - 1);
            var label = issues == 0 ? "✦" : Math.Min(issues, 99).ToString();
            using var font = new Font("Segoe UI", size * (issues == 0 ? 0.42f : 0.38f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var centred = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, Brushes.White, new RectangleF(pad, pad, size, size), centred);
        }
        return bitmap;
    }
}

/// <summary>
/// An invisible window that owns popup menus. Windows only closes a menu on an outside click when the menu's
/// app is in front, so it comes to the front while a menu is open and then hands focus back to the app.
/// </summary>
internal sealed class MenuHost : Form
{
    private IntPtr previous;

    public MenuHost()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(-32000, -32000, 1, 1);
        Opacity = 0;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public void Pop(ContextMenuStrip menu, Point at)
    {
        previous = GetForegroundWindow();
        if (!Visible) Show();
        ForceForeground(Handle);
        menu.Closed += (_, _) => { if (previous != IntPtr.Zero) SetForegroundWindow(previous); };
        menu.Show(this, PointToClient(at));
    }
}

internal static class Theme
{
    public static readonly Color Red = System.Drawing.Color.FromArgb(215, 0, 21);
    public static readonly Color Green = System.Drawing.Color.FromArgb(36, 138, 61);

    public static Color Color(SuggestionKind kind) => kind switch
    {
        SuggestionKind.Correctness => Red,
        SuggestionKind.Clarity => System.Drawing.Color.FromArgb(0, 64, 221),
        SuggestionKind.Delivery => System.Drawing.Color.FromArgb(137, 68, 171),
        _ => System.Drawing.Color.FromArgb(201, 52, 0),
    };
}
