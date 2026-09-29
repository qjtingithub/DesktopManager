using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using DesktopManager.Core;

namespace DesktopManager.UI;

public sealed class DesktopRegionBoundsChangedEventArgs(
    ScreenDelta delta,
    ScreenRect bounds,
    bool sizeChanged) : EventArgs
{
    public ScreenDelta Delta { get; } = delta;

    public ScreenRect Bounds { get; } = bounds;

    public bool SizeChanged { get; } = sizeChanged;
}

/// <summary>
/// UI-only desktop region prototype. It renders a translucent region and
/// publishes physical-pixel bounds; it never enumerates or moves Shell items.
/// </summary>
public partial class DesktopRegionWindow : Window
{
    private const int EditHotKeyId = 0x444D;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VirtualKeyE = 0x45;
    private const int WmHotKey = 0x0312;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int MaNoActivate = 3;

    private readonly DesktopRegionState _state;
    private IntPtr _windowHandle;
    private HwndSource? _hwndSource;
    private bool _isUpdatingVisuals = true;
    private bool _hasLastScreenBounds;
    private ScreenRect _lastScreenBounds;
    private bool _hotKeyRegistered;

    public DesktopRegionWindow()
        : this(new DesktopRegionState())
    {
    }

    public DesktopRegionWindow(DesktopRegionState state)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));

        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        LocationChanged += OnLocationChanged;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;

        ApplyStateToVisuals();
    }

    public DesktopRegionState State => _state;

    public bool IsGlobalEditHotKeyRegistered => _hotKeyRegistered;

    public ScreenRect ScreenBounds => ScreenCoordinateConverter.ToScreenRect(this);

    public event EventHandler<DesktopRegionBoundsChangedEventArgs>? ScreenBoundsChanged;

    public void SetMode(DesktopRegionMode mode)
    {
        if (_state.Mode == mode)
        {
            return;
        }

        _state.SetMode(mode);
        ApplyStateToVisuals();

        if (mode == DesktopRegionMode.Editing && IsVisible)
        {
            Activate();
            Focus();
        }
    }

    public void ToggleEditMode() => SetMode(
        _state.Mode == DesktopRegionMode.Editing
            ? DesktopRegionMode.Locked
            : DesktopRegionMode.Editing);

    public void ApplyScreenBounds(ScreenRect bounds)
    {
        ScreenCoordinateConverter.ApplyScreenRect(this, bounds);
        _lastScreenBounds = ScreenBounds;
        _hasLastScreenBounds = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_windowHandle);
        _hwndSource?.AddHook(WindowMessageHook);

        _hotKeyRegistered = NativeMethods.RegisterHotKey(
            _windowHandle,
            EditHotKeyId,
            ModControl | ModAlt,
            VirtualKeyE);

        ApplyMouseMode();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _lastScreenBounds = ScreenBounds;
        _hasLastScreenBounds = true;
    }

    private void OnLocationChanged(object? sender, EventArgs e) => NotifyBoundsChanged();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => NotifyBoundsChanged();

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_hotKeyRegistered && _windowHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, EditHotKeyId);
            _hotKeyRegistered = false;
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WindowMessageHook);
            _hwndSource = null;
        }
    }

    private void NotifyBoundsChanged()
    {
        if (!IsLoaded)
        {
            return;
        }

        ScreenRect current = ScreenBounds;
        if (!_hasLastScreenBounds)
        {
            _lastScreenBounds = current;
            _hasLastScreenBounds = true;
            return;
        }

        if (current == _lastScreenBounds)
        {
            return;
        }

        ScreenRect previous = _lastScreenBounds;
        _lastScreenBounds = current;
        ScreenDelta delta = new(current.Left - previous.Left, current.Top - previous.Top);
        bool sizeChanged = current.Width != previous.Width || current.Height != previous.Height;
        ScreenBoundsChanged?.Invoke(
            this,
            new DesktopRegionBoundsChangedEventArgs(delta, current, sizeChanged));
    }

    private void ApplyStateToVisuals()
    {
        _isUpdatingVisuals = true;
        try
        {
            bool isEditing = _state.Mode == DesktopRegionMode.Editing;
            TitleEditor.Text = _state.Name;
            TitleText.Text = _state.Name;
            TitleEditor.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
            TitleText.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
            EditControls.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
            LockedHint.Visibility = isEditing ? Visibility.Collapsed : Visibility.Visible;
            ResizeThumb.Visibility = isEditing ? Visibility.Visible : Visibility.Collapsed;
            OpacitySlider.Value = _state.BackgroundOpacity;
            OpacityValueText.Text = $"{_state.BackgroundOpacity:P0}";

            Surface.Background = new SolidColorBrush(
                Color.FromArgb(ToAlpha(_state.BackgroundOpacity), _state.Color.R, _state.Color.G, _state.Color.B));
            Surface.BorderBrush = new SolidColorBrush(_state.Color);
            TitleBar.Background = new SolidColorBrush(
                Color.FromArgb(0xE8, _state.Color.R, _state.Color.G, _state.Color.B));
            Surface.BorderThickness = new Thickness(isEditing ? 2 : 1);
            Root.IsHitTestVisible = isEditing;
            ApplyMouseMode();
        }
        finally
        {
            _isUpdatingVisuals = false;
        }
    }

    private void ApplyMouseMode()
    {
        bool passThrough = _state.Mode == DesktopRegionMode.Locked;
        Root.IsHitTestVisible = !passThrough;

        if (_windowHandle == IntPtr.Zero)
        {
            return;
        }

        IntPtr style = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.ExStyleIndex);
        long styleValue = style.ToInt64();
        if (passThrough)
        {
            styleValue |= NativeMethods.ExTransparent | NativeMethods.ExNoActivate;
        }
        else
        {
            styleValue &= ~(NativeMethods.ExTransparent | NativeMethods.ExNoActivate);
        }

        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.ExStyleIndex, new IntPtr(styleValue));
    }

    private void TitleEditor_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingVisuals)
        {
            return;
        }

        _state.SetName(TitleEditor.Text);
        TitleText.Text = _state.Name;
    }

    private void TitleEditor_OnLostFocus(object sender, RoutedEventArgs e)
    {
        _state.SetName(TitleEditor.Text);
        ApplyStateToVisuals();
    }

    private void OpacitySlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingVisuals)
        {
            return;
        }

        _state.SetBackgroundOpacity(e.NewValue);
        ApplyStateToVisuals();
    }

    private void ColorPreset_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string colorText })
        {
            return;
        }

        object? convertedColor = ColorConverter.ConvertFromString(colorText);
        if (convertedColor is not Color color)
        {
            return;
        }

        _state.SetColor(color);
        ApplyStateToVisuals();
    }

    private void LockButton_OnClick(object sender, RoutedEventArgs e) =>
        SetMode(DesktopRegionMode.Locked);

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.Mode != DesktopRegionMode.Editing ||
            e.ChangedButton != MouseButton.Left ||
            IsInteractiveSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // The window can close while a drag is being initiated.
        }
    }

    private void ResizeThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_state.Mode != DesktopRegionMode.Editing)
        {
            return;
        }

        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is TextBoxBase or ButtonBase or Slider or Thumb)
            {
                return true;
            }
        }

        return false;
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == EditHotKeyId)
        {
            SetMode(DesktopRegionMode.Editing);
            handled = true;
            return IntPtr.Zero;
        }

        if (_state.Mode == DesktopRegionMode.Locked && message == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        if (_state.Mode == DesktopRegionMode.Locked && message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

    private static byte ToAlpha(double opacity) =>
        (byte)Math.Round(Math.Clamp(opacity, 0, 1) * byte.MaxValue, MidpointRounding.AwayFromZero);

    private static class NativeMethods
    {
        public const int ExStyleIndex = -20;
        public const long ExTransparent = 0x00000020L;
        public const long ExNoActivate = 0x08000000L;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(
            IntPtr hWnd,
            int id,
            uint fsModifiers,
            uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) =>
            IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, index) : GetWindowLong32(hWnd, index);

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value) =>
            IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, index, value) : SetWindowLong32(hWnd, index, value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
        private static extern IntPtr GetWindowLong32(IntPtr hWnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
        private static extern IntPtr SetWindowLong32(IntPtr hWnd, int index, IntPtr value);
    }
}
