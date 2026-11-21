// The UI's view of the core. NativeCore talks to rexcore.dll; MockCore (UI development,
// screenshots, demo mode) fakes hosts/sessions/layouts and feeds local panes.
using Windex.Interop;

namespace Windex.Core;

public interface ICore : IDisposable
{
    /// <summary>Latest model snapshot. Always raised on the UI thread.</summary>
    RexModel Model { get; }
    event EventHandler<RexModel>? ModelChanged;
    /// <summary>A pane has a new frame (UI thread). Coalesced until the pane's frame is locked.</summary>
    event EventHandler<ulong>? PaneDirty;
    event EventHandler<ulong>? Bell;
    event EventHandler<(ulong Pane, string Text)>? ClipboardRequested;
    event EventHandler<Notice>? NoticeRaised;
    event EventHandler<DesktopNotify>? NotificationRaised;
    event EventHandler<IReadOnlyList<DiscoveredMachine>>? DiscoveryUpdated;

    /// <summary>Send a command (docs/contract.md). "req" is filled in; the task completes with the RESULT.</summary>
    Task<CommandResult> CommandAsync(string cmd, object? args = null);

    // Hot path — callable from the UI thread. Return codes per rexcore.h (0 ok, 1 paused, <0 error).
    int Key(ulong pane, in KeyInput key);
    int Text(ulong pane, string text);
    int Paste(ulong pane, string text);
    int Mouse(ulong pane, in RexMouse mouse);
    int Resize(ulong pane, ushort cols, ushort rows, uint cellWidthPx, uint cellHeightPx);
    int Focus(ulong pane, bool focused);
    int Scroll(ulong pane, RexScrollOp op, long value);
    int Select(ulong pane, RexSelectOp op, int clicks, float x, float y);
    string? SelectionText(ulong pane);
    string? HyperlinkAt(ulong pane, int col, int row);
    /// <summary>Find in terminal: search the pane's screen and scrollback (null or "" stops and clears).
    /// Results arrive in frames. 0 ok, -3 invalid regular expression, other negatives: unknown pane.</summary>
    int Search(ulong pane, string? query, RexSearchFlags flags);
    /// <summary>Moves the current match up (direction &lt; 0, toward older output: the find bar's Next) or down
    /// (&gt; 0), wrapping, and scrolls it into view.</summary>
    int SearchStep(ulong pane, int direction);
    /// <summary>The cursor shape while the program has not chosen one (DECSCUSR).</summary>
    int SetDefaultCursor(ulong pane, RexCursorShape shape);

    /// <summary>Lock the pane's latest frame; null if none yet. Must be paired with UnlockFrame on the same thread.</summary>
    unsafe RexFrame* LockFrame(ulong pane);
    void UnlockFrame(ulong pane);

    ulong LocalPaneNew(ushort cols, ushort rows, bool echo);
    void LocalPaneFeed(ulong pane, ReadOnlySpan<byte> bytes);
    void LocalPaneClose(ulong pane);
}

/// <summary>A key event in W3C terms (see rex_key).</summary>
public readonly record struct KeyInput(string Code, string? Key, string? Text, RexMods Mods, RexKeyAction Action);
