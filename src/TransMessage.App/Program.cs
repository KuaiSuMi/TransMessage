using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TransMessage.Core;
using TransMessage.Core.Configuration;

namespace TransMessage.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            // A console subsystem keeps CMD/PowerShell waiting and preserves exit codes.
            // A double-click owns its new console and can detach it without hiding the user's terminal.
            if (args.Length == 0 || args.Contains("--background") || args.Contains("gui") || args.Contains("apply-update"))
            {
                var processes = new uint[2];
                if (GetConsoleProcessList(processes, 2) <= 1) FreeConsole();
            }
            return Cli.InvokeAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            var message = AppException.Describe(e);
            if (args.Length == 0) System.Windows.MessageBox.Show(message, "TransMessage", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            else Console.Error.WriteLine(message);
            return 2;
        }
    }
    [DllImport("kernel32.dll")] private static extern uint GetConsoleProcessList(uint[] processList, uint count);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
}
