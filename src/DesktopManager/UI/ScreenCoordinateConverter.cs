using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopManager.Core;

namespace DesktopManager.UI;

/// <summary>
/// The conversion boundary between WPF device-independent units and the
/// physical-pixel coordinates used by the Shell integration layer.
/// </summary>
public readonly record struct DesktopDpiScale(double X, double Y)
{
    public ScreenPoint ToScreenPoint(double xDip, double yDip) =>
        new(ToPixels(xDip, X), ToPixels(yDip, Y));

    public ScreenRect ToScreenRect(
        double leftDip,
        double topDip,
        double widthDip,
        double heightDip) =>
        new(
            ToPixels(leftDip, X),
            ToPixels(topDip, Y),
            Math.Max(0, ToPixels(widthDip, X)),
            Math.Max(0, ToPixels(heightDip, Y)));

    public double ToDipX(int pixels) => pixels / X;

    public double ToDipY(int pixels) => pixels / Y;

    private static int ToPixels(double dip, double scale) =>
        (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
}

public static class ScreenCoordinateConverter
{
    public static DesktopDpiScale GetDpiScale(Visual visual)
    {
        if (PresentationSource.FromVisual(visual)?.CompositionTarget is { } target)
        {
            Matrix transform = target.TransformToDevice;
            return new DesktopDpiScale(transform.M11, transform.M22);
        }

        return new DesktopDpiScale(1, 1);
    }

    public static ScreenRect ToScreenRect(Window window)
    {
        DesktopDpiScale scale = GetDpiScale(window);
        return scale.ToScreenRect(
            window.Left,
            window.Top,
            window.ActualWidth > 0 ? window.ActualWidth : window.Width,
            window.ActualHeight > 0 ? window.ActualHeight : window.Height);
    }

    public static void ApplyScreenRect(Window window, ScreenRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "窗口尺寸必须为正数。");
        }

        DesktopDpiScale scale = GetDpiScale(window);
        window.Left = scale.ToDipX(bounds.Left);
        window.Top = scale.ToDipY(bounds.Top);
        window.Width = scale.ToDipX(bounds.Width);
        window.Height = scale.ToDipY(bounds.Height);
    }
}
