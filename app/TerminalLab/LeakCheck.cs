// The lifecycle check (--leak-check <cycles> --out <file>). It opens a full-size pane card and
// closes it again <cycles> times, the way the shell drops a card: remove it from the tree, Dispose
// the TerminalView, close the pane (every other cycle skips Dispose: leaving the tree must be
// enough). Before closing, each view has rendered coloured output, taken keyboard focus, selected
// everything and flashed its scrollbar, so the timers and handlers those paths start are all in
// play; every other cycle also moves focus to another pane first. Then it forces full
// collections and reports how many of the views are still alive, and how the process's memory
// moved. Every view must be collected, and dedicated GPU memory must not grow with the number of
// panes that came and went.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windex.Terminal;

namespace TerminalLab;

internal sealed class LeakCheck(RecordingCore core, Panel stage, bool dark)
{
    private const int WarmUp = 10;
    private readonly List<WeakReference> _views = new();
    /// <summary>Stands in for another pane's keyboard focus.</summary>
    private readonly TextBox _otherPane = new() { Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private PaneCard? _card;
    private ulong _pane;
    private double _surfaceBytes;

    /// <summary>Runs the cycles and returns the report; its last line starts with PASS or FAIL.</summary>
    public async Task<string> RunAsync(int cycles)
    {
        var report = new StringBuilder();
        stage.Children.Add(_otherPane);
        // Warm-up cycles first: shared fonts, glyph caches and sprite sheets, and the allocation pools
        // of the GPU driver and the compositor (tens of MB that are reused, not leaked), exist before
        // the baseline.
        for (int i = 0; i < WarmUp; i++) await OpenAndCloseAsync($"warm-up {i + 1}", i);
        await CollectAsync();
        var (privateBefore, managedBefore, gpuBefore) = Memory();
        var clock = Stopwatch.StartNew();
        for (int i = 0; i < cycles; i++) await OpenAndCloseAsync($"pane {i + 1} of {cycles}", i);
        await CollectAsync();
        var (privateAfter, managedAfter, gpuAfter) = Memory();
        int alive = _views.Count(v => v.IsAlive);
        report.AppendLine($"cycles {cycles} (after {WarmUp} warm-up) in {clock.Elapsed.TotalSeconds:0.0} s");
        report.AppendLine($"private memory {privateBefore:0.0} MB -> {privateAfter:0.0} MB ({(privateAfter - privateBefore) / cycles * 1024:0} KB per cycle)");
        report.AppendLine($"managed heap {managedBefore:0.0} MB -> {managedAfter:0.0} MB");
        // A pane that kept its surface would add at least that surface's size per cycle. The driver's
        // and compositor's pools still grow a little (sub-linearly: +26 MB after 10 cycles, +39 MB
        // after 100, +52 MB after 300), so the bar is half a surface per cycle.
        bool gpuFlat = true;
        if (gpuBefore is { } g0 && gpuAfter is { } g1)
        {
            double perCycleKb = (g1 - g0) / 1024.0 / cycles, surfaceKb = _surfaceBytes / 1024.0;
            gpuFlat = perCycleKb < surfaceKb / 2;
            report.AppendLine($"{(gpuFlat ? "PASS" : "FAIL")} dedicated GPU memory {g0 / 1048576.0:0.0} MB -> {g1 / 1048576.0:0.0} MB " +
                              $"({perCycleKb:0} KB per cycle; one pane's surface is {surfaceKb:0} KB)");
        }
        else
        {
            report.AppendLine("dedicated GPU memory: counters unavailable");
        }
        string which = alive == 0 ? "" : " (cycles " + string.Join(", ", _views.Select((v, i) => (v, i)).Where(x => x.v.IsAlive).Select(x => x.i + 1 - WarmUp)) + ")";
        report.AppendLine($"{(alive == 0 && gpuFlat ? "PASS" : "FAIL")} views alive after collection: {alive}/{_views.Count}{which}");
        return report.ToString();
    }

    /// <summary>One cycle; <paramref name="n"/> varies how it ends: disposed or only removed, and
    /// closed while focused or after focus moved to another pane.</summary>
    private async Task OpenAndCloseAsync(string label, int n)
    {
        var rendered = Open(label);
        await Task.WhenAny(rendered, Task.Delay(3000));
        double scale = stage.XamlRoot?.RasterizationScale ?? 1;
        _surfaceBytes = _card!.Terminal.ActualWidth * scale * _card.Terminal.ActualHeight * scale * 4;
        Exercise();
        await Task.Delay(150);
        if (n % 4 >= 2) _otherPane.Focus(FocusState.Programmatic);
        Close(dispose: n % 2 == 0);
        await Task.Delay(30);
    }

    /// <summary>Builds and shows a card; returns a task that completes on the view's first frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private Task Open(string label)
    {
        _pane = core.LocalPaneNew(120, 40, echo: true);
        var theme = TerminalTheme.ForAppTheme(dark);
        var view = new TerminalView { Core = core, Pane = _pane, Theme = theme };
        _views.Add(new WeakReference(view));
        var content = new StringBuilder(theme.ToOscSequence());
        for (int line = 0; line < 120; line++)
            content.Append($"\x1b[38;2;{80 + line % 160};150;220m{label}\x1b[0m line {line} \x1b[1mbold\x1b[0m 中文 😀 ─┼─ \x1b[4:3mcurly\x1b[0m\r\n");
        core.LocalPaneFeed(_pane, Encoding.UTF8.GetBytes(content.ToString()));
        _card = new PaneCard(view, label);
        _card.SetLook(dark, focused: true, single: true);
        stage.Children.Add(_card);
        var rendered = new TaskCompletionSource();
        EventHandler<TerminalRenderStats>? once = null;
        once = (_, stats) =>
        {
            if (stats.Rows == 0) return;
            view.Rendered -= once;
            rendered.TrySetResult();
        };
        view.Rendered += once;
        return rendered.Task;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Exercise()
    {
        var view = _card!.Terminal;
        view.Focus();
        view.SelectAll();
        view.ScrollToBottom();
        core.Scroll(_pane, Windex.Interop.RexScrollOp.Delta, -5);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Close(bool dispose)
    {
        stage.Children.Remove(_card);
        if (dispose) _card!.Terminal.Dispose();
        core.LocalPaneClose(_pane);
        _card = null;
        _pane = 0;
    }

    private static async Task CollectAsync()
    {
        // Unloaded and XAML's own deferred releases run on later ticks; collect between them.
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(250);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static (double PrivateMb, double ManagedMb, long? GpuBytes) Memory()
    {
        using var process = Process.GetCurrentProcess();
        return (process.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(forceFullCollection: true) / 1048576.0, GpuMemory.DedicatedBytes());
    }
}
