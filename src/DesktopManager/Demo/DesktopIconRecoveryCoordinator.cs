using System.Threading;
using DesktopManager.Core;
using DesktopManager.Shell;

namespace DesktopManager.Demo;

public sealed record DesktopRestoreResult(
    bool HadPendingRecord,
    bool Succeeded,
    string Message,
    DesktopIconRecoveryRecord? Record);

public sealed class DesktopIconRecoveryCoordinator(
    IDesktopIconService iconService,
    DesktopIconRecoveryStore recoveryStore)
{
    public void SaveBeforeMove(DesktopIconInfo icon, string fixedPath)
    {
        if (recoveryStore.Exists)
        {
            throw new InvalidOperationException("存在未完成的恢复记录，不能开始新的移动。");
        }

        if (!DemoSafety.TryNormalizeApprovedShortcut(
                fixedPath,
                out string approvedPath,
                out string pathError))
        {
            throw new ArgumentException(pathError, nameof(fixedPath));
        }

        if (!DemoSafety.IdentityMatchesPath(icon.Identity, approvedPath))
        {
            throw new ArgumentException(
                "图标身份与允许的临时测试快捷方式不匹配。",
                nameof(icon));
        }

        recoveryStore.Save(new DesktopIconRecoveryRecord(
            icon.Identity,
            approvedPath,
            icon.Position));
    }

    public DesktopRestoreResult RestorePending()
    {
        DesktopIconRecoveryRecord? record;
        try
        {
            record = recoveryStore.TryLoad();
        }
        catch (Exception exception)
        {
            return new DesktopRestoreResult(
                true,
                false,
                $"恢复记录无法读取：{exception.Message}",
                null);
        }

        if (record is null)
        {
            return new DesktopRestoreResult(false, true, "没有待恢复的图标。", null);
        }

        if (!DemoSafety.TryNormalizeApprovedShortcut(
                record.FixedPath,
                out string approvedPath,
                out string pathError))
        {
            return new DesktopRestoreResult(
                true,
                false,
                $"恢复记录未通过安全校验：{pathError} 记录已保留。",
                record);
        }

        if (!DemoSafety.IdentityMatchesPath(record.Identity, approvedPath))
        {
            return new DesktopRestoreResult(
                true,
                false,
                "恢复记录中的图标身份与允许的临时测试快捷方式不匹配；记录已保留。",
                record);
        }

        try
        {
            iconService.MoveIcons(new Dictionary<string, ScreenPoint>(
                StringComparer.OrdinalIgnoreCase)
            {
                [record.Identity] = record.OriginalPosition
            });

            for (int attempt = 0; attempt < 10; attempt++)
            {
                DesktopIconInfo? restored = iconService.GetIcons().FirstOrDefault(
                    icon => string.Equals(
                        icon.Identity,
                        record.Identity,
                        StringComparison.OrdinalIgnoreCase));

                if (restored?.Position == record.OriginalPosition)
                {
                    recoveryStore.TryClearAfterSuccessfulRestore(restoreSucceeded: true);
                    return new DesktopRestoreResult(
                        true,
                        true,
                        "测试图标已恢复到原始坐标，恢复记录已清除。",
                        record);
                }

                if (attempt < 9)
                {
                    Thread.Sleep(50);
                }
            }

            return new DesktopRestoreResult(
                true,
                false,
                "Shell 未确认原始坐标；恢复记录已保留，新的移动已禁用。",
                record);
        }
        catch (Exception exception)
        {
            return new DesktopRestoreResult(
                true,
                false,
                $"恢复失败：{exception.Message} 恢复记录已保留。",
                record);
        }
    }
}
