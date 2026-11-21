using System.Drawing;

namespace Nogrammerly.Core;

/// <summary>
/// Where the round field button sits. By default that is the field's bottom-right corner. The user can drag it;
/// the drag is remembered as an offset from that corner, so the button keeps its place in fields of any size.
/// It also steps aside when the text cursor comes close, so it never covers what is being typed.
/// All rectangles are physical screen pixels; the saved offset is in pixels at 100% scale.
/// </summary>
public static class ButtonPlacement
{
    private static Point Corner(Rectangle field, int size) => new(field.Right - size - size / 4, field.Bottom - size - size / 4);

    public static Point Place(Rectangle field, Rectangle screen, int size, Point offset, int dpi, Rectangle? caret = null)
    {
        var corner = Corner(field, size);
        var spot = Clamp(new Point(corner.X + offset.X * dpi / 96, corner.Y + offset.Y * dpi / 96), field, screen, size);
        if (caret is not { } cursor) return spot;

        // Keep a button's width of clear space around the text cursor.
        var keepClear = Rectangle.Inflate(cursor, size, size / 2);
        if (!keepClear.IntersectsWith(new Rectangle(spot, new Size(size, size)))) return spot;
        // Try above the cursor's line, then below it, then to its left; take the first that fits in the field.
        Point[] choices = [new(spot.X, keepClear.Top - size), new(spot.X, keepClear.Bottom), new(keepClear.Left - size, spot.Y)];
        foreach (var choice in choices)
        {
            if (Clamp(choice, field, screen, size) == choice) return choice;
        }
        return spot; // A field too small to dodge in: stay put.
    }

    /// <summary>The offset to remember after the user drops the button at <paramref name="dropped"/>.</summary>
    public static Point OffsetFor(Rectangle field, Rectangle screen, int size, Point dropped, int dpi)
    {
        var corner = Corner(field, size);
        var kept = Clamp(dropped, field, screen, size);
        return new Point((kept.X - corner.X) * 96 / dpi, (kept.Y - corner.Y) * 96 / dpi);
    }

    /// <summary>Keeps the button inside the field where the field is big enough, and always on the screen.</summary>
    private static Point Clamp(Point spot, Rectangle field, Rectangle screen, int size)
    {
        if (field.Width >= size) spot.X = Math.Clamp(spot.X, field.Left, field.Right - size);
        if (field.Height >= size) spot.Y = Math.Clamp(spot.Y, field.Top, field.Bottom - size);
        return new Point(
            Math.Clamp(spot.X, screen.Left, Math.Max(screen.Left, screen.Right - size)),
            Math.Clamp(spot.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - size)));
    }

    public static string Format(Point offset) => $"{offset.X},{offset.Y}";

    /// <summary>Reads a saved offset; anything unreadable means "not moved".</summary>
    public static Point Parse(string? saved)
    {
        var parts = (saved ?? "").Trim().Split(',');
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y) && Math.Abs(x) < 20_000 && Math.Abs(y) < 20_000
            ? new Point(x, y)
            : Point.Empty;
    }
}
