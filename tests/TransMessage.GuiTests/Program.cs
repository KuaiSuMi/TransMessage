using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TransMessage.App;
using TransMessage.Core.Configuration;
using TransMessage.Core.Hosting;

namespace TransMessage.GuiTests;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "app-server")
        {
            if (Environment.GetEnvironmentVariable("TM_TEST_CODEX_PID_FILE") is { } pidFile)
                File.WriteAllText(pidFile, Environment.ProcessId.ToString());
            while (Console.ReadLine() is { } line)
            {
                using var message = System.Text.Json.JsonDocument.Parse(line);
                if (message.RootElement.TryGetProperty("id", out var id))
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { id, result = new { } }));
            }
            return 0;
        }
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/gui"); Directory.CreateDirectory(output);
        var remoteOnly = args.Contains("--remote-only", StringComparer.Ordinal);
        var application = new Application();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TransMessage;component/Theme.xaml", UriKind.Relative) });
        foreach (var theme in new[] { "light", "dark" })
        {
            var paths = new AppPaths(Path.Combine(output, "state-" + theme)); var config = new ConfigStore(paths);
            config.Update(c =>
            {
                c.Ui.Theme = theme; c.Ui.TextScale = 1; c.Ui.ReduceTransparency = false; c.Update.Check = false; c.Server.Enabled = false; c.Codex.Enabled = false; c.Downloads.Enabled = false;
                c.Qq.Targets = [new() { Id = "render-owner", Name = "我的手机" }]; c.RemoteCodex.OwnerId = "render-owner";
                c.RemoteCodex.Threads =
                [
                    new() { Id = "01a183b4-4886-7990-b8d5-c86d05816805", Name = "修复截图中的按钮遮挡问题", Managed = true },
                    new() { Id = "01a183b4-4936-7d21-9d56-5f0970812e05", Name = "整理更新说明", Managed = true }
                ];
            });
            var runtime = new AppRuntime(paths); var window = new MainWindow(runtime); var navigation = (ListBox)window.FindName("Navigation");
            runtime.Dispatcher.History.Clear();
            foreach (var status in new[] { "sent", "failed", "uncertain" })
            {
                var key = runtime.Dispatcher.History.Begin(new() { Title = "Codex 完成 · TransMessage", Message = "通知历史与远程回答已完成验证。", Source = "codex" });
                runtime.Dispatcher.History.Complete(key, status, status == "sent" ? null : "连接中断，请检查送达状态。");
            }
            var root = (FrameworkElement)window.Content;
            for (var i = 0; i < navigation.Items.Count; i++)
            {
                var tag = (string)((ListBoxItem)navigation.Items[i]).Tag;
                if (remoteOnly && tag != "remote") continue;
                navigation.SelectedIndex = i;
                Render(root, Path.Combine(output, theme + "-" + tag + ".png"), 1050, 760);
                if (tag == "remote")
                {
                    RenderContent((FrameworkElement)window.FindName("PageContent"), Path.Combine(output, theme + "-remote-full.png"), 700);
                    var feedback = (TextBlock)window.FindName("FeedbackText"); var previousFeedback = feedback.Text;
                    var controls = Descendants((DependencyObject)window.FindName("PageContent")).ToArray();
                    var copyNames = controls.OfType<Button>().Where(x => x.Content as string == "复制会话编号").Select(AutomationProperties.GetName).ToArray();
                    if (!copyNames.Order().SequenceEqual(config.Load().RemoteCodex.Threads.Select(t => "复制会话编号 #" + t.Id[^12..]).Order()) || copyNames.Distinct().Count() != 2)
                        throw new InvalidOperationException("Concurrent UUIDv7 sessions must show distinct 12-character suffixes.");
                    controls.OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "项目目录").Text = paths.Root;
                    controls.OfType<Button>().Single(x => x.Content as string == "创建会话").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (!feedback.Text.Contains("[CONTROL_OFFLINE]", StringComparison.Ordinal)) throw new InvalidOperationException("Offline create must display an actionable connection error.");
                    window.Feedback(previousFeedback);
                    Console.WriteLine($"PASS offline create {theme}");
                }
                Console.WriteLine($"PASS render {theme}/{tag}");
            }
            root.LayoutTransform = new ScaleTransform(2, 2);
            foreach (var tag in remoteOnly ? ["remote"] : new[] { "settings", "remote", "history" })
            {
                navigation.SelectedItem = navigation.Items.Cast<ListBoxItem>().First(x => (string)x.Tag == tag);
                Render(root, Path.Combine(output, theme + "-large-" + tag + ".png"), 1400, 1000);
            }
            root.LayoutTransform = Transform.Identity;
            window.Exiting = true; window.Close(); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();

            config.Update(c => { c.Ui.ReduceTransparency = true; c.Ui.TextScale = 2; });
            var accessibleRuntime = new AppRuntime(paths); var accessibleWindow = new MainWindow(accessibleRuntime);
            var accessibleNavigation = (ListBox)accessibleWindow.FindName("Navigation");
            accessibleNavigation.SelectedItem = accessibleNavigation.Items.Cast<ListBoxItem>().First(x => (string)x.Tag == "remote");
            var accessibleRoot = (FrameworkElement)accessibleWindow.Content;
            Render(accessibleRoot, Path.Combine(output, theme + "-remote-accessible.png"), 1400, 1000);
            var accessibleContent = (FrameworkElement)accessibleWindow.FindName("PageContent"); accessibleContent.LayoutTransform = new ScaleTransform(2, 2);
            RenderContent(accessibleContent, Path.Combine(output, theme + "-remote-large-full.png"), 1400);
            accessibleContent.LayoutTransform = Transform.Identity;
            foreach (var key in new[] { "Page", "Surface", "Sidebar", "Fill" }) application.Resources[key] = Brushes.Black;
            foreach (var key in new[] { "Label", "Secondary", "Border" }) application.Resources[key] = Brushes.White;
            application.Resources["Accent"] = Brushes.DarkBlue; application.Resources["Selection"] = Brushes.Navy;
            Render(accessibleRoot, Path.Combine(output, theme + "-remote-contrast-palette.png"), 1400, 1000);
            accessibleWindow.Exiting = true; accessibleWindow.Close(); accessibleRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        application.Shutdown(); return 0;
    }
    private static void RenderContent(FrameworkElement content, string path, int width)
    {
        var parent = (ScrollViewer)content.Parent; parent.Content = null;
        var surface = new Border { Background = (Brush)Application.Current.FindResource("Page"), Child = content, Padding = new(20) };
        System.Windows.Documents.TextElement.SetForeground(surface, (Brush)Application.Current.FindResource("Label"));
        System.Windows.Documents.TextElement.SetFontFamily(surface, new FontFamily("Segoe UI, Microsoft YaHei UI"));
        System.Windows.Documents.TextElement.SetFontSize(surface, 14);
        try
        {
            surface.Measure(new Size(width, double.PositiveInfinity));
            Render(surface, path, width, (int)Math.Ceiling(surface.DesiredSize.Height));
        }
        finally { surface.Child = null; parent.Content = content; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Render(FrameworkElement root, string path, int width, int height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
