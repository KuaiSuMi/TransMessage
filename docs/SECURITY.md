# 安全与隐私

- AppSecret/API Token 存为 DPAPI CurrentUser 密文；QQ access_token 仅留内存。配置 TOML 不存密钥。
- Notification 在本机做 secret redaction：OpenAI/GitHub/AWS key、JWT、Bearer、Authorization/Cookie、常见 password/secret/token/.env 字段、PEM 私钥、已保存 QQ/API 凭证。正则有超时保护，超时时省略内容。脱敏不调用第三方 AI。
- 日志只含组件、状态、错误码，默认不记录任务全文、HTTP body、QQ 原始响应或凭证。每日/2 MiB 轮转，最多 14 个文件，最长 14 天。
- 通知内容会发送给腾讯及勾选的 QQ 目标。自动脱敏不能识别所有商业机密和任意无特征秘密，敏感项目可关闭结果摘要或暂停通知。
- API 默认 loopback，LAN 需手动开启；始终 Token 认证，无 CORS。HTTP 不提供远程命令执行。
- 本地控制管道仅当前用户可访问。配置与去重原子写入。未使用系统服务、进程注入、抓包或非官方 QQ。
- 更新来源限制为指定 GitHub 仓库 Release asset，校验 SHA256 后仅提取确切的 TransMessage.exe，不按 ZIP 任意路径写文件。下载失败不修改当前 exe。SHA256 不代替作者身份签名；本版未配置 Authenticode。
- Portable 密文仍绑定当前 Windows 账户；复制 data 到另一用户/电脑无法直接解密。不要把 data、logs 或自己的配置提交进 Git。
- UI 复制 Token 后 30 秒清除未变化的剪贴板，但系统剪贴板历史可能保留内容；无需复制时不要复制。

报告问题时只提供错误码、版本和脱敏诊断，不上传完整 rollout、AppSecret 或 Token。第三方依赖由锁文件固定，构建时启用 NuGet 全依赖漏洞审计。

0.3.0：通知历史先脱敏再用 DPAPI CurrentUser 加密，保留窗口为 30 天 / 500 条。新增 QQ 控制默认关闭，仅选定的已绑定启用私聊身份可操作本机普通会话和本程序创建的实时会话，无需逐会话授权；群聊不可控制。QQ 原始命令和回答不进入历史或日志，控制回复仅发给主人，不广播到其他通知目标。

每条控制命令校验事件时间、速率和持久去重。每次回答关联原请求 ID、会话、轮次、连接和短期问题码，确认后作废；超时不盲目重试。敏感答案只在本机输入。托管 Codex 使用 workspace-write / on-request / user，默认模式请求输入显式启用；不设置绕过沙箱或自动批准。命令审批仅允许完整呈现的单次操作，扩展权限与缺失差异不远程批准。

QQ 主人账户被盗也可能导致本机项目被操作；可从电脑停用控制或更换控制者。QQ 账号认证由腾讯负责，TransMessage 不把任意聊天文本插入本地 shell 命令。模型执行与数据发送仍受本机 Codex 配置及项目规则影响。

QQ 引用索引按发送者隔离并用 DPAPI 加密。图文草稿只保留在内存；图片为供 Codex 读取而保存到数据目录的 `qq-images`，属于本机普通文件，最多 512 MiB、保留 30 天。提交后图文会进入相应 Codex 会话和模型请求；图片本身不做内容脱敏。下载只接受公网 HTTPS、禁止重定向，并限制大小和实际解码像素。
