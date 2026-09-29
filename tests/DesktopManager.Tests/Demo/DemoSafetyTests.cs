using DesktopManager.Core;
using DesktopManager.Demo;

namespace DesktopManager.Tests.Demo;

public sealed class DemoSafetyTests
{
    [Fact]
    public void AcceptsOnlyNamedShortcutInCurrentDesktopDirectory()
    {
        Assert.True(DemoSafety.TryNormalizeApprovedShortcut(
            DemoSafety.DefaultTestShortcutPath,
            out string approved,
            out string approvedError));
        Assert.Equal(string.Empty, approvedError);
        Assert.Equal(DemoSafety.DefaultTestShortcutPath, approved, ignoreCase: true);

        string wrongName = Path.Combine(
            Path.GetDirectoryName(approved)!,
            "RealDocument.lnk");
        Assert.False(DemoSafety.TryNormalizeApprovedShortcut(
            wrongName,
            out _,
            out string wrongNameError));
        Assert.Contains(DemoSafety.TestShortcutFileName, wrongNameError);

        string wrongDirectory = Path.Combine(
            Path.GetTempPath(),
            DemoSafety.TestShortcutFileName);
        Assert.False(DemoSafety.TryNormalizeApprovedShortcut(wrongDirectory, out _, out _));
    }

    [Fact]
    public void FindsOnlyIconWhoseStableIdentityMatchesApprovedPath()
    {
        string approved = DemoSafety.DefaultTestShortcutPath;
        DesktopIconInfo expected = new(
            approved.ToUpperInvariant(),
            "test",
            new ScreenPoint(10, 20),
            new ScreenPoint(30, 40));
        DesktopIconInfo virtualIcon = new(
            "::{645FF040-5081-101B-9F08-00AA002F954E}",
            "回收站",
            new ScreenPoint(0, 0),
            new ScreenPoint(0, 0));

        Assert.Same(expected, DemoSafety.FindApprovedIcon([virtualIcon, expected], approved));
    }
}
