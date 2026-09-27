using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace MusicSpeedChanger;

/// <summary>
/// WinUI 3 application entry point.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    /// <summary>
    /// Catches UI-thread exceptions (e.g. a control event handler throwing) and
    /// records the full stack to %LocalAppData%\MusicSpeedChanger\crash.log
    /// instead of dying silently in Microsoft.UI.Xaml.dll. Handled so the app
    /// stays up and the user can report the log.
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MusicSpeedChanger");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}---{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
        e.Handled = true;
    }
}
