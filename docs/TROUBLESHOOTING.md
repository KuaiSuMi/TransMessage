# 故障排查

先运行 `TransMessage.exe doctor --online`，或 GUI → 诊断与日志。

| 状态 / 错误 | 操作 |
|---|---|
| QQ_NOT_CONFIGURED | QQ 设置中填写并验证 AppID/AppSecret |
| QQ 401 / QQ_AUTH_SCHEMA | 核对凭证、重置 AppSecret，确认机器人状态 |
| QQ 403 / 权限不足 | 检查当前腾讯控制台事件授权、主动消息权限、沙箱成员、IP 白名单 |
| QQ 404 | 机器人可能已被移出群或解除关系；重新绑定 |
| QQ 429 | 等待配额恢复，降低频率；系统最多有限重试 |
| QQ 5xx / QQ_DELIVERY_UNCERTAIN | 检查服务/网络；可能已送达，不自动重发同一 ID |
| 绑定确认成功但测试失败 | 被动回复与主动发送权限不同，检查主动消息授权 |
| CODEX_PARSE | 修复原 config.toml 语法后重新集成，原文件未改 |
| CODEX_MODIFIED | notify 已被其他工具修改；核对备份后自行合并，程序不会覆盖 |
| Codex 没通知 | 重启 Codex、保持本地后台、运行 doctor；未知或内部会话被安全跳过 |
| API 无法启动 | 端口被占用时换端口并保存；检查诊断中的后台组件错误 |
| LAN 无法访问 | 确认 GUI 开关、同网段、Token 和 Windows 防火墙手动规则 |
| DPAPI | 确认同一 Windows 账户；移动到另一电脑需重新输入凭证 |
| CONFIG_PARSE / DEDUP_CORRUPT | 先备份数据目录，修复 TOML；去重记录损坏可移走该文件再启动，历史事件可能失去去重保护 |
| 下载无通知 | 检查目录存在、是否启用、是否仍为临时扩展名/被程序占用 |
| 更新失败 | 查看 data 中 update-result.txt；当前 exe 或 .previous 仍可恢复 |

不同 `--data-dir` / `--portable` 是独立实例和配置。若 CLI 与 GUI 看起来不一致，先确认使用同一个数据目录。不要把被防病毒软件拦截等未验证情况描述成已安装成功。
