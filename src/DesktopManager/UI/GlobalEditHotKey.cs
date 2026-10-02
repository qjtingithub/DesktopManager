using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopManager.UI;

internal sealed class GlobalEditHotKey : IDisposable
{
    private const int HotKeyId = 0x444D;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VirtualKeyE = 0x45;
    private const int WmHotKey = 0x0312;

    private readonly Window _owner;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _disposed;

    public GlobalEditHotKey(Window owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _owner.SourceInitialized += Owner_OnSourceInitialized;
        _owner.Closed += Owner_OnClosed;
    }

    public bool IsRegistered { get; private set; }

    public event EventHandler? Pressed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.SourceInitialized -= Owner_OnSourceInitialized;
        _owner.Closed -= Owner_OnClosed;

        if (IsRegistered && _handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_handle, HotKeyId);
            IsRegistered = false;
        }

        if (_source is not null)
        {
            _source.RemoveHook(WindowMessageHook);
            _source = null;
        }
    }

    private void Owner_OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(_owner).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessageHook);
        IsRegistered = NativeMethods.RegisterHotKey(
            _handle,
            HotKeyId,
            ModControl | ModAlt,
            VirtualKeyE);
    }

    private void Owner_OnClosed(object? sender, EventArgs e) => Dispose();

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(
            IntPtr hWnd,
            int id,
            uint fsModifiers,
            uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
