using System.Runtime.InteropServices;
using Avalonia;
using CliCommands = Pm.Cli.Commands.Cli;

namespace Pm.Desktop;

internal static class Program
{
    private const int SwHide = 0;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "gui" or "--gui")
        {
            HideOwnedConsole();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args.Length == 0 ? args : args[1..]);
            return 0;
        }

        try
        {
            return CliCommands.RunAsync(args).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ошибка: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// The app is a console executable so CLI mode works from cmd and PowerShell. When it was
    /// started without a parent shell, hide the console window that Windows created for the GUI.
    /// </summary>
    private static void HideOwnedConsole()
    {
        if (!OperatingSystem.IsWindows()) return;

        var processes = new uint[2];
        if (GetConsoleProcessList(processes, (uint)processes.Length) > 1) return;

        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero) ShowWindow(handle, SwHide);
        FreeConsole();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
