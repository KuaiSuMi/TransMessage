# 开发与发布

开发：Windows x64 + .NET SDK 10.0.401。终端用户不需 SDK。`global.json` 锁定 feature band，`packages.lock.json` 锁定 NuGet。

```powershell
dotnet restore --locked-mode
dotnet build
./scripts/verify.ps1
./scripts/build.ps1
```

本次环境 SDK 下载在 `.tools/dotnet`，脚本自动发现；该目录不入库。安装 SDK 的官方 ZIP 按微软 SHA512 校验。可显式传 `-Dotnet`。

verify 依次运行格式检查、编译、自动测试、WPF 渲染检查和漏洞审计；不需要 QQ 凭证。测试目录均独立，不修改当前用户真实 Codex 配置或启动项。

build 生成自包含单 exe，包含 .NET/WPF/ASP.NET Core，不裁剪 WPF。输出 `dist/TransMessage-win-x64.zip`、`SHA256SUMS`、`build-info.json`。运行时原生库由 .NET 解包到当前用户临时目录，用户无需安装运行时。build-info 给出可运行 exe 的实际暂存路径和构建版本信息。

对最终发布 exe 运行 CLI 验收：

```powershell
$env:TM_TEST_EXE = (Get-Content dist/build-info.json | ConvertFrom-Json).executable
dotnet test tests/TransMessage.Tests --filter FullyQualifiedName~CliIntegrationTests
```

CI push/PR 运行相同检查并上传 ZIP。v* tag 在检查完成后发布 GitHub Release。版本目前为 0.2.0；发布下一版本应同步 Directory.Build.props 与 scripts/build.ps1，再打相同 tag。不能仅修改 ZIP 文件名伪造版本。

`tests/TransMessage.GuiTests` 使用实际 WPF 组件渲染 10 页浅色/深色，以及设置、QQ 控制、历史的 200% 文字样例，共 26 张，历史使用独立测试数据。它验证资源/布局可渲染，不代替真实鼠标、读屏、不同 DPI/OS 和系统托盘的完整实机验收。

可选原生协议联调（需要本机 Codex，不是常规 CI 前提）：

```powershell
dotnet run --project tests/TransMessage.CodexProbe
# 可附加一个全新输出目录和 codex.exe 绝对路径
```

该探针使用真实 Codex 进程、独立 CODEX_HOME、本地脚本化模型和模拟私聊发送，验证创建空会话、开始轮次、输入请求、QQ 指令回答、官方确认与完成结果；不访问真实 QQ，不使用付费推理，也不覆盖用户 Codex 配置。输出 evidence.json。它不能代替实际模型行为、QQ 平台权限和 Desktop 消费共享队列的验收。

Portable 无需安装脚本。Release 内 `scripts/install.ps1 -Destination ... [-Startup] [-Codex]` 仅复制到用户目录并建立开始菜单快捷方式；可选参数才改变启动/集成。uninstall 删除集成和快捷方式，默认保留配置；`-PurgeData` 才清理用户/portable 数据。程序目录由用户在退出托盘后删除，避免脚本递归误删任意目标。

发布前：审查验收记录，完成 QQ 实机测试，确认版本与 hash，按需要取得代码签名证书。仓库主项目 License 由所有者决定。
