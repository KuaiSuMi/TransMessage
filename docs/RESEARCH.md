# 研究与取舍

## 0.2.0 追加核查（2026-10-15）

- 本机 Codex CLI 为 0.153.4，Desktop 使用的原生程序为 0.162.0-alpha.17.2。以实际 exe 生成的 experimental JSON Schema 核对 `thread/queue/add/list/delete`、`item/tool/requestUserInput`、`serverRequest/resolved` 等字段。
- [官方 App Server 文档](https://learn.chatgpt.com/docs/app-server)定义 JSONL stdio 请求、通知与服务器请求；[CLI 参考](https://learn.chatgpt.com/docs/cli/reference)提供进程入口。实验接口可能随 Codex 更新变化，不做静默旧协议降级。
- 官方源码 `app-server/src/lib.rs` 的 stdio transport 不是可跨客户端接管的广播总线。本机 `app-server proxy` 因控制 socket 不可达而失败（Windows 10050）。未重启或注入用户现有 Desktop。
- 在现有 Desktop 会话上用官方队列 API 添加无操作提示，读取确认后立即删除并核验不存在；从未对该会话调用 resume / turn/start。此结果证明共享队列可访问，不证明 Desktop 已消费或执行。
- 官方 `rollout/src/policy.rs` 将 RequestUserInput 和审批列为 transient/non-durable；不能从持久历史可靠恢复当前待输入。0.2.0 不通过 JSONL 猜测 Desktop 的实时问题。
- 原生联调使用官方测试中的 request_user_input SSE 协议形态，真实启动本机 Codex，隔离 CODEX_HOME 和本地模型服务。生产 RemoteCodex 完成「创建 → 继续 → 问题 → 私聊答案 → serverRequest/resolved → turn/completed」。证据位于 `artifacts/native-codex-v020c/evidence.json`。真实 QQ 和真实模型不在这项测试范围内。
- 新建空会话尚无 rollout；必须在已创建的连接直接开始第一轮，不能先读取/恢复它。已有持久历史的托管会话才走 resume。将这个真实联调发现加入自动回归。

以下为首版来源记录，保留日期与当时版本，不能视为本日所有组件的最新状态。

调查日期：2026-09-18（Asia/Shanghai）。优先官方仓库，源码在开发时实际下载核对。在线文档会变化，下面保存了决定实现的提交标识。

## Codex

- [官方配置参考](https://developers.openai.com/codex/config-reference/)：用户级配置位于 CODEX_HOME / ~/.codex；notify 不能由项目配置覆盖。
- [openai/codex fcf05456bb27e6c3d5677550f54011db6a2a0817](https://github.com/openai/codex/tree/fcf05456bb27e6c3d5677550f54011db6a2a0817)。`codex-rs/hooks/src/legacy_notify.rs` 明确序列化 type、thread-id、turn-id、cwd、可选 client、input-messages、last-assistant-message，并把 JSON 加在命令的最后一个参数。源码仍支持此机制，但已命名为 legacy；不能承诺未来不变。
- `codex-rs/core/src/hook_runtime.rs`：由共享 core 发出完成 hook，client 来自 app-server client name；不能只凭 client 字符串区分子代理。
- `codex-rs/protocol/src/protocol.rs`：SessionMeta 包含 id/source/parent_thread_id/thread_source/agent_path；TurnStarted/TurnComplete 的 JSONL wire 名称为 task_started/task_complete，也接受 turn_started/turn_complete。完成事件含 turn_id、last_agent_message 和可选 error/duration_ms。
- app-server 的 turn/completed 面向已连接的宿主会话，不是所有 Desktop/VS Code 的现成系统广播总线。没有建立另一个 app-server 来冒充对现有客户端的订阅。
- 本版采用 notify + 只读 JSONL watcher，二者共享持久去重键。只接受可识别的用户会话、真实 user_message 和匹配 turn；subagent/internal/不认识的 source 安全跳过。后台标题生成以输入前缀再过滤。数据格式变化时不猜测。
- 本机只读检查最近三个 session_meta（未打印消息内容）：Desktop 0.154.0-alpha.6.2 / 0.155.0-alpha.2.6 均为 source=vscode、originator=Codex Desktop、thread_source=user，无 parent。与当前身份过滤器匹配；这只验证元数据兼容，不能替代 QQ 完成通知实机测试。

## QQ

- [qqbot-nodejs ca55d9c395b582b7fcfad0ec27209c35dd04e0b3](https://github.com/tencent-connect/qqbot-nodejs/tree/ca55d9c395b582b7fcfad0ec27209c35dd04e0b3)，MIT，仓库最后 push 2026-07-31，未归档。作为协议参考，未复制业务代码，也不分发 Node 运行时。
- [qqbot-agent-sdk 6163b5dc979a2f12379b1916805009075008c3c3](https://github.com/tencent-connect/qqbot-agent-sdk/tree/6163b5dc979a2f12379b1916805009075008c3c3)，MIT，最后 push 2026-05-07。当前这个仓库是 Python SDK，并非 Node SDK；其 README 确认官方 Gateway + OpenAPI v2。
- [botgo](https://github.com/tencent-connect/botgo)，Apache-2.0，最后 push 2024-12-27，未归档。没有因其“官方”标签就把所有模块视为当前规范；本版不依赖它。
- [官方接口鉴权](https://bot.q.qq.com/wiki/develop/api-v2/dev-prepare/interface-framework/api-use.html)、[QQ 控制台](https://q.qq.com/)。部分旧消息文档 URL 当前跳回接入首页，因此不写死未经证实的主动消息额度/窗口数字；界面要求按当前控制台的权限、沙箱和白名单配置，以实际 API 状态码及腾讯错误码诊断。
- 核对 Node SDK `protocol/api/token.ts`：`https://bots.qq.com/app/getAppAccessToken`，请求 appId/clientSecret，缓存 access_token/expires_in。
- `api-client.ts`、`routes.ts`、`messages.ts`：`Authorization: QQBot ...`；`/v2/users/{openid}/messages`、`/v2/groups/{openid}/messages`；文本 msg_type=0；主动消息省略 msg_id，被动绑定确认包含 msg_id/msg_seq。
- `gateway/constants.ts` 与 `gateway-connection.ts`：群与私聊 intent 为 1<<25；HELLO、IDENTIFY、HEARTBEAT、ACK、RESUME、INVALID_SESSION、RECONNECT；`event-dispatcher.ts` 定义 author.user_openid 与 group_openid。
- 官方 SDK 当前仍包含 WebSocket。应用据此实现本机长连接，无公网 webhook/relay。具体 AppID 是否获得此接入方式、主动推送额度仍由腾讯决定。
- QQ POST 超时/5xx 的送达状态可能不确定。本版不盲目重试；401 刷新 Token，429 遵循 Retry-After 做有限等待。GET 可以有限重试服务端错误。这是避免同一完成通知多次送达的有意选择。

## 采用的通用组件

| 依赖 | 锁定版本 | 官方维护核查（最后 push） | 用途 / 许可 |
|---|---|---|---|
| .NET / WPF / ASP.NET Core | 10.0，自包含 | dotnet/runtime 2026-09-17 | Windows GUI、进程、文件、HTTP、WebSocket；MIT |
| Tomlyn | 2.10.1 | xoofx/Tomlyn 2026-08-18 | TOML 语法树和序列化；BSD-2-Clause |
| System.CommandLine | 2.0.12 | dotnet/command-line-api 2026-09-17 | CLI 解析；MIT |
| Serilog | 4.4.0 | serilog/serilog 2026-09-13 | 结构化日志；Apache-2.0 |
| Serilog.Sinks.File | 7.0.0 | serilog/serilog-sinks-file 2026-07-16 | 滚动文件日志；Apache-2.0 |
| ProtectedData | 10.0.12 | dotnet/runtime | 官方 Windows DPAPI；MIT |
| xUnit / VS adapter | 2.9.3 / 4.0.0 | xunit 2026-09-16 | 测试开发依赖；Apache-2.0 |
| Microsoft.NET.Test.Sdk | 18.10.1 | microsoft/vstest 2026-09-17 | 测试开发依赖；MIT |

所有采用组件都可在 Windows 运行，无终端用户额外语言运行时要求。许可证文件随发布物提供；依赖锁文件入库。构建开启全依赖 NuGet audit，实际漏洞扫描与包清单保存在验收记录。没有声称维护活跃就意味着不存在未知漏洞。

[微软单文件自包含发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)用于确定单 exe 与原生库自提取设置；WPF 不启用不受支持的 trimming。
