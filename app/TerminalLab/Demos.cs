// The five demos. Each builds its UI around TerminalViews bound to local panes of the real core
// (rex_local_pane_new), feeds them VT content once their size is known, and closes its panes
// when replaced.
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Terminal;

namespace TerminalLab;

internal sealed record LabContext(RecordingCore Core, LabOptions Options, bool Dark, Action<string> Status);

internal abstract class Demo : IDisposable
{
    /// <summary>A new pane's grid until its view resizes it; with --non-owner it keeps a smaller grid for good.</summary>
    private (ushort Cols, ushort Rows) PaneGrid => Lab.Options.NonOwner ? ((ushort)100, (ushort)30) : ((ushort)120, (ushort)40);

    protected readonly LabContext Lab;
    private readonly List<ulong> _panes = new();
    public List<TerminalView> Views { get; } = new();
    public abstract FrameworkElement Root { get; }

    protected Demo(LabContext lab) => Lab = lab;

    protected TerminalView Terminal(string? content, bool echo) => Terminal(content is null ? null : _ => content, echo);

    /// <summary>A terminal on a new local pane; <paramref name="content"/> (given the column count) is fed once the view has sized it.</summary>
    protected TerminalView Terminal(Func<int, string>? content, bool echo)
    {
        var core = Lab.Core;
        ulong pane = core.LocalPaneNew(PaneGrid.Cols, PaneGrid.Rows, echo);
        _panes.Add(pane);
        var theme = TerminalTheme.ForAppTheme(Lab.Dark);
        var view = new TerminalView
        {
            Core = core,
            Pane = pane,
            Theme = theme,
            TerminalFontSize = Lab.Options.FontSize,
            TransparentDefaultBackground = !Lab.Options.Opaque,
            CursorBlinks = !Lab.Options.ScreenshotFriendly,
            ShortcutFilter = core.Offered,
        };
        // Record link activations instead of opening a browser.
        view.LinkActivated += (_, e) => { core.Note($"link {e.Uri}"); e.Handled = true; };
        Views.Add(view);
        core.LocalPaneFeed(pane, Encoding.UTF8.GetBytes(theme.ToOscSequence()));
        bool fed = false;
        view.Loaded += (_, _) =>
        {
            if (fed) return;
            fed = true;
            // After the view's own Loaded handler has sent its first resize.
            DispatcherQueue.GetForCurrentThread().TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                int cols = Lab.Options.NonOwner ? PaneGrid.Cols : view.GridSize.Columns;
                if (content is not null) core.LocalPaneFeed(pane, Encoding.UTF8.GetBytes(content(cols)));
                if (Lab.Options.NonOwner) core.LocalPaneFeed(pane, Encoding.UTF8.GetBytes(DemoContent.NonOwnerEdges(PaneGrid.Cols, PaneGrid.Rows)));
                if (Lab.Options.MouseTracking) core.LocalPaneFeed(pane, "\x1b[?1002h\x1b[?1006h"u8);
                OnFed(view);
            });
        };
        return view;
    }

    protected virtual void OnFed(TerminalView view)
    {
        if (Lab.Options.SelectAll) view.SelectAll();
        if (Lab.Options.ScreenshotFriendly && view == Views[0]) view.Focus();
    }

    public virtual void Dispose()
    {
        foreach (var view in Views) view.Dispose();
        foreach (var pane in _panes) Lab.Core.LocalPaneClose(pane);
        _panes.Clear();
    }

    protected static Grid Ground(UIElement child, Thickness? padding = null)
    {
        var grid = new Grid { Padding = padding ?? new Thickness(8) };
        grid.Children.Add(child);
        return grid;
    }
}

internal static class Demos
{
    public static readonly string[] Names = ["Showcase", "Scrollback", "Split", "Typing pad", "Stress"];
    public static int Count => Names.Length;

    public static Demo Create(int number, LabContext lab) => number switch
    {
        1 => new ShowcaseDemo(lab),
        2 => new ScrollbackDemo(lab),
        3 => new SplitDemo(lab),
        4 => new TypingPadDemo(lab),
        _ => new StressDemo(lab),
    };
}

internal sealed class ShowcaseDemo : Demo
{
    public override FrameworkElement Root { get; }

    public ShowcaseDemo(LabContext lab) : base(lab)
    {
        var card = new PaneCard(Terminal(DemoContent.Showcase, echo: true), "Showcase");
        card.SetLook(lab.Dark, focused: true, single: true);
        Root = Ground(card);
    }
}

internal sealed class ScrollbackDemo : Demo
{
    public override FrameworkElement Root { get; }

    public ScrollbackDemo(LabContext lab) : base(lab)
    {
        var card = new PaneCard(Terminal(DemoContent.Scrollback(), echo: true), "10,000 lines");
        card.SetLook(lab.Dark, focused: true, single: true);
        Root = Ground(card);
    }
}

/// <summary>Two cards side by side, 6 epx apart: compositing over the ground and focus styling.</summary>
internal sealed class SplitDemo : Demo
{
    private readonly PaneCard[] _cards;
    public override FrameworkElement Root { get; }

    public SplitDemo(LabContext lab) : base(lab)
    {
        _cards =
        [
            new PaneCard(Terminal(DemoContent.Listing(), echo: true), "left"),
            new PaneCard(Terminal(DemoContent.TypingPadIntro(), echo: true), "right — echo"),
        ];
        var grid = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition() } };
        for (int i = 0; i < _cards.Length; i++)
        {
            int index = i;
            Grid.SetColumn(_cards[i], i);
            grid.Children.Add(_cards[i]);
            _cards[i].Terminal.GotPaneFocus += (_, _) => FocusCard(index);
        }
        FocusCard(0);
        Root = Ground(grid);
    }

    private void FocusCard(int index)
    {
        for (int i = 0; i < _cards.Length; i++) _cards[i].SetLook(Lab.Dark, focused: i == index, single: false);
    }
}

/// <summary>An echo pane beside a live list of every input call that reached the core.</summary>
internal sealed class TypingPadDemo : Demo
{
    private readonly TextBlock _log = new() { FontFamily = new FontFamily("Cascadia Mono"), FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly List<string> _lines = new();
    public override FrameworkElement Root { get; }

    public TypingPadDemo(LabContext lab) : base(lab)
    {
        var card = new PaneCard(Terminal(DemoContent.TypingPadIntro(), echo: true), "Typing pad");
        card.SetLook(lab.Dark, focused: true, single: true);
        var panel = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Child = new ScrollViewer { Content = _log },
        };
        var grid = new Grid { ColumnSpacing = 6, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = new GridLength(420) } } };
        Grid.SetColumn(panel, 1);
        grid.Children.Add(card);
        grid.Children.Add(panel);
        lab.Core.Recorded += OnRecorded;
        Root = Ground(grid);
    }

    private void OnRecorded(object? sender, string line)
    {
        if (line.StartsWith("resize", StringComparison.Ordinal) || line.StartsWith("select Drag", StringComparison.Ordinal) || line.StartsWith("filter", StringComparison.Ordinal)) return;
        _lines.Add(line);
        if (_lines.Count > 40) _lines.RemoveAt(0);
        _log.Text = string.Join("\n", _lines);
    }

    protected override void OnFed(TerminalView view)
    {
        base.OnFed(view);
        view.Focus();
    }

    public override void Dispose()
    {
        Lab.Core.Recorded -= OnRecorded;
        base.Dispose();
    }
}

/// <summary>Continuous output with live frame timings.</summary>
internal sealed class StressDemo : Demo
{
    private readonly StressDriver _driver;
    private readonly TerminalView _view;
    private readonly List<TerminalRenderStats> _window = new();
    private readonly DispatcherQueueTimer _report;
    public override FrameworkElement Root { get; }
    public List<TerminalRenderStats> All { get; } = new();
    public bool Recording { get; set; }

    public StressDemo(LabContext lab) : base(lab)
    {
        _view = Terminal((string?)null, echo: false);
        _driver = new StressDriver(lab.Core, _view.Pane, _view);
        var card = new PaneCard(_view, "Stress");
        card.SetLook(lab.Dark, focused: true, single: true);
        var full = new ToggleSwitch { OnContent = "Full-screen truecolor frames", OffContent = "Streaming lines", IsOn = lab.Options.BenchSeconds > 0 && !lab.Options.BenchStreaming };
        full.Toggled += (_, _) => _driver.FullScreen = full.IsOn;
        _driver.FullScreen = full.IsOn;
        var layout = new Grid { RowSpacing = 6, RowDefinitions = { new RowDefinition { Height = GridLength.Auto }, new RowDefinition() } };
        Grid.SetRow(card, 1);
        layout.Children.Add(full);
        layout.Children.Add(card);
        Root = Ground(layout);
        _view.Rendered += (_, stats) =>
        {
            _window.Add(stats);
            if (Recording) All.Add(stats);
        };
        _report = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _report.Interval = TimeSpan.FromSeconds(1);
        _report.Tick += OnReportTimer;
        _report.Start();
    }

    protected override void OnFed(TerminalView view)
    {
        base.OnFed(view);
        _driver.Start();
    }

    private long _lastBytes;

    private void OnReportTimer(DispatcherQueueTimer timer, object args) => Report();

    private void Report()
    {
        if (_window.Count == 0) return;
        var last = _window[^1];
        double mb = (_driver.BytesFed - _lastBytes) / 1048576.0;
        _lastBytes = _driver.BytesFed;
        Lab.Status($"{last.Cols}×{last.Rows}  {_window.Count} fps  " +
                   $"prepare {_window.Average(s => s.PrepareMs):0.00} ms  draw {_window.Average(s => s.DrawMs):0.00} ms  " +
                   $"(p95 {Percentile(_window.Select(s => s.TotalMs), 0.95):0.00} ms)  " +
                   $"runs {last.GlyphRuns}  input {mb:0.0} MB/s");
        _window.Clear();
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
    }

    public override void Dispose()
    {
        _report.Stop();
        _report.Tick -= OnReportTimer;
        _driver.Dispose();
        base.Dispose();
    }
}
