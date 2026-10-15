# CLI

在 PowerShell 用 `& '含空格的路径\TransMessage.exe' ...`。CMD 用双引号包围路径。统一选项 `--portable` / `--data-dir <目录>` 放在子命令前最清晰。

| 命令 | 行为 |
|---|---|
| 无参数 / gui | 打开 GUI |
| --background / serve | 托盘后台，无需管理员 |
| send "完成" | 发送通知 |
| send --title "训练" --message "完成" --level success | 结构化通知 |
| run -- executable args | 运行并通知，保留退出码 |
| watch-file 文件 | 等待文件出现/稳定，通知后退出 |
| watch-dir 目录 | 持续监控目录，不递归 |
| setup | 终端交互式 QQ 配置、绑定、Codex、启动和测试 |
| doctor [--online] | 本地诊断；online 另测 QQ 认证和更新 |
| codex install / uninstall / status | 安装、移除和检测集成 |
| codex probe-desktop | 检测实时控制 socket 和会话发现，不提交提示 |
| startup enable / disable | 当前用户登录启动 |
| update [--install] | 检查版本；install 下载、校验并安装 |
| version | 版本、commit、构建时间、运行时、OS、架构 |
| --help | 内置帮助 |

level：info / success / warning / error。

run 继承 stdin 并实时复制 stdout/stderr，附带失败末尾输出（默认 12 行，最大 100）。Ctrl+C 到达同控制台子进程；取消后留 3 秒清理，再终止进程树。BAT/CMD 文件通过 cmd /d /s /c 执行，含字面引号的复杂 BAT 参数请显式使用 `run -- cmd /c ...`。

stdout/stderr 被转为管道以收集尾部，因此依赖 TTY 检测、终端原始模式或光标控制的交互应用可能改变缓冲/显示行为；没有宣称完整 ConPTY 终端仿真。文本命令和普通 stdin 交互正常代理。

QQ 发送失败仅警告，不改变被执行命令退出码。send 成功为 0、失败为 1；配置/输入错误为 2。doctor 有失败项返回 1，仅警告仍返回 0。CLI 与 GUI 使用同一 exe，无需别名或 PATH 修改。
