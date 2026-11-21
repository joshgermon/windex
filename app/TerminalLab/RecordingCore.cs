// An ICore that forwards to the real core and records every input call a TerminalView makes, so
// the typing pad can show (and the verification scripts can check) exactly what reaches the core.
using Windex.Core;
using Windex.Interop;

namespace TerminalLab;

internal sealed unsafe class RecordingCore(ICore inner, string? logPath) : ICore
{
    /// <summary>Records resizes but does not pass them on (a pane whose size another client owns).</summary>
    public bool IgnoreResize { get; init; }

    private readonly StreamWriter? _log = logPath is null ? null : new StreamWriter(logPath, append: false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

    /// <summary>One line per input call: "key code=KeyA key=a text=a mods=Shift action=Press".</summary>
    public event EventHandler<string>? Recorded;

    /// <summary>Records a key the view offered to its ShortcutFilter (the shell's hook).</summary>
    public bool Offered(KeyInput key)
    {
        Record($"filter code={key.Code} key={Show(key.Key)} text={Show(key.Text)} mods={key.Mods} action={key.Action}");
        return false;
    }

    /// <summary>Records something the lab observed (a link activation).</summary>
    public void Note(string line) => Record(line);

    private void Record(string line)
    {
        _log?.WriteLine(line);
        Recorded?.Invoke(this, line);
    }

    private static string Show(string? s)
    {
        if (s is null) return "null";
        var sb = new System.Text.StringBuilder("\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c < 0x20 || c == 0x7F) sb.Append($"\\x{(int)c:x2}");
            else if (char.IsSurrogatePair(s, i)) { sb.Append(c).Append(s[++i]); }
            else if (char.IsSurrogate(c)) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    public RexModel Model => inner.Model;
    public event EventHandler<RexModel>? ModelChanged { add => inner.ModelChanged += value; remove => inner.ModelChanged -= value; }
    public event EventHandler<ulong>? PaneDirty { add => inner.PaneDirty += value; remove => inner.PaneDirty -= value; }
    public event EventHandler<ulong>? Bell { add => inner.Bell += value; remove => inner.Bell -= value; }
    public event EventHandler<(ulong Pane, string Text)>? ClipboardRequested { add => inner.ClipboardRequested += value; remove => inner.ClipboardRequested -= value; }
    public event EventHandler<Notice>? NoticeRaised { add => inner.NoticeRaised += value; remove => inner.NoticeRaised -= value; }
    public event EventHandler<DesktopNotify>? NotificationRaised { add => inner.NotificationRaised += value; remove => inner.NotificationRaised -= value; }
    public event EventHandler<IReadOnlyList<DiscoveredMachine>>? DiscoveryUpdated { add => inner.DiscoveryUpdated += value; remove => inner.DiscoveryUpdated -= value; }

    public Task<CommandResult> CommandAsync(string cmd, object? args = null) => inner.CommandAsync(cmd, args);

    public int Key(ulong pane, in KeyInput key)
    {
        Record($"key code={key.Code} key={Show(key.Key)} text={Show(key.Text)} mods={key.Mods} action={key.Action}");
        return inner.Key(pane, key);
    }

    public int Text(ulong pane, string text) { Record($"text {Show(text)}"); return inner.Text(pane, text); }
    public int Paste(ulong pane, string text) { Record($"paste {Show(text)}"); return inner.Paste(pane, text); }

    public int Mouse(ulong pane, in RexMouse mouse)
    {
        if (mouse.Action != RexMouseAction.Move) Record($"mouse {mouse.Action} {mouse.Button} x={mouse.X:0} y={mouse.Y:0} wheel={mouse.WheelDy:0.##}");
        return inner.Mouse(pane, mouse);
    }

    public int Resize(ulong pane, ushort cols, ushort rows, uint cellWidthPx, uint cellHeightPx)
    {
        Record($"resize {cols}x{rows} cell={cellWidthPx}x{cellHeightPx}{(IgnoreResize ? " (ignored)" : "")}");
        return IgnoreResize ? 0 : inner.Resize(pane, cols, rows, cellWidthPx, cellHeightPx);
    }

    public int Focus(ulong pane, bool focused) { Record($"focus {focused}"); return inner.Focus(pane, focused); }

    public int Scroll(ulong pane, RexScrollOp op, long value) { Record($"scroll {op} {value}"); return inner.Scroll(pane, op, value); }

    public int Select(ulong pane, RexSelectOp op, int clicks, float x, float y)
    {
        Record($"select {op} clicks={clicks} x={x:0} y={y:0}");
        return inner.Select(pane, op, clicks, x, y);
    }

    public string? SelectionText(ulong pane)
    {
        var text = inner.SelectionText(pane);
        Record($"selection-text {Show(text)}");
        return text;
    }
    public string? HyperlinkAt(ulong pane, int col, int row) => inner.HyperlinkAt(pane, col, row);
    public int Search(ulong pane, string? query, RexSearchFlags flags) { Record($"search {Show(query)} {flags}"); return inner.Search(pane, query, flags); }
    public int SearchStep(ulong pane, int direction) { Record($"search-step {direction}"); return inner.SearchStep(pane, direction); }
    public int SetDefaultCursor(ulong pane, RexCursorShape shape) => inner.SetDefaultCursor(pane, shape);
    public RexFrame* LockFrame(ulong pane) => inner.LockFrame(pane);
    public void UnlockFrame(ulong pane) => inner.UnlockFrame(pane);
    public ulong LocalPaneNew(ushort cols, ushort rows, bool echo) => inner.LocalPaneNew(cols, rows, echo);
    public void LocalPaneFeed(ulong pane, ReadOnlySpan<byte> bytes) => inner.LocalPaneFeed(pane, bytes);
    public void LocalPaneClose(ulong pane) => inner.LocalPaneClose(pane);

    public void Dispose()
    {
        _log?.Dispose();
        inner.Dispose();
    }
}
