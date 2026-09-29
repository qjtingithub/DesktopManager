using System.IO;
using DesktopManager.Core;

namespace DesktopManager.Demo;

public static class DemoSafety
{
    public const string TestShortcutFileName = "DesktopManager.FeasibilityTest.lnk";

    public static string DefaultTestShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        TestShortcutFileName);

    public static string DefaultRecoveryFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopManager",
        "desktop-icon-recovery.json");

    public static bool TryNormalizeApprovedShortcut(
        string? candidate,
        out string fullPath,
        out string error)
    {
        fullPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = "测试快捷方式路径不能为空。";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(candidate);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            error = "测试快捷方式路径无效。";
            return false;
        }

        string desktopDirectory = Path.GetFullPath(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        if (!string.Equals(
                Path.GetFileName(fullPath),
                TestShortcutFileName,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetDirectoryName(fullPath),
                desktopDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            error = $"Demo 只允许操作桌面上的 {TestShortcutFileName}。";
            return false;
        }

        return true;
    }

    public static DesktopIconInfo? FindApprovedIcon(
        IEnumerable<DesktopIconInfo> icons,
        string approvedPath) =>
        icons.FirstOrDefault(icon => IdentityMatchesPath(icon.Identity, approvedPath));

    public static bool IdentityMatchesPath(string identity, string expectedPath)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(identity),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
