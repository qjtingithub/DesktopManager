using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using DesktopManager.Core;
using DesktopManager.Shell;

namespace DesktopManager.Demo;

internal static class FeasibilityProbeRunner
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !args[0].StartsWith("--probe-", StringComparison.Ordinal))
        {
            return false;
        }

        if (args is ["--probe-enumerate", string enumerationReport])
        {
            exitCode = RunEnumeration(enumerationReport);
            return true;
        }

        if (args is ["--probe-cycle", string shortcutPath, string recoveryPath, string cycleReport])
        {
            exitCode = RunMoveCycle(shortcutPath, recoveryPath, cycleReport);
            return true;
        }

        exitCode = 64;
        return true;
    }

    private static int RunEnumeration(string reportPath)
    {
        ProbeReport report = new("enumerate");
        int exitCode;

        try
        {
            WindowsDesktopIconService service = new();
            IReadOnlyList<DesktopIconInfo> icons = service.GetIcons();
            report.AutoArrangeEnabled = service.IsAutoArrangeEnabled();
            report.IconCount = icons.Count;
            report.Icons = icons.Select(ProbeIcon.From).ToList();
            report.Status = "passed";
            exitCode = 0;
        }
        catch (Exception exception)
        {
            report.Status = "failed";
            report.Error = exception.ToString();
            exitCode = 1;
        }

        WriteReport(reportPath, report);
        return exitCode;
    }

    private static int RunMoveCycle(
        string shortcutCandidate,
        string recoveryPath,
        string reportPath)
    {
        ProbeReport report = new("move-and-restore")
        {
            TestPath = shortcutCandidate
        };
        WindowsDesktopIconService service = new();
        DesktopIconRecoveryStore store = new(recoveryPath);
        DesktopIconRecoveryCoordinator recovery = new(service, store);

        if (store.Exists)
        {
            DesktopRestoreResult pendingRestore = recovery.RestorePending();
            report.Status = pendingRestore.Succeeded
                ? "pending-recovery-restored"
                : "pending-recovery-failed";
            report.RestoreMessage = pendingRestore.Message;
            report.OriginalPosition = pendingRestore.Record?.OriginalPosition;
            report.Restored = pendingRestore.Succeeded;
            WriteReport(reportPath, report);
            return pendingRestore.Succeeded ? 0 : 4;
        }

        int exitCode = 1;
        try
        {
            if (!DemoSafety.TryNormalizeApprovedShortcut(
                    shortcutCandidate,
                    out string shortcutPath,
                    out string pathError))
            {
                throw new InvalidOperationException(pathError);
            }

            report.TestPath = shortcutPath;
            if (!File.Exists(shortcutPath))
            {
                throw new FileNotFoundException("指定的临时测试快捷方式不存在。", shortcutPath);
            }

            IReadOnlyList<DesktopIconInfo> icons = service.GetIcons();
            report.IconCount = icons.Count;
            report.Icons = icons.Select(ProbeIcon.From).ToList();
            report.AutoArrangeEnabled = service.IsAutoArrangeEnabled();

            if (report.AutoArrangeEnabled == true)
            {
                report.Status = "blocked-auto-arrange";
                exitCode = 3;
            }
            else
            {
                DesktopIconInfo icon = DemoSafety.FindApprovedIcon(icons, shortcutPath)
                    ?? throw new InvalidOperationException("Explorer 桌面视图中未找到测试快捷方式。");

                report.Identity = icon.Identity;
                report.OriginalPosition = icon.Position;
                recovery.SaveBeforeMove(icon, shortcutPath);

                ScreenPoint target = ChooseSafeTarget(icon, icons);
                report.RequestedPosition = target;
                service.MoveIcons(new Dictionary<string, ScreenPoint>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [icon.Identity] = target
                });

                DesktopIconInfo? moved = WaitForIcon(
                    service,
                    icon.Identity,
                    candidate => candidate.Position != icon.Position);
                if (moved is null)
                {
                    throw new InvalidOperationException("Shell 未观察到测试图标坐标发生变化。");
                }

                report.MovedPosition = moved.Position;
                report.Status = "move-observed";
                exitCode = 0;
            }
        }
        catch (Exception exception)
        {
            report.Status = "failed";
            report.Error = exception.ToString();
            exitCode = 1;
        }
        finally
        {
            if (store.Exists)
            {
                DesktopRestoreResult restore = recovery.RestorePending();
                report.Restored = restore.Succeeded;
                report.RestoreMessage = restore.Message;
                if (!restore.Succeeded)
                {
                    report.Status = "recovery-pending";
                    exitCode = 4;
                }
                else if (exitCode == 0)
                {
                    report.Status = "passed";
                }
            }
        }

        WriteReport(reportPath, report);
        return exitCode;
    }

    private static ScreenPoint ChooseSafeTarget(
        DesktopIconInfo icon,
        IReadOnlyList<DesktopIconInfo> icons)
    {
        int screenWidth = Math.Max(320, NativeMethods.GetSystemMetrics(SmCxScreen));
        int screenHeight = Math.Max(240, NativeMethods.GetSystemMetrics(SmCyScreen));
        ScreenDelta[] candidates =
        [
            new(180, 120),
            new(180, -120),
            new(-180, 120),
            new(-180, -120)
        ];

        foreach (ScreenDelta delta in candidates)
        {
            ScreenPoint target = new(
                Math.Clamp(icon.Position.X + delta.X, 0, screenWidth - 64),
                Math.Clamp(icon.Position.Y + delta.Y, 0, screenHeight - 64));
            bool hasClearance = icons
                .Where(candidate => !string.Equals(
                    candidate.Identity,
                    icon.Identity,
                    StringComparison.OrdinalIgnoreCase))
                .All(candidate =>
                    Math.Abs(candidate.Position.X - target.X) >= 80 ||
                    Math.Abs(candidate.Position.Y - target.Y) >= 80);

            if (target != icon.Position && hasClearance)
            {
                return target;
            }
        }

        throw new InvalidOperationException("主显示器上没有找到安全的临时移动目标。");
    }

    private static DesktopIconInfo? WaitForIcon(
        IDesktopIconService service,
        string identity,
        Func<DesktopIconInfo, bool> predicate)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            DesktopIconInfo? icon = service.GetIcons().FirstOrDefault(candidate =>
                string.Equals(candidate.Identity, identity, StringComparison.OrdinalIgnoreCase));
            if (icon is not null && predicate(icon))
            {
                return icon;
            }

            if (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }

        return null;
    }

    private static void WriteReport(string reportPath, ProbeReport report)
    {
        string fullPath = Path.GetFullPath(reportPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class ProbeReport(string mode)
    {
        public string Mode { get; } = mode;

        public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;

        public string Status { get; set; } = "started";

        public bool? AutoArrangeEnabled { get; set; }

        public int IconCount { get; set; }

        public string? TestPath { get; set; }

        public string? Identity { get; set; }

        public ScreenPoint? OriginalPosition { get; set; }

        public ScreenPoint? RequestedPosition { get; set; }

        public ScreenPoint? MovedPosition { get; set; }

        public bool? Restored { get; set; }

        public string? RestoreMessage { get; set; }

        public string? Error { get; set; }

        public List<ProbeIcon> Icons { get; set; } = [];
    }

    private sealed record ProbeIcon(
        string DisplayName,
        string Identity,
        ScreenPoint Position,
        ScreenPoint HitTestPoint)
    {
        public static ProbeIcon From(DesktopIconInfo icon) => new(
            icon.DisplayName,
            icon.Identity,
            icon.Position,
            icon.HitTestPoint);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);
    }
}
