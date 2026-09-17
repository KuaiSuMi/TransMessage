using System.IO;
using System.Windows;
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
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/gui"); Directory.CreateDirectory(output);
        var application = new Application();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/TransMessage;component/Theme.xaml", UriKind.Relative) });
        foreach (var theme in new[] { "light", "dark" })
        {
            var paths = new AppPaths(Path.Combine(output, "state-" + theme)); var config = new ConfigStore(paths);
            config.Update(c => { c.Ui.Theme = theme; c.Update.Check = false; c.Server.Enabled = false; c.Codex.Enabled = false; c.Downloads.Enabled = false; });
            var runtime = new AppRuntime(paths); var window = new MainWindow(runtime); var navigation = (ListBox)window.FindName("Navigation");
            var root = (FrameworkElement)window.Content;
            for (var i = 0; i < navigation.Items.Count; i++)
            {
                navigation.SelectedIndex = i;
                var tag = (string)((ListBoxItem)navigation.SelectedItem).Tag;
                Render(root, Path.Combine(output, theme + "-" + tag + ".png"), 1050, 760);
                Console.WriteLine($"PASS render {theme}/{tag}");
            }
            navigation.SelectedIndex = 6; root.LayoutTransform = new ScaleTransform(2, 2);
            Render(root, Path.Combine(output, theme + "-large-text.png"), 1400, 1000);
            root.LayoutTransform = Transform.Identity;
            window.Exiting = true; window.Close(); runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        application.Shutdown(); return 0;
    }
    private static void Render(FrameworkElement root, string path, int width, int height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
