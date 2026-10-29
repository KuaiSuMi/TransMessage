# 研究与取舍

## 0.3.2 后台唤醒和文件（2026-10-29）

- 当前 Desktop 26.1021.3705.0 安装代码包含 `codex://threads/wake/<uuid>` 后台入口。原宿主读取共享队列并继续会话，不导航到目标页面。独立辅助进程不能直接启动未由自己恢复的队列；因此只负责 `thread/queue/add`，随后交给原 Desktop。唤醒入口来自本机版本核查，公开文档尚未承诺此入口长期稳定。
- Windows MSIX 程序通过 [IApplicationActivationManager.ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication) 激活，从运行进程读取 AUMID，不使用直接启动受限的包内 exe。实机已验证队列消费、完整回复与前台不变，见验收记录。
- 当前原生 schema 与实测支持 `thread/items/list {threadId,turnId,cursor,limit,sortDirection}`；采用按轮分页读取，避免将完整会话历史一次载入。QQ 引用关联持久保存准确轮次与原始时间。
- QQ [单聊文件上传](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_openid_files.post.html) 使用 `file_type=4`、`file_data` 或 `upload_id`、`srv_send_msg=false`；再通过 `msg_type=7` 发送文件卡片。大文件遵循 `upload_prepare`、预签名 HTTPS PUT、[upload_part_finish](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_id_upload_part_finish.post.html)。`part_index` 为整数，`block_size` 为字符串。
- 腾讯官方 SDK 的本地文件直传采用 Base64。私聊每条接收消息最多被动回复 4 次，文本与文件共享序号；因此全文每页最多 4 段，附件每页 1 条清单加 3 个文件，下一页使用新的 QQ 消息。
- 使用 Markdig 1.4.2 解析 CommonMark 文件链接，许可证随包分发；没有自制 Markdown 解析器。

## 0.3.1 编号交互核查（2026-10-29）

[官方文本交互](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/trans/text-chain.html)支持在 Markdown 中使用 `qqbot-cmd-input`，点击后填入输入框，由用户编辑发送。text/show 属性做 URL 编码，reference=false；本版仅为匹配的 12 位会话编号生成标签，其余正文转义为文字，不解释模型输出中的指令标签、Markdown 链接或图片，也不额外截断审批正文。

[官方 Markdown 文档](https://bot.q.qq.com/wiki/develop/api-v2/server-inter/message/type/markdown.html)说明私聊和群聊自定义 Markdown 已向所有机器人开放。无编号消息仍是普通文本；有编号消息使用 msg_type=2 与 markdown.content。官方没有保证标签固定蓝色，也未提供可确认的 markdown.content 数字上限，不伪造客户端外观或平台额度结论。
## 0.3.0 追加核查（2026-10-29）

- [QQ 私聊事件](https://bot.q.qq.com/wiki/develop/api-v2/autogen/event/c2c_message_create.html)：引用消息类型为 103；`message_scene.ext` 中的 `ref_msg_idx=` 关联原消息，值可能带 `=`，仅按首个等号拆分。新输入只读取顶层正文和附件，不重复执行 `msg_elements` 中的旧内容。
- [QQ 发送私聊消息](https://bot.q.qq.com/wiki/develop/api-v2/autogen/api/v2_users_user_openid_messages.post.html)：保留回执 `ext_info.ref_idx` 建立引用索引；同一 `msg_id` 的多条回复递增 `msg_seq`，传输重试不换序号。[官方变更记录](https://bot.q.qq.com/wiki/develop/api-v2/changelog.html)已将正式 API 域名调整到 `api.bot.qq.com`。
- 当前本机原生 Codex 0.164.0-alpha.11.1 的模型目录与输入协议经实际进程核对；图片使用 `localImage`，下一轮用 `model` / `effort`，选项来自 `model/list`。不通过猜测模型名称或套餐权限启用能力。
- 隔离 CODEX_HOME、本机脚本化 Responses 服务完成真实 Codex 联调：103 引用回答原问题、两会话 `/d` 和 `/draft` 无编号草稿、图文分开发送、两轮同时运行、反序完成。实际请求中的 base64 图片像素、文字、模型和推理设置均核验。QQ 事件和发送回执为夹具，没有真实 QQ 手机验收。证据：`artifacts/native-codex-v030/evidence.json`。
- 实际新建 UUIDv7 会话出现相同前 8 位，故短编号改为末 12 位，仍拒绝歧义并支持完整 UUID；不把时间前缀当唯一会话编号。
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
- 本版采用 notify + 只读 JSONL watcher，二者共享持久去重键。只接受可识别的用户会话、匹配轮次的用户输入；除 CLI 的 `user_message`，当前 Desktop 使用 `item_completed`，其中 `item.type=UserMessage`、`content[].type=text`，并校验 thread_id 和 turn_id。subagent/internal/不认识的 source 安全跳过。后台标题生成以输入前缀再过滤。数据格式变化时不猜测。
- 2026-10-15 只读核对本机 Desktop 日志，发现其用户输入保存在 response_item 和带轮次归属的 item_completed 中，不产生旧监听器依赖的 user_message。0.2.2 使用后者进行归属匹配；按生产解析器回放当天 6 个用户会话的 16 次完成，全部识别。未向 QQ 补发历史消息。证据：`artifacts/desktop-replay-v022/evidence.json`。
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
| Markdig | 1.4.2 | 2026-10-19 release | CommonMark 附件链接解析；BSD-2-Clause |
| System.CommandLine | 2.0.12 | dotnet/command-line-api 2026-09-17 | CLI 解析；MIT |
| Serilog | 4.4.0 | serilog/serilog 2026-09-13 | 结构化日志；Apache-2.0 |
| Serilog.Sinks.File | 7.0.0 | serilog/serilog-sinks-file 2026-07-16 | 滚动文件日志；Apache-2.0 |
| ProtectedData | 10.0.12 | dotnet/runtime | 官方 Windows DPAPI；MIT |
| xUnit / VS adapter | 2.9.3 / 4.0.0 | xunit 2026-09-16 | 测试开发依赖；Apache-2.0 |
| Microsoft.NET.Test.Sdk | 18.10.1 | microsoft/vstest 2026-09-17 | 测试开发依赖；MIT |

所有采用组件都可在 Windows 运行，无终端用户额外语言运行时要求。许可证文件随发布物提供；依赖锁文件入库。构建开启全依赖 NuGet audit，实际漏洞扫描与包清单保存在验收记录。没有声称维护活跃就意味着不存在未知漏洞。

[微软单文件自包含发布说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)用于确定单 exe 与原生库自提取设置；WPF 不启用不受支持的 trimming。
