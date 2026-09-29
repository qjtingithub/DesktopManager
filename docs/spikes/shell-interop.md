# Windows 桌面 Shell 互操作可行性记录

## 当前结论

Shell 适配层采用 Windows 公开 Shell 接口，目标是读取当前桌面视图中的真实图标，并在关闭“自动排列图标”时批量设置指定图标的位置。实现不会移动、复制、删除或重命名真实文件。

当前代码路径：

- `src/DesktopManager/Shell/DesktopShellInterop.cs`
- `tests/DesktopManager.Tests/Shell/DesktopIconRecoveryStoreTests.cs`

## 调用链

1. 创建 `CLSID_ShellWindows`（`IShellWindows`）。
2. 通过 `FindWindowSW` 查找 `SWC_DESKTOP`，取得桌面窗口的 `IDispatch`。
3. 通过 `IServiceProvider.QueryService(SID_STopLevelBrowser, IID_IShellBrowser)` 取得 `IShellBrowser`。
4. 通过 `IShellBrowser.QueryActiveShellView` 取得桌面 `IShellView`。
5. 对 `IShellView` 查询 `IFolderView`。
6. 通过 `SHGetDesktopFolder` 和 `IEnumIDList` 枚举桌面项目。
7. 使用桌面 `IShellFolder.GetDisplayNameOf` 的 `SHGDN_FORPARSING` 作为可重新解析身份，使用 `SHGDN_NORMAL` 作为显示名。这样名称由拥有相对 PIDL 的父文件夹解析，避免把相对 PIDL 误传给要求绝对 PIDL 的 API。
8. 使用 `IFolderView.GetItemPosition` 读取位置。写入前重新枚举当前桌面视图，按稳定 parsing identity 精确取得当前 child PIDL，再使用 `IFolderView.SelectAndPositionItems` 批量设置位置。

`IFolderView::GetAutoArrange` 返回 `S_OK` 表示开启，返回 `S_FALSE` 表示关闭；实现遇到开启状态时会在任何坐标写入前抛出异常。身份解析会先全部完成，任何一项失败都会停止批次，不按显示名猜测替代项目。

参考 Microsoft 文档：

- [IShellWindows::FindWindowSW](https://learn.microsoft.com/en-us/windows/win32/api/exdisp/nf-exdisp-ishellwindows-findwindowsw)
- [IShellBrowser::QueryActiveShellView](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellbrowser-queryactiveshellview)
- [IFolderView](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ifolderview)
- [IFolderView::GetAutoArrange](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview-getautoarrange)
- [IFolderView::GetItemPosition](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview-getitemposition)
- [IFolderView::SelectAndPositionItems](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifolderview-selectandpositionitems)
- [IShellFolder::GetDisplayNameOf](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellfolder-getdisplaynameof)
- [SHGDNF](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_shgdnf)

## 恢复记录

`DesktopIconRecoveryStore` 为单个测试图标保存身份、固定路径和原始位置。先写入同目录临时 JSON 文件并刷新到磁盘，再替换正式记录；这样进程异常退出后，正式记录仍可用于恢复。

`TryClearAfterSuccessfulRestore(false)` 不删除记录；只有调用方确认恢复成功并传入 `true` 后才会删除记录。启动时发现记录存在时，根代理应先读取并恢复该图标，恢复失败则保留文件并停止新的移动操作。

## 实机验证结果与剩余限制

- 已在 Windows 11 和真实 Explorer 桌面完成枚举、单个临时快捷方式移动、坐标确认和原位置恢复。独立 Shell 探针与 UI/Shell 集成探针均通过。
- 解决方案最终构建为 0 个警告、0 个错误，26 个自动化测试通过。
- `IFolderView` 返回的是桌面视图坐标。当前适配层将其作为相对主显示器原点的整数像素暴露；125%/150% DPI 下仍需人工确认与 WPF 物理像素边界一致。
- 命中点使用 `IFolderView.GetSpacing` 返回的项目间距中点作为近似值；该点用于分区命中判断，不代表图标可见位图的精确边界。
- `IShellView`/`IFolderView` 对 Explorer 重启后的 COM 引用不可复用。调用失败时应丢弃当前连接，在下一次调用中重新查找桌面视图；UI 层还需要取消当前拖动批次。
- “自动排列”状态是每次写入前即时读取的；用户在读取状态和 Shell 写入之间手工开启自动排列时，Shell 仍可能拒绝或重新布局，集成层必须在失败后重新枚举并报告。
- 不同 Explorer 版本、图标视图模式、特殊虚拟项目（例如回收站）可能无法返回绝对 parsing name 或位置；这些项目会被跳过并写入 `Trace`，不会按显示名称替代。

## 建议的手工验证步骤

1. 在桌面创建一个明确的临时快捷方式，并记录其固定路径。
2. 确认桌面右键菜单的“查看 → 自动排列图标”处于关闭状态；程序不得自动修改此设置。
3. 启动 Demo，先调用 `GetIcons`，确认临时快捷方式的身份、显示名和位置被列出。
4. 将该项目的身份和原始位置保存到 `DesktopIconRecoveryStore` 指定的工作区恢复文件。
5. 仅针对这个临时快捷方式调用 `MoveIcons` 设置一个相邻位置，确认文件路径、文件内容和快捷方式目标不变。
6. 再次调用 `MoveIcons` 恢复原坐标；只有确认恢复成功后才调用 `TryClearAfterSuccessfulRestore(true)`。
7. 在恢复前结束 Demo，重新启动，确认程序先处理仍存在的恢复记录，而不是开始新的移动。
8. 在上述流程中重启 Explorer，确认当前 Shell 调用失败后没有继续写入，下一次调用可以重新枚举。
9. 在 Windows 显示缩放为 125% 和 150% 时重复读取与恢复，记录 Shell 坐标和 WPF 方框的偏差。

