using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DesktopManager.Core;
using DesktopManager.Demo;
using DesktopManager.Shell;
using DesktopManager.UI;

namespace DesktopManager;

public partial class MainWindow : Window
{
    private readonly WindowsDesktopIconService _iconService = new();
    private readonly DesktopRegionSession _regionSession;
    private readonly DesktopIconRecoveryStore _recoveryStore;
    private readonly DesktopIconRecoveryCoordinator _recovery;
    private readonly object _movementGate = new();
    private readonly object _shellWriteGate = new();
    private DesktopRegionWindow? _regionWindow;
    private ScreenDelta _pendingMove;
    private volatile bool _captured;
    private bool _moveWorkerRunning;
    private bool _recoveryBlocked;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        _regionSession = new DesktopRegionSession(_iconService);
        _recoveryStore = new DesktopIconRecoveryStore(DemoSafety.DefaultRecoveryFilePath);
        _recovery = new DesktopIconRecoveryCoordinator(_iconService, _recoveryStore);
        TestShortcutPathTextBox.Text = DemoSafety.DefaultTestShortcutPath;
        Loaded += MainWindow_OnLoaded;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_recoveryStore.Exists)
        {
            DesktopRestoreResult restore = _recovery.RestorePending();
            _recoveryBlocked = !restore.Succeeded;
            SetStatus(
                restore.Message,
                restore.Succeeded ? StatusKind.Success : StatusKind.Error);
        }

        RefreshDesktop(showSuccessStatus: !_recoveryBlocked && !_recoveryStore.Exists);
        UpdateButtons();
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) =>
        RefreshDesktop(showSuccessStatus: true);

    private void OpenRegionButton_OnClick(object sender, RoutedEventArgs e)
    {
        EnsureRegionWindow();
        _regionWindow!.SetMode(DesktopRegionMode.Editing);
        SetStatus(
            "分区已显示。锁定后可把测试图标拖进方框；按 Ctrl+Alt+E 恢复编辑。",
            StatusKind.Info);
    }

    private void LocateRegionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryFindTestIcon(out DesktopIconInfo? icon))
        {
            return;
        }

        DesktopRegionWindow region = EnsureRegionWindow();
        region.ApplyScreenBounds(new ScreenRect(
            Math.Max(0, icon.HitTestPoint.X - 54),
            Math.Max(0, icon.HitTestPoint.Y - 54),
            420,
            240));
        region.SetMode(DesktopRegionMode.Editing);
        SetStatus("分区已定位，测试图标位于方框内。现在可以捕获。", StatusKind.Success);
    }

    private void CaptureButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_recoveryBlocked || _recoveryStore.Exists)
        {
            SetStatus("必须先成功恢复待处理记录，才能开始新的移动。", StatusKind.Error);
            return;
        }

        if (!TryFindTestIcon(out DesktopIconInfo? icon))
        {
            return;
        }

        if (_regionWindow is null || !_regionWindow.IsVisible)
        {
            SetStatus("请先打开分区，并让测试图标位于方框内。", StatusKind.Warning);
            return;
        }

        ScreenRect bounds = _regionWindow.ScreenBounds;
        if (!bounds.Contains(icon.HitTestPoint))
        {
            SetStatus("测试图标尚未位于方框内；可先锁定方框后手动拖入，或使用“定位”按钮。", StatusKind.Warning);
            return;
        }

        try
        {
            if (_iconService.IsAutoArrangeEnabled())
            {
                SetStatus("Windows 已开启自动排列图标。请手动关闭；Demo 不会修改该设置。", StatusKind.Error);
                return;
            }

            int captured = _regionSession.CaptureMembers(
                bounds,
                candidate => string.Equals(
                    candidate.Identity,
                    icon.Identity,
                    StringComparison.OrdinalIgnoreCase));
            if (captured != 1 ||
                !_regionSession.TryGetMemberPosition(icon.Identity, out ScreenPoint originalPosition))
            {
                _regionSession.Clear();
                SetStatus("重新枚举时未能唯一捕获测试图标，请刷新后重试。", StatusKind.Error);
                return;
            }

            DesktopIconInfo capturedIcon = icon with { Position = originalPosition };
            _recovery.SaveBeforeMove(capturedIcon, TestShortcutPathTextBox.Text);
            _captured = true;
            SetStatus("已捕获 1 个临时测试图标。拖动方框会同步移动该图标。", StatusKind.Success);
        }
        catch (Exception exception)
        {
            StopCaptureAndClear();
            SetStatus($"捕获失败：{exception.Message}", StatusKind.Error);
        }

        UpdateButtons();
    }

    private void RestoreButton_OnClick(object sender, RoutedEventArgs e) => RestorePending(showStatus: true);

    private DesktopRegionWindow EnsureRegionWindow()
    {
        if (_regionWindow is { IsVisible: true })
        {
            return _regionWindow;
        }

        _regionWindow = new DesktopRegionWindow(new DesktopRegionState(
            "学习资料",
            Color.FromRgb(47, 128, 237),
            0.26));
        _regionWindow.ScreenBoundsChanged += RegionWindow_OnScreenBoundsChanged;
        _regionWindow.Closed += RegionWindow_OnClosed;
        _regionWindow.Show();
        UpdateButtons();
        return _regionWindow;
    }

    private void RegionWindow_OnScreenBoundsChanged(
        object? sender,
        DesktopRegionBoundsChangedEventArgs e)
    {
        if (!_captured || e.Delta == default)
        {
            return;
        }

        lock (_movementGate)
        {
            if (!_captured)
            {
                return;
            }

            _pendingMove = new ScreenDelta(
                _pendingMove.X + e.Delta.X,
                _pendingMove.Y + e.Delta.Y);
            if (_moveWorkerRunning)
            {
                return;
            }

            _moveWorkerRunning = true;
        }

        _ = ProcessPendingMovesAsync();
    }

    private async Task ProcessPendingMovesAsync()
    {
        while (true)
        {
            ScreenDelta delta;
            lock (_movementGate)
            {
                if (!_captured || _pendingMove == default)
                {
                    _moveWorkerRunning = false;
                    return;
                }

                delta = _pendingMove;
                _pendingMove = default;
            }

            try
            {
                bool moved = await Task.Run(() =>
                {
                    lock (_shellWriteGate)
                    {
                        if (!_captured)
                        {
                            return false;
                        }

                        _regionSession.MoveBy(delta);
                        return true;
                    }
                });

                if (moved && !Dispatcher.HasShutdownStarted)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_captured)
                        {
                            SetStatus(
                                $"方框与测试图标已同步移动：ΔX={delta.X}, ΔY={delta.Y}（物理像素）。",
                                StatusKind.Success);
                        }
                    });
                }
            }
            catch (Exception exception)
            {
                if (!Dispatcher.HasShutdownStarted)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StopCaptureAndClear();
                        _recoveryBlocked = true;
                        SetStatus(
                            $"移动已立即停止：{exception.Message} 请点击“恢复测试图标原位置”。",
                            StatusKind.Error);
                        UpdateButtons();
                    });
                }

                lock (_movementGate)
                {
                    _moveWorkerRunning = false;
                }

                return;
            }
        }
    }

    private void RegionWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_regionWindow is not null)
        {
            _regionWindow.ScreenBoundsChanged -= RegionWindow_OnScreenBoundsChanged;
            _regionWindow.Closed -= RegionWindow_OnClosed;
        }

        _regionWindow = null;
        if (!_isClosing && _recoveryStore.Exists)
        {
            RestorePending(showStatus: true);
        }
        else
        {
            StopCaptureAndClear();
        }

        UpdateButtons();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        StopCaptureAndClear();

        if (_recoveryStore.Exists)
        {
            DesktopRestoreResult restore;
            lock (_shellWriteGate)
            {
                restore = _recovery.RestorePending();
            }

            if (!restore.Succeeded)
            {
                MessageBox.Show(
                    this,
                    restore.Message,
                    "测试图标尚未恢复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        _regionWindow?.Close();
    }

    private void RestorePending(bool showStatus)
    {
        StopCaptureAndClear();
        DesktopRestoreResult restore;
        lock (_shellWriteGate)
        {
            restore = _recovery.RestorePending();
        }

        _recoveryBlocked = !restore.Succeeded;

        if (showStatus)
        {
            SetStatus(
                restore.Message,
                restore.Succeeded ? StatusKind.Success : StatusKind.Error);
        }

        RefreshDesktop(showSuccessStatus: false);
        UpdateButtons();
    }

    private void StopCaptureAndClear()
    {
        _captured = false;
        lock (_movementGate)
        {
            _pendingMove = default;
        }

        lock (_shellWriteGate)
        {
            _regionSession.Clear();
        }
    }

    private bool TryFindTestIcon([NotNullWhen(true)] out DesktopIconInfo? icon)
    {
        icon = null;
        if (!DemoSafety.TryNormalizeApprovedShortcut(
                TestShortcutPathTextBox.Text,
                out string shortcutPath,
                out string error))
        {
            SetStatus(error, StatusKind.Error);
            return false;
        }

        if (!File.Exists(shortcutPath))
        {
            SetStatus($"未找到测试快捷方式：{shortcutPath}", StatusKind.Warning);
            return false;
        }

        try
        {
            IReadOnlyList<DesktopIconInfo> icons = _iconService.GetIcons();
            icon = DemoSafety.FindApprovedIcon(icons, shortcutPath);
            if (icon is null)
            {
                SetStatus("文件存在，但 Explorer 桌面视图尚未枚举到它；请稍后刷新。", StatusKind.Warning);
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            SetStatus($"读取测试图标失败：{exception.Message}", StatusKind.Error);
            return false;
        }
    }

    private void RefreshDesktop(bool showSuccessStatus)
    {
        try
        {
            IReadOnlyList<DesktopIconInfo> icons = _iconService.GetIcons();
            bool autoArrange = _iconService.IsAutoArrangeEnabled();
            DesktopIconsList.ItemsSource = icons.Select(icon =>
                $"({icon.Position.X,4}, {icon.Position.Y,4})  {icon.DisplayName}  [{icon.Identity}]");
            if (showSuccessStatus)
            {
                SetStatus(
                    $"已读取 {icons.Count} 个桌面图标；自动排列：{(autoArrange ? "开启（禁止移动）" : "关闭")}。",
                    autoArrange ? StatusKind.Warning : StatusKind.Success);
            }
        }
        catch (Exception exception)
        {
            DesktopIconsList.ItemsSource = null;
            SetStatus($"连接 Explorer 失败：{exception.Message}", StatusKind.Error);
        }

        UpdateButtons();
    }

    private void UpdateButtons()
    {
        CaptureButton.IsEnabled = !_recoveryBlocked && !_recoveryStore.Exists;
        RestoreButton.IsEnabled = _recoveryStore.Exists;
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        (StatusBorder.Background, StatusBorder.BorderBrush, StatusText.Foreground) = kind switch
        {
            StatusKind.Success => (Brushes.Honeydew, Brushes.SeaGreen, Brushes.DarkGreen),
            StatusKind.Warning => (Brushes.LemonChiffon, Brushes.Goldenrod, Brushes.DarkGoldenrod),
            StatusKind.Error => (Brushes.MistyRose, Brushes.IndianRed, Brushes.DarkRed),
            _ => (new SolidColorBrush(Color.FromRgb(229, 238, 249)), Brushes.SteelBlue, Brushes.MidnightBlue)
        };
    }

    private enum StatusKind
    {
        Info,
        Success,
        Warning,
        Error
    }
}
