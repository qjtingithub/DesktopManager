namespace DesktopManager.Core;

public interface IDesktopIconService
{
    IReadOnlyList<DesktopIconInfo> GetIcons();

    bool IsAutoArrangeEnabled();

    void MoveIcons(IReadOnlyDictionary<string, ScreenPoint> targetPositions);
}
