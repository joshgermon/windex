//! Selection gestures on libghostty-vt. Adapted from Rex Rune (MIT, https://github.com/shuv1337/rex-rune, src/terminal/selection.rs).
use anyhow::Result;
use ghostty_vt::{
    Terminal,
    error::Error,
    selection::{
        FormatOptions,
        gesture::{
            Autoscroll, AutoscrollTickEvent, Behavior, Behaviors, DragEvent, Gesture, PressEvent,
            ReleaseEvent,
        },
    },
    terminal::{Point, PointCoordinate, ScrollViewport},
};
use std::{
    sync::Arc,
    time::{Duration, Instant},
};

pub use ghostty_vt::selection::gesture::Geometry;

pub const COPY_LIMIT: usize = 8 * 1024 * 1024;

#[derive(Clone, Copy, Debug)]
pub enum Phase {
    Down(u32),
    Move,
    Up,
    Clear,
    /// Select everything the terminal holds, scrollback included; `at`, `position` and
    /// `geometry` are unused.
    All,
}

#[derive(Clone, Copy, Debug)]
pub struct Input {
    pub generation: u64,
    pub revision: u64,
    pub phase: Phase,
    pub at: (u16, u16),
    pub position: (f64, f64),
    pub geometry: Geometry,
}

pub struct Controller {
    gesture: Gesture,
    press: PressEvent,
    drag: DragEvent,
    release: ReleaseEvent,
    tick: AutoscrollTickEvent,
    pointer: Option<Input>,
    screen: Option<i32>,
    last_tick: Instant,
    pub revision: u64,
}

impl Controller {
    pub fn new() -> Result<Self> {
        // Match the client's word/path selection: shell path separators stay inside words.
        let boundaries: Vec<char> = " \t\r\n!\"#$&'()*,;<>?[]^`{|}".chars().collect();
        let mut press = PressEvent::new()?;
        press.set_word_boundary_codepoints(&boundaries)?;
        let mut drag = DragEvent::new()?;
        drag.set_word_boundary_codepoints(&boundaries)?;
        let mut tick = AutoscrollTickEvent::new()?;
        tick.set_word_boundary_codepoints(&boundaries)?;
        Ok(Self {
            gesture: Gesture::new()?,
            press,
            drag,
            release: ReleaseEvent::new()?,
            tick,
            pointer: None,
            screen: None,
            last_tick: Instant::now(),
            revision: 0,
        })
    }

    fn point(terminal: &Terminal, input: Input) -> Result<PointCoordinate> {
        Ok(PointCoordinate {
            x: input.at.0.min(terminal.cols()?.saturating_sub(1)),
            y: u32::from(input.at.1.min(terminal.rows()?.saturating_sub(1))),
        })
    }

    pub fn apply(&mut self, terminal: &Terminal, input: Input) -> Result<()> {
        self.revision = input.revision;
        let screen = terminal.active_screen()? as i32;
        if self.screen != Some(screen) {
            self.gesture.reset(terminal);
            self.pointer = None;
            terminal.set_selection(None)?;
            self.screen = Some(screen);
            if !matches!(input.phase, Phase::Down(_) | Phase::All) {
                return Ok(());
            }
        }
        if matches!(input.phase, Phase::Clear | Phase::All) {
            self.gesture.reset(terminal);
            self.pointer = None;
            let all = match input.phase {
                Phase::All => terminal.select_all()?,
                _ => None,
            };
            terminal.set_selection(all.as_ref())?;
            return Ok(());
        }
        let at = terminal.grid_ref(Point::Viewport(Self::point(terminal, input)?))?;
        match input.phase {
            Phase::Down(clicks) => {
                self.gesture.reset(terminal);
                let behavior = match clicks {
                    0 | 1 => Behavior::Cell,
                    2 => Behavior::Word,
                    _ => Behavior::Line,
                };
                self.press
                    .set_behaviors(&Behaviors::new().with_single_click_behavior(behavior))?
                    .set_position(input.position.0, input.position.1)?;
                let selected = self.press.apply(&mut self.gesture, terminal, at)?;
                terminal.set_selection(selected.as_ref())?;
                self.pointer = Some(input);
            }
            Phase::Move => {
                if self.pointer.is_some() {
                    self.drag.set_position(input.position.0, input.position.1)?;
                    let selected =
                        self.drag
                            .apply(&mut self.gesture, terminal, at, input.geometry)?;
                    terminal.set_selection(selected.as_ref())?;
                    self.pointer = Some(input);
                }
            }
            Phase::Up => {
                self.release.apply(&mut self.gesture, terminal, Some(at))?;
                self.pointer = None;
            }
            Phase::Clear | Phase::All => unreachable!(),
        }
        Ok(())
    }

    pub fn after_scroll(&mut self, terminal: &Terminal) -> Result<()> {
        if let Some(mut input) = self.pointer {
            input.phase = Phase::Move;
            self.apply(terminal, input)?;
        }
        Ok(())
    }

    pub fn stop(&mut self, terminal: &Terminal) -> Result<()> {
        if self.pointer.take().is_some() {
            if self.screen == Some(terminal.active_screen()? as i32) {
                self.release.apply(&mut self.gesture, terminal, None)?;
            } else {
                self.gesture.reset(terminal);
            }
        }
        Ok(())
    }

    pub fn autoscrolling(&self, terminal: &Terminal) -> bool {
        self.pointer.is_some()
            && terminal
                .active_screen()
                .ok()
                .is_some_and(|s| self.screen == Some(s as i32))
            && matches!(
                self.gesture.autoscroll(terminal),
                Ok(Autoscroll::Up | Autoscroll::Down)
            )
    }

    pub fn tick(&mut self, terminal: &mut Terminal) -> Result<bool> {
        if self.screen != Some(terminal.active_screen()? as i32) {
            self.stop(terminal)?;
            return Ok(false);
        }
        let Some(input) = self.pointer else {
            return Ok(false);
        };
        if self.last_tick.elapsed() < Duration::from_millis(50) {
            return Ok(false);
        }
        let delta = match self.gesture.autoscroll(terminal)? {
            Autoscroll::Up => -1,
            Autoscroll::Down => 1,
            _ => return Ok(false),
        };
        terminal.scroll_viewport(ScrollViewport::Delta(delta));
        let at = Self::point(terminal, input)?;
        self.tick.set_position(input.position.0, input.position.1)?;
        let selected = self
            .tick
            .apply(&mut self.gesture, terminal, at, input.geometry)?;
        terminal.set_selection(selected.as_ref())?;
        self.last_tick = Instant::now();
        Ok(true)
    }
}

/// Reuse a bounded formatting buffer; copying a large selection cannot allocate without limit.
pub fn text(terminal: &Terminal, buffer: &mut Vec<u8>) -> Result<Option<Arc<str>>> {
    let options = || FormatOptions::new().with_unwrap(true).with_trim(true);
    if buffer.is_empty() {
        buffer.resize(4096, 0)
    }
    let length = match terminal.format_selection_buf(options(), buffer) {
        Err(Error::OutOfSpace { required }) if required <= COPY_LIMIT => {
            buffer.resize(required, 0);
            terminal.format_selection_buf(options(), buffer)?
        }
        Err(Error::OutOfSpace { .. }) => anyhow::bail!("Selection exceeds the 8 MiB copy limit"),
        result => result?,
    };
    length
        .map(|n| {
            std::str::from_utf8(&buffer[..n])
                .map(Arc::from)
                .map_err(Into::into)
        })
        .transpose()
}

