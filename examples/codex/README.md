# 自动接入

在解压目录运行 `TransMessage.exe codex install`，重启当前 Codex 会话；解除使用 `codex uninstall`。无需手填 JSON 或每次运行额外命令。

正式程序会校验 session_meta 为用户任务，测试时需同时准备匹配的会话元数据，不能仅凭任意 JSON 触发。
