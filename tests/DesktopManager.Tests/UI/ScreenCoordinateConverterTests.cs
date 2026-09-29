using DesktopManager.Core;
using DesktopManager.UI;

namespace DesktopManager.Tests.UI;

public sealed class ScreenCoordinateConverterTests
{
    [Fact]
    public void ConvertsDipToPhysicalPixelsUsingNearestInteger()
    {
        DesktopDpiScale scale = new(1.5, 2);

        ScreenRect result = scale.ToScreenRect(10.25, -4.25, 100.5, 50.25);

        Assert.Equal(new ScreenRect(15, -9, 151, 101), result);
    }

    [Fact]
    public void ConvertsPhysicalPixelsBackToDipAtEachAxisScale()
    {
        DesktopDpiScale scale = new(1.25, 1.5);

        Assert.Equal(80d, scale.ToDipX(100));
        Assert.Equal(60d, scale.ToDipY(90));
    }
}
