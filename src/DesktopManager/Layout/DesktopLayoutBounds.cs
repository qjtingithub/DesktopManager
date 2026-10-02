using DesktopManager.Core;

namespace DesktopManager.Layout;

public static class DesktopLayoutBounds
{
    public static ScreenRect ClampToPrimary(ScreenRect bounds, ScreenRect primary)
    {
        long primaryRight = (long)primary.Left + primary.Width;
        long primaryBottom = (long)primary.Top + primary.Height;

        if (primary.Width <= 0 ||
            primary.Height <= 0 ||
            primaryRight > int.MaxValue ||
            primaryBottom > int.MaxValue)
        {
            throw new ArgumentException("主屏边界无效。", nameof(primary));
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentException("分区边界宽高必须为正数。", nameof(bounds));
        }

        int width = Math.Min(bounds.Width, primary.Width);
        int height = Math.Min(bounds.Height, primary.Height);

        long maximumLeft = primaryRight - width;
        long maximumTop = primaryBottom - height;
        long left = Math.Clamp((long)bounds.Left, (long)primary.Left, maximumLeft);
        long top = Math.Clamp((long)bounds.Top, (long)primary.Top, maximumTop);

        return new ScreenRect((int)left, (int)top, width, height);
    }
}
