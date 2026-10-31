# 0.3.5 运行中闪退修复验收

日期：2026-11-01（Asia/Shanghai）。用户反馈运行一段时间后自动退出；排查时 0.3.4 未运行，原事故没有对应 Windows 崩溃记录。重新启动后 QQ、API、下载和 Codex 监听正常。

- 在独立数据目录运行原版 0.3.4，分别删除 Codex sessions 目录和下载监控目录，两种场景均产生未处理的 `DirectoryNotFoundException` 并结束进程。Windows .NET Runtime 1026 记录定位到 `CodexSessionWatcher.Rescan` 和 `StableFileWatcher` 的错误回调。证据：`artifacts/crash-v035/reproduced-crashes.json`、`artifacts/test-results/crash-before.trx`。
- 修复错误类型区分：仅缓冲溢出触发补扫；目录失效显示监听已停止。补扫捕获目录不可访问异常，避免从系统回调传播到整个进程。
- 新增两项真实子进程回归：删除监控目录后进程存活、状态可查、窗口可打开，恢复目录并重新应用设置后恢复监听，最后正常退出。启动、退出请求、异常类型及调用栈记录到日志；不记录异常消息中的外部内容。
- `scripts/verify.ps1` 通过 269 项测试、锁定还原、格式检查、零警告构建、34 张 WPF 渲染及依赖审计。证据：`artifacts/crash-v035/verify.log`、`artifacts/test-results/verified.trx`。

原事故缺少调用栈，目前确认修复的是已复现的目录监听闪退；长时间挂机的实际表现仍待观察。

---

## 0.3.4 QQ 项目列表与新建会话验收

日期：2026-10-29（Asia/Shanghai）。新增 `/p`（`项目`）分页查看已有 Codex Desktop 项目，以及 `/new 项目编号 [首条要求]`（`新建`）创建实时问答会话。每页 8 个项目，编号沿用当前列表快照；新会话关联所选项目及全部工作目录。

- 原生项目接口只读核查返回 46 个已有项目。
- 69 项相关自动测试通过，0 失败、0 跳过。证据：`artifacts/test-results/projects-v034.trx`。
- 本机 Codex 0.164.0-alpha.11.1 在隔离 `CODEX_HOME` 中完成原生联调：项目列表、多根目录项目新建并执行首条要求、项目关联与会话名称、引用回答原问题、两个会话的图文轮次并发反序完成、下一轮模型设置，以及恢复会话后保留全部根目录均通过。共 11 条控制指令、4 次模型请求；请求发往本机脚本模型服务，QQ 事件和回执使用夹具。证据：`artifacts/native-codex-v034-final/evidence.json`、`project-thread.json`。
- 隔离 `CODEX_HOME` 的原生探针验证了 `projectId`、两个 `runtimeWorkspaceRoots`、首根目录 `cwd` 和会话名称；重启后可读取原会话并恢复两个根目录。证据：`artifacts/native-project-probe/20261029-133209/evidence.json`。此探针验证空会话与元数据操作。
- 探针创建的空会话在创建后、重启后的 `thread/list` 中均未列出。未运行首轮时的 Desktop 侧栏显示待实测。

- `scripts/verify.ps1` 完整门禁通过：267 项自动测试，0 失败、0 跳过，零警告构建，GUI 渲染和依赖审计。新增托管通知去重测试覆盖真实 Desktop 元信息、notify 与日志两个入口，以及停用控制后恢复普通通知。证据：`artifacts/test-results/verified.trx`。
- GUI 沿用现有「新建实时问答会话」卡片，补充 QQ 指令说明；浅色大字体与深色整页渲染已检查，无新增裁切和重叠。

[v0.3.4 标签 CI](https://github.com/KuaiSuMi/TransMessage/actions/runs/40826062332) 已通过最终 267 项测试、GUI 渲染、依赖审计、打包及 4 项发布程序检查；本地 ZIP 解压后的 4 项程序检查同样通过。证据：`artifacts/release-v034-ci.log`、`artifacts/test-results/published-v034.trx`。

[v0.3.4](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.3.4) 已公开 ZIP 与 SHA256SUMS。本机实际实例通过正式更新源从 0.3.3 升至 0.3.4，配置字节保留、旧版 exe 备份、自动重启及窗口唤起通过；证据：`artifacts/update-actual-v034.json`。QQ 手机端项目选择与新建会话仍待用户实测。

---

## 0.3.3 链接与附件标签修复验收

日期：2026-10-29（Asia/Shanghai）。修复整段 Markdown 转义导致的链接失效、多余转义符号，以及会话编号匹配改写 URL 片段的问题。复用 Markdig 处理链接、代码与正文，并为启用本人私聊控制的完成通知增加附件标签入口。

- `scripts/verify.ps1` 完整门禁通过：零警告构建、231 项自动测试、GUI 渲染和依赖审计。测试 0 失败、0 跳过，证据：`artifacts/test-results/verified.trx`、`artifacts/gui/`。
- 最终源码的格式检查与 111 项相关回归通过，覆盖网页、编号、附件、引用式链接跨段、标题换行、全文导出和网络行为，0 失败、0 跳过。证据：`artifacts/test-results/links-complete-v033.trx`；最终源码由 CI 再执行完整门禁。
- 网页链接保留地址、标题、查询参数和编号形态的 URL 片段；独立的行内代码编号生成填入标签，代码块保留代码内容。来源文本中的机器人动作标签保持非交互文本。
- 本地附件标签按接收目标及控制配置生成，填入 `/file` 并引用原消息；回归覆盖会话与轮次关联、回复序号和引用回执。
- 全文分段覆盖中文、四字节 emoji、带空格的链接标题、编号形态的 URL 片段和行内代码。引用式链接先展开为内联链接，再按每段 2600 字节上限拆分；预算内的链接保持完整，超预算内容逐字符推进。

最终 [v0.3.3 CI](https://github.com/KuaiSuMi/TransMessage/actions/runs/40818981814) 完整门禁通过：243 项自动测试、GUI 渲染、依赖审计和 4 项发布程序检查，0 失败、0 跳过；本地最终 ZIP 解压后同样通过 4 项程序检查。证据：`artifacts/release-v033-ci.log`、`artifacts/test-results/published-v033.trx`。

[v0.3.3](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.3.3) 已公开 ZIP 与 SHA256SUMS。本机实际实例经正式更新源从 0.3.2 升至 0.3.3，配置字节保留、旧版 exe 备份、自动重启和窗口唤起均通过；证据：`artifacts/update-actual-v033.json`。本次没有主动发送 QQ 测试消息，手机端链接显示、点击及附件下载待用户确认。

---

## 0.3.2 后台唤醒、全文与附件验收

日期：2026-10-29（Asia/Shanghai）。QQ 继续消息入队后，使用 Windows 应用激活 API 将后台唤醒请求交给原 Codex Desktop；新增引用完成通知后的 `/full [页码]` 与 `/file [页码]`。QQ 回复改为简洁的操作结果和下一步。

- `scripts/verify.ps1` 全部门禁通过：锁定依赖恢复、格式检查、零警告/零错误构建、213 项自动测试（无跳过）、WPF 深浅色与 200% 字体渲染、依赖审计。当前依赖源未报告已知漏洞。证据：`artifacts/test-results/verified.trx`、`artifacts/gui/`。
- 经用户明确授权，在现有“解释 Kubernetes”会话只入队一次指定测试提示。Windows MSIX 直接启动返回拒绝访问，定位后改用 `IApplicationActivationManager`；原队列被消费，新轮次完成并精确回复测试文字。前台窗口句柄始终不变。最终正式方法在线程池 MTA 上再次通过空队列唤醒验证，既未增加消息也未增加轮次。证据：`artifacts/desktop-wake-live/evidence.json`、`production-wake-only-evidence.json`。本机 Desktop 为 26.1021.3705.0，Codex 原生程序为 0.164.0-alpha.11.1。
- 全文按被引用的 `thread_id + turn_id` 分页读取，保留首尾空白、Unicode 与原始北京时间；不会取同会话后来的回复。每页最多 4 条消息，下一页通过新指令获取。附件解析使用 Markdig，支持中文、空格、括号、相对路径、引用式 Markdown 与代码行号；代码块、网络路径、设备路径、备用数据流及重解析路径排除。
- 附件为该轮最终回复链接的本地实际文件，每页清单加最多 3 个文件卡片。≤5 MiB 直接上传，更大文件分片上传，最高 200 MiB；源文件保持只读。HTTP 测试覆盖分片哈希、上传地址、序号配额、取消和发送状态不确定时不重发。导出独立于会话指令处理，上传期间可继续其他会话；使用同一请求的 5 分钟回复期限。
- 新卡片已检查浅色、深色、200% 字体，无裁切或重叠。QQ 文件卡片的手机端下载仍待用户验收；未新增主动 QQ 测试消息。现有编号点击行为复用 0.3.1 用户确认结果。

最终 ZIP 内 4 项发布程序检查通过，证据 `artifacts/test-results/published-v032.trx`。[v0.3.2 标签 CI](https://github.com/KuaiSuMi/TransMessage/actions/runs/40809967698)通过完整验证、打包和发布程序检查，已公开 [Release](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.3.2)。本机实际实例从公开更新源由 0.3.1 升至 0.3.2，配置字节保留、旧 exe 备份匹配、自动重启与窗口唤起通过；证据 `artifacts/update-actual-v032.json`。更新前本机没有托管会话，原 Codex Desktop 会话保持运行。

---

## 0.3.1 编号点击验收

日期：2026-10-29（Asia/Shanghai）。将 QQ 消息中的 `#末12位会话编号` 改为官方 Markdown 参数指令标签，点击后仅填入输入框；保留手动编辑发送。任务列表、普通完成通知、私聊控制回复与待回答通知复用同一发送入口。

- format、build 通过，零警告、零错误。88 项相关测试通过，包含 QQ 网络、引用输入、通知时间与审批回归；新增 5 项 HTTP 载荷测试验证多个编号、URL 编码、仅填入不发送、正文转义与完整保留、长编号不误匹配、序号重试和引用回执。证据：`artifacts/test-results/clickable-v031.trx`。
- 无编号消息保持普通文本。正文不会执行模型输出中的指令标签、Markdown 图片和链接；新增标签不额外截断正文。平台未公布可确认的 Markdown 长度上限，不将本机预算当作平台额度。
- GUI、图文路由、Codex 控制、依赖和更新逻辑没有改变，复用 0.3.0 的 34 张渲染、原生并发图文联调和升级验证，不重复无关检查。

经用户明确授权，向已绑定本人 QQ 私聊发送一条真实编号测试；QQ API 接收成功，用户确认“可以点击并填入”，没有自动发送任务。证据：`artifacts/qq-number-check/sent.json`。官方没有承诺所有客户端固定显示蓝色；旧消息不会被改写。最终 ZIP 内 4 项 CLI 测试通过（`artifacts/test-results/published-v031.trx`）；[标签发布 CI](https://github.com/KuaiSuMi/TransMessage/actions/runs/40803806476) 通过完整检查并发布 [v0.3.1](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.3.1)。本机实际实例经软件内更新从 0.3.0 升至 0.3.1，配置保持、旧版备份、自动重启和窗口唤起均通过，证据 `artifacts/update-actual-v031.json`。

---
## 0.3.0 验收记录

日期：2026-10-29（Asia/Shanghai）。本版实现 QQ 引用回复、图文草稿、会话编号、免逐会话授权、统一连接入口和下一轮模型设置。不新增运行中引导、队列管理命令或多会话管理面板。

- locked restore、format、build 通过，零警告、零错误；155 项自动测试全部通过，无跳过。依赖审计未发现已知漏洞。证据：`artifacts/test-results/verified.trx`。
- 引用覆盖 QQ 103 事件、带等号的 `ref_msg_idx`、发送回执关联、旧问题与冲突目标拒绝、引用后直接批准原请求。顶层附件出错时整条拒绝，不污染草稿。
- 图文覆盖 `/d`、`/draft` 无编号引用、先图后文与先文后图、交错草稿、显式 `/s` 一次提交、取消、不明目标、实际图片解码、大小/数量/地址限制和取消清理。
- 并行覆盖不同会话独立模型设置、同时运行反序完成、摘要与引用不串会话、第二轮失败不沿用旧摘要。原有 Desktop 多会话通知回归也通过。
- 使用真实 Codex 0.164.0-alpha.11.1、隔离 CODEX_HOME 和本地脚本化模型完成完整链路。两会话图文请求实际同时等待并反序结束；解码实际模型请求中的图片确认像素归属，核对文字、model 和 effort。11 条私聊指令、4 次原生模型请求；QQ 事件/回执为模拟，没有付费推理或真实 QQ 发送。证据：`artifacts/native-codex-v030/evidence.json`。最终编号展示变更及旧摘要清理由自动回归覆盖，未重复不受影响的原生协议联调。
- 原生联调发现 UUIDv7 前 8 位会重复，短编号改成末 12 位；同前缀正确路由、末尾冲突拒绝和完整 UUID 选择均有回归。旧前 8 位指令不再使用，引用索引仍关联完整会话 ID。
- 保持 Apple-Style，实际 WPF 生成 34 张浅/深色、200% 文字、降低透明度与对比配色样例；新编号复制目标和未连接时新建错误反馈检查通过。证据：`artifacts/gui/`。对比配色为模拟样例，不代表系统高对比模式实机验收。

最终本机 ZIP 内 4 项 CLI 验收通过，覆盖窗口唤起、单实例、退出和重新打开，证据 `artifacts/test-results/published-v030-final.trx`。[标签发布检查](https://github.com/KuaiSuMi/TransMessage/actions/runs/40744098731)通过完整验证、打包和发布程序检查；[v0.3.0](https://github.com/KuaiSuMi/TransMessage/releases/tag/v0.3.0) 已公开 ZIP 与 SHA256SUMS。SDK 已固定 10.0.401，避免 runner 补丁滚动导致隐式依赖与锁文件不一致。现有 Desktop 仍只支持共享队列追加，实时问题和模型设置需要在原窗口处理。真实 QQ 手机引用和图文收发、Windows 11、完整读屏/键鼠验收尚未完成；发布包未签名。

本机软件内更新已实测：实际运行的 0.2.5 从公开 v0.3.0 Release 检查、下载并校验后替换自身，自动重启到 0.3.0。原配置字节未变，旧 exe 备份哈希匹配，窗口再次唤起成功；QQ 已重新连接，Codex 与下载监听正常。更新前本机没有托管会话运行，没有中断用户 Codex Desktop 会话。证据：`artifacts/update-actual-v030.json`、`artifacts/update-actual-v030-status.json`。更新和回滚实现未修改，回滚复用 0.2.3 已验证结果。

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
