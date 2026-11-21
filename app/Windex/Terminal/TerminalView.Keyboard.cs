// Keyboard, text and IME input.
//
// Keyboard focus lives in a borderless, transparent TextBox (the input sink) parked at the
// terminal cursor. It is the text path: Windows applies the layout, dead keys and AltGr before a
// character reaches it, and IMEs compose in it, so their candidate window opens at the cursor.
// Its text is never kept: every insertion is cancelled and forwarded to the core.
//
//   PreviewKeyDown  every key is described in W3C terms (KeyboardMap) and offered to the shell's
//                   ShortcutFilter, then to the terminal's own clipboard shortcuts. Keys that do
//                   not type (arrows, Tab, Enter, F-keys, Ctrl/Alt chords) go to Core.Key at once
//                   and are marked handled, so XAML never turns Tab or arrows into focus moves.
//                   A key that types waits as "pending" until its text arrives.
//   KeyDown         typing keys (and dead keys) are deliberately left unhandled as they bubble:
//                   when any element marks such a KeyDown handled, WinUI drops the character of
//                   some keys (on a US layout ` [ ] \ and ', shifted or not), and possibly the
//                   dead-key state. Hosts must therefore ignore unhandled KeyDown from a terminal;
//                   app shortcuts belong in ShortcutFilter or a PreviewKeyDown handler.
//   BeforeTextChanging  the text the pending key typed (after dead keys and AltGr) completes it;
//                   text without a key (IME results, the emoji panel, injected text) goes to Core.Text.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Interop;
using Windows.System;

namespace Windex.Terminal;

public sealed partial class TerminalView
{
    private const VirtualKey ImeProcessKey = (VirtualKey)229, PacketKey = (VirtualKey)231;

    private readonly TextBox _input;
    /// <summary>Codes whose press reached the core; only those get a release.</summary>
    private readonly HashSet<string> _pressed = new();
    private KeyInput? _pending;
    private bool _composing, _clearingInput;
    private char? _heldSurrogate;

    private static TextBox CreateInputSink()
    {
        // Only the text host: no border, background, placeholder, delete button or focus visual.
        const string template = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TextBox">
                <Border Background="{TemplateBinding Background}">
                    <ScrollViewer x:Name="ContentElement" Padding="0" Margin="0" IsTabStop="False" ZoomMode="Disabled"
                                  HorizontalScrollMode="Disabled" VerticalScrollMode="Disabled"
                                  HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="Hidden" />
                </Border>
            </ControlTemplate>
            """;
        var box = new TextBox
        {
            Template = (ControlTemplate)XamlReader.Load(template),
            AcceptsReturn = false,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            IsTextScaleFactorEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            MinWidth = 1,
            MinHeight = 0,
            UseSystemFocusVisuals = false,
            IsTabStop = true,
            Opacity = 0,
        };
        AutomationProperties.SetName(box, "Terminal input");
        return box;
    }

    private void InitializeInput()
    {
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown), true);
        AddHandler(PreviewKeyUpEvent, new KeyEventHandler(OnPreviewKeyUp), true);
    }

    /// <summary>The input box's handlers are attached only while the view is in the tree: XAML and
    /// accessibility clients keep the text box that last had focus for a while after it is gone,
    /// and through these handlers it would keep the whole view.</summary>
    private void HookInputSink(bool hook)
    {
        if (hook)
        {
            _input.GotFocus += OnInputGotFocus;
            _input.LostFocus += OnInputLostFocus;
            _input.BeforeTextChanging += OnInputBeforeTextChanging;
            _input.TextCompositionStarted += OnTextCompositionStarted;
            _input.TextCompositionEnded += OnTextCompositionEnded;
        }
        else
        {
            _input.GotFocus -= OnInputGotFocus;
            _input.LostFocus -= OnInputLostFocus;
            _input.BeforeTextChanging -= OnInputBeforeTextChanging;
            _input.TextCompositionStarted -= OnTextCompositionStarted;
            _input.TextCompositionEnded -= OnTextCompositionEnded;
            // A composition cut short by leaving the tree never ends: start clean next time.
            _composing = false;
            _input.Opacity = 0;
            _input.Text = "";
        }
    }

    private void OnInputGotFocus(object sender, RoutedEventArgs e) => SetKeyboardFocus(true);

    private void OnInputLostFocus(object sender, RoutedEventArgs e) => SetKeyboardFocus(false);

    private void OnTextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _composing = true;
        _pending = null;
        _input.Opacity = 1;
    }

    private void SetKeyboardFocus(bool focused)
    {
        if (_hasFocus == focused) return;
        _hasFocus = focused;
        if (!focused)
        {
            FlushPendingKey();
            _pressed.Clear();
        }
        if (Core is { } core && Pane != 0) core.Focus(Pane, focused);
        OnFocusLookChanged();
        if (focused) GotPaneFocus?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateInputSinkFont()
    {
        if (_fonts is null) return;
        _input.FontFamily = new FontFamily(_fonts.Family);
        _input.FontSize = TerminalFontSize * 96.0 / 72.0;
        _input.Height = _fonts.CellHeight / _scale;
    }

    /// <summary>Parks the input box on the cursor cell, where the IME draws its composition and candidates.</summary>
    private void PositionInputSink()
    {
        if (_fonts is null || _frame.CursorX < 0 || _frame.CursorY < 0) return;
        Canvas.SetLeft(_input, (_originX + _frame.CursorX * _fonts.CellWidth) / _scale);
        Canvas.SetTop(_input, (_originY + _frame.CursorY * _fonts.CellHeight) / _scale);
    }

    // ------------------------------------------------------------------ keys

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Keys typed into the find bar are the bar's.
        if (FindBarHas(e.OriginalSource)) return;
        FlushPendingKey();
        // The IME owns keys while it composes; VK_PACKET carries injected text, which arrives as text.
        if (e.Handled || _composing || e.Key is ImeProcessKey or PacketKey) return;
        var facts = KeyboardMap.Describe(e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
        // A key with no meaning on this layout (injected characters, media keys) is not a terminal
        // key; whatever text it types still arrives through the input box.
        if (facts.Key == "Unidentified") return;
        var mods = facts.Mods & (RexMods.Shift | RexMods.Ctrl | RexMods.Alt | RexMods.Super);
        // Alt+F4 and Alt+Space belong to the window.
        if (mods == RexMods.Alt && facts.Code is "F4" or "Space") return;

        var action = e.KeyStatus.WasKeyDown ? RexKeyAction.Repeat : RexKeyAction.Press;
        // Alt chords carry the character the key types (the core prefixes ESC); Ctrl chords do not.
        string? text = facts.Printable || facts.AltGr || (mods & RexMods.Alt) != 0 && (mods & RexMods.Ctrl) == 0 ? facts.LayoutText : null;
        var key = new KeyInput(facts.Code, facts.Key, text, facts.Mods, action);

        // While the find bar is open, F3 / Shift+F3 step through its matches from the terminal too.
        if (_findBar.IsOpen && facts.Code == "F3" && (mods & ~RexMods.Shift) == 0)
        {
            e.Handled = true;
            if ((mods & RexMods.Shift) != 0) FindPrevious();
            else FindNext();
            return;
        }
        if (ShortcutFilter?.Invoke(key) == true || HandleClipboardShortcut(key))
        {
            e.Handled = true;
            return;
        }
        if (Core is null || Pane == 0) return;
        ResetBlink();
        if (facts.Dead || facts.Printable)
        {
            // Left to the input box (see the KeyDown note at the top). A dead key types nothing
            // itself: the next key's text carries the composed character.
            if (facts.Printable) _pending = key with { Text = null };
            return;
        }
        e.Handled = true;
        SendKey(key);
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_composing || e.Key is ImeProcessKey or PacketKey || FindBarHas(e.OriginalSource)) return;
        var facts = KeyboardMap.Describe(e.Key, e.KeyStatus.ScanCode, e.KeyStatus.IsExtendedKey);
        if (_pending is { } pending && pending.Code == facts.Code) FlushPendingKey();
        if (!_pressed.Remove(facts.Code)) return;
        e.Handled = true;
        Core?.Key(Pane, new KeyInput(facts.Code, facts.Key, null, facts.Mods, RexKeyAction.Release));
    }

    /// <summary>Terminal clipboard keys (after the shell's filter, design §11): Ctrl+Shift+C / Ctrl+Insert
    /// copy, Ctrl+V / Ctrl+Shift+V / Shift+Insert paste, and Ctrl+C copies while there is a selection
    /// (else it is ^C).</summary>
    private bool HandleClipboardShortcut(KeyInput key)
    {
        if (key.Action == RexKeyAction.Release || Core is null || Pane == 0) return false;
        var mods = key.Mods & (RexMods.Shift | RexMods.Ctrl | RexMods.Alt | RexMods.Super);
        switch (key.Code)
        {
            case "KeyC" when mods == (RexMods.Ctrl | RexMods.Shift):
            case "Insert" when mods == RexMods.Ctrl:
                Copy();
                return true;
            case "KeyV" when mods == RexMods.Ctrl:
            case "KeyV" when mods == (RexMods.Ctrl | RexMods.Shift):
            case "Insert" when mods == RexMods.Shift:
                Paste();
                return true;
            case "KeyC" when mods == RexMods.Ctrl && key.Action == RexKeyAction.Press:
                if (!Copy()) return false;
                Core.Select(Pane, RexSelectOp.Clear, 0, 0, 0);
                return true;
        }
        return false;
    }

    private void SendKey(KeyInput key)
    {
        if (Core is not { } core || Pane == 0) return;
        if (core.Key(Pane, key) < 0) return;
        if (key.Action != RexKeyAction.Release) _pressed.Add(key.Code);
        if (key.Key is not ("Shift" or "Control" or "Alt" or "Meta" or "CapsLock" or "NumLock")) ScrollToBottomIfScrolled();
    }

    /// <summary>A typing key whose text never came (nothing was inserted): it still was a key press.</summary>
    private void FlushPendingKey()
    {
        if (_pending is not { } pending) return;
        _pending = null;
        SendKey(pending);
    }

    // ------------------------------------------------------------------ text

    private void OnInputBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        if (_composing || _clearingInput) return;
        args.Cancel = true;
        string old = sender.Text, now = args.NewText;
        string inserted = now.StartsWith(old, StringComparison.Ordinal) ? now[old.Length..] : now;
        // Injected text can arrive one UTF-16 unit at a time: hold a high surrogate for its partner.
        if (_heldSurrogate is { } high)
        {
            inserted = high + inserted;
            _heldSurrogate = null;
        }
        if (inserted.Length > 0 && char.IsHighSurrogate(inserted[^1]))
        {
            _heldSurrogate = inserted[^1];
            inserted = inserted[..^1];
        }
        if (inserted.Length == 0) return;
        if (_pending is { } pending)
        {
            _pending = null;
            bool oneCharacter = inserted.Length == 1 || inserted.Length == 2 && char.IsSurrogatePair(inserted, 0);
            SendKey(pending with { Text = inserted, Key = oneCharacter ? inserted : pending.Key });
        }
        else
        {
            SendText(inserted);
        }
    }

    private void OnTextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _composing = false;
        _input.Opacity = 0;
        var text = sender.Text;
        int start = Math.Clamp(args.StartIndex, 0, text.Length);
        int length = Math.Clamp(args.Length, 0, text.Length - start);
        if (length > 0) SendText(text.Substring(start, length));
        // Empty the box after the IME has finished with it.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_composing) return;
            _clearingInput = true;
            _input.Text = "";
            _clearingInput = false;
        });
    }

    private void SendText(string text)
    {
        if (Core is not { } core || Pane == 0) return;
        if (core.Text(Pane, text) >= 0) ScrollToBottomIfScrolled();
    }
}
