using DesktopManager.Core;

namespace DesktopManager.Tests.Core;

public sealed class ScreenGeometryTests
{
    [Theory]
    [InlineData(10, 20, true)]
    [InlineData(109, 69, true)]
    [InlineData(110, 69, false)]
    [InlineData(109, 70, false)]
    [InlineData(9, 20, false)]
    [InlineData(10, 19, false)]
    public void ContainsUsesLeftClosedRightOpenBounds(int x, int y, bool expected)
    {
        ScreenRect rect = new(10, 20, 100, 50);

        Assert.Equal(expected, rect.Contains(new ScreenPoint(x, y)));
    }

    [Fact]
    public void MoveByPreservesSizeAndOffsetsOrigin()
    {
        ScreenRect rect = new(10, 20, 100, 50);

        ScreenRect moved = rect.MoveBy(new ScreenDelta(-4, 7));

        Assert.Equal(new ScreenRect(6, 27, 100, 50), moved);
    }

    [Fact]
    public void PointPlusDeltaProducesTargetPosition()
    {
        ScreenPoint target = new ScreenPoint(20, 30) + new ScreenDelta(5, -8);

        Assert.Equal(new ScreenPoint(25, 22), target);
    }
}
