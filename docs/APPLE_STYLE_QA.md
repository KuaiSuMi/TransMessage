# Apple-Style 界面检查

应用了用户指定的 Apple-Style skill；Windows 原生 WPF 实现，采用其可落地的分层、形状、排版和可访问性原则。没有分发 Apple 字体或符号。

| 检查项 | 实现 / 验证 |
|---|---|
| 内容与导航分层 | 左侧导航单独成层；正文、列表、设置分组均为实色，无玻璃卡片叠加 |
| 主操作 | 每页仅主要动作采用蓝底白字；其余按钮中性填充 |
| 排版 | 左对齐加粗标题，Segoe UI / 微软雅黑，正文 14 DIP；提供 100–200% 文字缩放 |
| 形状 | 导航容器 24、内层选择项 14；内容容器 20、字段 10；主要按钮胶囊 |
| 控件 | WPF 原生按钮、复选框、下拉框、密码框；最小 44 DIP 操作区域 |
| 键盘/读屏 | 焦点边框、标准控件语义、AutomationProperties 名称、状态 LiveSetting |
| 深浅色 | 8 页各渲染一张，修复了 primary button 被 TextBlock 默认样式覆盖文字颜色的问题 |
| 增大文字 | 浅色/深色 200% 样例渲染，侧栏与内容分别可滚动 |
| 降低透明度 | 导航材质不透明化；内容始终不透明 |
| 高对比 | SystemParameters.HighContrast 时使用 Windows 系统前景/背景/高亮色；未在真实系统切换中完整验收 |
| 降低动态效果 | 未引入装饰动画、视差或自动播放效果 |
| 实机交互 | 本机窗口启动成功、可访问性树完整可读；Computer Use 截图/输入驱动失败，因此未声明完整鼠标/键盘交互通过 |

WPF 没有 Apple 的原生 Liquid Glass、Dynamic Type 或 VoiceOver。导航采用适合 Windows 的克制材质；字体缩放与 UI Automation 是本平台对应能力。真实玻璃折射、拖动式自定义开关和 Apple 系统级交互没有伪造为已实现。

渲染证据：`artifacts/gui/light-*.png`、`dark-*.png`，由 `tests/TransMessage.GuiTests` 生成。最终验收边界见 ACCEPTANCE.md。
