// Windows keyboard input in W3C KeyboardEvent terms (the rex_key contract):
//   code  the physical key, from the scan code, so it does not depend on the layout;
//   key   what the key means on the current layout ("a", "A", "@", "Dead", "Enter");
//   text  left to the text path (TextBox input), which applies dead keys and AltGr.
using System.Runtime.InteropServices;
using Windex.Interop;
using Windows.System;

namespace Windex.Terminal;

internal static partial class KeyboardMap
{
    /// <summary>A key event's facts as Windows reports them, resolved against the current layout.</summary>
    internal readonly record struct KeyFacts(string Code, string Key, RexMods Mods, bool Printable, bool Dead, bool AltGr, string? LayoutText);

    public static KeyFacts Describe(VirtualKey vk, uint scanCode, bool extended)
    {
        Span<byte> state = stackalloc byte[256];
        GetKeyboardState(state);
        bool shift = Down(state, VK_SHIFT), ctrl = Down(state, VK_CONTROL), alt = Down(state, VK_MENU);
        bool win = Down(state, VK_LWIN) || Down(state, VK_RWIN);
        bool altGr = Down(state, VK_RMENU) && Down(state, VK_LCONTROL);

        var mods = RexMods.None;
        if (shift) mods |= RexMods.Shift;
        if (ctrl) mods |= RexMods.Ctrl;
        if (alt) mods |= RexMods.Alt;
        if (win) mods |= RexMods.Super;
        if ((state[VK_CAPITAL] & 1) != 0) mods |= RexMods.Caps;
        if ((state[VK_NUMLOCK] & 1) != 0) mods |= RexMods.Num;

        if (scanCode == 0)
        {
            // Injected input often carries only a virtual key; recover the physical key.
            uint sc = MapVirtualKeyExW((uint)vk, MAPVK_VK_TO_VSC_EX, GetKeyboardLayout(0));
            scanCode = sc & 0xFF;
            extended = (sc & 0xFF00) is 0xE000 or 0xE100;
        }
        string code = CodeFor(vk, scanCode, extended);

        if (NamedKey(vk) is { } named)
            return new KeyFacts(code, named, mods, false, false, false, null);

        // What the key types on this layout. AltGr keeps Ctrl+Alt (that is how Windows encodes it);
        // otherwise Ctrl and Alt are left out so `key` is the character, as in a browser.
        var hkl = GetKeyboardLayout(0);
        string? withMods = ToUnicode(vk, scanCode, state, hkl, out bool dead);
        if (altGr && withMods is { Length: > 0 } && !TypesNothing(withMods))
        {
            mods &= ~(RexMods.Ctrl | RexMods.Alt);
            return new KeyFacts(code, withMods, mods, true, false, true, withMods);
        }
        if (dead) return new KeyFacts(code, "Dead", mods, false, true, altGr, null);

        state[VK_CONTROL] = state[VK_LCONTROL] = state[VK_RCONTROL] = 0;
        state[VK_MENU] = state[VK_LMENU] = state[VK_RMENU] = 0;
        string? plain = ToUnicode(vk, scanCode, state, hkl, out bool plainDead);
        if (plainDead) return new KeyFacts(code, "Dead", mods, false, true, false, null);
        if (plain is { Length: > 0 } && !TypesNothing(plain))
        {
            // Printable: the text arrives through the input box unless Ctrl/Alt turn it into a chord.
            bool chord = (mods & (RexMods.Ctrl | RexMods.Alt)) != 0;
            return new KeyFacts(code, plain, mods, !chord, false, false, plain);
        }
        return new KeyFacts(code, "Unidentified", mods, false, false, false, null);
    }

    private static bool Down(ReadOnlySpan<byte> state, int vk) => (state[vk] & 0x80) != 0;

    /// <summary>Control characters, and half a surrogate pair (injected text arrives one UTF-16 unit per key).</summary>
    private static bool TypesNothing(string s) => s.Length == 1 && (s[0] < 0x20 || s[0] == 0x7F || char.IsSurrogate(s[0]));

    private static unsafe string? ToUnicode(VirtualKey vk, uint scanCode, Span<byte> state, nint hkl, out bool dead)
    {
        char* buffer = stackalloc char[8];
        // Flag 0x4: leave the keyboard's dead-key state alone (Windows 10 1607+), so the real
        // TranslateMessage still composes the next character.
        int n;
        fixed (byte* s = state) n = ToUnicodeEx((uint)vk, scanCode, s, buffer, 8, 0x4, hkl);
        dead = n < 0;
        return n > 0 ? new string(buffer, 0, n) : null;
    }

    /// <summary>W3C key names for keys that do not type (null for keys that do).</summary>
    private static string? NamedKey(VirtualKey vk) => vk switch
    {
        VirtualKey.Back => "Backspace",
        VirtualKey.Tab => "Tab",
        VirtualKey.Enter => "Enter",
        VirtualKey.Escape => "Escape",
        VirtualKey.PageUp => "PageUp",
        VirtualKey.PageDown => "PageDown",
        VirtualKey.End => "End",
        VirtualKey.Home => "Home",
        VirtualKey.Left => "ArrowLeft",
        VirtualKey.Up => "ArrowUp",
        VirtualKey.Right => "ArrowRight",
        VirtualKey.Down => "ArrowDown",
        VirtualKey.Insert => "Insert",
        VirtualKey.Delete => "Delete",
        VirtualKey.Clear => "Clear",
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "Shift",
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "Control",
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "Alt",
        VirtualKey.LeftWindows or VirtualKey.RightWindows => "Meta",
        VirtualKey.Application => "ContextMenu",
        VirtualKey.CapitalLock => "CapsLock",
        VirtualKey.NumberKeyLock => "NumLock",
        VirtualKey.Scroll => "ScrollLock",
        VirtualKey.Pause => "Pause",
        VirtualKey.Snapshot => "PrintScreen",
        VirtualKey.Help => "Help",
        >= VirtualKey.F1 and <= VirtualKey.F24 => "F" + ((int)vk - (int)VirtualKey.F1 + 1),
        _ => null,
    };

    /// <summary>The W3C code of a scan code (set 1; extended = E0 prefix), with the virtual key as a tiebreak.</summary>
    private static string CodeFor(VirtualKey vk, uint scanCode, bool extended)
    {
        if (vk == VirtualKey.Pause) return "Pause";
        if (vk == VirtualKey.NumberKeyLock) return "NumLock";
        string? code = extended ? Extended(scanCode) : Plain(scanCode);
        return code ?? FromVirtualKey(vk);
    }

    private static readonly string[] Row1 = ["Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "Digit8", "Digit9", "Digit0"];
    private static readonly string[] Qwerty = ["KeyQ", "KeyW", "KeyE", "KeyR", "KeyT", "KeyY", "KeyU", "KeyI", "KeyO", "KeyP"];
    private static readonly string[] Asdf = ["KeyA", "KeyS", "KeyD", "KeyF", "KeyG", "KeyH", "KeyJ", "KeyK", "KeyL"];
    private static readonly string[] Zxcv = ["KeyZ", "KeyX", "KeyC", "KeyV", "KeyB", "KeyN", "KeyM"];

    private static string? Plain(uint sc) => sc switch
    {
        0x01 => "Escape",
        >= 0x02 and <= 0x0B => Row1[sc - 0x02],
        0x0C => "Minus",
        0x0D => "Equal",
        0x0E => "Backspace",
        0x0F => "Tab",
        >= 0x10 and <= 0x19 => Qwerty[sc - 0x10],
        0x1A => "BracketLeft",
        0x1B => "BracketRight",
        0x1C => "Enter",
        0x1D => "ControlLeft",
        >= 0x1E and <= 0x26 => Asdf[sc - 0x1E],
        0x27 => "Semicolon",
        0x28 => "Quote",
        0x29 => "Backquote",
        0x2A => "ShiftLeft",
        0x2B => "Backslash",
        >= 0x2C and <= 0x32 => Zxcv[sc - 0x2C],
        0x33 => "Comma",
        0x34 => "Period",
        0x35 => "Slash",
        0x36 => "ShiftRight",
        0x37 => "NumpadMultiply",
        0x38 => "AltLeft",
        0x39 => "Space",
        0x3A => "CapsLock",
        >= 0x3B and <= 0x44 => "F" + (sc - 0x3B + 1),
        0x45 => "NumLock",
        0x46 => "ScrollLock",
        0x47 => "Numpad7",
        0x48 => "Numpad8",
        0x49 => "Numpad9",
        0x4A => "NumpadSubtract",
        0x4B => "Numpad4",
        0x4C => "Numpad5",
        0x4D => "Numpad6",
        0x4E => "NumpadAdd",
        0x4F => "Numpad1",
        0x50 => "Numpad2",
        0x51 => "Numpad3",
        0x52 => "Numpad0",
        0x53 => "NumpadDecimal",
        0x54 => "PrintScreen",
        0x56 => "IntlBackslash",
        0x57 => "F11",
        0x58 => "F12",
        0x59 => "NumpadEqual",
        >= 0x64 and <= 0x6E => "F" + (sc - 0x64 + 13),
        0x70 => "KanaMode",
        0x71 => "Lang2",
        0x72 => "Lang1",
        0x73 => "IntlRo",
        0x76 => "F24",
        0x79 => "Convert",
        0x7B => "NonConvert",
        0x7D => "IntlYen",
        0x7E => "NumpadComma",
        _ => null,
    };

    private static string? Extended(uint sc) => sc switch
    {
        0x10 => "MediaTrackPrevious",
        0x19 => "MediaTrackNext",
        0x1C => "NumpadEnter",
        0x1D => "ControlRight",
        0x20 => "AudioVolumeMute",
        0x21 => "LaunchApp2",
        0x22 => "MediaPlayPause",
        0x24 => "MediaStop",
        0x2E => "AudioVolumeDown",
        0x30 => "AudioVolumeUp",
        0x32 => "BrowserHome",
        0x35 => "NumpadDivide",
        0x37 => "PrintScreen",
        0x38 => "AltRight",
        0x45 => "NumLock",
        0x46 => "Pause",
        0x47 => "Home",
        0x48 => "ArrowUp",
        0x49 => "PageUp",
        0x4B => "ArrowLeft",
        0x4D => "ArrowRight",
        0x4F => "End",
        0x50 => "ArrowDown",
        0x51 => "PageDown",
        0x52 => "Insert",
        0x53 => "Delete",
        0x5B => "MetaLeft",
        0x5C => "MetaRight",
        0x5D => "ContextMenu",
        0x5E => "Power",
        0x5F => "Sleep",
        0x63 => "WakeUp",
        0x65 => "BrowserSearch",
        0x66 => "BrowserFavorites",
        0x67 => "BrowserRefresh",
        0x68 => "BrowserStop",
        0x69 => "BrowserForward",
        0x6A => "BrowserBack",
        0x6B => "LaunchApp1",
        0x6C => "LaunchMail",
        0x6D => "MediaSelect",
        _ => null,
    };

    /// <summary>Last resort when there is no usable scan code: the US-layout position of a virtual key.</summary>
    private static string FromVirtualKey(VirtualKey vk) => vk switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => "Key" + (char)vk,
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => "Digit" + (char)vk,
        >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => "Numpad" + ((int)vk - (int)VirtualKey.NumberPad0),
        >= VirtualKey.F1 and <= VirtualKey.F24 => "F" + ((int)vk - (int)VirtualKey.F1 + 1),
        VirtualKey.Multiply => "NumpadMultiply",
        VirtualKey.Add => "NumpadAdd",
        VirtualKey.Separator => "NumpadComma",
        VirtualKey.Subtract => "NumpadSubtract",
        VirtualKey.Decimal => "NumpadDecimal",
        VirtualKey.Divide => "NumpadDivide",
        VirtualKey.Back => "Backspace",
        VirtualKey.Tab => "Tab",
        VirtualKey.Enter => "Enter",
        VirtualKey.Escape => "Escape",
        VirtualKey.Space => "Space",
        VirtualKey.PageUp => "PageUp",
        VirtualKey.PageDown => "PageDown",
        VirtualKey.End => "End",
        VirtualKey.Home => "Home",
        VirtualKey.Left => "ArrowLeft",
        VirtualKey.Up => "ArrowUp",
        VirtualKey.Right => "ArrowRight",
        VirtualKey.Down => "ArrowDown",
        VirtualKey.Insert => "Insert",
        VirtualKey.Delete => "Delete",
        VirtualKey.LeftShift or VirtualKey.Shift => "ShiftLeft",
        VirtualKey.RightShift => "ShiftRight",
        VirtualKey.LeftControl or VirtualKey.Control => "ControlLeft",
        VirtualKey.RightControl => "ControlRight",
        VirtualKey.LeftMenu or VirtualKey.Menu => "AltLeft",
        VirtualKey.RightMenu => "AltRight",
        VirtualKey.LeftWindows => "MetaLeft",
        VirtualKey.RightWindows => "MetaRight",
        VirtualKey.Application => "ContextMenu",
        VirtualKey.CapitalLock => "CapsLock",
        VirtualKey.Scroll => "ScrollLock",
        VirtualKey.Snapshot => "PrintScreen",
        (VirtualKey)0xBA => "Semicolon",
        (VirtualKey)0xBB => "Equal",
        (VirtualKey)0xBC => "Comma",
        (VirtualKey)0xBD => "Minus",
        (VirtualKey)0xBE => "Period",
        (VirtualKey)0xBF => "Slash",
        (VirtualKey)0xC0 => "Backquote",
        (VirtualKey)0xDB => "BracketLeft",
        (VirtualKey)0xDC => "Backslash",
        (VirtualKey)0xDD => "BracketRight",
        (VirtualKey)0xDE => "Quote",
        (VirtualKey)0xE2 => "IntlBackslash",
        _ => "Unidentified",
    };

    // ------------------------------------------------------------------ Win32

    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_CAPITAL = 0x14, VK_NUMLOCK = 0x90;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const uint MAPVK_VK_TO_VSC_EX = 4;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKeyboardState(Span<byte> state);

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyExW(uint code, uint mapType, nint hkl);

    [LibraryImport("user32.dll")]
    private static unsafe partial int ToUnicodeEx(uint virtualKey, uint scanCode, byte* keyState, char* buffer, int bufferSize, uint flags, nint hkl);

    [LibraryImport("user32.dll")]
    internal static partial uint GetCaretBlinkTime();

    [LibraryImport("user32.dll")]
    internal static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    internal const int SM_CXDOUBLECLK = 36, SM_CYDOUBLECLK = 37;
}
