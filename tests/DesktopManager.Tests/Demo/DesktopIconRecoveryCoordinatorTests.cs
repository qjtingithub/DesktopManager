using DesktopManager.Core;
using DesktopManager.Demo;
using DesktopManager.Shell;

namespace DesktopManager.Tests.Demo;

public sealed class DesktopIconRecoveryCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "DesktopManager.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        string recoveryFile = Path.Combine(_directory, "recovery.json");
        if (File.Exists(recoveryFile))
        {
            File.Delete(recoveryFile);
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory);
        }
    }

    [Fact]
    public void RestoreMovesExactIdentityBackAndClearsRecordAfterVerification()
    {
        string identity = DemoSafety.DefaultTestShortcutPath;
        ScreenPoint original = new(20, 30);
        FakeIconService service = new(new DesktopIconInfo(
            identity,
            "test",
            new ScreenPoint(200, 300),
            new ScreenPoint(210, 310)));
        DesktopIconRecoveryStore store = CreateStore();
        store.Save(new DesktopIconRecoveryRecord(identity, identity, original));
        DesktopIconRecoveryCoordinator coordinator = new(service, store);

        DesktopRestoreResult result = coordinator.RestorePending();

        Assert.True(result.HadPendingRecord);
        Assert.True(result.Succeeded);
        Assert.False(store.Exists);
        Assert.Equal(original, service.Icon.Position);
    }

    [Fact]
    public void FailedCoordinateVerificationKeepsRecordAndBlocksOverwrite()
    {
        string identity = DemoSafety.DefaultTestShortcutPath;
        DesktopIconInfo icon = new(
            identity,
            "test",
            new ScreenPoint(200, 300),
            new ScreenPoint(210, 310));
        FakeIconService service = new(icon) { ApplyMoves = false };
        DesktopIconRecoveryStore store = CreateStore();
        DesktopIconRecoveryCoordinator coordinator = new(service, store);
        coordinator.SaveBeforeMove(icon, identity);
        service.SetCurrentPosition(new ScreenPoint(400, 500));

        DesktopRestoreResult result = coordinator.RestorePending();
        InvalidOperationException overwriteError = Assert.Throws<InvalidOperationException>(
            () => coordinator.SaveBeforeMove(icon, identity));

        Assert.False(result.Succeeded);
        Assert.True(store.Exists);
        Assert.Contains("未完成", overwriteError.Message);
    }

    [Fact]
    public void TamperedFixedPathKeepsRecordWithoutShellWrite()
    {
        string approved = DemoSafety.DefaultTestShortcutPath;
        FakeIconService service = new(Icon(approved));
        DesktopIconRecoveryStore store = CreateStore();
        store.Save(new DesktopIconRecoveryRecord(
            approved,
            Path.Combine(Path.GetTempPath(), DemoSafety.TestShortcutFileName),
            new ScreenPoint(20, 30)));
        DesktopIconRecoveryCoordinator coordinator = new(service, store);

        DesktopRestoreResult result = coordinator.RestorePending();

        Assert.False(result.Succeeded);
        Assert.True(store.Exists);
        Assert.Equal(0, service.MoveCallCount);
        Assert.Contains("安全校验", result.Message);
    }

    [Fact]
    public void TamperedIdentityKeepsRecordWithoutShellWrite()
    {
        string approved = DemoSafety.DefaultTestShortcutPath;
        string differentIdentity = Path.Combine(
            Path.GetDirectoryName(approved)!,
            "AnotherDesktopIcon.lnk");
        FakeIconService service = new(Icon(differentIdentity));
        DesktopIconRecoveryStore store = CreateStore();
        store.Save(new DesktopIconRecoveryRecord(
            differentIdentity,
            approved,
            new ScreenPoint(20, 30)));
        DesktopIconRecoveryCoordinator coordinator = new(service, store);

        DesktopRestoreResult result = coordinator.RestorePending();

        Assert.False(result.Succeeded);
        Assert.True(store.Exists);
        Assert.Equal(0, service.MoveCallCount);
        Assert.Contains("身份", result.Message);
    }

    private static DesktopIconInfo Icon(string identity) => new(
        identity,
        "test",
        new ScreenPoint(200, 300),
        new ScreenPoint(210, 310));

    private DesktopIconRecoveryStore CreateStore() => new(
        Path.Combine(_directory, "recovery.json"));

    private sealed class FakeIconService(DesktopIconInfo icon) : IDesktopIconService
    {
        public DesktopIconInfo Icon { get; private set; } = icon;

        public bool ApplyMoves { get; init; } = true;

        public int MoveCallCount { get; private set; }

        public IReadOnlyList<DesktopIconInfo> GetIcons() => [Icon];

        public bool IsAutoArrangeEnabled() => false;

        public void SetCurrentPosition(ScreenPoint position) =>
            Icon = Icon with { Position = position };

        public void MoveIcons(IReadOnlyDictionary<string, ScreenPoint> targetPositions)
        {
            MoveCallCount++;
            if (ApplyMoves && targetPositions.TryGetValue(Icon.Identity, out ScreenPoint target))
            {
                Icon = Icon with { Position = target };
            }
        }
    }
}
