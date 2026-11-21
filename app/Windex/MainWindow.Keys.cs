// Keyboard shortcuts (design §11). The root's PreviewKeyDown sees keys before the focused pane,
// so app shortcuts are consumed here and never reach the terminal; other keys pass through.
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windex.Services;
using Windex.Surfaces;
using Windex.Terminal;
using Windows.System;
using Windows.UI.Core;

namespace Windex;

public sealed partial class MainWindow
{
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var modifiers = Modifiers();
        var key = e.Key == VirtualKey.Menu ? e.OriginalKey : e.Key;
        // AltGr (Right Alt) arrives as Ctrl+Alt: never treat it as a shortcut chord.
        bool altGr = IsDown(VirtualKey.RightMenu) && modifiers.HasFlag(VirtualKeyModifiers.Control);
        string? action = altGr ? null : Shortcuts.Match(modifiers, key, _settings.CtrlKSwitcher);
        if (action is null)
        {
            // Typing into a pane is working in the session on screen.
            if (!IsModifierKey(key) && FocusIsIn(PaneArea)) AdoptCurrent();
            return;
        }
        // Text fields keep their own editing keys (F2 renames only from the tab strip or sidebar).
        if (action == "rename-tab" && !FocusIsIn(Tabs)) return;
        if (_dialogOpen || PalettePopup.IsOpen) return;
        e.Handled = Run(action);
    }

    private static VirtualKeyModifiers Modifiers()
    {
        var m = VirtualKeyModifiers.None;
        if (IsDown(VirtualKey.Control)) m |= VirtualKeyModifiers.Control;
        if (IsDown(VirtualKey.Shift)) m |= VirtualKeyModifiers.Shift;
        if (IsDown(VirtualKey.Menu)) m |= VirtualKeyModifiers.Menu;
        return m;
    }

    private static bool IsModifierKey(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.Menu or VirtualKey.LeftMenu
        or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private bool FocusIsIn(DependencyObject container)
    {
        if (Content.XamlRoot is null) return false;
        for (var n = FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject; n is not null; n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(n))
            if (n == container) return true;
        return false;
    }

    /// <summary>Run a named action; false when it does not apply right now (the key then passes through).</summary>
    public bool Run(string action)
    {
        switch (action)
        {
            case "new-tab": if (CurrentSession is null) return false; _ = NewTabAsync(); return true;
            case "close-tab":
                if (CurrentHost is not { } h || CurrentSession is not { } s || CurrentWindow is not { } w) return false;
                _ = CloseTabAsync(h.Id, s.Id, w.Id); return true;
            case "next-tab": CycleTab(+1); return CurrentSession is not null;
            case "prev-tab": CycleTab(-1); return CurrentSession is not null;
            case "rename-tab": BeginRenameTab(); return true;
            case "split-right": if (FocusedPane is null) return false; SplitFocused("right"); return true;
            case "split-down": if (FocusedPane is null) return false; SplitFocused("down"); return true;
            case "close-pane": if (FocusedPane is not { } p) return false; ClosePane(p); return true;
            case "zoom-pane": if (FocusedPane is not { } z) return false; ToggleZoom(z); return true;
            case "find": return OpenFind(FocusedPane);
            case "focus-left" or "focus-right" or "focus-up" or "focus-down": return FocusDirection(action["focus-".Length..]);
            case "resize-left" or "resize-right" or "resize-up" or "resize-down": return ResizeFocused(action["resize-".Length..]);
            case "switch-session": ShowPalette(PaletteMode.Switcher, anchored: false); return true;
            case "new-session": _ = NewSessionAsync(null); return true;
            case "next-session": CycleSession(+1); return true;
            case "prev-session": CycleSession(-1); return true;
            case "palette": ShowPalette(PaletteMode.Commands, anchored: false); return true;
            case "toggle-sidebar": ToggleSidebar(); return true;
            case "activity": ShowActivity(ActivityButton.Visibility == Visibility.Visible ? ActivityButton : MoreActionsButton); return true;
            case "fullscreen": ToggleFullScreen(); return true;
            case "settings": OpenSettings(null); return true;
            case "font-bigger": AdjustFontSize(+1); return true;
            case "font-smaller": AdjustFontSize(-1); return true;
            case "font-reset": AdjustFontSize(0); return true;
            case "cycle-focus": CycleFocus(+1); return true;
            case "cycle-focus-back": CycleFocus(-1); return true;
            case "rename-pane": BeginRenamePane(); return true;
            case "rename-session": _ = BeginRenameSessionAsync(); return true;
            case "close-session":
                if (CurrentHost is not { } kh || CurrentSession is not { } ks) return false;
                _ = KillSessionAsync(kh.Id, ks.Id); return true;
            case "add-host": ShowAddHost(null); return true;
            case "balance-panes":
                if (CurrentHost is not { } bh || CurrentSession is not { } bs || CurrentWindow is not { } bw) return false;
                _ = Command("balance", new { host = bh.Id, session = bs.Id, window = bw.Id }); return true;
            case "reconnect-host": if (CurrentHost is not { } rh) return false; RetryHost(rh.Id); return true;
            case "refresh-hosts": RefreshHosts(); return true;
        }
        if (action.StartsWith("tab-") && int.TryParse(action[4..], out int n)) { GoToTab(n); return CurrentSession is not null; }
        return false;
    }

    /// <summary>Opens a pane's find bar (Ctrl+Shift+F, the pane menu, the palette); false when the pane is not on screen.</summary>
    private bool OpenFind(ulong? pane)
    {
        if (pane is not { } p || !PaneArea.IsShown(p) || PaneArea.Card(p)?.PaneContent is not TerminalView view) return false;
        view.OpenFind();
        return true;
    }

    /// <summary>Ctrl+F6: tab strip → sidebar → panes (the accessibility path into the chrome).</summary>
    private void CycleFocus(int direction)
    {
        var stops = new Action?[]
        {
            Tabs.SelectedItem is TabViewItem tab ? () => tab.Focus(FocusState.Keyboard) : null,
            SidebarHost.Visibility == Visibility.Visible ? SidebarView.FocusList : null,
            FocusedPane is { } pane ? () => PaneArea.FocusContent(pane) : null,
        };
        int current = FocusIsIn(Tabs) ? 0 : FocusIsIn(SidebarHost) ? 1 : 2;
        for (int step = 1; step <= stops.Length; step++)
        {
            int i = ((current + direction * step) % stops.Length + stops.Length) % stops.Length;
            if (stops[i] is { } go) { go(); return; }
        }
    }
}
