namespace DesktopManager.Core;

public sealed class DesktopRegionSession(IDesktopIconService iconService)
{
    private readonly Dictionary<string, ScreenPoint> _memberPositions =
        new(StringComparer.OrdinalIgnoreCase);

    public int MemberCount => _memberPositions.Count;

    public bool TryGetMemberPosition(string identity, out ScreenPoint position) =>
        _memberPositions.TryGetValue(identity, out position);

    public int CaptureMembers(ScreenRect region, Func<DesktopIconInfo, bool>? filter = null)
    {
        _memberPositions.Clear();

        foreach (DesktopIconInfo icon in iconService.GetIcons())
        {
            if (region.Contains(icon.HitTestPoint) && (filter is null || filter(icon)))
            {
                _memberPositions.Add(icon.Identity, icon.Position);
            }
        }

        return _memberPositions.Count;
    }

    public void MoveBy(ScreenDelta delta)
    {
        if (_memberPositions.Count == 0)
        {
            throw new InvalidOperationException("请先捕获分区内的测试图标。");
        }

        if (delta == default)
        {
            return;
        }

        if (iconService.IsAutoArrangeEnabled())
        {
            throw new InvalidOperationException("Windows 已启用自动排列图标，不能移动分区成员。");
        }

        Dictionary<string, ScreenPoint> targetPositions = _memberPositions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value + delta,
            StringComparer.OrdinalIgnoreCase);

        iconService.MoveIcons(targetPositions);

        foreach ((string identity, ScreenPoint position) in targetPositions)
        {
            _memberPositions[identity] = position;
        }
    }

    public void Clear() => _memberPositions.Clear();
}
