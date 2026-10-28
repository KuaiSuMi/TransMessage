# 0.3.0 验收记录

日期：2026-10-29（Asia/Shanghai）。本版实现 QQ 引用回复、图文草稿、会话编号、免逐会话授权、统一连接入口和下一轮模型设置。不新增运行中引导、队列管理命令或多会话管理面板。

- locked restore、format、build 通过，零警告、零错误；155 项自动测试全部通过，无跳过。依赖审计未发现已知漏洞。证据：`artifacts/test-results/verified.trx`。
- 引用覆盖 QQ 103 事件、带等号的 `ref_msg_idx`、发送回执关联、旧问题与冲突目标拒绝、引用后直接批准原请求。顶层附件出错时整条拒绝，不污染草稿。
- 图文覆盖 `/d`、`/draft` 无编号引用、先图后文与先文后图、交错草稿、显式 `/s` 一次提交、取消、不明目标、实际图片解码、大小/数量/地址限制和取消清理。
- 并行覆盖不同会话独立模型设置、同时运行反序完成、摘要与引用不串会话、第二轮失败不沿用旧摘要。原有 Desktop 多会话通知回归也通过。
- 使用真实 Codex 0.164.0-alpha.11.1、隔离 CODEX_HOME 和本地脚本化模型完成完整链路。两会话图文请求实际同时等待并反序结束；解码实际模型请求中的图片确认像素归属，核对文字、model 和 effort。11 条私聊指令、4 次原生模型请求；QQ 事件/回执为模拟，没有付费推理或真实 QQ 发送。证据：`artifacts/native-codex-v030/evidence.json`。最终编号展示变更及旧摘要清理由自动回归覆盖，未重复不受影响的原生协议联调。
- 原生联调发现 UUIDv7 前 8 位会重复，短编号改成末 12 位；同前缀正确路由、末尾冲突拒绝和完整 UUID 选择均有回归。旧前 8 位指令不再使用，引用索引仍关联完整会话 ID。
- 保持 Apple-Style，实际 WPF 生成 34 张浅/深色、200% 文字、降低透明度与对比配色样例；新编号复制目标和未连接时新建错误反馈检查通过。证据：`artifacts/gui/`。对比配色为模拟样例，不代表系统高对比模式实机验收。

最终本机 ZIP 内 4 项 CLI 验收通过，覆盖窗口唤起、单实例、退出和重新打开，证据 `artifacts/test-results/published-v030-final.trx`。[标签发布检查](https://github.com/KuaiSuMi/TransMessage/actions/runs/40744098731)通过完整验证、打包和发布程序检查；[v0.3.0](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.3.0) 已公开 ZIP 与 SHA256SUMS。SDK 已固定 10.0.401，避免 runner 补丁滚动导致隐式依赖与锁文件不一致。现有 Desktop 仍只支持共享队列追加，实时问题和模型设置需要在原窗口处理。真实 QQ 手机引用和图文收发、Windows 11、完整读屏/键鼠验收尚未完成；发布包未签名。

---
## 0.2.5 验收记录

日期：2026-10-16（Asia/Shanghai）。修复启用 QQ 控制后退出阻塞：Codex RPC 读取和远程消息循环原先捕获界面同步上下文，界面停止后无法完成取消，进程等待清理并持续占用单实例锁。改为在线程池运行这些后台循环，托盘退出与 IPC 停止共用退出入口。

- 旧实现复现：启用控制时退出等待 15 秒超时；关闭控制的对照通过。证据：`artifacts/test-results/exit-before.trx`。
- 修复后 19 项相关测试通过。新增真实 GUI 进程测试配合本地假 Codex 子进程，覆盖启用/关闭控制、单实例、退出码为 0、连接子进程结束、相同数据目录再次启动并再次退出。窗口唤起也纳入发布包测试。证据：`artifacts/test-results/exit-v025.trx`。
- 最终 ZIP 解压后的 4 项 CLI 测试通过，包含退出前后两次真实窗口唤起。证据：`artifacts/test-results/published-v025.trx`。
- 本机旧进程窗口句柄为 0、IPC 不可连接；清理该残留、备份旧 exe、替换为已测 0.2.5 后，重新打开成功并获得可见窗口句柄，配置文件没有改写。证据：`artifacts/recovery-v025.json`。
- 测试不连接真实 QQ 或提交 Codex 任务；界面样式、配置格式、消息内容与更新机制未改变。
- [标签发布门禁](https://github.com/KuaiSuMi/TransMessage/actions/runs/38871325462)已通过完整自动测试、格式/构建、GUI 渲染、依赖审计和发布 CLI 检查。[v0.2.5](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.2.5) 已发布 ZIP 与 SHA256SUMS，可供软件内更新。

未执行人工托盘鼠标点击和真实 QQ 收发复测；进程测试调用托盘共用的退出入口。发布包未签名。

---

## 0.2.4 验收记录

日期：2026-10-15（Asia/Shanghai）。本补丁仅简化任务名称：通知、QQ 任务列表、状态与托管完成消息统一提取实际请求，清理 Markdown 和附件包装，使用首行首句，限制为 32 个可见字符；问答回复显示可读的问题摘要，不暴露答案或内部编号。配置名称、旧消息、原始时间和去重标识不变。

- format、build 通过，零警告/错误。
- 87 项相关测试通过，覆盖附件和问答包装、Markdown、长名称、组合 emoji、脱敏、hook 与会话解析一致性、配置不被重命名，以及现有通知、Desktop、多会话、私聊、历史和网络回归。
- 本地发布 ZIP 内自包含 exe 的 3 项 CLI 测试通过；[标签发布门禁](https://github.com/KuaiSuMi/TransMessage/actions/runs/38705735562)通过完整 103 项测试、格式/构建、GUI 渲染、依赖审计和 3 项发布 CLI 测试。[v0.2.4](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.2.4) 已发布 ZIP 与 SHA256SUMS，可供软件内检查更新。
- GUI 样式、下载监控、更新与依赖未改变，复用 0.2.2 的界面渲染和 0.2.3 的公开更新源升级/回滚验证，不重复这些本机检查。

证据：`artifacts/test-results/short-names-v024.trx`、`published-v024.trx`及上述 Actions 记录。未进行真实 QQ 收发复测；发布包未签名。名称采用规则提取，不生成语义摘要；超长名称末尾显示省略号。

---

## 0.2.3 验收记录

日期：2026-10-15（Asia/Shanghai）。本补丁只调整时间显示：QQ 消息和问题码有效期统一换算到北京时间（UTC+08:00），使用 `yyyy-MM-dd HH:mm:ss`，去掉小数秒；内部原始时间戳和接收时间标记不变。

- locked restore、format、build 通过，零警告/错误。
- 71 项相关测试通过，覆盖通知格式、跨日期/正负时区换算、原始记录保留、绑定 HTTP 正文、私聊回复、问题有效期，以及现有 Codex/历史/网络回归。
- 发布 ZIP 内自包含 exe 的 3 项 CLI 集成测试通过。
- 未改动 GUI 样式、下载监控、更新与依赖；复用 0.2.2 的相应测试、26 张界面渲染和依赖审计结果，没有重复无关检查。

证据：`artifacts/test-results/beijing-v023.trx`、`published-v023.trx`。未进行真实 QQ 收发复测；发布包未签名。控制模式边界与此前相同，见下方记录。

2026-10-15 软件内更新验收：现有更新入口与替换逻辑无需修改。[v0.2.3](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.2.3) 已通过 [tag 发布门禁](https://github.com/KuaiSuMi/TransMessage/actions/runs/38703171454)，发布 Windows ZIP 与 SHA256SUMS。隔离目录中使用真实 0.2.1 exe 执行与软件界面相同的更新路径，完成公开更新源发现、下载、校验、替换、后台重启；版本变为 0.2.3，配置保持不变，并保留 0.2.1 备份。另用无效新版验证自动回滚及旧版重新启动。证据：`artifacts/update-e2e/run-20261015-080402/evidence.json`、`artifacts/update-e2e/run-20261015-080807/evidence.json`。未替换用户正在运行的实例，未操作真实 QQ。

---

## 0.2.2 验收记录

本补丁修正已有 Desktop 对话完成后漏通知：解析带 thread_id / turn_id 的 UserMessage 完成条目；重启监听时流式恢复完整轮次状态，取消丢失长任务起点的固定 4 MB 尾部限制；单次读取以开始时的文件长度为界，让其他会话得到处理。保留 0.2.1 原始时间戳修复。原生远程控制联调结果按原测试日期复用，控制协议连接未改。

日期：2026-10-15（Asia/Shanghai）。Windows 10 专业版 22H2 / 10.0.19045 x64；.NET SDK 10.0.401。发布运行时 .NET 10.0.12。精确构建时间、提交和 SHA256 见 `dist/build-info.json`。

| 项目 | 本次结果 |
|---|---|
| 编译与依赖 | locked restore、format、build 通过，零警告/错误；NuGet 全依赖审计未发现已知漏洞 |
| 自动测试 | 84 项通过、0 失败、0 跳过；新增 7 项 Desktop 回归，覆盖 12 会话交错完成、慢发送、分段追加、重复完成、长任务启动恢复、扫描期间完成，以及跨会话/轮次与内部任务过滤 |
| 本机日志回放 | 生产解析器只读回放当天 6 个用户会话的 16 次完成，全部识别；没有 QQ 发送和历史补发 |
| QQ 事件 | 模拟 HTTP/WebSocket 验证绑定、心跳、私聊指令转交及发送错误处理 |
| 真实 Codex 进程 | 12:12:58 +08:00，原生 Codex 0.162.0-alpha.17.2，在隔离 CODEX_HOME 和本地脚本化模型下：创建→启动→输入请求→私聊指令回答→官方确认→完成，通过；没有使用真实 QQ 或付费模型 |
| 现有 Desktop | 已有会话的共享队列添加→读取→删除通过，探针消息已移除。没有 resume / turn/start，没有验证 Desktop 消费执行 |
| 实时控制 | 当前 Windows Desktop 官方 proxy 不可连接（socket 10050）；程序 probe-desktop 同样检测不可连接，可发现 100 个本机会话 |
| GUI | Apple-Style；10 页深浅色 + 设置/控制/历史各两张 200% 文字图，共 26 张新渲染，修正深色下拉框与历史错误展示 |
| 发布 exe | 最终自包含程序的 3 项 CLI 集成测试通过，覆盖版本/诊断/发送/退出码/Codex 去重，以及后台单实例、API 启动与退出 |

本次证据：`artifacts/test-results/verified.trx`、`published-v022.trx`、`desktop-before.trx`（旧实现漏报复现）、`desktop-startup-before.trx`、`desktop-final.trx`、`artifacts/desktop-replay-v022/evidence.json`；`artifacts/gui/`。复用原生联调证据：`artifacts/native-codex-v020c/evidence.json`、`artifacts/desktop-probe.json`。探针与测试数据不打入发布包。

**0.2.x 边界**：现有 Desktop 只支持查询和共享队列追加，不能获取其待输入状态或回答原窗口；完整等待提醒和回答仅支持 TransMessage 托管会话。队列入队不代表执行，空队列不证明完成。部分接口是实验协议，未来 Codex 变化可能明确报错。退出或切换连接会结束托管进程，普通通知设置刷新不会重启它。

新增控制尚未完成真实 QQ 手机验收；Windows 11、完整键鼠/读屏/高对比实机测试和真实发布升级尚未验证。发布包未签名，本次只生成本地 ZIP，未上传 Release。已有下载和 Codex 监测的用户确认仍有效，见 [实机记录](REAL_DEVICE_RESULTS.md)。

---

## 首版历史记录（0.1.0）

以下保留原验收证据；不将旧版实机或自动测试结论扩大为新版全部通过。

验收日期：2026-09-18（Asia/Shanghai）。版本：0.1.0。开发与运行主机：Windows 10 专业版 22H2，10.0.19045，x64。工具链：.NET SDK 10.0.401；发布包包含 .NET 10.0.12 运行时。

2026-10-15 用户完成连接和配置后，确认下载检测与 Codex 监测均通过。详见[实机测试记录](REAL_DEVICE_RESULTS.md)；以下自动测试结果仍为原开发验收记录。

## 已验证

| 项目 | 结果与证据 |
|---|---|
| 依赖与编译 | 锁定依赖还原成功，格式检查通过，编译零警告、零错误；NuGet 审计未发现已知漏洞 |
| 自动测试 | 55 项全量测试通过；其后新增后台进程测试 1 项通过，共 56 个不同用例。API 和后台退出修正分别通过 10 项、2 项定向复测 |
| QQ 协议 | 模拟 HTTP/WebSocket 覆盖认证、私聊、群聊、绑定、Token 刷新、401/429/500、超时与重复防护；无需真实凭证 |
| Codex | 完成事件、同一 turn 去重、内部标题/子代理过滤、格式错误、取消/失败、只读会话监听、TOML 配置保留与备份恢复通过 |
| 命令与文件 | 命令成功/失败、退出码、输出、取消、中文和空格路径、持续写入、临时下载文件重命名及重复事件通过 |
| HTTP / 更新 | 本机与 LAN 鉴权、无效请求与大小限制通过；模拟 Release 版本比较、下载中断与校验和通过 |
| 发布程序 | 实际自包含 exe 的 version、doctor、send、run、Codex 通知/去重/过滤通过；QQ 发送失败不改变子命令退出码；后台单实例、API 启动和退出通过 |
| 无外部运行时 | 设置不存在的 DOTNET_ROOT、禁止多级运行时查找，并将 PATH 限制为 Windows 系统目录后，发布 exe 仍能正常报告版本 |
| GUI | 实际 WPF 组件渲染 18 张图：8 页分别深浅色，加 2 张 200% 字体缩放；已检查代表性页面并修正主按钮文字对比问题 |
| 脚本与示例 | PowerShell 脚本语法、Python 编译、JavaScript 语法检查通过 |

自动测试原始记录位于开发工作区 `artifacts/test-results/verified.trx`、`network-final.trx`、`ipc-final.trx`、`background.trx`、`published.trx`；GUI 图像位于 `artifacts/gui/`。这些是开发验收证据，不打入用户数据目录。

## 发布产物

- `dist/TransMessage-win-x64.zip`：Windows x64 自包含单文件程序、中文文档、示例、安装/卸载脚本及第三方许可证；解压后运行 `TransMessage.exe`。
- `dist/SHA256SUMS`：压缩包 SHA-256。校验值以此文件为准，避免在包内重复记录自身校验值。
- `dist/build-info.json`：准确版本、源码提交、UTC 构建时间、大小及 SHA-256；程序 `version` 命令也显示构建信息。
- GitHub Actions 构建/检查/标签发布流程已配置。本次只生成本地发布包，未上传 GitHub Release，也未声称远程 CI 已通过。

## 尚未验证的外部环节

1. **QQ 专项验收**：原开发验收未提供真实凭证。后续用户已确认下载检测与 Codex 监测通过，尚未逐项记录私聊、群聊、多目标开关与主动发送权限测试；其余项目按 [QQ_REAL_DEVICE_TEST.md](QQ_REAL_DEVICE_TEST.md) 完成，配额和审核结果以腾讯控制台为准。
2. **Windows 11 与另一台设备**：本次在 Windows 10 主机验证，未在 Windows 11 或另一台干净机器执行完整验收。
3. **完整实机 GUI 操作**：真实窗口启动成功，可访问性树可读；本机 Computer Use 驱动返回 `SetIsBorderRequired failed: 不支持此接口 (0x80004002)` 和 `coordinate input geometry is unavailable`，因此未完成真实鼠标/键盘流程、高对比系统切换和读屏验收。WPF 渲染检查不等同于这些交互通过。
4. **真实发布升级**：下载与校验用模拟服务验证；替换、重启检查和失败回滚已实现，尚未通过公开 GitHub Release 执行一次真实跨版本升级。
5. **代码签名**：发布包未做 Authenticode 签名，Windows 可能显示未识别发布者。

## 产品行为边界

Codex 兼容结论基于研究记录中的官方源码及本机会话元数据，不代表所有未来客户端版本。未知事件格式安全跳过；没有可靠证据时不虚构完成通知。去重保留七天且在发送前占用一次尝试；网络结果不明确时不会自动重发，不能保证异常情况下送达。命令代理保留标准流与退出码，但没有实现完整 ConPTY 终端仿真。下载完成使用元数据稳定与独占打开检查，无法证明任意生产程序以后绝不会继续写入。
