using System.Windows;

namespace DiskTrim.UI;

/// <summary>Starts the window UI.</summary>
public static class GuiHost
{
    public static int Run(string? startPath) => RunWindow(() => new MainWindow(startPath));

    /// <summary>Starts WPF with the app's theme and styles and shows <paramref name="createWindow"/>.</summary>
    public static int RunWindow(Func<Window> createWindow)
    {
        var app = new App();
        app.InitializeComponent();
        ApplyTheme(AppSettings.Load().Theme);

        app.DispatcherUnhandledException += (_, e) =>
        {
            AppPaths.LogError(e.Exception);
            Dialogs.Error("Something went wrong: " + e.Exception.Message);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { AppPaths.LogError(e.Exception); e.SetObserved(); };

        return app.Run(createWindow());
    }

    /// <summary>Light, Dark or System (Windows 11 Fluent look).</summary>
    public static void ApplyTheme(string theme)
    {
        Application.Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }
}
