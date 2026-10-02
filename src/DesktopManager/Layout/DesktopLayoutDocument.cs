using System.Text.Json.Serialization;
using DesktopManager.Core;

namespace DesktopManager.Layout;

public sealed record DesktopRegionLayout(
    Guid Id,
    string Name,
    string Color,
    double BackgroundOpacity,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsLocked,
    string[] MemberIdentities)
{
    public const string DefaultColor = "#2F80ED";
    public const double MinBackgroundOpacity = 0.12;
    public const double MaxBackgroundOpacity = 0.85;

    [JsonIgnore]
    public ScreenRect Bounds => new(Left, Top, Width, Height);
}

public sealed record DesktopLayoutDocument(
    int Version,
    DesktopRegionLayout[] Regions)
{
    public const int CurrentVersion = 1;
    public const int MaxRegionCount = 32;

    public static DesktopLayoutDocument Empty { get; } =
        new(CurrentVersion, []);
}
