# 架构

```text
CLI / Runner / FileSystemWatcher / Codex notify + JSONL / HTTP
                           ↓
                      Notification
                           ↓
                Validate → local Sanitizer
                           ↓
             persistent Deduplicator (7 days)
                           ↓
                serialized Dispatcher
                           ↓
                INotificationChannel
                           ↓
                  QQOfficialChannel
```

核心 Notification 不引用 QQ，包含 id、timestamp、title、message、level、source、device、tags、metadata。新增 channel 只需实现接口，不修改各来源。没有额外数据库或服务端。

`src/TransMessage.Core` 包含配置、DPAPI、通知管道、官方 QQ、Codex、Runner、文件监听、HTTP 和更新。`src/TransMessage.App` 提供 WPF、托盘、CLI、HKCU Run 和更新 helper。测试使用注入 channel、模拟 HTTP/WebSocket 及当前用户命名管道，不提供生产“测试通道”后门。

GUI / serve 由每数据目录命名 mutex 限制为一个后台实例。CLI 优先使用仅当前用户可访问的命名管道；无后台时直接发送。不同数据目录相互独立，允许隔离测试；同一数据目录共享配置、凭证和持久去重。

配置写入用命名 mutex + 临时文件原子替换；其他实例修改后会重新读取。去重键仅保存 SHA256 和时间，不保存消息。发送前先落盘占用 ID，即使崩溃或网络状态不确定也不再次尝试相同 ID；代价是该事件可能丢失。GUI/日志明确报告失败，用户可发一条新通知。队列为有界 HTTP 连接、单管道处理和进程内发送串行化，不做持久消息重放。

WPF 运行于 STA UI 线程；后台 HTTP、WebSocket 和文件事件异步执行。QQ Gateway 按服务器心跳周期发送，断线以指数退避和抖动重连，支持 resume。Downloads / Codex 由文件事件唤醒，不全盘每秒扫描。GitHub 自动检查缓存 24 小时。

## 0.2.0 历史和控制

Dispatcher 在发送前写入加密历史的 sending 状态，再更新 sent / failed / uncertain；历史跨进程原子写入，保留 30 天和 500 条。传输不确定时沿用不自动重放的原则。

QQ Gateway → 私聊身份与时效校验 → 有界指令队列 → 持久去重 → RemoteCodex → CodexRpc。CodexRpc 通过原生 exe 的标准输入输出交换 JSONL，相关请求有超时和关联 ID。RPC 事件接收与 QQ 通知发送分别运行，网络慢不会阻塞审批确认。

Desktop 模式只读取历史与写官方共享队列。托管模式仅恢复自己创建且仍在白名单的会话，消费实时 turn/item/serverRequest 事件。新建空会话尚未落盘时直接在当前连接启动第一轮，避免错误地恢复不存在的 rollout。普通服务配置刷新保持该连接；连接身份改变时销毁旧 pending request。远程控制没有开放新的 HTTP 端点。
