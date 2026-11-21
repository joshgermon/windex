//! Render frames: libghostty-vt render state flattened into the `rex_frame` cell buffer.
use crate::search::{QueryError, Search, Summary};
use anyhow::Result;
use ghostty_vt::{
    Terminal,
    render::{CellIterator, CursorVisualStyle, RenderState, RowIterator},
    screen::CellWide,
    style::{StyleColor, Underline},
    terminal::Mode,
};
use std::ffi::{CString, c_char};

pub const ABI_VERSION: u32 = 2;

pub mod flags {
    pub const BOLD: u16 = 1 << 0;
    pub const ITALIC: u16 = 1 << 1;
    pub const FAINT: u16 = 1 << 2;
    pub const BLINK: u16 = 1 << 3;
    pub const STRIKE: u16 = 1 << 4;
    pub const OVERLINE: u16 = 1 << 5;
    pub const UL_SINGLE: u16 = 1 << 6;
    pub const UL_DOUBLE: u16 = 1 << 7;
    pub const UL_CURLY: u16 = 1 << 8;
    pub const UL_DOTTED: u16 = 1 << 9;
    pub const UL_DASHED: u16 = 1 << 10;
    pub const WIDE: u16 = 1 << 11;
    pub const SPACER: u16 = 1 << 12;
    pub const SELECTED: u16 = 1 << 13;
    pub const LINK: u16 = 1 << 14;
    pub const DEFAULT_BG: u16 = 1 << 15;
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RexCell {
    pub fg: u32,
    pub bg: u32,
    pub ul_color: u32,
    pub text_off: u32,
    pub text_len: u16,
    pub flags: u16,
}

/// One row's part of a find-in-terminal match (`rex_match`).
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct RexMatch {
    /// Viewport row.
    pub row: u16,
    /// First and last column on that row (inclusive).
    pub col0: u16,
    pub col1: u16,
    /// 1: part of the current match.
    pub current: u16,
}

#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct RexFrame {
    pub abi: u32,
    pub cols: u16,
    pub rows: u16,
    pub cells: *const RexCell,
    pub text: *const u16,
    pub text_len: u32,
    pub fg: u32,
    pub bg: u32,
    pub cursor_color: u32,
    pub cursor_x: i16,
    pub cursor_y: i16,
    pub cursor_shape: u8,
    pub cursor_blink: u8,
    pub mouse_tracking: u8,
    pub alt_screen: u8,
    pub paused: u8,
    pub bracketed_paste: u8,
    pub focus_reporting: u8,
    pub _pad: u8,
    pub generation: u64,
    pub total_rows: u64,
    pub viewport_top: u64,
    pub wrapped: *const u8,
    pub title: *const c_char,
    pub pwd: *const c_char,
    pub matches: *const RexMatch,
    pub match_count: u32,
    pub search_total: u32,
    pub search_current: u32,
    pub search_active: u8,
    pub search_busy: u8,
    pub search_error: u8,
    pub _pad2: u8,
}

/// An owned frame. `view` points into the vectors below; it is refreshed by `publish`
/// and stays valid while the buffer is neither mutated nor dropped (moving it is fine:
/// heap allocations do not move with the struct).
pub struct FrameBuf {
    pub cells: Vec<RexCell>,
    pub text: Vec<u16>,
    pub wrapped: Vec<u8>,
    pub title: CString,
    pub pwd: CString,
    pub matches: Vec<RexMatch>,
    pub meta: RexFrame,
}

// SAFETY: the raw pointers in `meta` only ever point into this struct's own allocations.
unsafe impl Send for FrameBuf {}

impl Default for FrameBuf {
    fn default() -> Self {
        let mut buf = Self {
            cells: Vec::new(),
            text: Vec::new(),
            wrapped: Vec::new(),
            title: CString::default(),
            pwd: CString::default(),
            matches: Vec::new(),
            meta: RexFrame {
                abi: ABI_VERSION,
                cols: 0,
                rows: 0,
                cells: std::ptr::null(),
                text: std::ptr::null(),
                text_len: 0,
                fg: 0xE8EEF6,
                bg: 0x101418,
                cursor_color: 0xE8EEF6,
                cursor_x: -1,
                cursor_y: -1,
                cursor_shape: 0,
                cursor_blink: 0,
                mouse_tracking: 0,
                alt_screen: 0,
                paused: 0,
                bracketed_paste: 0,
                focus_reporting: 0,
                _pad: 0,
                generation: 0,
                total_rows: 0,
                viewport_top: 0,
                wrapped: std::ptr::null(),
                title: std::ptr::null(),
                pwd: std::ptr::null(),
                matches: std::ptr::null(),
                match_count: 0,
                search_total: 0,
                search_current: 0,
                search_active: 0,
                search_busy: 0,
                search_error: 0,
                _pad2: 0,
            },
        };
        buf.publish();
        buf
    }
}

impl FrameBuf {
    pub fn publish(&mut self) {
        self.meta.abi = ABI_VERSION;
        self.meta.cells = self.cells.as_ptr();
        self.meta.text = self.text.as_ptr();
        self.meta.text_len = self.text.len() as u32;
        self.meta.wrapped = self.wrapped.as_ptr();
        self.meta.title = self.title.as_ptr();
        self.meta.pwd = self.pwd.as_ptr();
        self.meta.matches = self.matches.as_ptr();
        self.meta.match_count = self.matches.len() as u32;
    }

    /// The text of viewport row `row` (for probes and tests), spacer cells skipped.
    pub fn row_text(&self, row: usize) -> String {
        let cols = self.meta.cols as usize;
        let mut out = String::new();
        for cell in self.cells.iter().skip(row * cols).take(cols) {
            if cell.flags & flags::SPACER != 0 {
                continue;
            }
            if cell.text_len == 0 {
                out.push(' ');
            } else {
                let start = cell.text_off as usize;
                out.push_str(&String::from_utf16_lossy(&self.text[start..start + cell.text_len as usize]));
            }
        }
        out.trim_end().to_owned()
    }

    pub fn screen_text(&self) -> String {
        (0..self.meta.rows as usize).map(|r| self.row_text(r)).collect::<Vec<_>>().join("\n")
    }
}

fn rgb(c: ghostty_vt::style::RgbColor) -> u32 {
    (u32::from(c.r) << 16) | (u32::from(c.g) << 8) | u32::from(c.b)
}

fn to_cstring(s: &str) -> CString {
    CString::new(s.replace('\0', "")).unwrap_or_default()
}

pub struct FrameBuilder {
    state: RenderState,
    rows: RowIterator,
    cells: CellIterator,
    grapheme: String,
}

pub struct FrameInputs<'a> {
    pub paused: bool,
    pub generation: u64,
    pub title: &'a str,
    pub pwd: &'a str,
    /// Find in terminal, when the user is searching this pane.
    pub search: Option<&'a Search>,
    /// Why the user's query cannot be searched for (`search` is `None`).
    pub search_error: Option<QueryError>,
}

impl FrameBuilder {
    pub fn new() -> Result<Self> {
        Ok(Self {
            state: RenderState::new()?,
            rows: RowIterator::new()?,
            cells: CellIterator::new()?,
            grapheme: String::new(),
        })
    }

    /// Flatten the terminal's current viewport into `out` (which is then published).
    pub fn build(&mut self, terminal: &Terminal, out: &mut FrameBuf, inputs: FrameInputs<'_>) -> Result<()> {
        let snapshot = self.state.update(terminal)?;
        let colors = snapshot.colors()?;
        let default_fg = rgb(colors.foreground);
        let default_bg = rgb(colors.background);
        let cols = snapshot.cols()?;
        let rows = snapshot.rows()?;

        out.cells.clear();
        out.text.clear();
        out.wrapped.clear();
        out.cells.reserve(usize::from(cols) * usize::from(rows));

        let mut row_iter = self.rows.update(&snapshot)?;
        while let Some(row) = row_iter.next() {
            let selection = row.selection()?.map(|s| (s.start_x, s.end_x));
            let raw_row = row.raw_row()?;
            out.wrapped.push(u8::from(raw_row.is_wrapped()?));
            let row_links = raw_row.has_hyperlink()?;
            let row_start = out.cells.len();
            let mut cells = self.cells.update(row)?;
            let mut x: u16 = 0;
            while let Some(cell) = cells.next() {
                self.grapheme.clear();
                cell.graphemes_utf8(&mut self.grapheme)?;
                let style = cell.style()?;
                let explicit_bg = cell.bg_color()?;
                let mut fg = cell.fg_color()?.map(rgb).unwrap_or(default_fg);
                let mut bg = explicit_bg.map(rgb).unwrap_or(default_bg);
                let mut f: u16 = 0;
                if style.inverse {
                    std::mem::swap(&mut fg, &mut bg);
                } else if explicit_bg.is_none() {
                    f |= flags::DEFAULT_BG;
                }
                if style.bold {
                    f |= flags::BOLD;
                }
                if style.italic {
                    f |= flags::ITALIC;
                }
                if style.faint {
                    f |= flags::FAINT;
                }
                if style.blink {
                    f |= flags::BLINK;
                }
                if style.strikethrough {
                    f |= flags::STRIKE;
                }
                if style.overline {
                    f |= flags::OVERLINE;
                }
                f |= match style.underline {
                    Underline::None => 0,
                    Underline::Single => flags::UL_SINGLE,
                    Underline::Double => flags::UL_DOUBLE,
                    Underline::Curly => flags::UL_CURLY,
                    Underline::Dotted => flags::UL_DOTTED,
                    Underline::Dashed => flags::UL_DASHED,
                    _ => flags::UL_SINGLE,
                };
                let raw = cell.raw_cell()?;
                match raw.wide()? {
                    CellWide::Wide => f |= flags::WIDE,
                    CellWide::SpacerTail | CellWide::SpacerHead => f |= flags::SPACER,
                    CellWide::Narrow => {}
                }
                if row_links && raw.has_hyperlink()? {
                    f |= flags::LINK;
                }
                if let Some((start, end)) = selection
                    && x >= start
                    && x <= end
                {
                    f |= flags::SELECTED;
                }
                let ul_color = match style.underline_color {
                    StyleColor::None => 0xFFFF_FFFF,
                    StyleColor::Palette(index) => rgb(colors.palette[index.0 as usize]),
                    StyleColor::Rgb(color) => rgb(color),
                };
                let text_off = out.text.len() as u32;
                let mut text_len = 0u16;
                if !style.invisible && f & flags::SPACER == 0 {
                    for unit in self.grapheme.encode_utf16() {
                        out.text.push(unit);
                        text_len = text_len.saturating_add(1);
                    }
                }
                out.cells.push(RexCell { fg, bg, ul_color, text_off, text_len, flags: f });
                x += 1;
            }
            // Keep the grid rectangular even if a row came back short.
            while out.cells.len() < row_start + usize::from(cols) {
                out.cells.push(RexCell {
                    fg: default_fg,
                    bg: default_bg,
                    ul_color: 0xFFFF_FFFF,
                    text_off: out.text.len() as u32,
                    text_len: 0,
                    flags: flags::DEFAULT_BG,
                });
            }
            out.cells.truncate(row_start + usize::from(cols));
        }

        let cursor = snapshot.cursor()?;
        let (cx, cy) = match (cursor.visible, cursor.viewport) {
            (true, Some(p)) => (p.x as i16, p.y as i16),
            _ => (-1, -1),
        };
        let scrollbar = terminal.scrollbar().ok();
        let m = &mut out.meta;
        m.cols = cols;
        m.rows = rows;
        m.fg = default_fg;
        m.bg = default_bg;
        m.cursor_color = colors.cursor.map(rgb).unwrap_or(default_fg);
        m.cursor_x = cx;
        m.cursor_y = cy;
        m.cursor_shape = match cursor.visual_style {
            CursorVisualStyle::Bar => 1,
            CursorVisualStyle::Underline => 2,
            CursorVisualStyle::BlockHollow => 3,
            _ => 0,
        };
        m.cursor_blink = u8::from(cursor.blinking);
        m.mouse_tracking = u8::from(terminal.is_mouse_tracking().unwrap_or(false));
        m.alt_screen = u8::from(matches!(terminal.active_screen(), Ok(ghostty_vt::screen::Screen::Alternate)));
        m.paused = u8::from(inputs.paused);
        m.bracketed_paste = u8::from(terminal.mode(Mode::BRACKETED_PASTE).unwrap_or(false));
        m.focus_reporting = u8::from(terminal.mode(Mode::FOCUS_EVENT).unwrap_or(false));
        m.generation = inputs.generation;
        match scrollbar {
            Some(s) => {
                m.total_rows = s.total;
                m.viewport_top = s.offset;
            }
            None => {
                m.total_rows = u64::from(rows);
                m.viewport_top = 0;
            }
        }
        out.matches.clear();
        let summary = match inputs.search {
            Some(search) => search.visible(m.viewport_top, rows, cols, &mut out.matches),
            None => Summary::default(),
        };
        let m = &mut out.meta;
        m.search_active = u8::from(inputs.search.is_some() || inputs.search_error.is_some());
        m.search_error = inputs.search_error.map_or(0, |error| error as u8);
        m.search_total = summary.total;
        m.search_current = summary.current;
        m.search_busy = u8::from(summary.busy);
        out.title = to_cstring(inputs.title);
        out.pwd = to_cstring(inputs.pwd);
        snapshot.clean()?;
        out.publish();
        Ok(())
    }
}
