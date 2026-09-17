# HTTP API / Webhook

所有语言和 webhook 发送方都可调用此接口，没有执行命令端点。

```http
POST http://127.0.0.1:18765/v1/notify
Authorization: Bearer <GUI复制的Token>
Content-Type: application/json

{"title":"训练完成","message":"loss=0.032","level":"success","source":"train.py","tags":["training"]}
```

可选 id（最多 256 字符）用于请求重试去重；未提供则生成随机 ID。建议同一个业务事件每次提交保持同一 id。timestamp/device/metadata 可选。核心字段含大小、枚举和空值校验；整个请求限制 32 KiB，message 最多 16000 字符，title 最多 300。

成功：`{"ok":true,"id":"...","status":"sent"}`；重复：status=duplicate，不再发送。失败 error 为简短中文诊断，不返回平台原始 body、堆栈或密钥。

| 状态 | 含义 |
|---|---|
| 200 | 发送成功或已去重 |
| 400 | JSON / 字段无效 |
| 401 | Token 缺失或错误 |
| 403 | 不接受浏览器 Origin 请求 |
| 408 | 请求处理超时 |
| 409 | 通知暂停 |
| 413 | 请求过大 |
| 415 | 不是 application/json |
| 429 | 本地全局每分钟 60 请求限额 |
| 502 | QQ 发送失败或结果不确定 |

默认 127.0.0.1；GUI 开启 LAN 后为 0.0.0.0。两种模式都要求 256-bit 随机 Bearer Token，固定时间比较。最多 64 并发连接，10 秒请求头超时，30 秒处理超时，无 CORS、无远程控制、无任意文件读写。

LAN 为明文 HTTP，Token 不保护窃听；仅在可信网络内使用。程序不会自动修改防火墙、端口转发或请求管理员权限。重置令牌后旧 Token 立即失效。
