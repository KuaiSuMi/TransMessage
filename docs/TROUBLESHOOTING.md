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
| Desktop 无实时控制通道 | 本机当前版本只支持共享队列；远程问答改用 TransMessage 托管会话 |
| 入队成功但没执行 | 确认 Desktop 正在运行并查看原会话；入队确认不等于执行确认，不盲目重复提交 |
| CONTROL_THREAD / CONTROL_OWNER | 在电脑授权正确会话并选择启用的私聊主人；群聊不能控制 |
| CONTROL_EXPIRED / CONTROL_ANSWER_UNCERTAIN | 在电脑检查原问题和连接；旧码不重放，不重复批准 |
| CODEX_TIMEOUT / CONTROL_OFFLINE | 查询原会话是否已执行，再在电脑重新连接；不会自动恢复不确定指令 |
| CODEX_RPC / no rollout found | 新建后从未启动的会话没有持久历史；撤销该空会话并重新创建 |
| 历史无法解密或读取 | 保留并备份 history.dpapi；确认 Windows 账户，程序不覆盖损坏文件 |
| API 无法启动 | 端口被占用时换端口并保存；检查诊断中的后台组件错误 |
| LAN 无法访问 | 确认 GUI 开关、同网段、Token 和 Windows 防火墙手动规则 |
| DPAPI | 确认同一 Windows 账户；移动到另一电脑需重新输入凭证 |
| CONFIG_PARSE / DEDUP_CORRUPT | 先备份数据目录，修复 TOML；去重记录损坏可移走该文件再启动，历史事件可能失去去重保护 |
| 下载无通知 | 检查目录存在、是否启用、是否仍为临时扩展名/被程序占用 |
| 目录监听已停止 | 恢复目录后，在 Codex 或下载与文件页面保存设置，重新启动对应监听；其他功能继续运行 |
| 运行中退出 | 更新到 0.3.5；此版本修复监控目录消失时的闪退。日志记录 GUI 启动、退出请求及异常类型和调用栈，可据此区分主动退出与异常退出 |
| 更新失败 | 查看 data 中 update-result.txt；当前 exe 或 .previous 仍可恢复 |

不同 `--data-dir` / `--portable` 是独立实例和配置。若 CLI 与 GUI 看起来不一致，先确认使用同一个数据目录。不要把被防病毒软件拦截等未验证情况描述成已安装成功。
