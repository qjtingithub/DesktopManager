using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopManager.Core;
using DesktopManager.Demo;
using DesktopManager.Layout;
using DesktopManager.Shell;
using DesktopManager.UI;

namespace DesktopManager;

public partial class MainWindow : Window
{

    private static readonly Color[] RegionColors =
    [
        Color.FromRgb(47, 128, 237),
        Color.FromRgb(39, 174, 96),
        Color.FromRgb(217, 119, 6),
        Color.FromRgb(155, 81, 224)
    ];

    private readonly WindowsDesktopIconService _iconService = new();
    private readonly DesktopRegionSession _regionSession;
    private readonly DesktopIconRecoveryStore _recoveryStore;
    private readonly DesktopIconRecoveryCoordinator _recovery;
    private readonly DesktopLayoutStore _layoutStore = new(DesktopLayoutStore.DefaultFilePath);
    private readonly Dictionary<Guid, DesktopRegionWindow> _regionWindows = [];
    private readonly Dictionary<Guid, string[]> _regionMembers = [];
    private readonly List<Guid> _regionOrder = [];
    private readonly DispatcherTimer _layoutSaveTimer;
    private readonly GlobalEditHotKey _editHotKey;
    private readonly object _movementGate = new();
    private readonly object _shellWriteGate = new();

    private Guid? _selectedRegionId;
    private Guid? _capturedRegionId;
    private ScreenDelta _pendingMove;
    private volatile bool _captured;
    private bool _moveWorkerRunning;
    private bool _recoveryBlocked;
    private bool _layoutPersistenceBlocked;
    private bool _isUpdatingRegionSelector;
    private bool _isRestoringLayout;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        _regionSession = new DesktopRegionSession(_iconService);
        _recoveryStore = new DesktopIconRecoveryStore(DemoSafety.DefaultRecoveryFilePath);
        _recovery = new DesktopIconRecoveryCoordinator(_iconService, _recoveryStore);
        _layoutSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _layoutSaveTimer.Tick += LayoutSaveTimer_OnTick;

        _editHotKey = new GlobalEditHotKey(this);
        _editHotKey.Pressed += EditHotKey_OnPressed;

        TestShortcutPathTextBox.Text = DemoSafety.DefaultTestShortcutPath;
        Loaded += MainWindow_OnLoaded;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        string? startupMessage = null;
        StatusKind startupKind = StatusKind.Info;

        if (_recoveryStore.Exists)
        {
            DesktopRestoreResult restore = _recovery.RestorePending();
            _recoveryBlocked = !restore.Succeeded;
            startupMessage = restore.Message;
            startupKind = restore.Succeeded ? StatusKind.Success : StatusKind.Error;
        }

        try
        {
            int restoredRegionCount = RestoreRegions();
            if (startupMessage is null && restoredRegionCount > 0)
            {
                startupMessage = $"已从本地配置恢复 {restoredRegionCount} 个桌面分区。";
                startupKind = StatusKind.Success;
            }
        }
        catch (Exception exception)
        {
            _layoutPersistenceBlocked = true;
            startupMessage =
                $"桌面分区配置读取失败，原文件已保留且本次不会覆盖：{exception.Message}";
            startupKind = StatusKind.Error;
        }

        RefreshDesktop(showSuccessStatus: startupMessage is null);

        if (startupMessage is not null)
        {
            SetStatus(startupMessage, startupKind);
        }
        else if (!_editHotKey.IsRegistered)
        {
            SetStatus(
                "Ctrl+Alt+E 已被其他程序占用；仍可使用“全部编辑”按钮解锁分区。",
                StatusKind.Warning);
        }

        RefreshRegionSelector();
        UpdateButtons();
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) =>
        RefreshDesktop(showSuccessStatus: true);

    private void CreateRegionButton_OnClick(object sender, RoutedEventArgs e)
    {
        DesktopRegionWindow? region = CreateRegion();
        if (region is null)
        {
            return;
        }

        bool saved = TrySaveLayout(showError: true);
        if (saved)
        {
            SetStatus(
                $"已创建“{region.State.Name}”；拖动标题栏可以移动分区。",
                StatusKind.Success);
        }
    }

    private void EditAllRegionsButton_OnClick(object sender, RoutedEventArgs e) =>
        SetAllRegionsEditing("所有分区已进入编辑模式。");

    private void DeleteRegionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRegion(out Guid regionId, out DesktopRegionWindow? region))
        {
            SetStatus("请先选择要删除的分区。", StatusKind.Warning);
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            this,
            $"删除分区“{region.State.Name}”？\n\n这只会删除 DesktopManager 中的分区和成员归属，不会删除或移动任何桌面文件。",
            "删除桌面分区",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (_captured && _capturedRegionId == regionId)
        {
            RestorePending(showStatus: false);
            if (_recoveryStore.Exists)
            {
                SetStatus("测试图标尚未恢复，已取消删除分区。", StatusKind.Error);
                return;
            }
        }

        RemoveRegion(regionId, closeWindow: true);
        if (TrySaveLayout(showError: true))
        {
            SetStatus(
                $"已删除分区“{region.State.Name}”；桌面文件未被处理。",
                StatusKind.Success);
        }
    }

    private void RegionSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingRegionSelector)
        {
            return;
        }

        _selectedRegionId = RegionSelector.SelectedValue is Guid id ? id : null;
        UpdateButtons();
    }

    private void LocateRegionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryFindTestIcon(out DesktopIconInfo? icon))
        {
            return;
        }

        DesktopRegionWindow? region = EnsureSelectedRegionWindow();
        if (region is null)
        {
            return;
        }

        region.ApplyScreenBounds(new ScreenRect(
            Math.Max(0, icon.HitTestPoint.X - 54),
            Math.Max(0, icon.HitTestPoint.Y - 54),
            420,
            240));
        region.SetMode(DesktopRegionMode.Editing);
        SetStatus(
            $"分区“{region.State.Name}”已定位，测试图标位于方框内。现在可以捕获。",
            StatusKind.Success);
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

        if (!TryGetSelectedRegion(out Guid regionId, out DesktopRegionWindow? region) ||
            !region.IsVisible)
        {
            SetStatus("请先创建并选择一个可见分区。", StatusKind.Warning);
            return;
        }

        ScreenRect bounds = region.ScreenBounds;
        if (!bounds.Contains(icon.HitTestPoint))
        {
            SetStatus(
                "测试图标尚未位于所选分区内；可先锁定方框后手动拖入，或使用“定位到测试图标”。",
                StatusKind.Warning);
            return;
        }

        try
        {
            if (_iconService.IsAutoArrangeEnabled())
            {
                SetStatus(
                    "Windows 已开启自动排列图标。请手动关闭；程序不会修改该设置。",
                    StatusKind.Error);
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
            _capturedRegionId = regionId;
            _captured = true;
            SetStatus(
                $"已由“{region.State.Name}”捕获 1 个临时测试图标；拖动该分区会同步移动图标。",
                StatusKind.Success);
        }
        catch (Exception exception)
        {
            StopCaptureAndClear();
            SetStatus($"捕获失败：{exception.Message}", StatusKind.Error);
        }

        UpdateButtons();
    }

    private void RestoreButton_OnClick(object sender, RoutedEventArgs e) =>
        RestorePending(showStatus: true);

    private DesktopRegionWindow? CreateRegion()
    {
        if (_layoutPersistenceBlocked)
        {
            SetStatus(
                $"布局配置仍处于保护状态。请先备份或修复：{DesktopLayoutStore.DefaultFilePath}",
                StatusKind.Error);
            return null;
        }

        if (_regionWindows.Count >= DesktopLayoutDocument.MaxRegionCount)
        {
            SetStatus(
                $"最多允许创建 {DesktopLayoutDocument.MaxRegionCount} 个分区。",
                StatusKind.Warning);
            return null;
        }

        Guid id = Guid.NewGuid();
        DesktopRegionState state = new(
            FindNextRegionName(),
            RegionColors[_regionWindows.Count % RegionColors.Length],
            0.26);
        DesktopRegionWindow region = AddRegionWindow(
            id,
            state,
            memberIdentities: [],
            restoredBounds: null);
        _selectedRegionId = id;
        RefreshRegionSelector(id);
        UpdateButtons();
        return region;
    }

    private DesktopRegionWindow? EnsureSelectedRegionWindow()
    {
        if (TryGetSelectedRegion(out _, out DesktopRegionWindow? region))
        {
            return region;
        }

        return CreateRegion();
    }

    private DesktopRegionWindow AddRegionWindow(
        Guid id,
        DesktopRegionState state,
        string[] memberIdentities,
        ScreenRect? restoredBounds)
    {
        DesktopRegionWindow region = new(state, registerEditHotKey: false)
        {
            Opacity = 0
        };
        int offset = (_regionWindows.Count % 8) * 24;
        region.Left = 72 + offset;
        region.Top = 72 + offset;
        region.ScreenBoundsChanged += RegionWindow_OnScreenBoundsChanged;
        region.RegionStateChanged += RegionWindow_OnRegionStateChanged;
        region.Closed += RegionWindow_OnClosed;

        _regionWindows.Add(id, region);
        _regionMembers.Add(id, memberIdentities.ToArray());
        _regionOrder.Add(id);

        try
        {
            region.Show();
            if (restoredBounds is ScreenRect bounds)
            {
                region.ApplyScreenBounds(DesktopLayoutBounds.ClampToPrimary(
                    bounds,
                    GetPrimaryWorkAreaBounds()));
            }

            region.Opacity = 1;
            return region;
        }
        catch
        {
            DetachRegionEvents(region);
            _regionWindows.Remove(id);
            _regionMembers.Remove(id);
            _regionOrder.Remove(id);
            region.Close();
            throw;
        }
    }

    private int RestoreRegions()
    {
        DesktopLayoutDocument document = _layoutStore.Load();
        _isRestoringLayout = true;
        try
        {
            foreach (DesktopRegionLayout layout in document.Regions)
            {
                Color color = (Color)ColorConverter.ConvertFromString(layout.Color)!;
                DesktopRegionState state = new(
                    layout.Name,
                    color,
                    layout.BackgroundOpacity,
                    layout.IsLocked ? DesktopRegionMode.Locked : DesktopRegionMode.Editing);
                AddRegionWindow(
                    layout.Id,
                    state,
                    layout.MemberIdentities,
                    layout.Bounds);
            }

            _selectedRegionId = _regionOrder.Count > 0 ? _regionOrder[0] : null;
            RefreshRegionSelector();
            return document.Regions.Length;
        }
        catch
        {
            foreach (DesktopRegionWindow region in _regionWindows.Values.ToArray())
            {
                DetachRegionEvents(region);
                region.Close();
            }

            _regionWindows.Clear();
            _regionMembers.Clear();
            _regionOrder.Clear();
            _selectedRegionId = null;
            RefreshRegionSelector();
            throw;
        }
        finally
        {
            _isRestoringLayout = false;
        }
    }

    private ScreenRect GetPrimaryWorkAreaBounds()
    {
        Rect workArea = SystemParameters.WorkArea;
        DesktopDpiScale scale = ScreenCoordinateConverter.GetDpiScale(this);
        return scale.ToScreenRect(
            workArea.Left,
            workArea.Top,
            workArea.Width,
            workArea.Height);
    }

    private void RefreshRegionSelector(Guid? preferredId = null)
    {
        _isUpdatingRegionSelector = true;
        try
        {
            Guid? targetId = preferredId ?? _selectedRegionId;
            RegionListItem[] items = _regionOrder
                .Where(_regionWindows.ContainsKey)
                .Select(id => new RegionListItem(id, _regionWindows[id].State.Name))
                .ToArray();
            RegionSelector.ItemsSource = items;

            if (targetId is Guid selected && items.Any(item => item.Id == selected))
            {
                RegionSelector.SelectedValue = selected;
                _selectedRegionId = selected;
            }
            else if (items.Length > 0)
            {
                RegionSelector.SelectedIndex = 0;
                _selectedRegionId = items[0].Id;
            }
            else
            {
                RegionSelector.SelectedIndex = -1;
                _selectedRegionId = null;
            }
        }
        finally
        {
            _isUpdatingRegionSelector = false;
        }
    }

    private bool TryGetSelectedRegion(
        out Guid regionId,
        [NotNullWhen(true)] out DesktopRegionWindow? region)
    {
        if (_selectedRegionId is Guid id && _regionWindows.TryGetValue(id, out region))
        {
            regionId = id;
            return true;
        }

        regionId = Guid.Empty;
        region = null;
        return false;
    }

    private void RegionWindow_OnScreenBoundsChanged(
        object? sender,
        DesktopRegionBoundsChangedEventArgs e)
    {
        ScheduleLayoutSave();

        if (!_captured ||
            e.Delta == default ||
            sender is not DesktopRegionWindow region ||
            _capturedRegionId is not Guid capturedRegionId ||
            !_regionWindows.TryGetValue(capturedRegionId, out DesktopRegionWindow? capturedRegion) ||
            !ReferenceEquals(region, capturedRegion))
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

    private void RegionWindow_OnRegionStateChanged(object? sender, EventArgs e)
    {
        if (sender is DesktopRegionWindow region)
        {
            Guid? id = FindRegionId(region);
            RefreshRegionSelector(id);
        }

        ScheduleLayoutSave();
        UpdateButtons();
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
                                $"所选分区与测试图标已同步移动：ΔX={delta.X}, ΔY={delta.Y}（物理像素）。",
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
        if (_isClosing || sender is not DesktopRegionWindow region)
        {
            return;
        }

        Guid? regionId = FindRegionId(region);
        if (regionId is null)
        {
            return;
        }

        if (_captured && _capturedRegionId == regionId && _recoveryStore.Exists)
        {
            RestorePending(showStatus: true);
        }

        RemoveRegion(regionId.Value, closeWindow: false);
        TrySaveLayout(showError: true);
    }

    private void RemoveRegion(Guid regionId, bool closeWindow)
    {
        if (!_regionWindows.Remove(regionId, out DesktopRegionWindow? region))
        {
            return;
        }

        DetachRegionEvents(region);
        _regionMembers.Remove(regionId);
        _regionOrder.Remove(regionId);

        if (closeWindow)
        {
            region.Close();
        }

        if (_selectedRegionId == regionId)
        {
            _selectedRegionId = _regionOrder.Count > 0 ? _regionOrder[0] : null;
        }

        RefreshRegionSelector();
        UpdateButtons();
    }

    private void DetachRegionEvents(DesktopRegionWindow region)
    {
        region.ScreenBoundsChanged -= RegionWindow_OnScreenBoundsChanged;
        region.RegionStateChanged -= RegionWindow_OnRegionStateChanged;
        region.Closed -= RegionWindow_OnClosed;
    }

    private Guid? FindRegionId(DesktopRegionWindow region)
    {
        foreach ((Guid id, DesktopRegionWindow candidate) in _regionWindows)
        {
            if (ReferenceEquals(region, candidate))
            {
                return id;
            }
        }

        return null;
    }

    private void EditHotKey_OnPressed(object? sender, EventArgs e) =>
        SetAllRegionsEditing("所有分区已通过 Ctrl+Alt+E 返回编辑模式。");

    private void SetAllRegionsEditing(string message)
    {
        if (_regionWindows.Count == 0)
        {
            SetStatus("当前还没有桌面分区。", StatusKind.Warning);
            return;
        }

        foreach (DesktopRegionWindow region in _regionWindows.Values)
        {
            region.SetMode(DesktopRegionMode.Editing);
        }

        SetStatus(message, StatusKind.Success);
        ScheduleLayoutSave();
    }

    private void LayoutSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _layoutSaveTimer.Stop();
        TrySaveLayout(showError: true);
    }

    private void ScheduleLayoutSave()
    {
        if (_isRestoringLayout || _isClosing || _layoutPersistenceBlocked)
        {
            return;
        }

        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    private bool TrySaveLayout(bool showError)
    {
        if (_layoutPersistenceBlocked)
        {
            return false;
        }

        try
        {
            _layoutStore.Save(CreateLayoutDocument());
            return true;
        }
        catch (Exception exception)
        {
            if (showError && !_isClosing)
            {
                SetStatus($"桌面分区配置保存失败：{exception.Message}", StatusKind.Error);
            }

            return false;
        }
    }

    private DesktopLayoutDocument CreateLayoutDocument()
    {
        DesktopRegionLayout[] regions = _regionOrder
            .Where(_regionWindows.ContainsKey)
            .Select(id =>
            {
                DesktopRegionWindow region = _regionWindows[id];
                ScreenRect bounds = region.ScreenBounds;
                Color color = region.State.Color;
                return new DesktopRegionLayout(
                    id,
                    region.State.Name,
                    $"#{color.R:X2}{color.G:X2}{color.B:X2}",
                    region.State.BackgroundOpacity,
                    bounds.Left,
                    bounds.Top,
                    bounds.Width,
                    bounds.Height,
                    region.State.Mode == DesktopRegionMode.Locked,
                    _regionMembers[id].ToArray());
            })
            .ToArray();

        return new DesktopLayoutDocument(DesktopLayoutDocument.CurrentVersion, regions);
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _layoutSaveTimer.Stop();
        bool layoutSaved = TrySaveLayout(showError: false);
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

        if (!layoutSaved && !_layoutPersistenceBlocked)
        {
            MessageBox.Show(
                this,
                $"桌面分区配置未能保存，旧配置仍被保留：\n{DesktopLayoutStore.DefaultFilePath}",
                "分区配置未保存",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        _editHotKey.Pressed -= EditHotKey_OnPressed;
        _editHotKey.Dispose();

        foreach (DesktopRegionWindow region in _regionWindows.Values.ToArray())
        {
            DetachRegionEvents(region);
            region.Close();
        }

        _regionWindows.Clear();
        _regionMembers.Clear();
        _regionOrder.Clear();
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
        _capturedRegionId = null;
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

    private string FindNextRegionName()
    {
        HashSet<string> names = _regionWindows.Values
            .Select(region => region.State.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int number = 1; ; number++)
        {
            string candidate = $"分区 {number}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private void UpdateButtons()
    {
        bool hasRegions = _regionWindows.Count > 0;
        bool hasSelectedRegion = TryGetSelectedRegion(out _, out _);

        CreateRegionButton.IsEnabled = !_layoutPersistenceBlocked &&
            _regionWindows.Count < DesktopLayoutDocument.MaxRegionCount;
        RegionSelector.IsEnabled = hasRegions;
        EditAllRegionsButton.IsEnabled = hasRegions;
        DeleteRegionButton.IsEnabled = hasSelectedRegion && !_layoutPersistenceBlocked;
        LocateRegionButton.IsEnabled = !_layoutPersistenceBlocked;
        CaptureButton.IsEnabled = hasSelectedRegion &&
            !_recoveryBlocked &&
            !_recoveryStore.Exists;
        RestoreButton.IsEnabled = _recoveryStore.Exists;
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusText.Text = message;
        (StatusBorder.Background, StatusBorder.BorderBrush, StatusText.Foreground) = kind switch
        {
            StatusKind.Success => (
                new SolidColorBrush(Color.FromRgb(236, 253, 245)),
                new SolidColorBrush(Color.FromRgb(110, 231, 183)),
                new SolidColorBrush(Color.FromRgb(6, 95, 70))),
            StatusKind.Warning => (
                new SolidColorBrush(Color.FromRgb(255, 251, 235)),
                new SolidColorBrush(Color.FromRgb(252, 211, 77)),
                new SolidColorBrush(Color.FromRgb(146, 64, 14))),
            StatusKind.Error => (
                new SolidColorBrush(Color.FromRgb(254, 242, 242)),
                new SolidColorBrush(Color.FromRgb(252, 165, 165)),
                new SolidColorBrush(Color.FromRgb(153, 27, 27))),
            _ => (
                new SolidColorBrush(Color.FromRgb(239, 246, 255)),
                new SolidColorBrush(Color.FromRgb(147, 197, 253)),
                new SolidColorBrush(Color.FromRgb(30, 58, 138)))
        };
    }

    private sealed record RegionListItem(Guid Id, string Name);

    private enum StatusKind
    {
        Info,
        Success,
        Warning,
        Error
    }
}
