// P/Invoke surface for rexcore.dll. Mirrors core/include/rexcore.h exactly — change both together.
using System.Runtime.InteropServices;

namespace Windex.Interop;

public enum RexEventKind
{
    Model = 1,
    PaneDirty = 2,
    Result = 3,
    Notice = 4,
    Bell = 5,
    Clipboard = 6,
    Notify = 7,
    Log = 8,
    Discovery = 9,
}

[Flags]
public enum RexMods : uint
{
    None = 0, Shift = 1, Ctrl = 2, Alt = 4, Super = 8, Caps = 16, Num = 32,
}

public enum RexKeyAction : uint { Release = 0, Press = 1, Repeat = 2 }
public enum RexMouseAction : uint { Press = 1, Release = 2, Move = 3, Wheel = 4 }
public enum RexMouseButton : uint { None = 0, Left = 1, Right = 2, Middle = 3 }
public enum RexScrollOp { Delta = 0, Top = 1, Bottom = 2, Prompt = 3, Row = 4 }
public enum RexSelectOp { Clear = 0, Press = 1, Drag = 2, Release = 3, All = 4 }
public enum RexCursorShape : byte { Block = 0, Bar = 1, Underline = 2, Hollow = 3 }

[Flags]
public enum RexSearchFlags : uint { None = 0, MatchCase = 1, Regex = 2 }

/// <summary>Why a query cannot be searched for (rex_frame.search_error).</summary>
public enum RexSearchError : byte { None = 0, Invalid = 1, TooComplex = 2 }

[Flags]
public enum RexCellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Faint = 1 << 2,
    Blink = 1 << 3,
    Strike = 1 << 4,
    Overline = 1 << 5,
    UnderlineSingle = 1 << 6,
    UnderlineDouble = 1 << 7,
    UnderlineCurly = 1 << 8,
    UnderlineDotted = 1 << 9,
    UnderlineDashed = 1 << 10,
    Wide = 1 << 11,
    Spacer = 1 << 12,
    Selected = 1 << 13,
    Link = 1 << 14,
    DefaultBg = 1 << 15,
    AnyUnderline = UnderlineSingle | UnderlineDouble | UnderlineCurly | UnderlineDotted | UnderlineDashed,
}

[StructLayout(LayoutKind.Sequential)]
public struct RexCell
{
    public uint Fg;        // 0x00RRGGBB
    public uint Bg;        // 0x00RRGGBB
    public uint UlColor;   // 0x00RRGGBB or 0xFFFFFFFF = use fg
    public uint TextOff;   // UTF-16 offset into frame text pool
    public ushort TextLen; // UTF-16 units; 0 = blank
    public RexCellFlags Flags;
}

/// <summary>One viewport row's part of a find-in-terminal match (a match across a wrap has one per row).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RexMatch
{
    public ushort Row;          // viewport row
    public ushort Col0, Col1;   // first and last cell, inclusive
    public ushort Current;      // 1: part of the current match
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RexFrame
{
    public uint Abi;
    public ushort Cols, Rows;
    public RexCell* Cells;
    public char* Text;
    public uint TextLen;
    public uint Fg, Bg;
    public uint CursorColor;
    public short CursorX, CursorY;
    public RexCursorShape CursorShape;
    public byte CursorBlink;
    public byte MouseTracking;
    public byte AltScreen;
    public byte Paused;
    public byte BracketedPaste;
    public byte FocusReporting;
    public byte Pad;
    public ulong Generation;
    public ulong TotalRows;
    public ulong ViewportTop;
    public byte* Wrapped;
    public byte* Title; // UTF-8, NUL-terminated
    public byte* Pwd;   // UTF-8, NUL-terminated
    // Find in terminal (rex_pane_search); all zero when not searching.
    public RexMatch* Matches;   // MatchCount spans on the viewport, top to bottom
    public uint MatchCount;
    public uint SearchTotal;    // matches found so far (screen and scrollback)
    public uint SearchCurrent;  // 1-based place of the current match counting from the newest (bottom); 0 none
    public byte SearchActive;   // a query is set
    public byte SearchBusy;     // scrollback still being read: counts may grow
    public RexSearchError SearchError; // the query cannot be searched for (no matches)
    public byte Pad2;
}

[StructLayout(LayoutKind.Sequential)]
public struct RexKey
{
    public nint Code;   // UTF-8 char*
    public nint Key;    // UTF-8 char* or 0
    public nint Text;   // UTF-8 char* or 0
    public RexMods Mods;
    public RexKeyAction Action;
}

[StructLayout(LayoutKind.Sequential)]
public struct RexMouse
{
    public RexMouseAction Action;
    public RexMouseButton Button;
    public RexMods Mods;
    public float X, Y;
    public float WheelDy;
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate void RexEventCallback(nint user, int kind, ulong pane, nint json, nuint jsonLen);

public static unsafe partial class RexCoreNative
{
    public const string Dll = "rexcore";
    public const uint AbiVersion = 2;

    [LibraryImport(Dll, EntryPoint = "rex_core_new", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint CoreNew(string optionsJson, nint callback, nint user);

    [LibraryImport(Dll, EntryPoint = "rex_core_free")]
    public static partial void CoreFree(nint core);

    [LibraryImport(Dll, EntryPoint = "rex_core_abi_version")]
    public static partial uint CoreAbiVersion();

    [LibraryImport(Dll, EntryPoint = "rex_string_free")]
    public static partial void StringFree(nint s);

    [LibraryImport(Dll, EntryPoint = "rex_core_command", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int CoreCommand(nint core, string commandJson);

    [LibraryImport(Dll, EntryPoint = "rex_pane_key")]
    public static partial int PaneKey(nint core, ulong pane, RexKey* key);

    [LibraryImport(Dll, EntryPoint = "rex_pane_text", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int PaneText(nint core, ulong pane, string utf8);

    [LibraryImport(Dll, EntryPoint = "rex_pane_paste", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int PanePaste(nint core, ulong pane, string utf8);

    [LibraryImport(Dll, EntryPoint = "rex_pane_mouse")]
    public static partial int PaneMouse(nint core, ulong pane, RexMouse* mouse);

    [LibraryImport(Dll, EntryPoint = "rex_pane_resize")]
    public static partial int PaneResize(nint core, ulong pane, ushort cols, ushort rows, uint cellWidthPx, uint cellHeightPx);

    [LibraryImport(Dll, EntryPoint = "rex_pane_focus")]
    public static partial int PaneFocus(nint core, ulong pane, int focused);

    [LibraryImport(Dll, EntryPoint = "rex_pane_scroll")]
    public static partial int PaneScroll(nint core, ulong pane, int op, long value);

    [LibraryImport(Dll, EntryPoint = "rex_pane_select")]
    public static partial int PaneSelect(nint core, ulong pane, int op, int clicks, float x, float y);

    [LibraryImport(Dll, EntryPoint = "rex_pane_selection_text")]
    public static partial nint PaneSelectionText(nint core, ulong pane);

    [LibraryImport(Dll, EntryPoint = "rex_pane_hyperlink_at")]
    public static partial nint PaneHyperlinkAt(nint core, ulong pane, int col, int row);

    /// <summary>0 ok, -1 unknown pane, -3 invalid regular expression (the search is cleared). Null or "" clears.</summary>
    [LibraryImport(Dll, EntryPoint = "rex_pane_search", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int PaneSearch(nint core, ulong pane, string? query, uint flags);

    [LibraryImport(Dll, EntryPoint = "rex_pane_search_step")]
    public static partial int PaneSearchStep(nint core, ulong pane, int direction);

    [LibraryImport(Dll, EntryPoint = "rex_pane_set_default_cursor")]
    public static partial int PaneSetDefaultCursor(nint core, ulong pane, int shape);

    [LibraryImport(Dll, EntryPoint = "rex_pane_frame_lock")]
    public static partial RexFrame* PaneFrameLock(nint core, ulong pane);

    [LibraryImport(Dll, EntryPoint = "rex_pane_frame_unlock")]
    public static partial void PaneFrameUnlock(nint core, ulong pane);

    [LibraryImport(Dll, EntryPoint = "rex_local_pane_new")]
    public static partial ulong LocalPaneNew(nint core, ushort cols, ushort rows, int echo);

    [LibraryImport(Dll, EntryPoint = "rex_local_pane_feed")]
    public static partial int LocalPaneFeed(nint core, ulong pane, byte* bytes, nuint len);

    [LibraryImport(Dll, EntryPoint = "rex_local_pane_close")]
    public static partial void LocalPaneClose(nint core, ulong pane);

    /// <summary>Takes ownership of a core-allocated string and frees it.</summary>
    public static string? TakeString(nint s)
    {
        if (s == 0) return null;
        try { return Marshal.PtrToStringUTF8(s); }
        finally { StringFree(s); }
    }

    public static string Utf8Z(byte* p) => p == null ? string.Empty : Marshal.PtrToStringUTF8((nint)p) ?? string.Empty;
}
