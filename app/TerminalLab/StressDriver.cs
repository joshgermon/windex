// Continuous output for the stress demo and the benchmark: either streaming coloured lines
// (scrolling, like a build log) or full-screen truecolor frames where every cell has its own
// foreground and background (the renderer's worst case).
using System.Text;
using Microsoft.UI.Dispatching;
using Windex.Core;
using Windex.Terminal;

namespace TerminalLab;

internal sealed class StressDriver : IDisposable
{
    private readonly ICore _core;
    private readonly ulong _pane;
    private readonly TerminalView _view;
    private readonly DispatcherQueueTimer _timer;
    private readonly Random _random = new(42);
    private readonly List<byte[]> _frames = new();
    private (int Cols, int Rows) _framesFor;
    private int _frame;

    public bool FullScreen { get; set; }
    public long BytesFed { get; private set; }

    public StressDriver(ICore core, ulong pane, TerminalView view)
    {
        _core = core;
        _pane = pane;
        _view = view;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(8);
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void OnTick(DispatcherQueueTimer timer, object args) => Feed();

    private void Feed()
    {
        byte[] bytes = FullScreen ? NextFrame() : Encoding.UTF8.GetBytes(Lines(40));
        _core.LocalPaneFeed(_pane, bytes);
        BytesFed += bytes.Length;
    }

    private static readonly string[] Words = ["compiling", "rexcore", "libghostty-vt", "frame", "glyph", "render", "swapchain", "pane", "session", "warning:", "ok", "Finished", "test", "result:"];

    private string Lines(int count)
    {
        var s = new StringBuilder();
        for (int l = 0; l < count; l++)
        {
            int words = 6 + _random.Next(14);
            for (int w = 0; w < words; w++)
            {
                var (r, g, b) = DemoContent.Hue(_random.NextDouble());
                s.Append($"\x1b[38;2;{r};{g};{b}m").Append(Words[_random.Next(Words.Length)]).Append(' ');
            }
            s.Append("\x1b[0m\r\n");
        }
        return s.ToString();
    }

    /// <summary>A full screen where every cell has its own truecolor foreground and background.</summary>
    private byte[] NextFrame()
    {
        var grid = _view.GridSize;
        if (grid != _framesFor)
        {
            _frames.Clear();
            _framesFor = grid;
            for (int f = 0; f < 8; f++) _frames.Add(Encoding.UTF8.GetBytes(FullFrame(grid.Columns, grid.Rows, f / 8.0)));
        }
        return _frames[_frame++ % _frames.Count];
    }

    private static string FullFrame(int cols, int rows, double phase)
    {
        var s = new StringBuilder(cols * rows * 40);
        s.Append("\x1b[H");
        const string text = "Windex renders every cell with its own colours — ";
        for (int r = 0; r < rows; r++)
        {
            s.Append($"\x1b[{r + 1};1H");
            for (int c = 0; c < cols; c++)
            {
                var (fr, fg, fb) = DemoContent.Hue((c / (double)cols + phase) % 1);
                var (br, bg, bb) = DemoContent.Hue((r / (double)rows + phase + 0.5) % 1);
                s.Append($"\x1b[38;2;{fr};{fg};{fb};48;2;{br / 3};{bg / 3};{bb / 3}m").Append(text[(r + c) % text.Length]);
            }
        }
        s.Append("\x1b[0m");
        return s.ToString();
    }

    /// <summary>Stops and detaches the timer (a subscribed DispatcherQueueTimer keeps its handler's target alive).</summary>
    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
