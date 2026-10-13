# ApexPing

Windows 桌面的 Apex 战术标记轮盘，支持原生图标、音效与英文角色语音。

## 安装

使用发布包时，完整解压后运行 `ApexPing.exe`，保留所有配套文件。

- 系统：Windows x64。
- 普通版本需要 .NET 8 Desktop Runtime（x64）。
- 使用 `-Portable` 编译的版本自带运行时，无需单独安装 .NET。

## 编译

安装 .NET 8 SDK 和 Visual Studio Build Tools 的「使用 C++ 的桌面开发」组件。

原生资源放在 `assets/native/`，包含 `manifest.json` 及其引用的 PNG / WAV 文件。资源保留在本地，不纳入 Git；也可以在应用中选择已有的资源文件夹。

在项目目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

自带运行时的版本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Portable
```

编译结果位于 `release/`，运行 `release/ApexPing.exe`。

## 使用

| 操作 | 方法 |
| --- | --- |
| 打开轮盘 | 按住 Alt，再按住左键拖动 |
| 放置标记 | 拖向图标后松开左键；标记落在最初按下的位置 |
| 取消 | 回到中央、右键、Esc 或先松开 Alt |
| 暂停 / 恢复 | Ctrl + Alt + P，或设置中的启用按钮 |
| 预览 | 点击设置窗口里的标记按钮 |
| 角色语音 | 在「声音 → 角色语音」中选择角色或关闭语音 |

设置可调整触发键、音量、静音、标记大小和显示时间。默认在全屏应用中暂停，可在设置中关闭此选项。

关闭窗口后继续在托盘运行；点击托盘图标打开设置，右键菜单退出。设置保存于 `%APPDATA%/ApexPing/settings.json`。

## 感谢

- [lolPing](https://github.com/darrenprx/lolPing)：项目灵感与 Windows 输入组件，保留其 [MIT 许可证](native/hook-helper/LICENSE)。
- [RSX](https://github.com/r-ex/rsx)：原生资源提取工具。
- EA / Respawn：Apex Legends 的图标、音效与角色语音；这些素材不属于输入组件的 MIT 授权范围。
