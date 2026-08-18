# dsh-launcher

> DeepSeek Harness 托盘启动器 · A system-tray launcher for [DeepSeek Harness](https://www.npmjs.com/package/@deepseek-ai/dsh) (DSH)

把 DSH 的日常使用变成一个托盘小工具：双击启动，自动拉起服务、自动打开界面、插件变动自动重启、常驻系统托盘。

A tiny Windows tray app that turns DSH into a one-click tool: starts the service hidden, opens the UI, auto-restarts when plugins change, and lives in the system tray.

![Windows](https://img.shields.io/badge/Windows-10%2F11-blue) ![C#](https://img.shields.io/badge/.NET-Framework%204.x-512BD4) ![build](https://img.shields.io/github/actions/workflow/status/zhaobanghong/dsh-launcher/build.yml?label=build)

---

## 功能 Features

- **一键启动**：双击 exe → 隐藏启动 `npx @deepseek-ai/dsh web` → 等就绪 → 打开 DSH 界面 → 常驻托盘
  One-click start: hidden server launch, wait until ready, open the UI, stay in tray.
- **插件变动自动重启**：监控 DSH 插件目录（`~/.dsh/profiles/web`），检测到插件安装/卸载/启停后，**等聊天工作全部结束**再自动重启服务（重启会关闭并重开界面，插件才生效）
  Auto-restart on plugin changes: watches the plugin profile, waits until all agent work finishes, then restarts the service (web UIs are closed and reopened so plugins load).
- **防写一半重启**：连续 3 秒没有新的插件变动才确认（每次新变动都会刷新计时）
  3-second quiet window before confirming a change (new events refresh the timer).
- **托盘菜单**：打开界面 / 重启服务 / 关于 / 退出；菜单跟随系统深浅色模式
  Tray menu: open UI / restart / about / exit; follows Windows light/dark mode.
- **单实例**：重复双击只会重新打开界面
  Single instance: launching again just re-opens the UI.
- **中/英文界面**：`config.json` 里 `language` 切换
  Chinese/English UI, switch via `language` in `config.json`.

## 下载 Download

从 [Releases](https://github.com/zhaobanghong/dsh-launcher/releases) 下载 `dsh-launcher.exe`（Windows 10/11，无需安装 .NET——系统自带 .NET Framework 4.x）。

Download `dsh-launcher.exe` from Releases. No .NET SDK needed; Windows 10/11 ships the required .NET Framework 4.x.

## 使用 Usage

1. 把 `dsh-launcher.exe` 放到任意文件夹（例如 `D:\tools\dsh-launcher\`）或直接放桌面
   Put `dsh-launcher.exe` anywhere (e.g. `D:\tools\dsh-launcher\`) or on the Desktop.
2. 双击 `dsh-launcher.exe`。**不会在 exe 旁边生成任何文件**——配置自动写到 `%APPDATA%\dsh-launcher\config.json`（可随时编辑）
   Double-click the exe. **No files are created next to it** - config is auto-created at `%APPDATA%\dsh-launcher\config.json`.
3. 右键托盘图标可：打开界面、手动重启服务、退出（退出会停止服务）
   Right-click the tray icon: open UI, restart service, or exit (exit stops the service).
4. 日志在 `%TEMP%\dsh-launcher.log`，出问题先看它
   Logs go to `%TEMP%\dsh-launcher.log` - check it first when something is off.

> **便携模式 Portable mode**：把 `config.json` 放在 exe **同目录**，程序会优先读它（适合整个文件夹拷走用）；否则用 `%APPDATA%\dsh-launcher\config.json`（首次运行自动生成）。
> If a `config.json` sits next to the exe it wins (portable setup); otherwise the AppData one is used (auto-created on first run).

> 前提：已安装 [Node.js](https://nodejs.org)（用于 `npx`），且能通过 `npx @deepseek-ai/dsh web` 启动 DSH。
> Prerequisite: [Node.js](https://nodejs.org) installed (for `npx`), DSH startable via `npx @deepseek-ai/dsh web`.

## 配置 config.json

| 字段 | 默认值 | 说明 |
|---|---|---|
| `port` | `3080` | DSH 服务端口 |
| `serverCommand` | `npx @deepseek-ai/dsh web` | 启动服务的命令 |
| `workspace` | `""`（exe 所在目录） | 服务工作目录 |
| `profileDir` | `""`（`~/.dsh/profiles/web`） | 插件监控目录 |
| `chromeAppId` | `""`（自动发现） | DSH Chrome PWA 的 app-id；留空自动扫描 |
| `language` | `"zh"` | `"zh"` 或 `"en"` |
| `appName` | `"dsh-launcher"` | 托盘提示/气泡标题 |
| `openOnStart` | `true` | 启动后自动打开界面 |
| `autoRestart` | `true` | 插件变动自动重启总开关 |
| `closeWebUisOnRestart` | `true` | 重启时关闭并重开界面窗口 |
| `watchdog` | `true` | 服务意外退出时自动重启 |
| `changeQuietSeconds` | `3` | 变动确认前的静默秒数 |
| `idlePollSeconds` | `3` | 会话空闲轮询间隔（秒） |
| `idleConfirmCount` | `2` | 连续几次空闲才重启 |
| `stopTimeoutSeconds` | `15` | 等待旧服务停止上限 |
| `startTimeoutSeconds` | `180` | 等待新服务启动上限 |
| `logFile` | `""`（`%TEMP%\dsh-launcher.log`） | 日志路径 |

## 从源码构建 Build from source

```powershell
git clone https://github.com/zhaobanghong/dsh-launcher.git
cd dsh-launcher
.\build.ps1          # 输出 dist\dsh-launcher.exe
```

无需任何 SDK：构建脚本直接调用系统自带的 .NET Framework 编译器（csc.exe）。
No SDK needed: the script uses the csc.exe that ships with Windows.

GitHub Actions 会在打 `v*` 标签时自动构建并发布 Release。

## 测试环境变量 Test-only environment variables

| 变量 | 作用 |
|---|---|
| `DSH_MUTEX_SUFFIX` | 单实例标识后缀（用于并行跑多个实例测试） |
| `DSH_WATCH_DIR` | 覆盖插件监控目录 |
| `DSH_NO_RESTART=1` | 只记录日志、不真正重启（安全测试） |

## 工作原理 How it works

1. **启动服务**：隐藏窗口运行 `serverCommand`，轮询端口直到就绪
   Starts the server hidden, polls the port until ready.
2. **空闲检测**：通过 DSH 的 HTTP API（`POST /api/session.list`）判断所有会话是否空闲
   Idle detection via DSH's HTTP API (`session.list`), checking every session's `running` flag.
3. **插件监控**：只认清单文件（`package.json` / `pnpm-lock.yaml` / `pnpm-workspace.yaml` / `cordis.patch.yml`）和 `node_modules` 下新增/删除的包目录；插件包内部写入（缓存、运行时数据）一律忽略
   Plugin watch: only manifest files and new/removed packages under `node_modules` count; runtime writes inside packages are ignored.
4. **界面打开**：优先用 Chrome 内部 PWA 快捷方式 / `--app-id` 打开独立应用窗口；没有则回退默认浏览器
   UI open: Chrome PWA app window first (auto-detected app-id), browser as fallback.

## 注意 / Notes

- 重启时会强制结束占用 `port` 的进程（`taskkill /T /F`），请勿在服务运行关键任务时手动重启
  Restart force-kills the process listening on `port`; avoid manual restarts while critical work is running.
- 重启会把 DSH 的网页窗口关掉并重新打开（插件需要全新加载）
  Restart closes and reopens the DSH web windows (plugins need a fresh page load).

## License

[MIT](LICENSE)
