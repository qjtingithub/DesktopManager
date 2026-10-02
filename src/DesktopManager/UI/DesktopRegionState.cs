using System.Windows.Media;

namespace DesktopManager.UI;

public enum DesktopRegionMode
{
    Editing,
    Locked
}

/// <summary>
/// Stores the UI-only state of a desktop region. It intentionally has no
/// dependency on the Windows Shell or on desktop icon identities.
/// </summary>
public sealed class DesktopRegionState
{
    public const double MinBackgroundOpacity = 0.12;
    public const double MaxBackgroundOpacity = 0.85;

    public DesktopRegionState(
        string? name = null,
        Color? color = null,
        double backgroundOpacity = 0.26,
        DesktopRegionMode mode = DesktopRegionMode.Editing)
    {
        Name = NormalizeName(name);
        Color = color ?? Colors.DodgerBlue;
        BackgroundOpacity = ClampOpacity(backgroundOpacity);
        Mode = mode;
    }

    public string Name { get; private set; }

    public Color Color { get; private set; }

    public double BackgroundOpacity { get; private set; }

    public DesktopRegionMode Mode { get; private set; } = DesktopRegionMode.Editing;

    public void SetName(string? name) => Name = NormalizeName(name);

    public void SetColor(Color color) => Color = color;

    public void SetBackgroundOpacity(double opacity) => BackgroundOpacity = ClampOpacity(opacity);

    public void SetMode(DesktopRegionMode mode) => Mode = mode;

    public void ToggleMode() => Mode = Mode == DesktopRegionMode.Editing
        ? DesktopRegionMode.Locked
        : DesktopRegionMode.Editing;

    private static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "新分区" : name.Trim();

    private static double ClampOpacity(double opacity)
    {
        if (double.IsNaN(opacity) || double.IsInfinity(opacity))
        {
            return MinBackgroundOpacity;
        }

        return Math.Clamp(opacity, MinBackgroundOpacity, MaxBackgroundOpacity);
    }
}
