# TransMessage

TransMessage 可以在 Codex、代码、下载等任务完成后，通过 **腾讯官方 QQ 机器人**通知你，并在手机私聊中查询、继续授权的 Codex 会话。

Windows 10 / 11 x64 · 中文 GUI · 托盘后台 · Portable · 无需安装 .NET、Python、Node 或 Docker。

## 5 分钟开始

1. 从 [Releases](https://github.com/KuaiSuMi/TransMessage/releases) 下载 `TransMessage-win-x64.zip`，解压到固定目录。
2. 双击 `TransMessage.exe`。
3. 进入 **QQ 机器人**，打开 [QQ 开放平台](https://q.qq.com/)，创建机器人并填写 AppID/AppSecret，点击保存并验证。
4. 生成绑定码，在手机私聊机器人发送 `bind 123456`；群聊先 @机器人。回到程序刷新目标。
5. 点击 **发送测试通知**。腾讯控制台可能要求沙箱成员、事件权限、IP 白名单或主动消息授权，按控制台当前规则完成。
6. 进入 **Codex**，一键连接，然后重启已打开的 Codex 会话。保持 TransMessage 后台运行。

QQ 创建/审核耗时由平台决定，不保证五分钟内获得权限。凭证从不随软件提供；真实 QQ 网络收发需要你自己的机器人。

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

## 0.2.2 修复

修复已有 Desktop 对话完成后漏通知：识别当前 `item_completed / UserMessage` 输入事件；重启监听时流式恢复完整轮次状态，避免长会话起点落在原 4 MB 尾部范围之外。每个会话独立跟踪，忙碌文件按读取开始时的长度处理，避免持续写入阻塞其他会话。启动扫描期间新完成的任务会正常通知，启动前已完成的历史记录不补发。

## 0.2.0 新功能

- **通知历史**：搜索、状态筛选、复制和清空；保存最近 30 天、最多 500 条，内容脱敏后用 Windows DPAPI 加密。
- **QQ 私聊控制**：在「QQ 控制」选择主人、启用连接并授权会话，然后发送 `任务`、`状态 编号`、`继续 编号 要求`。
- **等待输入与回答**：由 TransMessage 创建的托管会话可提醒问题，使用 `回答 问题码 答案` 继续；敏感答案只在本机输入。
- **原始时间戳**：QQ 消息使用来源事件时间，统一换算为北京时间并显示到秒，例如 `原始时间：2026-10-15 12:34:56（北京时间）`；来源未提供时明确标注接收时间。

**现有 Desktop 的边界**：本机已验证官方共享队列写入、读取和删除；追加消息能排队，但尚未验证 Desktop 消费执行。当前 Windows Desktop 实时控制接口不可连接，无法获取其待输入状态或回答原窗口的问题。完整远程问答需要选择「TransMessage 托管」。设置步骤与命令见 [QQ 控制 Codex](docs/REMOTE_CODEX.md)。

## 后台和数据

- 关闭窗口后留在系统托盘。右键可打开窗口、测试、暂停、打开日志或真正退出。
- 设置中可开启当前用户登录启动，不需要管理员。
- 默认数据：`%LOCALAPPDATA%\TransMessage`。
- 使用 `--portable`，或在 exe 旁建立空文件 `portable.flag`，数据改为旁边的 `data`。
- Secret 和 API Token 使用 Windows DPAPI 加密，只能由同一 Windows 账户解密。换电脑后重新输入。
- API 默认监听 127.0.0.1:18765，所有请求要求 Token；局域网访问需要在 GUI 主动开启。

## 版本边界

当前版本 **0.2.3**（QQ 时间统一为北京时间，精确到秒）。官方 QQ 协议通过模拟 HTTP/WebSocket 测试；用户此前已确认下载检测与 Codex 监测通过。本版新增远程控制尚未完成真实 QQ 手机收发验收。主动消息权限和配额由腾讯决定。

Codex 使用官方 notify 与只读本地会话事件；未知格式、内部任务、子代理会安全跳过。同一 thread + turn 七天内只占用一次发送尝试；已交付结果不确定的请求不会自动重发。不能保证网络或平台异常时一定送达。

没有系统服务、非官方 QQ 协议、云中继。发布包当前未签名。详见 [验收记录](docs/ACCEPTANCE.md) 与 [真实设备测试](docs/QQ_REAL_DEVICE_TEST.md)。

## 文档

[QQ 配置](docs/QQ_SETUP.md) · [Codex](docs/CODEX.md) · [CLI](docs/CLI.md) · [HTTP API](docs/HTTP_API.md) · [下载监控](docs/DOWNLOAD_WATCHER.md) · [故障排查](docs/TROUBLESHOOTING.md) · [隐私与安全](docs/SECURITY.md) · [开发与发布](docs/DEVELOPMENT.md) · [架构](docs/ARCHITECTURE.md) · [研究依据](docs/RESEARCH.md)

仓库没有新增主项目 LICENSE。第三方许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
