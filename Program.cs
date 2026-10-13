using DiskTrim.Cli;
using DiskTrim.UI;

namespace DiskTrim;

static class Program
{
    /// <summary>
    /// No arguments (double-click) or "--path folder" (right-click menu) opens the window;
    /// anything else is a console command.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        // "Run without installing" from the setup wizard: open the app from this file.
        if (args is ["--portable"]) args = [];
        // Install / uninstall; a shipped DiskTrim.exe that isn't installed yet offers to install itself.
        else if (Setup.SetupHost.TryRun(args, out int setupExit)) return setupExit;

        if (IsWindowLaunch(args, out var path))
        {
            ConsoleWindow.Detach();
            return GuiHost.Run(path);
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.CancelKeyPress += Ui.OnCancelKeyPress;
        try
        {
            return CommandLine.Run(args);
        }
        catch (Exception ex)
        {
            Ui.Error("Something went wrong: " + ex.Message);
            AppPaths.LogError(ex);
            return 1;
        }
    }

    static bool IsWindowLaunch(string[] args, out string? path)
    {
        path = null;
        if (args.Length == 0) return true;
        if (args.Length == 2 && args[0].Equals("--path", StringComparison.OrdinalIgnoreCase))
        {
            path = args[1];
            return true;
        }
        return false;
    }
}
