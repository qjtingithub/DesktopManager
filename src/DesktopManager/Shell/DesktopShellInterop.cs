using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopManager.Core;

namespace DesktopManager.Shell;

/// <summary>
/// Reads and updates the icon positions in the current Windows desktop folder view.
/// </summary>
/// <remarks>
/// This class only changes the view position of an item. It never moves, copies,
/// deletes, or renames the item represented by a PIDL.
/// </remarks>
public sealed class WindowsDesktopIconService : IDesktopIconService
{
    private const int ShellWindowClassDesktop = 8;
    private const int ShellWindowFindNeedDispatch = 1;
    private const uint ShellFolderContents = 0x00000020 | 0x00000040;
    private const uint SelectAndPositionItem = 0x00000080;
    private const uint ShgdnNormal = 0x00000000;
    private const uint ShgdnForParsing = 0x00008000;
    private const int DefaultHitTestOffset = 16;

    public IReadOnlyList<DesktopIconInfo> GetIcons()
    {
        using ShellConnection connection = ShellConnection.Open();
        NativePoint spacing = GetItemSpacing(connection.FolderView);
        List<DesktopIconInfo> icons = [];
        HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);

        int hr = connection.DesktopFolder.EnumObjects(
            IntPtr.Zero,
            ShellFolderContents,
            out IEnumIdList? enumerator);
        ShellHResults.ThrowIfFailed(hr, "枚举桌面项目");
        if (enumerator is null)
        {
            throw new DesktopShellException(
                "桌面 Shell 文件夹没有返回枚举器。",
                unchecked((int)0x80004005));
        }

        try
        {
            while (true)
            {
                IntPtr childPidl = IntPtr.Zero;
                uint fetched = 0;
                hr = enumerator.Next(1, out childPidl, out fetched);

                if (hr < 0)
                {
                    ShellHResults.ThrowIfFailed(hr, "读取桌面项目");
                }

                if (fetched == 0 || childPidl == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    DesktopIconInfo? icon = TryReadIcon(
                        connection.DesktopFolder,
                        connection.FolderView,
                        childPidl,
                        spacing);
                    if (icon is not null && identities.Add(icon.Identity))
                    {
                        icons.Add(icon);
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(childPidl);
                }
            }
        }
        finally
        {
            ShellCom.Release(enumerator);
        }

        return icons;
    }

    public bool IsAutoArrangeEnabled()
    {
        using ShellConnection connection = ShellConnection.Open();
        return IsAutoArrangeEnabled(connection.FolderView);
    }

    public void MoveIcons(IReadOnlyDictionary<string, ScreenPoint> targetPositions)
    {
        ArgumentNullException.ThrowIfNull(targetPositions);

        foreach (string identity in targetPositions.Keys)
        {
            if (string.IsNullOrWhiteSpace(identity))
            {
                throw new ArgumentException("桌面图标身份不能为空。", nameof(targetPositions));
            }
        }

        using ShellConnection connection = ShellConnection.Open();
        DesktopIconMovePolicy.ThrowIfAutoArrangeEnabled(
            IsAutoArrangeEnabled(connection.FolderView));

        if (targetPositions.Count == 0)
        {
            return;
        }

        Dictionary<string, IntPtr> resolvedPidls = [];
        try
        {
            resolvedPidls = ResolveChildPidls(
                connection.DesktopFolder,
                targetPositions.Keys);
            IntPtr[] childPidls = new IntPtr[targetPositions.Count];
            NativePoint[] positions = new NativePoint[targetPositions.Count];
            int index = 0;

            // Resolve every identity before calling the write API. A missing item
            // therefore cannot cause a partially applied batch.
            foreach ((string identity, ScreenPoint position) in targetPositions)
            {
                childPidls[index] = resolvedPidls[identity];
                positions[index++] = new NativePoint(position.X, position.Y);
            }

            int hr = connection.FolderView.SelectAndPositionItems(
                (uint)childPidls.Length,
                childPidls,
                positions,
                SelectAndPositionItem);
            ShellHResults.ThrowIfFailed(hr, "设置桌面图标位置");
        }
        finally
        {
            foreach (IntPtr childPidl in resolvedPidls.Values)
            {
                Marshal.FreeCoTaskMem(childPidl);
            }
        }
    }

    private static DesktopIconInfo? TryReadIcon(
        IShellFolder desktopFolder,
        IFolderView folderView,
        IntPtr childPidl,
        NativePoint spacing)
    {
        if (!TryGetName(
                desktopFolder,
                childPidl,
                ShgdnForParsing,
                out string? identity))
        {
            Trace.WriteLine("跳过无法解析桌面 parsing name 的桌面项目。");
            return null;
        }

        if (!TryGetName(
                desktopFolder,
                childPidl,
                ShgdnNormal,
                out string? displayName))
        {
            Trace.WriteLine($"跳过无法读取显示名的桌面项目: {identity}");
            return null;
        }

        int hr = folderView.GetItemPosition(childPidl, out NativePoint position);
        if (hr != 0)
        {
            Trace.WriteLine(
                $"跳过无法读取位置的桌面项目: {identity}; HRESULT=0x{hr:X8}");
            return null;
        }

        int hitTestOffsetX = spacing.X > 0 ? Math.Max(1, spacing.X / 2) : DefaultHitTestOffset;
        int hitTestOffsetY = spacing.Y > 0 ? Math.Max(1, spacing.Y / 2) : DefaultHitTestOffset;

        return new DesktopIconInfo(
            identity!,
            displayName!,
            new ScreenPoint(position.X, position.Y),
            new ScreenPoint(position.X + hitTestOffsetX, position.Y + hitTestOffsetY));
    }

    private static NativePoint GetItemSpacing(IFolderView folderView)
    {
        NativePoint spacing = new(DefaultHitTestOffset * 2, DefaultHitTestOffset * 2);
        int hr = folderView.GetSpacing(ref spacing);
        if (hr != 0 || spacing.X <= 0 || spacing.Y <= 0)
        {
            Trace.WriteLine($"无法读取桌面图标间距，使用默认命中偏移。HRESULT=0x{hr:X8}");
            return new NativePoint(DefaultHitTestOffset * 2, DefaultHitTestOffset * 2);
        }

        return spacing;
    }

    private static bool IsAutoArrangeEnabled(IFolderView folderView)
    {
        int hr = folderView.GetAutoArrange();
        ShellHResults.ThrowIfFailed(hr, "读取桌面自动排列状态");
        return hr == 0;
    }

    private static bool TryGetName(
        IShellFolder desktopFolder,
        IntPtr pidl,
        uint nameKind,
        out string? name)
    {
        IntPtr strRet = Marshal.AllocCoTaskMem(520);
        try
        {
            int hr = desktopFolder.GetDisplayNameOf(pidl, nameKind, strRet);
            if (hr != 0)
            {
                name = null;
                return false;
            }

            uint type = unchecked((uint)Marshal.ReadInt32(strRet));
            int unionOffset = IntPtr.Size == 8 ? 8 : 4;
            name = type switch
            {
                0 => ReadOleString(strRet, unionOffset),
                1 => ReadOffsetString(strRet, unionOffset, pidl),
                2 => Marshal.PtrToStringAnsi(IntPtr.Add(strRet, unionOffset)),
                _ => null
            };

            return !string.IsNullOrWhiteSpace(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(strRet);
        }
    }

    private static string? ReadOleString(IntPtr strRet, int unionOffset)
    {
        IntPtr oleString = Marshal.ReadIntPtr(strRet, unionOffset);
        if (oleString == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(oleString);
        }
        finally
        {
            Marshal.FreeCoTaskMem(oleString);
        }
    }

    private static string? ReadOffsetString(IntPtr strRet, int unionOffset, IntPtr pidl)
    {
        int offset = Marshal.ReadInt32(strRet, unionOffset);
        if (offset < 0)
        {
            return null;
        }

        return Marshal.PtrToStringUni(IntPtr.Add(pidl, offset));
    }

    private static Dictionary<string, IntPtr> ResolveChildPidls(
        IShellFolder desktopFolder,
        IEnumerable<string> identities)
    {
        HashSet<string> requested = new(identities, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, IntPtr> resolved = new(StringComparer.OrdinalIgnoreCase);
        int hr = desktopFolder.EnumObjects(
            IntPtr.Zero,
            ShellFolderContents,
            out IEnumIdList? enumerator);
        ShellHResults.ThrowIfFailed(hr, "重新枚举桌面图标身份");
        if (enumerator is null)
        {
            throw new DesktopShellException(
                "桌面 Shell 文件夹没有返回身份枚举器。",
                unchecked((int)0x80004005));
        }

        try
        {
            while (resolved.Count < requested.Count)
            {
                IntPtr childPidl = IntPtr.Zero;
                uint fetched = 0;
                hr = enumerator.Next(1, out childPidl, out fetched);
                if (hr < 0)
                {
                    ShellHResults.ThrowIfFailed(hr, "重新读取桌面图标身份");
                }

                if (fetched == 0 || childPidl == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    if (TryGetName(
                            desktopFolder,
                            childPidl,
                            ShgdnForParsing,
                            out string? identity) &&
                        requested.Contains(identity!) &&
                        resolved.TryAdd(identity!, childPidl))
                    {
                        childPidl = IntPtr.Zero;
                    }
                }
                finally
                {
                    if (childPidl != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(childPidl);
                    }
                }
            }

            if (resolved.Count != requested.Count)
            {
                string missing = string.Join(
                    ", ",
                    requested.Where(identity => !resolved.ContainsKey(identity)));
                throw new DesktopShellException(
                    $"无法在当前桌面视图中重新解析图标身份，已停止本批次: {missing}",
                    unchecked((int)0x80070002));
            }

            return resolved;
        }
        catch
        {
            foreach (IntPtr childPidl in resolved.Values)
            {
                Marshal.FreeCoTaskMem(childPidl);
            }

            throw;
        }
        finally
        {
            ShellCom.Release(enumerator);
        }
    }

    private sealed class ShellConnection : IDisposable
    {
        private readonly ComApartmentScope _apartment;
        private readonly object _shellWindows;
        private readonly object _shellDispatch;
        private readonly IShellBrowser _shellBrowser;
        private readonly IShellView _shellView;

        private ShellConnection(
            ComApartmentScope apartment,
            object shellWindows,
            object shellDispatch,
            IShellBrowser shellBrowser,
            IShellView shellView,
            IFolderView folderView,
            IShellFolder desktopFolder)
        {
            _apartment = apartment;
            _shellWindows = shellWindows;
            _shellDispatch = shellDispatch;
            _shellBrowser = shellBrowser;
            _shellView = shellView;
            FolderView = folderView;
            DesktopFolder = desktopFolder;
        }

        public IFolderView FolderView { get; }

        public IShellFolder DesktopFolder { get; }

        public static ShellConnection Open()
        {
            ComApartmentScope apartment = ComApartmentScope.Enter();
            object? shellWindows = null;
            object? shellDispatch = null;
            IShellBrowser? shellBrowser = null;
            IShellView? shellView = null;
            IFolderView? folderView = null;
            IShellFolder? desktopFolder = null;

            try
            {
                shellWindows = new CShellWindows();
                IShellWindows windows = (IShellWindows)shellWindows;
                object location = 0;
                object root = new object();

                shellDispatch = windows.FindWindowSW(
                    ref location,
                    ref root,
                    ShellWindowClassDesktop,
                    out _,
                    ShellWindowFindNeedDispatch);
                if (shellDispatch is null)
                {
                    throw new DesktopShellException(
                        "Windows 桌面 Shell 窗口尚未就绪。",
                        unchecked((int)0x80004005));
                }

                IServiceProvider serviceProvider = (IServiceProvider)shellDispatch;
                Guid serviceId = ShellGuids.SidTopLevelBrowser;
                Guid browserId = typeof(IShellBrowser).GUID;
                object browserObject = serviceProvider.QueryService(
                    ref serviceId,
                    ref browserId);
                if (browserObject is null)
                {
                    throw new DesktopShellException(
                        "无法取得桌面 Shell 浏览器。",
                        unchecked((int)0x80004002));
                }

                shellBrowser = (IShellBrowser)browserObject;
                shellView = shellBrowser.QueryActiveShellView();
                if (shellView is null)
                {
                    throw new DesktopShellException(
                        "无法取得桌面 Shell 视图。",
                        unchecked((int)0x80004005));
                }

                folderView = QueryInterface<IFolderView>(shellView);

                int hr = ShellNativeMethods.SHGetDesktopFolder(out desktopFolder);
                ShellHResults.ThrowIfFailed(hr, "取得桌面 Shell 文件夹");

                return new ShellConnection(
                    apartment,
                    shellWindows,
                    shellDispatch,
                    shellBrowser,
                    shellView,
                    folderView,
                    desktopFolder);
            }
            catch
            {
                ShellCom.Release(desktopFolder);
                ShellCom.Release(folderView);
                ShellCom.Release(shellView);
                ShellCom.Release(shellBrowser);
                ShellCom.Release(shellDispatch);
                ShellCom.Release(shellWindows);
                apartment.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            ShellCom.Release(DesktopFolder);
            ShellCom.Release(FolderView);
            ShellCom.Release(_shellView);
            ShellCom.Release(_shellBrowser);
            ShellCom.Release(_shellDispatch);
            ShellCom.Release(_shellWindows);
            _apartment.Dispose();
        }
    }

    private static T QueryInterface<T>(object source)
        where T : class
    {
        IntPtr unknown = IntPtr.Zero;
        IntPtr requested = IntPtr.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(source);
            Guid interfaceId = typeof(T).GUID;
            int hr = Marshal.QueryInterface(unknown, in interfaceId, out requested);
            ShellHResults.ThrowIfFailed(hr, $"取得 Shell 接口 {typeof(T).Name}");
            return (T)Marshal.GetObjectForIUnknown(requested);
        }
        finally
        {
            if (requested != IntPtr.Zero)
            {
                Marshal.Release(requested);
            }

            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }
}

/// <summary>
/// A persisted single-item recovery record used by the real desktop probe.
/// </summary>
public sealed record DesktopIconRecoveryRecord(
    string Identity,
    string FixedPath,
    ScreenPoint OriginalPosition);

/// <summary>
/// Stores the recovery record atomically. The file is intentionally not deleted
/// unless the caller explicitly confirms that restoration succeeded.
/// </summary>
public sealed class DesktopIconRecoveryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public DesktopIconRecoveryStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("恢复记录路径不能为空。", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
    }

    public bool Exists => File.Exists(_filePath);

    public void Save(DesktopIconRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Identity))
        {
            throw new ArgumentException("恢复记录的身份不能为空。", nameof(record));
        }

        if (string.IsNullOrWhiteSpace(record.FixedPath))
        {
            throw new ArgumentException("恢复记录的固定路径不能为空。", nameof(record));
        }

        string? directory = Path.GetDirectoryName(_filePath);
        if (directory is null)
        {
            throw new InvalidOperationException("恢复记录路径必须包含目录。");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = _filePath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, record, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public DesktopIconRecoveryRecord? TryLoad()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        using FileStream stream = new(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        DesktopIconRecoveryRecord? record = JsonSerializer.Deserialize<DesktopIconRecoveryRecord>(
            stream,
            JsonOptions);
        if (record is null ||
            string.IsNullOrWhiteSpace(record.Identity) ||
            string.IsNullOrWhiteSpace(record.FixedPath))
        {
            throw new InvalidDataException("恢复记录缺少身份或固定路径。");
        }

        return record;
    }

    public bool TryClearAfterSuccessfulRestore(bool restoreSucceeded)
    {
        if (!restoreSucceeded)
        {
            return false;
        }

        if (!File.Exists(_filePath))
        {
            return true;
        }

        File.Delete(_filePath);
        return true;
    }
}

internal static class DesktopIconMovePolicy
{
    public static void ThrowIfAutoArrangeEnabled(bool enabled)
    {
        if (enabled)
        {
            throw new InvalidOperationException(
                "Windows 已启用自动排列图标，已停止桌面图标位置写入。请先手动关闭自动排列。");
        }
    }
}

internal sealed class DesktopShellException : Exception
{
    public DesktopShellException(string message, int errorCode)
        : base($"{message} HRESULT=0x{errorCode:X8}")
    {
        ErrorCode = errorCode;
        HResult = errorCode;
    }

    public int ErrorCode { get; }
}

internal static class ShellHResults
{
    public static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new DesktopShellException(operation, hr);
        }
    }
}

internal static class ShellCom
{
    public static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal sealed class ComApartmentScope : IDisposable
{
    private const uint CoInitApartmentThreaded = 0x2;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private bool _initialized;

    private ComApartmentScope()
    {
    }

    public static ComApartmentScope Enter()
    {
        ComApartmentScope scope = new();
        int hr = ShellNativeMethods.CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
        if (hr < 0 && hr != RpcEChangedMode)
        {
            scope.Dispose();
            throw new DesktopShellException("初始化 COM 单元失败。", hr);
        }

        // RPC_E_CHANGED_MODE means the calling thread already initialized COM
        // with another apartment model. COM is still usable, but this scope must
        // not balance another component's CoInitializeEx call.
        scope._initialized = hr >= 0;
        return scope;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            ShellNativeMethods.CoUninitialize();
            _initialized = false;
        }
    }
}

internal static class ShellGuids
{
    public static readonly Guid SidTopLevelBrowser =
        new("4C96BE40-915C-11CF-99D3-00AA004AE837");
}

internal static class ShellNativeMethods
{
    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    [DllImport("shell32.dll", ExactSpelling = true)]
    public static extern int SHGetDesktopFolder(
        [MarshalAs(UnmanagedType.Interface)] out IShellFolder desktopFolder);

}

[ComImport]
[Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class CShellWindows
{
}

[ComImport]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellWindows
{
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW(
        [MarshalAs(UnmanagedType.Struct)] ref object location,
        [MarshalAs(UnmanagedType.Struct)] ref object root,
        int shellWindowClass,
        out int windowHandle,
        int findWindowOptions);
}

[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider
{
    [return: MarshalAs(UnmanagedType.Interface)]
    object QueryService(ref Guid serviceId, ref Guid interfaceId);
}

[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    void VTableGap01();

    void VTableGap02();

    void VTableGap03();

    void VTableGap04();

    void VTableGap05();

    void VTableGap06();

    void VTableGap07();

    void VTableGap08();

    void VTableGap09();

    void VTableGap10();

    void VTableGap11();

    void VTableGap12();

    IShellView QueryActiveShellView();
}

[ComImport]
[Guid("000214E3-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    void VTableGap01();

    void VTableGap02();

    void VTableGap03();

    void VTableGap04();

    void VTableGap05();

    void VTableGap06();

    void VTableGap07();

    void VTableGap08();

    void VTableGap09();

    void VTableGap10();

    void VTableGap11();

    void VTableGap12();

    [return: MarshalAs(UnmanagedType.Interface)]
    object GetItemObject(uint aspectOfView, ref Guid interfaceId);
}

[ComImport]
[Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFolderView
{
    [PreserveSig]
    int GetCurrentViewMode(out uint viewMode);

    [PreserveSig]
    int SetCurrentViewMode(uint viewMode);

    [PreserveSig]
    int GetFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder folder);

    [PreserveSig]
    int Item(int itemIndex, out IntPtr pidl);

    [PreserveSig]
    int ItemCount(uint flags, out int itemCount);

    [PreserveSig]
    int Items(
        uint flags,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out object items);

    [PreserveSig]
    int GetSelectionMarkedItem(out int itemIndex);

    [PreserveSig]
    int GetFocusedItem(out int itemIndex);

    [PreserveSig]
    int GetItemPosition(IntPtr pidl, out NativePoint position);

    [PreserveSig]
    int GetSpacing(ref NativePoint spacing);

    [PreserveSig]
    int GetDefaultSpacing(ref NativePoint spacing);

    [PreserveSig]
    int GetAutoArrange();

    [PreserveSig]
    int SelectItem(int itemIndex, uint flags);

    [PreserveSig]
    int SelectAndPositionItems(
        uint itemCount,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] NativePoint[] positions,
        uint flags);
}

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellFolder
{
    [PreserveSig]
    int ParseDisplayName(
        IntPtr hwnd,
        IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPWStr)] string displayName,
        ref uint charactersEaten,
        out IntPtr pidl,
        ref uint attributes);

    [PreserveSig]
    int EnumObjects(
        IntPtr hwnd,
        uint flags,
        [MarshalAs(UnmanagedType.Interface)] out IEnumIdList enumIdList);

    [PreserveSig]
    int BindToObject(
        IntPtr pidl,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IntPtr result);

    [PreserveSig]
    int BindToStorage(
        IntPtr pidl,
        IntPtr bindContext,
        ref Guid interfaceId,
        out IntPtr result);

    [PreserveSig]
    int CompareIds(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

    [PreserveSig]
    int CreateViewObject(
        IntPtr hwndOwner,
        ref Guid interfaceId,
        out IntPtr result);

    [PreserveSig]
    int GetAttributesOf(
        uint itemCount,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls,
        ref uint attributes);

    [PreserveSig]
    int GetUIObjectOf(
        IntPtr hwndOwner,
        uint itemCount,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls,
        ref Guid interfaceId,
        IntPtr reserved,
        out IntPtr result);

    [PreserveSig]
    int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);

    [PreserveSig]
    int SetNameOf(
        IntPtr hwnd,
        IntPtr pidl,
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        uint flags,
        out IntPtr result);
}

[ComImport]
[Guid("000214F2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumIdList
{
    [PreserveSig]
    int Next(uint count, out IntPtr pidl, out uint fetched);

    [PreserveSig]
    int Skip(uint count);

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumIdList enumerator);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePoint(int x, int y)
{
    public readonly int X = x;

    public readonly int Y = y;
}
