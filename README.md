# TransMessage

TransMessage 可以在 Codex、代码、下载等任务完成后，通过 **腾讯官方 QQ 机器人**通知你，并在手机私聊中引用回复、发送图文并继续 Codex 会话。

Windows 10 / 11 x64 · 中文 GUI · 托盘后台 · Portable · 无需安装 .NET、Python、Node 或 Docker。

## 快速开始

1. 从 [Releases](https://github.com/KuaiSuMi/TransMessage/releases) 下载 `TransMessage-win-x64.zip`，解压到固定目录。
2. 双击 `TransMessage.exe`。
3. 进入 **QQ 机器人**，打开 [QQ 开放平台](https://q.qq.com/)，创建机器人并填写 AppID/AppSecret，点击保存并验证。
4. 生成绑定码，在手机私聊机器人发送 `bind 123456`；群聊先 @机器人。回到程序刷新目标。
5. 点击 **发送测试通知**。腾讯控制台可能要求沙箱成员、事件权限、IP 白名单或主动消息授权，按控制台当前规则完成。
6. 进入 **Codex**，一键连接，然后重启已打开的 Codex 会话。保持 TransMessage 后台运行。

配置需要你自己的腾讯官方 QQ 机器人，按 QQ 开放平台完成创建与审核。

## 日常使用

```powershell
# 路径可包含中文和空格
.\TransMessage.exe send '任务完成'
.\TransMessage.exe run -- python train.py
.\TransMessage.exe run -- cmd /c echo hello
.\TransMessage.exe watch-file 'D:\output\model.bin'
.\TransMessage.exe watch-dir 'D:\Downloads'
.\TransMessage.exe doctor --online
```

命令结束后，成功/失败都通知，退出码仍是原命令的退出码。下载监控识别 `.crdownload`、`.part`、`.tmp`，结合重命名、大小、修改时间和独占打开检查。只读取文件元数据，不读取下载内容。

GUI **快捷集成**提供 PowerShell、CMD、Python、JavaScript、HTTP 代码与一键复制。

## 在手机继续 Codex

引用完成通知，直接回复下一步要求。已有 Desktop 会话会在后台被唤醒，当前页面保持原样。需要远程回答问题或调整模型时，在「QQ 控制」创建实时问答会话。

- 引用后发送 `/d`，分开发送图片和文字，最后 `/s` 一起提交；`/c` 取消草稿。
- 引用完成通知发送 `/full` 阅读完整回复，每页最多 4 段；按提示用 `/full 2` 继续。
- 引用完成通知发送 `/file` 取回该轮回复中链接的本地文件和图片，每页最多 3 个附件；用 `/file 2` 继续。
- 全文、文件清单和附件消息保留原轮次关联，可继续引用操作。全文与清单标注原始北京时间，精确到秒。

完整用法见 [QQ 控制 Codex](docs/REMOTE_CODEX.md)。

## 0.3.2 后台继续、全文与附件

QQ 继续消息入队后，自动唤醒原 Desktop 会话处理。引用新版完成通知发送 `/full` 获取该轮全文，发送 `/file` 获取该轮回复链接的实际本地附件；支持分页，保留原始北京时间。文件使用 QQ 原生文件卡片，单个文件最高 200 MB。

## 0.3.1 点击编号

QQ 会话编号改为官方可点击标签，点击后自动填入输入框，方便接着输入要求或复制编号；不会自动发送。

## 0.3.0 图文与引用回复

QQ 引用通知后可直接回复；引用 + `/d`（或 `/draft`）开始草稿，分开发送图片和文字，最后 `/s` 提交、`/c` 取消。消息带 `#会话编号`，无需逐会话授权。Desktop 共享队列与实时会话合并到同一页，实时会话支持 `/model`、`/reasoning` 设置下一轮。多个草稿需引用或编号明确目标。

使用方法见 [QQ 控制 Codex](docs/REMOTE_CODEX.md)。

## 0.2.2 修复

修复已有 Desktop 对话完成后漏通知：识别当前 `item_completed / UserMessage` 输入事件；重启监听时流式恢复完整轮次状态，避免长会话起点落在原 4 MB 尾部范围之外。每个会话独立跟踪，忙碌文件按读取开始时的长度处理，避免持续写入阻塞其他会话。启动扫描期间新完成的任务会正常通知，启动前已完成的历史记录不补发。

## 0.2.5 退出修复

修复启用 QQ 控制后，托盘退出仍残留进程、无法再次打开的问题。Codex 后台读取与消息循环不再依赖窗口线程，退出时可完成清理并释放单实例锁；托盘退出与后台停止使用同一退出入口。

## 0.2.4 名称简化

提取实际请求，去掉附件清单和 Markdown 标记，使用首行首句，最多 32 个可见字符；问答回复显示「确认：问题摘要」，不显示内部 JSON。QQ 任务列表、状态和托管完成消息使用相同短名称。仅影响新消息，不重命名会话或改写旧消息。

## 0.2.0 新功能

- **通知历史**：搜索、状态筛选、复制和清空；保存最近 30 天、最多 500 条，内容脱敏后用 Windows DPAPI 加密。
- **QQ 私聊控制**：在「QQ 控制」选择主人、启用连接，然后发送 `任务`、`状态 编号`、`继续 编号 要求`。
- **等待输入与回答**：由 TransMessage 创建的托管会话可提醒问题，使用 `回答 问题码 答案` 继续；敏感答案只在本机输入。
- **原始时间戳**：QQ 消息使用来源事件时间，统一换算为北京时间并显示到秒，例如 `原始时间：2026-10-15 12:34:56（北京时间）`；来源未提供时明确标注接收时间。

**已有 Desktop 会话**：通过官方共享队列接收图文，随后在后台唤醒原会话处理。保持 Codex Desktop 运行；原窗口的问题和模型设置在 Desktop 中处理。需要在 QQ 实时回答问题时，使用 TransMessage 创建的实时问答会话。设置步骤与命令见 [QQ 控制 Codex](docs/REMOTE_CODEX.md)。

## 后台和数据

- 关闭窗口后留在系统托盘。右键可打开窗口、测试、暂停、打开日志或真正退出。
- 设置中可开启当前用户登录启动，不需要管理员。
- 默认数据：`%LOCALAPPDATA%\TransMessage`。
- 使用 `--portable`，或在 exe 旁建立空文件 `portable.flag`，数据改为旁边的 `data`。
- Secret 和 API Token 使用 Windows DPAPI 加密，只能由同一 Windows 账户解密。换电脑后重新输入。
- API 默认监听 127.0.0.1:18765，所有请求要求 Token；局域网访问需要在 GUI 主动开启。

## 版本边界

当前版本 **0.3.2**（Desktop 后台唤醒、全文与附件）。用户已确认 QQ 会话编号可点击并填入输入框，下载检测与 Codex 监测通过。各项实测结果见 [验收记录](docs/ACCEPTANCE.md)。主动消息权限和配额由腾讯决定。

**软件内更新**：打开「诊断与日志 → 软件更新」，点击「检查更新」，发现新版后点击「下载并更新」。程序从本仓库正式 Release 下载并校验，替换后自动重启到托盘，保留配置；新版启动失败会回滚。[0.3.1 已发布](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.3.1)，可从软件内获取；本机已通过真实更新源从 0.3.0 升至 0.3.1，配置、旧版备份和重启均已核验。自动检查只提示新版，不自动安装。若旧版退出后已残留且无法打开，先在任务管理器中结束 TransMessage，再解压新版 ZIP 覆盖程序。

Codex 使用官方 notify 与只读本地会话事件识别普通用户任务。同一 thread + turn 七天内只占用一次发送尝试；投递结果可在通知历史查看，送达状态不确定时由你决定是否重发。

没有系统服务、非官方 QQ 协议、云中继。发布包当前未签名。详见 [验收记录](docs/ACCEPTANCE.md) 与 [真实设备测试](docs/QQ_REAL_DEVICE_TEST.md)。

## 文档

[QQ 配置](docs/QQ_SETUP.md) · [Codex](docs/CODEX.md) · [CLI](docs/CLI.md) · [HTTP API](docs/HTTP_API.md) · [下载监控](docs/DOWNLOAD_WATCHER.md) · [故障排查](docs/TROUBLESHOOTING.md) · [隐私与安全](docs/SECURITY.md) · [开发与发布](docs/DEVELOPMENT.md) · [架构](docs/ARCHITECTURE.md) · [研究依据](docs/RESEARCH.md)

仓库没有新增主项目 LICENSE。第三方许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
