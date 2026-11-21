using System.Drawing;
using Nogrammerly.Core;
using Xunit;

public class ButtonPlacementTests
{
    private static readonly Rectangle Screen = new(0, 0, 1920, 1080);
    private static readonly Rectangle Field = new(100, 100, 800, 400); // Right edge 900, bottom 500.
    private const int Size = 26;

    [Fact]
    public void SitsInTheBottomRightCornerByDefault() =>
        Assert.Equal(new Point(868, 468), ButtonPlacement.Place(Field, Screen, Size, Point.Empty, 96));

    [Fact]
    public void ADragIsRememberedAndFollowsTheFieldAround()
    {
        var offset = ButtonPlacement.OffsetFor(Field, Screen, Size, new Point(468, 268), 96);
        Assert.Equal(new Point(-400, -200), offset);
        Assert.Equal(new Point(468, 268), ButtonPlacement.Place(Field, Screen, Size, offset, 96));
        var moved = new Rectangle(300, 50, 800, 400);
        Assert.Equal(new Point(668, 218), ButtonPlacement.Place(moved, Screen, Size, offset, 96));
    }

    [Fact]
    public void TheOffsetScalesWithTheDisplay()
    {
        var offset = ButtonPlacement.OffsetFor(Field, Screen, 52, new Point(635, 235), 192);
        Assert.Equal(new Point(-100, -100), offset);
        Assert.Equal(new Point(768, 368), ButtonPlacement.Place(Field, Screen, Size, offset, 96));
    }

    [Fact]
    public void StaysInsideTheFieldAndOnTheScreen()
    {
        Assert.Equal(new Point(100, 100), ButtonPlacement.Place(Field, Screen, Size, new Point(-5000, -5000), 96));
        // Dropped far outside, it lands in the field's very corner: 6 pixels past the default spot each way.
        Assert.Equal(new Point(6, 6), ButtonPlacement.OffsetFor(Field, Screen, Size, new Point(5000, 5000), 96));
        var offScreen = new Rectangle(1800, 1000, 400, 300);
        Assert.Equal(new Point(1894, 1054), ButtonPlacement.Place(offScreen, Screen, Size, Point.Empty, 96));
    }

    [Fact]
    public void StepsAboveTheTextCursorWhenItComesClose()
    {
        var caret = new Rectangle(860, 470, 1, 18);
        var spot = ButtonPlacement.Place(Field, Screen, Size, Point.Empty, 96, caret);
        Assert.Equal(868, spot.X);
        Assert.True(spot.Y + Size <= caret.Top - Size / 2, $"{spot} should be clear above {caret}");
    }

    [Fact]
    public void LeavesTheButtonAloneWhenTheCursorIsFarAway() =>
        Assert.Equal(new Point(868, 468), ButtonPlacement.Place(Field, Screen, Size, Point.Empty, 96, new Rectangle(120, 110, 1, 18)));

    [Fact]
    public void InAOneLineFieldItStepsToTheLeftOfTheCursor()
    {
        var line = new Rectangle(100, 100, 800, 30);
        var caret = new Rectangle(880, 106, 1, 18);
        var spot = ButtonPlacement.Place(line, Screen, Size, Point.Empty, 96, caret);
        Assert.True(spot.X + Size <= caret.Left - Size, $"{spot} should be clear to the left of {caret}");
        Assert.InRange(spot.Y, line.Top, line.Bottom - Size);
    }

    [Theory]
    [InlineData("-40,12", -40, 12)]
    [InlineData(" 0,0\n", 0, 0)]
    [InlineData("junk", 0, 0)]
    [InlineData("1,2,3", 0, 0)]
    [InlineData("999999,1", 0, 0)]
    [InlineData(null, 0, 0)]
    public void SavedOffsetsRoundTripAndBadOnesMeanNotMoved(string? saved, int x, int y)
    {
        Assert.Equal(new Point(x, y), ButtonPlacement.Parse(saved));
        Assert.Equal(new Point(x, y), ButtonPlacement.Parse(ButtonPlacement.Format(new Point(x, y))));
    }
}
