namespace DesktopManager.Core;

public sealed record DesktopIconInfo(
    string Identity,
    string DisplayName,
    ScreenPoint Position,
    ScreenPoint HitTestPoint);
