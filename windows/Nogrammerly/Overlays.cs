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
    private (int Size, int Issues, bool Rewriting, float Angle) drawn;
    private bool rewriting;
    private float angle;
    private long lastTurn;
    private readonly System.Windows.Forms.Timer spin = new() { Interval = 33 };
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
        spin.Tick += (_, _) =>
        {
            var now = Environment.TickCount64;
            angle = (angle + 300f * (now - lastTurn) / 1000f) % 360f;
            lastTurn = now;
            if (Visible) Draw();
        };
    }

    /// <summary>Shows the ring while a rewrite the user asked for is under way, and hides it when the rewrite ends.</summary>
    public void SetRewriting(bool value)
    {
        if (rewriting == value) return;
        rewriting = value;
        lastTurn = Environment.TickCount64;
        // People who turn animations off in Windows get a ring that stays still.
        spin.Enabled = value && AnimationsEnabled();
        if (Visible) Draw();
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

    /// <summary>Room around the circle for its shadow, which reaches furthest below it.</summary>
    private int Pad => PadFor(size);

    internal static int PadFor(int size) => (int)Math.Ceiling(size * 0.5);

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
        if (drawn != (size, issues, rewriting, angle)) Draw();
    }

    /// <summary>Paints the shadow, circle and label, and hands the picture to Windows with its transparency.</summary>
    private void Draw()
    {
        if (!IsHandleCreated) return;
        using var bitmap = Render(size, Pad, issues, rewriting, angle);
        if (SetLayeredBitmap(Handle, bitmap, Location)) drawn = (size, issues, rewriting, angle);
    }

    /// <summary>
    /// The button as a picture. Depth comes from three things that mimic light falling from above:
    /// a wide, soft shadow pushed down below the circle (how high it floats), a short dark one hugging its
    /// lower edge (where it meets the page), and a face that is lighter at the top than at the bottom.
    /// </summary>
    internal static Bitmap Render(int size, int pad, int issues, bool rewriting = false, float angle = 0)
    {
        var side = size + 2 * pad;
        var bitmap = new Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // Grey-scale smoothing: ClearType's coloured fringes don't survive a transparent background.
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            var centre = pad + size / 2f;
            var radius = size / 2f;

            // Wide and soft: a little narrower than the circle, so it shows below it rather than all round.
            var blur = size * 0.30f;
            Shadow(g, centre, centre + size * 0.19f, radius - blur * 0.30f, blur, 0.42f);
            // Short and dark, just under the lower edge.
            Shadow(g, centre, centre + size * 0.06f, radius - size * 0.02f, size * 0.10f, 0.45f);

            var colour = issues == 0 ? Theme.Green : Theme.Red;
            var face = new RectangleF(pad, pad, size, size);
            using (var fill = new LinearGradientBrush(new RectangleF(pad, pad - 1, size, size + 2), Shade(colour, 0.16f), Shade(colour, -0.12f), LinearGradientMode.Vertical))
                g.FillEllipse(fill, face);
            // A thin bright rim along the top edge, where the light catches it.
            using (var rim = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, size / 26f)))
                g.DrawArc(rim, RectangleF.Inflate(face, -rim.Width / 2, -rim.Width / 2), 200, 140);

            if (rewriting) Ring(g, centre, radius, size, angle);

            var label = issues == 0 ? "✦" : Math.Min(issues, 99).ToString();
            using var font = new Font("Segoe UI", size * (issues == 0 ? 0.42f : 0.38f), FontStyle.Bold, GraphicsUnit.Pixel);
            using var centred = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, Brushes.White, face, centred);
        }
        return bitmap;
    }

    /// <summary>
    /// A ring of colour just outside the circle, turned by <paramref name="angle"/> degrees, over a wider,
    /// fainter copy that gives it a glow. The colours run blue, violet, pink, amber and back again.
    /// </summary>
    private static void Ring(Graphics g, float centre, float radius, int size, float angle)
    {
        var width = size * 0.11f;
        const float opacity = 1f;
        var r = radius + size * 0.03f + width / 2;
        var side = (int)Math.Ceiling(2 * centre);
        foreach (var (extra, strength) in new[] { (width * 1.5f, opacity * 0.22f), (0f, opacity) })
        {
            // The ring is painted solid on a sheet of its own and then laid down see-through as one piece.
            // Painting see-through arcs directly would show a seam wherever two of them overlap.
            using var layer = new Bitmap(side, side, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var paint = Graphics.FromImage(layer))
            {
                paint.SmoothingMode = SmoothingMode.AntiAlias;
                const int Arcs = 90;
                var box = new RectangleF(centre - r, centre - r, 2 * r, 2 * r);
                for (var i = 0; i < Arcs; i++)
                {
                    using var pen = new Pen(Glow(i / (float)Arcs), width + extra);
                    paint.DrawArc(pen, box, angle + i * 360f / Arcs, 360f / Arcs + 1.5f);
                }
            }
            using var fade = new System.Drawing.Imaging.ImageAttributes();
            fade.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = strength });
            g.DrawImage(layer, new Rectangle(0, 0, side, side), 0, 0, side, side, GraphicsUnit.Pixel, fade);
        }
    }

    private static readonly Color[] GlowStops =
        [
            Color.FromArgb(79, 139, 255), Color.FromArgb(155, 107, 255), Color.FromArgb(255, 95, 162), Color.FromArgb(255, 179, 71),
            // Back the way it came: amber straight to blue would pass through a muddy grey.
            Color.FromArgb(255, 95, 162), Color.FromArgb(155, 107, 255),
        ];

    /// <summary>A colour from the ring's loop of colours; 0 and 1 are the same colour.</summary>
    private static Color Glow(float t)
    {
        var place = t * GlowStops.Length;
        var from = GlowStops[(int)place % GlowStops.Length];
        var to = GlowStops[((int)place + 1) % GlowStops.Length];
        var mix = place - (int)place;
        int Blend(int a, int b) => (int)Math.Round(a + (b - a) * mix);
        return Color.FromArgb(Blend(from.R, to.R), Blend(from.G, to.G), Blend(from.B, to.B));
    }

    private static bool AnimationsEnabled() => !ClientAreaAnimationOff();

    /// <summary>
    /// A blurred black disc: full strength (<paramref name="strength"/>, 0 to 1) out to <paramref name="core"/>,
    /// then fading over <paramref name="blur"/> pixels. It is built from rings whose strengths add up to a curve
    /// that eases out, like a real soft shadow, rather than a straight ramp.
    /// </summary>
    private static void Shadow(Graphics g, float x, float y, float core, float blur, float strength)
    {
        var rings = Math.Max(2, (int)Math.Ceiling(blur * 2));
        for (var i = rings; i >= 1; i--)
        {
            static float Curve(float t) => (1 - t) * (1 - t);
            var alpha = 255 * strength * (Curve((i - 1f) / rings) - Curve((float)i / rings));
            var r = core + blur * i / rings;
            using var ring = new SolidBrush(Color.FromArgb((int)Math.Round(Math.Clamp(alpha, 0, 255)), 0, 0, 0));
            if (ring.Color.A > 0) g.FillEllipse(ring, x - r, y - r, 2 * r, 2 * r);
        }
    }

    /// <summary>Lighter (positive) or darker (negative) by a fraction.</summary>
    private static Color Shade(Color colour, float amount)
    {
        int Mix(int channel) => (int)Math.Clamp(amount >= 0 ? channel + (255 - channel) * amount : channel * (1 + amount), 0, 255);
        return Color.FromArgb(Mix(colour.R), Mix(colour.G), Mix(colour.B));
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
