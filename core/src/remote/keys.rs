//! UI key events (W3C `KeyboardEvent.code` + layout text) → Rex `client.key` payloads
//! (docs/protocol.md §5.1). The server's Ghostty encodes them for the program's modes; the
//! client never writes escape sequences itself.
//!
//! The code naming and text rules follow Rex Rune's `src/app/keys.rs` (MIT).
use crate::core::{KeyEvent, mods};
use serde_json::{Value, json};

/// Every key name the server accepts (LLMS `client.key`), except `unidentified`.
const KEY_NAMES: &[&str] = &[
    "backquote", "backslash", "bracket_left", "bracket_right", "comma", "digit_0", "digit_1", "digit_2",
    "digit_3", "digit_4", "digit_5", "digit_6", "digit_7", "digit_8", "digit_9", "equal", "intl_backslash",
    "intl_ro", "intl_yen", "key_a", "key_b", "key_c", "key_d", "key_e", "key_f", "key_g", "key_h", "key_i",
    "key_j", "key_k", "key_l", "key_m", "key_n", "key_o", "key_p", "key_q", "key_r", "key_s", "key_t",
    "key_u", "key_v", "key_w", "key_x", "key_y", "key_z", "minus", "period", "quote", "semicolon", "slash",
    "alt_left", "alt_right", "backspace", "caps_lock", "context_menu", "control_left", "control_right",
    "enter", "meta_left", "meta_right", "shift_left", "shift_right", "space", "tab", "convert", "kana_mode",
    "non_convert", "delete", "end", "help", "home", "insert", "page_down", "page_up", "arrow_down",
    "arrow_left", "arrow_right", "arrow_up", "num_lock", "numpad_0", "numpad_1", "numpad_2", "numpad_3",
    "numpad_4", "numpad_5", "numpad_6", "numpad_7", "numpad_8", "numpad_9", "numpad_add",
    "numpad_backspace", "numpad_clear", "numpad_clear_entry", "numpad_comma", "numpad_decimal",
    "numpad_divide", "numpad_enter", "numpad_equal", "numpad_memory_add", "numpad_memory_clear",
    "numpad_memory_recall", "numpad_memory_store", "numpad_memory_subtract", "numpad_multiply",
    "numpad_paren_left", "numpad_paren_right", "numpad_subtract", "numpad_separator", "numpad_up",
    "numpad_down", "numpad_right", "numpad_left", "numpad_begin", "numpad_home", "numpad_end",
    "numpad_insert", "numpad_delete", "numpad_page_up", "numpad_page_down", "escape", "f1", "f2", "f3",
    "f4", "f5", "f6", "f7", "f8", "f9", "f10", "f11", "f12", "f13", "f14", "f15", "f16", "f17", "f18",
    "f19", "f20", "f21", "f22", "f23", "f24", "f25", "fn", "fn_lock", "print_screen", "scroll_lock",
    "pause", "browser_back", "browser_favorites", "browser_forward", "browser_home", "browser_refresh",
    "browser_search", "browser_stop", "eject", "launch_app_1", "launch_app_2", "launch_mail",
    "media_play_pause", "media_select", "media_stop", "media_track_next", "media_track_previous", "power",
    "sleep", "audio_volume_down", "audio_volume_mute", "audio_volume_up", "wake_up", "copy", "cut", "paste",
];

/// Rex's name for a W3C code: `KeyA` → `key_a`, `Digit1` → `digit_1`, `ArrowUp` → `arrow_up`,
/// `Numpad0` → `numpad_0`, `F12` → `f12`, `NumpadEnter` → `numpad_enter`. `None` for codes the
/// server doesn't know.
pub fn code_name(code: &str) -> Option<&'static str> {
    // Older browser spellings still seen in the wild.
    let code = match code {
        "OSLeft" => "MetaLeft",
        "OSRight" => "MetaRight",
        "VolumeUp" => "AudioVolumeUp",
        "VolumeDown" => "AudioVolumeDown",
        "VolumeMute" => "AudioVolumeMute",
        other => other,
    };
    let name = if let Some(n) = code.strip_prefix('F').filter(|n| !n.is_empty() && n.bytes().all(|b| b.is_ascii_digit())) {
        format!("f{n}")
    } else {
        snake(code)
    };
    KEY_NAMES.iter().copied().find(|k| *k == name)
}

fn snake(code: &str) -> String {
    let mut out = String::with_capacity(code.len() + 4);
    let mut previous: Option<char> = None;
    for ch in code.chars() {
        if let Some(p) = previous
            && ((ch.is_ascii_uppercase() && (p.is_ascii_lowercase() || p.is_ascii_digit()))
                || (ch.is_ascii_digit() && p.is_ascii_alphabetic()))
        {
            out.push('_');
        }
        out.push(ch.to_ascii_lowercase());
        previous = Some(ch);
    }
    out
}

/// `mods` spelling: `shift`, `ctrl`, `alt`, `super` joined with `+`, in that order.
pub fn mods_name(m: u32) -> String {
    [(mods::SHIFT, "shift"), (mods::CTRL, "ctrl"), (mods::ALT, "alt"), (mods::SUPER, "super")]
        .into_iter()
        .filter(|(bit, _)| m & bit != 0)
        .map(|(_, name)| name)
        .collect::<Vec<_>>()
        .join("+")
}

/// What an unshifted press of `code` types on a US layout, for keys whose layout text we lack.
fn us_unshifted(name: &str) -> Option<char> {
    if let Some(c) = name.strip_prefix("key_").filter(|c| c.len() == 1) {
        return c.chars().next();
    }
    if let Some(d) = name.strip_prefix("digit_") {
        return d.chars().next();
    }
    Some(match name {
        "backquote" => '`',
        "minus" => '-',
        "equal" => '=',
        "bracket_left" => '[',
        "bracket_right" => ']',
        "backslash" => '\\',
        "semicolon" => ';',
        "quote" => '\'',
        "comma" => ',',
        "period" => '.',
        "slash" => '/',
        "space" => ' ',
        _ => return None,
    })
}

/// The key's own character without Shift: from the layout's `key` when it is one character
/// (lower-cased for letters), else the US position.
fn unshifted(name: &str, key: Option<&str>, shift: bool) -> Option<char> {
    let mut chars = key.unwrap_or("").chars();
    if let (Some(c), None) = (chars.next(), chars.next())
        && !c.is_control()
    {
        if c.is_alphabetic() {
            return c.to_lowercase().next();
        }
        if !shift {
            return Some(c);
        }
    }
    us_unshifted(name)
}

/// The `key` object of a `client.key` request, or `None` for a key Rex can't name and that
/// types nothing.
pub fn payload(event: &KeyEvent) -> Option<Value> {
    let release = event.action == 0;
    let action = match event.action {
        0 => "release",
        2 => "repeat",
        _ => "press",
    };
    let m = event.mods;
    let (shift, ctrl, alt, logo) = (m & mods::SHIFT != 0, m & mods::CTRL != 0, m & mods::ALT != 0, m & mods::SUPER != 0);
    let name = code_name(&event.code);
    // Windows AltGr arrives as Ctrl+Alt; text it produced is the layout's, not a chord.
    let layout_text = event.text.as_deref().filter(|t| !t.is_empty() && !t.chars().any(char::is_control));
    let altgr = ctrl && alt && !logo && layout_text.is_some();
    let typed: String = match name {
        Some("enter" | "numpad_enter") if !ctrl && !alt && !logo => "\n".into(),
        Some("tab") if !ctrl && !alt && !logo => "\t".into(),
        _ if altgr => layout_text.unwrap_or_default().into(),
        _ if ctrl || logo => String::new(),
        _ => layout_text.unwrap_or_default().into(),
    };
    let name = match name {
        Some(name) => name,
        None if !typed.is_empty() => "unidentified",
        None => return None,
    };
    let base = unshifted(name, event.key.as_deref(), shift);
    let mut consumed = Vec::new();
    if shift && !typed.is_empty() && typed != "\n" && typed != "\t" {
        let differs = match base {
            Some(b) => b.is_alphabetic() || typed.chars().ne(std::iter::once(b)),
            None => typed != " ",
        };
        if differs {
            consumed.push("shift");
        }
    }
    if altgr {
        consumed.extend(["ctrl", "alt"]);
    }
    Some(json!({
        "key": name,
        "mods": mods_name(m),
        "consumed_mods": consumed.join("+"),
        "text": if release { String::new() } else { typed },
        "unshifted_codepoint": base.map_or(0, u32::from),
        "composing": false,
        "action": action,
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn key(code: &str, key: Option<&str>, text: Option<&str>, mods: u32, action: u32) -> Value {
        payload(&KeyEvent {
            code: code.into(),
            key: key.map(Into::into),
            text: text.map(Into::into),
            mods,
            action,
        })
        .unwrap()
    }

    #[test]
    fn code_names() {
        for (code, name) in [
            ("KeyA", "key_a"),
            ("Digit1", "digit_1"),
            ("ArrowUp", "arrow_up"),
            ("Numpad0", "numpad_0"),
            ("NumpadEnter", "numpad_enter"),
            ("F5", "f5"),
            ("F12", "f12"),
            ("F24", "f24"),
            ("Backquote", "backquote"),
            ("BracketLeft", "bracket_left"),
            ("PageUp", "page_up"),
            ("ControlLeft", "control_left"),
            ("MetaRight", "meta_right"),
            ("OSLeft", "meta_left"),
            ("IntlBackslash", "intl_backslash"),
            ("LaunchApp1", "launch_app_1"),
            ("AudioVolumeUp", "audio_volume_up"),
            ("NumpadClearEntry", "numpad_clear_entry"),
            ("PrintScreen", "print_screen"),
            ("Escape", "escape"),
            ("Space", "space"),
            ("Fn", "fn"),
        ] {
            assert_eq!(code_name(code), Some(name), "{code}");
        }
        assert_eq!(code_name("Lang1"), None);
        assert_eq!(code_name(""), None);
        assert_eq!(code_name("F"), None);
    }

    #[test]
    fn mods_order() {
        assert_eq!(mods_name(0), "");
        assert_eq!(mods_name(mods::SUPER | mods::SHIFT | mods::ALT | mods::CTRL), "shift+ctrl+alt+super");
        assert_eq!(mods_name(mods::CTRL | 16 | 32), "ctrl", "lock states are not sent");
    }

    #[test]
    fn plain_and_shifted_letters() {
        let a = key("KeyA", Some("a"), Some("a"), 0, 1);
        assert_eq!((a["key"].as_str(), a["text"].as_str(), a["consumed_mods"].as_str(), a["unshifted_codepoint"].as_u64()),
            (Some("key_a"), Some("a"), Some(""), Some(97)));
        let shifted = key("KeyA", Some("A"), Some("A"), mods::SHIFT, 1);
        assert_eq!((shifted["mods"].as_str(), shifted["consumed_mods"].as_str(), shifted["text"].as_str(), shifted["unshifted_codepoint"].as_u64()),
            (Some("shift"), Some("shift"), Some("A"), Some(97)));
        let bang = key("Digit1", Some("!"), Some("!"), mods::SHIFT, 1);
        assert_eq!((bang["consumed_mods"].as_str(), bang["unshifted_codepoint"].as_u64()), (Some("shift"), Some(u64::from(b'1'))));
        let space = key("Space", Some(" "), Some(" "), mods::SHIFT, 1);
        assert_eq!(space["consumed_mods"], "", "Shift+Space types what Space does");
    }

    #[test]
    fn control_chords_type_nothing() {
        let c = key("KeyC", Some("c"), Some("\u{3}"), mods::CTRL, 1);
        assert_eq!((c["key"].as_str(), c["mods"].as_str(), c["text"].as_str()), (Some("key_c"), Some("ctrl"), Some("")));
        assert_eq!(c["unshifted_codepoint"], 99);
        let alt_b = key("KeyB", Some("b"), Some("b"), mods::ALT, 1);
        assert_eq!((alt_b["mods"].as_str(), alt_b["text"].as_str(), alt_b["consumed_mods"].as_str()), (Some("alt"), Some("b"), Some("")));
    }

    #[test]
    fn altgr_text_consumes_ctrl_alt() {
        let at = key("KeyQ", Some("@"), Some("@"), mods::CTRL | mods::ALT, 1);
        assert_eq!((at["text"].as_str(), at["consumed_mods"].as_str()), (Some("@"), Some("ctrl+alt")));
        let chord = key("KeyB", Some("b"), None, mods::CTRL | mods::ALT, 1);
        assert_eq!((chord["text"].as_str(), chord["consumed_mods"].as_str()), (Some(""), Some("")));
    }

    #[test]
    fn enter_tab_and_named_keys() {
        assert_eq!(key("Enter", Some("Enter"), Some("\r"), 0, 1)["text"], "\n");
        assert_eq!(key("NumpadEnter", Some("Enter"), None, 0, 1)["key"], "numpad_enter");
        assert_eq!(key("Tab", Some("Tab"), None, 0, 1)["text"], "\t");
        assert_eq!(key("Enter", None, None, mods::CTRL, 1)["text"], "");
        let up = key("ArrowUp", Some("ArrowUp"), None, 0, 2);
        assert_eq!((up["key"].as_str(), up["text"].as_str(), up["action"].as_str(), up["unshifted_codepoint"].as_u64()),
            (Some("arrow_up"), Some(""), Some("repeat"), Some(0)));
        assert_eq!(key("Backspace", None, Some("\u{8}"), 0, 1)["text"], "", "control text is never sent");
    }

    #[test]
    fn releases_carry_no_text() {
        let release = key("KeyE", Some("é"), Some("é"), 0, 0);
        assert_eq!((release["action"].as_str(), release["text"].as_str()), (Some("release"), Some("")));
    }

    #[test]
    fn unknown_codes() {
        let unknown = key("Lang1", None, Some("あ"), 0, 1);
        assert_eq!((unknown["key"].as_str(), unknown["text"].as_str()), (Some("unidentified"), Some("あ")));
        assert!(payload(&KeyEvent { code: "Lang2".into(), action: 1, ..Default::default() }).is_none());
    }

    #[test]
    fn layout_following_unshifted_codepoint() {
        // AZERTY: the KeyQ position types 'a'.
        assert_eq!(key("KeyQ", Some("a"), Some("a"), 0, 1)["unshifted_codepoint"], 97);
        assert_eq!(key("KeyQ", Some("A"), Some("A"), mods::SHIFT, 1)["unshifted_codepoint"], 97);
    }
}
