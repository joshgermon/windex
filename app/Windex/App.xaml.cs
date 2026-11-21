// Startup: pick the core (rexcore.dll unless --mock / WINDEX_MOCK=1 or the DLL is missing), load
// settings and hosts, register notifications and open the main window.
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windex.Core;
using Windex.Services;
using Windex.Settings;

namespace Windex;

public partial class App : Application
{
    public static ICore Core { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static HostRegistry Hosts { get; private set; } = null!;
    public static Notifications Notifications { get; private set; } = null!;
    public static MainWindow? Main { get; private set; }

    /// <summary>Why the app runs on demo data when nobody asked for it (null otherwise).</summary>
    public static string? DemoReason { get; private set; }

    public static string LogDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Windex", "logs");

    private static SettingsWindow? _settingsWindow;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log($"unhandled: {e.Exception}");
            e.Handled = true; // keep the window alive; the log says what went wrong
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Settings = AppSettings.Load(AppSettings.DefaultDirectory());
        Core = CreateCore(DispatcherQueue.GetForCurrentThread());
        Hosts = new HostRegistry(Core, Settings, Core is MockCore ? MockCore.DemoHosts : null);
        Notifications = new Notifications();

        Main = new MainWindow();
        Main.Closed += (_, _) =>
        {
            _settingsWindow?.Close();
            Core.Dispose();
        };
        Main.Activate();
        _ = Hosts.RegisterAllAsync();
        if (DemoReason is not null) Main.ShowNotice("info", "Demo mode", DemoReason);
    }

    private static ICore CreateCore(DispatcherQueue dispatcher)
    {
        bool mock = Environment.GetCommandLineArgs().Contains("--mock") || Environment.GetEnvironmentVariable("WINDEX_MOCK") == "1";
        if (!mock)
        {
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "rexcore.dll")))
                DemoReason = "rexcore.dll isn't next to Windex.exe, so Windex shows demo hosts.";
            else
            {
                try { return new NativeCore(dispatcher); }
                catch (Exception ex)
                {
                    Log($"rexcore failed to start: {ex}");
                    DemoReason = $"rexcore.dll couldn't start ({ex.Message}), so Windex shows demo hosts.";
                }
            }
        }
        return new MockCore(dispatcher);
    }

    public static void ShowSettings(string? page)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Navigate(page);
        _settingsWindow.Activate();
    }

    public static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[windex] {message}");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(Path.Combine(LogDirectory, "windex.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* logging must never take the app down */ }
    }
}
