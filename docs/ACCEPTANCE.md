# 0.1.0 验收记录

构建和测试记录将在最终打包验收后写入本文件。这里没有声称完成腾讯实机收发验证。

已知外部边界：QQ AppID/AppSecret 尚未提供；未执行真实 QQ 私聊/群聊主动发送；未在另一台 Windows 10 机器验证；本机 Computer Use 的 Windows 截图/输入驱动返回 `SetIsBorderRequired failed: 不支持此接口 (0x80004002)` 和 `coordinate input geometry is unavailable`，真实窗口可访问性树可读取，但完整鼠标交互未获验证。

GUI 使用实际 WPF 组件完成渲染检查，不将渲染测试混同为完整实机交互通过。
