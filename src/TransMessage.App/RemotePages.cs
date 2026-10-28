using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.Win32;
using TransMessage.Core;
using TransMessage.Core.Codex;
using TransMessage.Core.Configuration;
using TransMessage.Core.Notifications;

namespace TransMessage.App;

public partial class MainWindow
{
    private void History()
    {
        Heading("每条通知，都有记录", "保留最近 30 天、最多 500 条。内容先脱敏，再由 Windows 加密保存，仅当前账户可读。");
        var searchCard = Card("查找通知"); var query = Field(searchCard, "标题、内容或来源", "");
        var filter = new ComboBox { ItemsSource = new[] { "全部状态", "已发送", "发送失败", "送达不确定" }, SelectedIndex = 0 };
        AutomationProperties.SetName(filter, "历史状态筛选"); searchCard.Children.Add(filter);
        var list = new StackPanel(); PageContent.Children.Add(list);
        void Render()
        {
            list.Children.Clear();
            IReadOnlyList<HistoryEntry> history;
            try { history = _app.Dispatcher.History.Read(); }
            catch (Exception e) { list.Children.Add(Text(AppException.Describe(e) + " 原文件保留，请在诊断页打开数据目录后备份检查。")); return; }
            var rows = history.Where(x => (query.Text.Length == 0 || $"{x.Notification.Title}\n{x.Notification.Message}\n{x.Notification.Source}".Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                && (filter.SelectedIndex == 0 || NotificationHistory.Label(x.Status) == (string)filter.SelectedItem)).Take(50).ToArray();
            list.Children.Add(Text(rows.Length == 0 ? "还没有匹配的通知。发送测试通知后，这里会记录结果。" : $"显示最近 {rows.Length} 条匹配记录。"));
            foreach (var entry in rows)
            {
                var content = new StackPanel { Margin = new(0, 12, 0, 12) };
                content.Children.Add(Text($"{entry.Notification.Source} · {entry.Route}", true));
                content.Children.Add(Text(entry.Notification.Message, false));
                if (entry.Error is not null) content.Children.Add(Text(entry.Error));
                Action(Actions(content), "复制这条记录", () => { Clipboard.SetText($"[{entry.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}] {entry.Notification.Title}\n{entry.Notification.Message}\n{NotificationHistory.Label(entry.Status)}\n{entry.Error}"); Feedback("已复制脱敏记录。"); });
                var expander = new Expander
                {
                    Header = Text($"{entry.Time.ToLocalTime():MM-dd HH:mm:ss zzz} · {NotificationHistory.Label(entry.Status)}\n{entry.Notification.Title}", false),
                    Content = content,
                    Padding = new(16),
                    Margin = new(0, 0, 0, 12),
                    MinHeight = 64
                };
                expander.SetResourceReference(Control.BackgroundProperty, "Surface"); expander.SetResourceReference(Control.ForegroundProperty, "Label");
                AutomationProperties.SetName(expander, entry.Notification.Title + "，" + NotificationHistory.Label(entry.Status)); list.Children.Add(expander);
            }
        }
        var actions = Actions(searchCard); Action(actions, "查询 / 刷新", Render, true);
        Action(actions, "清空历史", () =>
        {
            if (MessageBox.Show(this, "这会删除本机保存的通知历史，不影响 QQ 消息与通知去重。", "清空通知历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            _app.Dispatcher.History.Clear(); Render();
        });
        Render();
    }
    private void RemoteControl()
    {
        Heading("在 QQ，接着做", "引用通知即可回复，也可直接带上会话编号。图片与文字可以分开发送，再一起提交。");
        var c = _app.Config.Load(); var settings = c.RemoteCodex;
        var connection = Card("控制连接", "只接受你选定的 QQ 私聊控制者。会话无需逐个授权，群消息不会启动任务。");
        var enabled = Check(connection, "启用 QQ 私聊控制", settings.Enabled);
        connection.Children.Add(Text("谁可以控制", false));
        var owners = c.Qq.Targets.Where(t => t.Kind == "c2c" && t.Enabled).ToArray();
        var owner = new ComboBox { ItemsSource = owners, SelectedItem = owners.FirstOrDefault(t => t.Id == settings.OwnerId) };
        AutomationProperties.SetName(owner, "私聊控制者"); connection.Children.Add(owner);
        if (owners.Length == 0) Action(Actions(connection), "先绑定手机私聊", () => Go("qq"));
        var executable = Field(connection, "Codex 程序（留空自动发现原生 exe）", settings.Executable);
        Action(Actions(connection), "选择 codex.exe…", () => { var dialog = new OpenFileDialog { Filter = "Codex 可执行程序|*.exe", CheckFileExists = true }; if (dialog.ShowDialog(this) == true) executable.Text = dialog.FileName; });
        var buttons = Actions(connection);
        Button(buttons, "保存并连接", async () =>
        {
            var nextOwner = (owner.SelectedItem as QqTarget)?.Id ?? "";
            if (settings.Enabled && settings.Threads.Any(t => t.Managed) && (enabled.IsChecked != true || executable.Text.Trim() != settings.Executable || nextOwner != settings.OwnerId)
                && MessageBox.Show(this, "更改连接会结束当前托管进程，未完成工作可能中断。", "更改控制连接", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
            _app.Config.Update(cfg => { cfg.RemoteCodex.Enabled = enabled.IsChecked == true; cfg.RemoteCodex.OwnerId = nextOwner; cfg.RemoteCodex.Executable = executable.Text.Trim(); });
            await _app.Remote.ApplySettingsAsync(); Feedback(_app.Remote.Status); ReloadPage();
        }, true);
        Button(buttons, "检测现有 Desktop", async () => Feedback(await _app.Remote.ProbeDesktopAsync()));
        State(connection, "remote", "当前连接"); State(connection, "questions", "等待回答");

        var threads = Card("你的会话", "现有 Desktop 与实时问答会话放在一起。共享队列中的消息仍需 Desktop 处理，不能据此确认已经执行。");
        var threadList = new StackPanel(); threads.Children.Add(threadList);
        void ShowThreads(IReadOnlyList<CodexTaskInfo> rows)
        {
            threadList.Children.Clear();
            var managedIds = _app.Config.Load().RemoteCodex.Threads.Where(t => t.Managed).Select(t => t.Id).ToHashSet();
            if (rows.Count == 0) threadList.Children.Add(Text("连接后点击“刷新会话”，查看本机的普通 Codex 会话。"));
            foreach (var thread in rows)
            {
                var row = new StackPanel { Margin = new(0, 8, 0, 12) };
                var managed = managedIds.Contains(thread.Id);
                row.Children.Add(Text($"#{thread.Id[^12..]} · {TaskName.Short(thread.Name)}", false));
                row.Children.Add(Text($"{(managed ? "实时问答" : "共享队列")} · {thread.State}"));
                if (thread.Directory.Length > 0) row.Children.Add(Text(thread.Directory));
                var copy = Action(Actions(row), "复制会话编号", () => { Clipboard.SetText("#" + thread.Id[^12..]); Feedback("已复制会话编号，可在 QQ 消息开头使用。"); });
                AutomationProperties.SetName(copy, "复制会话编号 #" + thread.Id[^12..]);
                threadList.Children.Add(row);
            }
        }
        ShowThreads(settings.Threads.Where(t => t.Managed).Select(t => new CodexTaskInfo(t.Id, t.Name, "", "连接后刷新状态")).ToArray());
        Button(Actions(threads), "刷新会话", async () => { ShowThreads(await _app.Remote.TasksAsync()); Feedback("会话已刷新，可直接引用通知或使用编号回复。"); });

        var draft = Card("图文一起发", "先引用一条会话通知，再发送 /d（也支持 /draft）。随后分开发送图片和文字，最后用 /s 一起提交；/c 取消草稿。");
        draft.Children.Add(Text("不引用消息时，发送 /d #会话编号。草稿固定属于该会话，不会随其他会话的通知切换。"));
        draft.Children.Add(Text("实时问答会话支持 /model 和 /reasoning，设置从下一轮生效。现有 Desktop 的模型设置与实时问题仍需在原窗口处理。"));

        var create = Card("新建实时问答会话", "需要在 QQ 回答问题或调整模型时，由 TransMessage 创建并运行会话。退出或停用控制会结束托管进程。");
        var name = Field(create, "会话名称", "我的远程任务"); var directory = Field(create, "项目目录", "");
        var createActions = Actions(create);
        Action(createActions, "选择项目目录…", () => { var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) directory.Text = dialog.FolderName; });
        Button(createActions, "创建会话", async () => { var thread = await _app.Remote.CreateManagedAsync(directory.Text, name.Text); Feedback($"会话 #{thread.Id[^12..]} 已创建。在 QQ 发送“#{thread.Id[^12..]} 你的要求”。"); ReloadPage(); });
        var pending = Card("等待你的回答", "问题码只对当前会话、当前请求有效。连接断开或问题已结束后，旧回答不会用于新的请求。有效期 15 分钟。");
        Action(Actions(pending), "刷新待回答问题", ReloadPage);
        foreach (var request in _app.Remote.Pending)
        {
            pending.Children.Add(Text(_app.Sanitizer.Redact(RemoteCodex.Describe(request)), false));
            var answers = new List<Func<string>>();
            foreach (var question in request.Questions)
            {
                if (question.Secret)
                {
                    pending.Children.Add(Text(question.Text, false)); pending.Children.Add(Text("敏感答案仅在本机输入，不记录历史。")); var password = new PasswordBox(); AutomationProperties.SetName(password, "本机敏感答案"); pending.Children.Add(password); answers.Add(() => password.Password);
                }
                else { var field = Field(pending, question.Text, ""); answers.Add(() => field.Text); }
            }
            var actions = Actions(pending);
            if (request.Questions.Length > 0) Button(actions, "发送回答", async () => { await _app.Remote.AnswerAsync(request.Code, string.Join(" | ", answers.Select(a => a())), true); Feedback("Codex 已确认回答。"); ReloadPage(); });
            else
            {
                if (request.CanApprove) Button(actions, "仅批准本次", async () => { await _app.Remote.AnswerAsync(request.Code, "accept", true); ReloadPage(); });
                Button(actions, "拒绝本次", async () => { await _app.Remote.AnswerAsync(request.Code, "decline", true); ReloadPage(); });
            }
        }
        if (_app.Remote.Pending.Count == 0) pending.Children.Add(Text("当前没有待回答问题。共享队列会话无法读取现有 Desktop 窗口里的实时问题。"));
        var commands = Card("在 QQ 中这样说"); Code(commands, RemoteCodex.Help);
    }
}
