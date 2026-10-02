using DesktopManager.Core;
using DesktopManager.Layout;

namespace DesktopManager.Tests.Layout;

public sealed class DesktopLayoutBoundsTests
{
    private static readonly ScreenRect Primary = new(0, 0, 1920, 1080);

    [Fact]
    public void ClampMovesRectangleBackFromAllFourEdges()
    {
        Assert.Equal(
            new ScreenRect(0, 20, 400, 200),
            DesktopLayoutBounds.ClampToPrimary(new ScreenRect(-100, 20, 400, 200), Primary));
        Assert.Equal(
            new ScreenRect(1520, 20, 400, 200),
            DesktopLayoutBounds.ClampToPrimary(new ScreenRect(1800, 20, 400, 200), Primary));
        Assert.Equal(
            new ScreenRect(20, 0, 400, 200),
            DesktopLayoutBounds.ClampToPrimary(new ScreenRect(20, -100, 400, 200), Primary));
        Assert.Equal(
            new ScreenRect(20, 880, 400, 200),
            DesktopLayoutBounds.ClampToPrimary(new ScreenRect(20, 1000, 400, 200), Primary));
    }

    [Fact]
    public void ClampReducesOversizedRectangleAndKeepsItInsidePrimary()
    {
        ScreenRect result = DesktopLayoutBounds.ClampToPrimary(
            new ScreenRect(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue),
            Primary);

        Assert.Equal(Primary, result);
    }

    [Fact]
    public void ClampPreservesRectangleAlreadyInsidePrimary()
    {
        ScreenRect bounds = new(300, 200, 400, 250);

        Assert.Equal(bounds, DesktopLayoutBounds.ClampToPrimary(bounds, Primary));
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    [InlineData(1920, -1)]
    public void ClampRejectsInvalidPrimary(int width, int height)
    {
        Assert.Throws<ArgumentException>(() => DesktopLayoutBounds.ClampToPrimary(
            new ScreenRect(0, 0, 100, 100),
            new ScreenRect(0, 0, width, height)));
    }

    [Fact]
    public void ClampRejectsPrimaryWhoseRightOrBottomOverflowsIntegerCoordinates()
    {
        Assert.Throws<ArgumentException>(() => DesktopLayoutBounds.ClampToPrimary(
            new ScreenRect(0, 0, 10, 10),
            new ScreenRect(int.MaxValue, 0, 1, 10)));
        Assert.Throws<ArgumentException>(() => DesktopLayoutBounds.ClampToPrimary(
            new ScreenRect(0, 0, 10, 10),
            new ScreenRect(0, int.MaxValue, 10, 1)));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(-1, 100)]
    [InlineData(100, -1)]
    public void ClampRejectsNonPositiveBounds(int width, int height)
    {
        Assert.Throws<ArgumentException>(() => DesktopLayoutBounds.ClampToPrimary(
            new ScreenRect(0, 0, width, height),
            Primary));
    }
}
