using DesktopManager.Core;

namespace DesktopManager.Tests.Core;

public sealed class DesktopRegionSessionTests
{
    [Fact]
    public void CaptureMembersUsesHitTestPointAndFilter()
    {
        FakeDesktopIconService service = new(
        [
            Icon("allowed", 10, 20, 30, 40),
            Icon("filtered", 10, 20, 35, 45),
            Icon("outside", 200, 200, 220, 220)
        ]);
        DesktopRegionSession session = new(service);

        int count = session.CaptureMembers(
            new ScreenRect(0, 0, 100, 100),
            icon => icon.Identity == "allowed");

        Assert.Equal(1, count);
        Assert.True(session.TryGetMemberPosition("allowed", out ScreenPoint position));
        Assert.Equal(new ScreenPoint(10, 20), position);
    }

    [Fact]
    public void MoveByMovesOnlyCapturedMembersAndAccumulatesSuccessfulDeltas()
    {
        FakeDesktopIconService service = new(
        [
            Icon("inside", 10, 20, 30, 40),
            Icon("outside", 200, 200, 220, 220)
        ]);
        DesktopRegionSession session = new(service);
        session.CaptureMembers(new ScreenRect(0, 0, 100, 100));

        session.MoveBy(new ScreenDelta(5, -2));
        session.MoveBy(new ScreenDelta(3, 4));

        Assert.Collection(
            service.Moves,
            move => Assert.Equal(new ScreenPoint(15, 18), move["inside"]),
            move => Assert.Equal(new ScreenPoint(18, 22), move["inside"]));
        Assert.All(service.Moves, move => Assert.DoesNotContain("outside", move.Keys));
    }

    [Fact]
    public void MoveByRejectsAutoArrangeBeforeWriting()
    {
        FakeDesktopIconService service = new([Icon("inside", 10, 20, 30, 40)])
        {
            AutoArrangeEnabled = true
        };
        DesktopRegionSession session = new(service);
        session.CaptureMembers(new ScreenRect(0, 0, 100, 100));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => session.MoveBy(new ScreenDelta(5, 5)));

        Assert.Contains("自动排列", error.Message);
        Assert.Empty(service.Moves);
    }

    [Fact]
    public void MoveByRequiresCapturedMember()
    {
        DesktopRegionSession session = new(new FakeDesktopIconService([]));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => session.MoveBy(new ScreenDelta(5, 5)));

        Assert.Contains("捕获", error.Message);
    }

    private static DesktopIconInfo Icon(
        string identity,
        int x,
        int y,
        int hitTestX,
        int hitTestY) =>
        new(identity, identity, new ScreenPoint(x, y), new ScreenPoint(hitTestX, hitTestY));

    private sealed class FakeDesktopIconService(IReadOnlyList<DesktopIconInfo> icons)
        : IDesktopIconService
    {
        public bool AutoArrangeEnabled { get; init; }

        public List<IReadOnlyDictionary<string, ScreenPoint>> Moves { get; } = [];

        public IReadOnlyList<DesktopIconInfo> GetIcons() => icons;

        public bool IsAutoArrangeEnabled() => AutoArrangeEnabled;

        public void MoveIcons(IReadOnlyDictionary<string, ScreenPoint> targetPositions) =>
            Moves.Add(new Dictionary<string, ScreenPoint>(targetPositions));
    }
}
