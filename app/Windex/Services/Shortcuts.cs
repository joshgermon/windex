// App keyboard shortcuts (design §11): Windows Terminal conventions, so plain Ctrl+letter keeps
// reaching programs. The window consumes these keys first (PreviewKeyDown); everything else goes
// to the focused pane.
using Windows.System;

namespace Windex.Services;

public readonly record struct KeyChord(VirtualKeyModifiers Modifiers, VirtualKey Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
        if (Modifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Keycap labels for palette rows: ["Ctrl", "Shift", "D"].</summary>
    public IReadOnlyList<string> Caps => ToString().Split('+');

    private static string KeyName(VirtualKey key) => key switch
    {
        (VirtualKey)188 => ",",
        (VirtualKey)219 => "[",
        (VirtualKey)221 => "]",
        (VirtualKey)187 => "Plus",
        (VirtualKey)189 => "Minus",
        VirtualKey.Enter => "Enter",
        VirtualKey.Left => "Left",
        VirtualKey.Right => "Right",
        VirtualKey.Up => "Up",
        VirtualKey.Down => "Down",
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)key - (int)VirtualKey.Number0).ToString(),
        _ => key.ToString(),
    };
}

public sealed record ShortcutDef(string Id, string Group, string Label, KeyChord[] Keys);

public static class Shortcuts
{
    private const VirtualKeyModifiers C = VirtualKeyModifiers.Control;
    private const VirtualKeyModifiers S = VirtualKeyModifiers.Shift;
    private const VirtualKeyModifiers A = VirtualKeyModifiers.Menu;

    private static KeyChord K(VirtualKeyModifiers m, VirtualKey k) => new(m, k);
    private static KeyChord K(VirtualKeyModifiers m, int vk) => new(m, (VirtualKey)vk);

    public static readonly IReadOnlyList<ShortcutDef> All =
    [
        new("new-tab", "Tabs", "New tab", [K(C | S, VirtualKey.T)]),
        new("close-tab", "Tabs", "Close tab", [K(C, VirtualKey.F4)]),
        new("next-tab", "Tabs", "Next tab", [K(C, VirtualKey.Tab)]),
        new("prev-tab", "Tabs", "Previous tab", [K(C | S, VirtualKey.Tab)]),
        new("rename-tab", "Tabs", "Rename tab", [K(VirtualKeyModifiers.None, VirtualKey.F2)]),
        new("split-right", "Panes", "Split right", [K(C | S, VirtualKey.D), K(A | S, 187)]),
        new("split-down", "Panes", "Split down", [K(C | S, VirtualKey.E), K(A | S, 189)]),
        new("close-pane", "Panes", "Close pane", [K(C | S, VirtualKey.W)]),
        new("zoom-pane", "Panes", "Zoom or restore pane", [K(C | S, VirtualKey.Enter), K(C | S, VirtualKey.Z)]),
        new("find", "Panes", "Find in terminal", [K(C | S, VirtualKey.F)]),
        new("focus-left", "Panes", "Focus pane left", [K(A, VirtualKey.Left)]),
        new("focus-right", "Panes", "Focus pane right", [K(A, VirtualKey.Right)]),
        new("focus-up", "Panes", "Focus pane up", [K(A, VirtualKey.Up)]),
        new("focus-down", "Panes", "Focus pane down", [K(A, VirtualKey.Down)]),
        new("resize-left", "Panes", "Move divider left", [K(A | S, VirtualKey.Left)]),
        new("resize-right", "Panes", "Move divider right", [K(A | S, VirtualKey.Right)]),
        new("resize-up", "Panes", "Move divider up", [K(A | S, VirtualKey.Up)]),
        new("resize-down", "Panes", "Move divider down", [K(A | S, VirtualKey.Down)]),
        new("switch-session", "Sessions", "Switch session", [K(C | S, VirtualKey.K)]),
        new("new-session", "Sessions", "New session", [K(C | S, VirtualKey.N)]),
        new("next-session", "Sessions", "Next session", [K(C | S, 221)]),
        new("prev-session", "Sessions", "Previous session", [K(C | S, 219)]),
        new("palette", "App", "Command palette", [K(C | S, VirtualKey.P)]),
        new("toggle-sidebar", "Window", "Toggle sidebar", [K(C | S, VirtualKey.B)]),
        new("activity", "Window", "Activity", [K(C | S, VirtualKey.A)]),
        new("fullscreen", "Window", "Full screen", [K(VirtualKeyModifiers.None, VirtualKey.F11), K(A, VirtualKey.Enter)]),
        new("cycle-focus", "Navigation", "Move focus to the next area", [K(C, VirtualKey.F6)]),
        new("cycle-focus-back", "Navigation", "Move focus to the previous area", [K(C | S, VirtualKey.F6)]),
        new("settings", "App", "Settings", [K(C, 188)]),
        new("font-bigger", "Window", "Bigger text", [K(C, 187)]),
        new("font-smaller", "Window", "Smaller text", [K(C, 189)]),
        new("font-reset", "Window", "Reset text size", [K(C, VirtualKey.Number0)]),
        .. Enumerable.Range(1, 9).Select(i => new ShortcutDef($"tab-{i}", "Tabs", i == 9 ? "Go to last tab" : $"Go to tab {i}", [K(C | A, (VirtualKey)((int)VirtualKey.Number0 + i))])),
    ];

    private static readonly KeyChord CtrlK = new(C, VirtualKey.K);

    public static ShortcutDef? Get(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>"Ctrl+Shift+D" for tooltips and menus, or null when unbound.</summary>
    public static string? Text(string id) => Primary(id)?.ToString();

    public static KeyChord? Primary(string id) => Get(id) is { Keys.Length: > 0 } s ? s.Keys[0] : null;

    /// <summary>The action bound to a key press, if any.</summary>
    public static string? Match(VirtualKeyModifiers modifiers, VirtualKey key, bool ctrlKSwitcher)
    {
        // Left/right variants and numpad +/- map onto the table's keys.
        if (key == VirtualKey.Add) key = (VirtualKey)187;
        if (key == VirtualKey.Subtract) key = (VirtualKey)189;
        var chord = new KeyChord(modifiers & (C | S | A), key);
        if (ctrlKSwitcher && chord == CtrlK) return "switch-session";
        foreach (var s in All)
            foreach (var k in s.Keys)
                if (k == chord) return s.Id;
        return null;
    }
}
