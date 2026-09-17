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
