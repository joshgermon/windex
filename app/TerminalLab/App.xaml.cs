using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windex.Core;

namespace TerminalLab;

public partial class App : Application
{
    private MainWindow? _window;
    private RecordingCore? _core;

    public App()
    {
        InitializeComponent();
        // A crash in a XAML callback otherwise only leaves a stowed-exception code in the event log.
        UnhandledException += (_, e) =>
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "TerminalLab", "crash.log"), $"{DateTime.Now:O} {e.Exception}\n\n");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var options = LabOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
        var stateDir = Path.Combine(Path.GetTempPath(), "TerminalLab");
        _core = new RecordingCore(new NativeCore(DispatcherQueue.GetForCurrentThread(), stateDir), options.LogPath) { IgnoreResize = options.NonOwner };
        _window = new MainWindow(_core, options);
        _window.Closed += (_, _) => _core.Dispose();
        _window.Activate();
        if (options.BenchSeconds > 0 && options.OutPath is { } outPath) _window.RunBenchmark(options.BenchSeconds, outPath);
        else if (options.LeakCycles > 0 && options.OutPath is { } leakPath) _window.RunLeakCheck(options.LeakCycles, leakPath);
        if (options.LoseDeviceAfter > 0) _window.LoseDeviceAfter(options.LoseDeviceAfter);
    }
}
