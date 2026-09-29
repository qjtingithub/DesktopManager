# DesktopManager 可行性 Demo

DesktopManager 是一个面向 Windows 11 主显示器的桌面整理可行性 Demo。它通过 Windows Shell 接口读取真实桌面图标，在半透明 WPF 分区移动时同步移动一个明确指定的测试快捷方式，并在关闭窗口或下次启动时恢复该图标的原始位置。

本阶段只验证关键技术链路，不移动、复制、删除或重命名真实桌面文件，也不修改注册表、Explorer 设置或“自动排列图标”状态。

## 已实现

- 枚举真实 Explorer 桌面图标，读取稳定身份和物理像素坐标。
- 创建可命名、换色、调整透明度、拖动和缩放的半透明分区。
- 将测试图标捕获进分区，并在拖动分区时同步移动图标。
- 锁定分区后启用鼠标穿透，使用 `Ctrl+Alt+E` 返回编辑模式。
- 每次坐标写入前检查“自动排列图标”；开启时中止，不替用户修改设置。
- 只允许操作当前用户桌面的 `DesktopManager.FeasibilityTest.lnk`。
- 首次移动前原子写入恢复记录；正常关闭、手动恢复或下次启动时尝试恢复。
- 将窗口移动事件与 Explorer COM 写入解耦，后台串行合并位移，避免 UI 输入同步回调中的 COM 重入错误。

完整的验证结果见 [docs/demo-validation.md](docs/demo-validation.md)，Shell 技术说明见 [docs/spikes/shell-interop.md](docs/spikes/shell-interop.md)。

## 环境要求

- Windows 11，Explorer 桌面正常运行。
- 主显示器；当前版本不支持多显示器。
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，具体版本由 `global.json` 固定。
- 真实移动测试前，手动关闭桌面右键菜单中的“查看 → 自动排列图标”。

应用不要求管理员权限，也不使用网络或在线同步。

## 构建与测试

在仓库根目录运行：

```powershell
dotnet restore DesktopManager.sln
dotnet build DesktopManager.sln -m:1 -p:UseSharedCompilation=false
dotnet test DesktopManager.sln --no-build --no-restore -m:1 -p:UseSharedCompilation=false
```

运行 Demo：

```powershell
dotnet run --project src/DesktopManager/DesktopManager.csproj
```

## 安全地准备测试快捷方式

真实图标移动被严格限制为当前用户桌面的固定文件名 `DesktopManager.FeasibilityTest.lnk`。如果该路径不存在，可以在 PowerShell 中创建一个指向记事本的临时快捷方式：

```powershell
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DesktopManager.FeasibilityTest.lnk'
if (Test-Path -LiteralPath $shortcutPath) {
    throw "测试快捷方式已存在：$shortcutPath"
}
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $env:WINDIR 'System32\notepad.exe'
$shortcut.Save()
```

建议的操作顺序：

1. 确认“自动排列图标”已关闭，并启动 Demo。
2. 点击“刷新桌面图标”，确认列表中出现测试快捷方式。
3. 点击“打开 / 显示分区”，再点击“定位到测试图标”。
4. 按界面提示捕获图标，拖动分区，观察图标是否保持相对位置。
5. 点击“恢复测试图标原位置”，或正常关闭分区/主窗口。
6. 确认图标已恢复后，再由你手动删除测试快捷方式。

若恢复失败，不要删除测试快捷方式或恢复记录。记录位于：

```text
%LOCALAPPDATA%\DesktopManager\desktop-icon-recovery.json
```

应用会保留该记录并停止新的移动，便于下一次启动继续恢复。

## 项目结构

```text
src/DesktopManager/Core/                 公共坐标、图标模型和分区会话
src/DesktopManager/Shell/                Windows Shell 枚举与坐标写入
src/DesktopManager/UI/                   WPF 半透明分区窗口
src/DesktopManager/Demo/                 白名单、恢复协调与探针
tests/DesktopManager.Tests/              可脱离真实桌面的自动化测试
docs/spikes/                              Shell 可行性和实机验证记录
.agents/roles/                            专业子代理职责与边界
```

## 当前边界

- 只支持主显示器，坐标边界为相对主显示器原点的整数物理像素。
- 只允许移动一个固定名称的测试快捷方式，不提供任意真实图标批量管理。
- Explorer 重启时会终止当前移动；后续恢复依赖重新连接和重新解析图标身份。
- 100%、125% 和 150% 缩放下的视觉对齐仍需在目标机器上逐项人工确认。
- 全局快捷键若被其他软件占用，当前 Demo 只显示基础错误信息。
- 日程表和番茄钟属于下一阶段；按项目安全边界，只有核心 Demo 通过后再开发。

## Git 状态

仓库初始化在 `main` 分支，但不会替你创建提交、配置远端或推送。准备同步到 GitHub 时可自行执行：

```powershell
git add .
git commit -m "feat: add DesktopManager feasibility demo"
git remote add origin <你的 GitHub 仓库地址>
git push -u origin main
```
