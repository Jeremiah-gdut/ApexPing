# ApexPing

Windows 桌面的 Apex 战术标记轮盘，支持原生图标、音效、英文角色语音和好友房间。

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

编译结果位于 `release/ApexPing-win-x64/`，运行其中的 `ApexPing.exe`。运行时合并进 EXE，`assets` 和输入组件等配套文件仍需保留。

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

### 好友联机

1. 点击设置窗口或托盘菜单中的「好友房间」，填写昵称，在声音设置中选择角色。
2. 一人点击「创建房间 → 复制」，将 APEX 房间码发给朋友；朋友粘贴后点击「加入」。最多 8 人，双方都需运行 ApexPing。
3. 程序优先使用局域网直连或 UDP 打洞 / IPv6 P2P；直连不可用时自动通过免费公共 Nostr 中继转发。成员列表显示实际连接方式。

首次联机请允许 Windows 防火墙的网络访问。关闭「允许公网联机」后只使用局域网；要更改此项请先离开房间。暂停会停止标记收发，「屏蔽收到的房间标记与语音」只屏蔽接收；音量、静音和关闭角色语音也适用于收到的标记。预览按钮只在本机显示。

房间消息端到端加密，房间码仅分享给朋友，不保存到配置，也不会在重启后自动加入。直连双方可知对方连接地址，公共中继可见连接 IP、匿名房间标识与时间。免费节点可能限流或不可用，UDP 打洞不保证成功。

联机同步的是屏幕相对位置；不同视角下，同一标记位置未必对应同一目标。显示器按主屏优先、其余屏幕按位置排序，目标显示器不存在时显示在主屏。

## 感谢

- [lolPing](https://github.com/darrenprx/lolPing)：项目灵感、房间通信思路与 Windows 输入组件，随源码及发布包保留 MIT 声明。
- [NBitcoin.Secp256k1](https://github.com/MetacoSA/NBitcoin)：Nostr 消息签名与验证，随发布包保留 MIT 声明。
- [RSX](https://github.com/r-ex/rsx)：原生资源提取工具。
