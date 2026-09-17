# Codex 集成

GUI → Codex → 一键连接，或：

```powershell
.\TransMessage.exe codex install
.\TransMessage.exe codex status
.\TransMessage.exe codex uninstall
```

优先使用进程环境 CODEX_HOME；否则 `%USERPROFILE%\.codex\config.toml`。修改前创建 `config.toml.transmessage-backup-YYYYMMDD-HHMMSS-fffffff`，保留最近五份。Tomlyn 解析配置，通过语法节点定位顶层 notify 的值，只替换该值，保留其他配置和注释。解析失败不改文件。重复安装无重复项；卸载恢复原 notify，而不会用旧备份覆盖用户后来新增的其他设置。notify 被别人修改时卸载安全停止。

原 notify 命令保存在本地 integration 状态中并继续转发；该命令由用户本来就已配置的程序执行。若涉及复杂命令，请用 exe 脚本宿主及分开的参数，不把整个 shell 命令当作文件路径。

CLI/TUI/共享 core 的 app-server 客户端使用 notify；Desktop/VS Code 同时由 `sessions/**/*.jsonl` 只读监听补充。无需对每个任务另加命令。正在运行的 Codex 会话需重启以加载新配置。将软件放在固定目录后再集成；移动 exe 后重新执行 install。

完成必须属于可识别的用户 session。子代理、internal source、非 user thread_source、不匹配 root_turn_id、已知内部标题/压缩提示均跳过。JSONL 需要 task_started → user_message → 匹配的 task_complete/turn_aborted；工具调用、普通 assistant 中间文本不算结束。

以 thread-id + turn-id 在磁盘去重七天，跨重启和 notify/watcher 两路径有效。格式不认识时不推断。启动扫描不重放旧会话；只扫描最近会话，追加事件由文件监听接续。单行上限 2 MiB，超大历史只读取元数据和 4 MiB 尾部；无法重建真实轮次时跳过。

摘要设置位于 `[codex] include_result`、`max_result_chars`。发送前本地脱敏；没有可靠耗时则省略。不上传原始 rollout。`doctor` 检查配置和最近本地元数据，真正的端到端完成通知需要实际跑一轮 Codex。
