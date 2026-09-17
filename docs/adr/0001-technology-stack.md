# ADR 0001：Windows 原生、自包含的 .NET 10 应用

状态：已采用。日期：2026-09-18。首版版本：0.1.0。

## Architecture spike

仓库本地无提交、无有效文件；GitHub contents API 也返回 empty repository。未覆盖任何既有实现；使用 `codex/transmessage-v1` 分支。未添加主项目开源许可。

| 关注点 | C# / .NET 10 | Go | Rust |
|---|---|---|---|
| Windows GUI | WPF 原生控件、无 WebView | Fyne 需要图形栈；Walk 社区较小 | Tauri 引入 WebView2；其他原生 GUI 需更多整合 |
| Portable、自包含 | self-contained 单 exe，内嵌运行时 | 单 exe 较小，GUI 仍有代价 | 编译为原生 exe，GUI 后端影响部署 |
| 托盘 | 随附 WinForms NotifyIcon | 额外库 / Win32 | 额外 crate / Win32 |
| 凭证 | 官方 ProtectedData / DPAPI | Windows syscall | windows crate |
| 文件监听 | FileSystemWatcher | fsnotify | notify crate |
| 子进程 | Process、异步流、Windows 控制台 | os/exec | std::process / tokio |
| HTTP | Kestrel，大小/连接/限流边界成熟 | net/http | axum / hyper |
| WebSocket | BCL ClientWebSocket | 外部实现 | tokio-tungstenite |
| 当前用户启动 | Registry HKCU Run | Win32 包 | windows crate |
| Windows Service | 可做，但跨登录会话与用户凭证冲突 | Windows service 包 | Windows service crate |
| 更新 | HttpClient + SHA256 + File.Replace | stdlib 可实现 | crate / std 可实现 |
| QQ / Codex | 官方协议直接实现，无 Node/Python | 同样可实现 | 同样可实现 |
| CI | windows-latest + setup-dotnet | Go + GUI 构建依赖 | Rust + GUI / 链接依赖 |
| 包体积 | 较大；实际数值见验收报告 | 通常较小；未测 GUI 发布物 | 通常较小；未测 GUI 发布物 |
| 长期维护 | Windows-only 要求与官方桌面栈契合 | GUI 整合成本较高 | GUI 整合与异步复杂度较高 |

选择 .NET 10 LTS + WPF + WinForms 托盘。没有浏览器后端、WebView、Node、Python 或安装器依赖。开发 SDK 为 10.0.401，运行时发布锁定到项目构建解析的 .NET 10 补丁版。SDK ZIP 的 SHA512 按微软 release-metadata 验证。

GUI 使用 Apple-Style skill 的内容/导航分层、语义色、留白、圆角、44 DIP 控件、可见焦点和原生键盘行为。Windows 使用 Segoe UI / Microsoft YaHei UI，不分发 SF 字体、SF Symbols，不伪装 macOS 系统控件。WPF 没有 Apple 的 Liquid Glass API；本版使用克制的导航材质与实色内容，未宣称实现物理折射。没有装饰性动画，因此降低动态效果无需另起动画路径。

CLI 使用控制台子系统，保证 CMD 等待、标准流和退出码正确。双击时仅释放应用自己创建的控制台；不隐藏用户已有终端。代价是部分机器上双击时可能短暂闪过控制台。

核心、应用、自动测试、GUI 渲染测试共四个项目。Windows Service 暂不提供：普通用户托盘后台 + HKCU Run 已满足需求，Service 会引入 Session 0、交互绑定和 DPAPI 账户管理问题。

使用 0.1.0 表达首个可用版本：已完成自动化模拟测试和发布构建；QQ 真实凭证与 Windows 10 实机验证仍需外部环境，不能据此称为已经通过所有实机验收的 1.0。

## 发布与更新

采用自包含单 exe，避免多 DLL 更新时发生混合版本。更新先下载、验证 SHA256、暂存，再由当前版本的独立 helper 等待应用退出、原子替换。新版通过本地命名管道报告存活后完成；启动失败恢复旧 exe。保留 `.previous`，不覆盖 data 和用户配置。

SHA256 来自同一 GitHub Release，提供传输完整性验证，不等价于代码签名。未配置代码签名证书，首版不伪造发布者签名。
