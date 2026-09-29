using DesktopManager.Core;
using DesktopManager.Shell;

namespace DesktopManager.Tests.Shell;

public sealed class DesktopIconRecoveryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "DesktopManager.Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        string recoveryFile = Path.Combine(_directory, "desktop-icon-recovery.json");
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
    public void SaveAndLoadRoundTripsIdentityPathAndPosition()
    {
        DesktopIconRecoveryStore store = CreateStore();
        DesktopIconRecoveryRecord expected = new(
            @"C:\Users\Test\Desktop\Probe.lnk",
            @"C:\Users\Test\Desktop\Probe.lnk",
            new ScreenPoint(123, 456));

        store.Save(expected);

        Assert.True(store.Exists);
        Assert.Equal(expected, store.TryLoad());
    }

    [Fact]
    public void FailedRestoreKeepsRecoveryRecord()
    {
        DesktopIconRecoveryStore store = CreateStore();
        store.Save(new DesktopIconRecoveryRecord(
            @"C:\Users\Test\Desktop\Probe.lnk",
            @"C:\Users\Test\Desktop\Probe.lnk",
            new ScreenPoint(10, 20)));

        Assert.False(store.TryClearAfterSuccessfulRestore(restoreSucceeded: false));
        Assert.True(store.Exists);
    }

    [Fact]
    public void SuccessfulRestoreDeletesRecoveryRecord()
    {
        DesktopIconRecoveryStore store = CreateStore();
        store.Save(new DesktopIconRecoveryRecord(
            @"C:\Users\Test\Desktop\Probe.lnk",
            @"C:\Users\Test\Desktop\Probe.lnk",
            new ScreenPoint(10, 20)));

        Assert.True(store.TryClearAfterSuccessfulRestore(restoreSucceeded: true));
        Assert.False(store.Exists);
        Assert.Null(store.TryLoad());
    }

    private DesktopIconRecoveryStore CreateStore()
    {
        return new DesktopIconRecoveryStore(Path.Combine(_directory, "desktop-icon-recovery.json"));
    }
}
