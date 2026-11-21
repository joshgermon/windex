// TerminalLab's window: a demo picker and options over a Mica + aurora ground (the surface pane
// cards float on in Windex), the current demo, and a status line.
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windex.Terminal;
using Windows.Graphics;
using Windows.UI;

namespace TerminalLab;

internal sealed partial class MainWindow : Window
{
    private readonly RecordingCore _core;
    private readonly LabOptions _options;
    private readonly Grid _root = new();
    private readonly Grid _stage = new();
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly SelectorBar _picker = new();
    private bool _dark;
    private Demo? _demo;
    private int _demoNumber;
    private DispatcherQueueTimer? _benchTimer, _deviceTimer;

    public MainWindow(RecordingCore core, LabOptions options)
    {
        _core = core;
        _options = options;
        _dark = options.Dark ?? true;
        Title = "TerminalLab";
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
        if (options.Size is { } size) AppWindow.Resize(new SizeInt32(size.W, size.H));
        if (options.Position is { } pos) AppWindow.Move(new PointInt32(pos.X, pos.Y));

        foreach (var name in Demos.Names) _picker.Items.Add(new SelectorBarItem { Text = name });
        _picker.SelectionChanged += (_, _) =>
        {
            int index = _picker.Items.IndexOf(_picker.SelectedItem) + 1;
            if (index > 0 && index != _demoNumber) ShowDemo(index);
        };
        var themeToggle = new ToggleSwitch { OnContent = "Dark", OffContent = "Light", IsOn = _dark, VerticalAlignment = VerticalAlignment.Center };
        themeToggle.Toggled += (_, _) => { _dark = themeToggle.IsOn; ApplyTheme(); ShowDemo(_demoNumber); };

        var toolbar = new Grid { Padding = new Thickness(8, 4, 12, 4), ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(themeToggle, 1);
        toolbar.Children.Add(_picker);
        toolbar.Children.Add(themeToggle);

        var statusBar = new Border { Padding = new Thickness(12, 0, 12, 0), Height = 28, Child = _status };
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition());
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_stage, 1);
        Grid.SetRow(statusBar, 2);
        _root.Children.Add(toolbar);
        _root.Children.Add(_stage);
        _root.Children.Add(statusBar);
        Content = _root;
        _stage.PointerWheelChanged += OnStageWheel;
        ApplyTheme();
        ShowDemo(options.Demo);
        Closed += (_, _) => _demo?.Dispose();
    }

    public Demo? CurrentDemo => _demo;

    /// <summary>The host's font zoom (design §11, Ctrl+wheel), which TerminalView leaves unhandled for its host.</summary>
    private void OnStageWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_demo is not { Views.Count: > 0 } demo || !e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        int step = Math.Sign(e.GetCurrentPoint(_stage).Properties.MouseWheelDelta);
        if (step == 0) return;
        e.Handled = true;
        double size = Math.Clamp(demo.Views[0].TerminalFontSize + step, 6, 36);
        foreach (var view in demo.Views) view.TerminalFontSize = size;
        _status.Text = $"Font {size:0} pt";
        _core.Note($"zoom {size:0}");
    }

    /// <summary>Runs the device-loss recovery after a while (--lose-device): every view must rebuild and redraw.</summary>
    public void LoseDeviceAfter(double seconds)
    {
        _deviceTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _deviceTimer.Interval = TimeSpan.FromSeconds(seconds);
        _deviceTimer.IsRepeating = false;
        _deviceTimer.Tick += (timer, _) =>
        {
            timer.Stop();
            if (_demo is not { Views.Count: > 0 } demo) return;
            _core.Note("device lost (simulated)");
            demo.Views[0].Rendered += OnRenderedAfterDeviceLoss;
            TerminalGraphics.Recover();
        };
        _deviceTimer.Start();
    }

    private void OnRenderedAfterDeviceLoss(object? sender, TerminalRenderStats stats)
    {
        ((TerminalView)sender!).Rendered -= OnRenderedAfterDeviceLoss;
        _core.Note($"rendered after device loss ({stats.Cols}x{stats.Rows})");
        _status.Text = "Device loss simulated: the terminals rebuilt their GPU resources and redrew.";
    }

    private void ApplyTheme()
    {
        _root.RequestedTheme = _dark ? ElementTheme.Dark : ElementTheme.Light;
        // The aurora wash (docs/design.md §4.3) over Mica Alt.
        _root.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Offset = 0, Color = _dark ? Color.FromArgb(0x4D, 0x7B, 0x4F, 0x8F) : Color.FromArgb(0x8C, 0xF5, 0xC6, 0xEA) },
                new GradientStop { Offset = 0.5, Color = _dark ? Color.FromArgb(0x1F, 0x40, 0x4A, 0x7A) : Color.FromArgb(0x40, 0xD8, 0xDB, 0xF8) },
                new GradientStop { Offset = 1, Color = _dark ? Color.FromArgb(0x40, 0xA0, 0x6A, 0x3A) : Color.FromArgb(0x8C, 0xFF, 0xE4, 0xA0) },
            },
        };
    }

    private void ShowDemo(int number)
    {
        _demo?.Dispose();
        _stage.Children.Clear();
        _demoNumber = number;
        _demo = Demos.Create(number, new LabContext(_core, _options, _dark, text => _status.Text = text));
        _stage.Children.Add(_demo.Root);
        if (_picker.Items.Count >= number) _picker.SelectedItem = _picker.Items[number - 1];
        _status.Text = $"{Demos.Names[number - 1]} — local pane on rexcore {(_options.ScreenshotFriendly ? "(screenshot-friendly)" : "")}";
        foreach (var view in _demo.Views) view.Rendered += OnFirstRender;
    }

    private void OnFirstRender(object? sender, Windex.Terminal.TerminalRenderStats stats)
    {
        if (sender is not Windex.Terminal.TerminalView view) return;
        view.Rendered -= OnFirstRender;
        if (_demoNumber != 5) _status.Text += $"  ·  {stats.Cols}×{stats.Rows} cells, first frame {stats.TotalMs:0.00} ms";
    }

    /// <summary>The lifecycle check (<see cref="LeakCheck"/>): replaces the demo, writes its report, exits.</summary>
    public async void RunLeakCheck(int cycles, string outPath)
    {
        _demo?.Dispose();
        _demo = null;
        _stage.Children.Clear();
        _status.Text = $"Leak check: opening and closing {cycles} pane cards…";
        string report;
        try
        {
            report = await new LeakCheck(_core, _stage, _dark).RunAsync(cycles);
        }
        catch (Exception e)
        {
            report = $"FAIL {e}";
        }
        File.WriteAllText(outPath, report);
        Close();
    }

    /// <summary>The benchmark: full-screen truecolor frames for a while, then timings to a file, then exit.</summary>
    public void RunBenchmark(double seconds, string outPath)
    {
        if (_demo is not StressDemo stress) return;
        // The timer lives in a field: an unreferenced DispatcherQueueTimer can be collected before it fires.
        _benchTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _benchTimer.Interval = TimeSpan.FromSeconds(1.5);
        _benchTimer.IsRepeating = false;
        _benchTimer.Tick += (warmup, _) =>
        {
            stress.Recording = true;
            warmup.Stop();
            _benchTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _benchTimer.Interval = TimeSpan.FromSeconds(seconds);
            _benchTimer.IsRepeating = false;
            _benchTimer.Tick += (_, _) =>
            {
                stress.Recording = false;
                var all = stress.All;
                string Stat(Func<Windex.Terminal.TerminalRenderStats, double> f) =>
                    $"avg {all.Average(f):0.000}  p50 {StressDemo.Percentile(all.Select(f), 0.5):0.000}  p95 {StressDemo.Percentile(all.Select(f), 0.95):0.000}  max {all.Max(f):0.000}";
                var last = all[^1];
                File.WriteAllText(outPath,
                    $"grid {last.Cols}x{last.Rows} ({last.Cols * last.Rows} cells), {all.Count} frames in {seconds:0.#} s ({all.Count / seconds:0.#} fps)\n" +
                    $"prepare ms (frame locked): {Stat(s => s.PrepareMs)}\n" +
                    $"draw ms (CPU, incl. D2D flush): {Stat(s => s.DrawMs)}\n" +
                    $"prepare+draw ms: {Stat(s => s.TotalMs)}\n" +
                    $"glyph runs per frame: {last.GlyphRuns}\n");
                Close();
            };
            _benchTimer.Start();
        };
        _benchTimer.Start();
    }
}
