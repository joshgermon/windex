//! Find in terminal (`rex_pane_search`): a query run over a pane's screen and scrollback on the
//! pane's worker thread, a bounded amount of work at a time, and kept up to date as output arrives.
//!
//! Text. A logical line is a row and the rows it soft-wraps into (at most [`MAX_LINE_ROWS`]), so a
//! match can cross a wrap. Each cell gives its grapheme cluster (a blank cell a space, a spacer
//! cell nothing); the trailing blanks of a line's last row are dropped. Every cell remembers where
//! its text starts, so a match maps back to cells, wide ones included. A literal query is an
//! escaped regular expression; matching ignores case (Unicode simple case folding) unless asked.
//!
//! Rows. Positions are absolute rows: a screen row (0 = the oldest scrollback row) plus `base`,
//! the rows pruned off the top of the scrollback since the search began, so a match keeps its
//! position while output scrolls and old history is dropped. A tracked grid reference on the
//! newest scanned scrollback row measures the pruning. When it is lost (a reset, a cleared
//! scrollback) or the grid moves wholesale (a resize or reflow, a screen switch) the search
//! starts over.
//!
//! Place. Another tracked grid reference follows the current match's first cell through
//! scrolling, pruning and reflow. A search that starts over picks its first match from there, so a
//! resize keeps the user's place, and so does a query that replaces this one (typing on): it is
//! handed the place.
//!
//! Regions. Scrollback only grows at the bottom and shrinks at the top, so it is read once:
//! upward from the active screen (the matches nearest the prompt come first) and, as output
//! scrolls rows into it, downward from where that left off. The live region (the active screen,
//! plus the start of a line wrapped into it) can change with any write and is read again after
//! every change.
use crate::frame::RexMatch;
use anyhow::Result;
use ghostty_vt::{
    Terminal,
    screen::{CellContentTag, CellWide, GridRef, Screen, TrackedGridRef},
    terminal::{Point, PointCoordinate, PointSpace, ScrollViewport},
};
use regex::{Regex, RegexBuilder};
use regex_syntax::ParserBuilder;
use std::borrow::Cow;
use std::collections::VecDeque;
use std::time::{Duration, Instant};

/// `rex_search_flags`: match case.
pub const MATCH_CASE: u32 = 1;
/// `rex_search_flags`: the query is a regular expression.
pub const REGEX: u32 = 2;

/// The longest logical line, in rows. A longer soft-wrapped line is searched in pieces (a match
/// cannot cross the cut); it also bounds how far the live region reaches back into scrollback.
const MAX_LINE_ROWS: u32 = 64;
/// Scrollback reading per step: the worker renders and handles input between steps. The live
/// region is always read whole.
const STEP_TIME: Duration = Duration::from_millis(4);

/// The most memory a compiled query may take (its automata), in bytes.
const SIZE_LIMIT: usize = 1 << 22;

/// Why a query cannot be searched for (`rex_search_error`).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum QueryError {
    /// Not a valid regular expression.
    Invalid = 1,
    /// A valid regular expression too big to compile within [`SIZE_LIMIT`].
    TooComplex = 2,
}

/// The regular expression a query stands for: itself with `REGEX`, else its text escaped.
fn pattern(query: &str, flags: u32) -> Cow<'_, str> {
    if flags & REGEX != 0 { Cow::Borrowed(query) } else { Cow::Owned(regex::escape(query)) }
}

/// Check a query's syntax without compiling it: quick on any thread, so a bad pattern can be
/// reported at once. Compiling ([`compile`]) a big one takes tens of milliseconds.
pub fn check(query: &str, flags: u32) -> Result<(), QueryError> {
    // The parser `RegexBuilder` uses, configured the same way.
    ParserBuilder::new()
        .case_insensitive(flags & MATCH_CASE == 0)
        .build()
        .parse(&pattern(query, flags))
        .map(drop)
        .map_err(|_| QueryError::Invalid)
}

/// Compile a query: literal text unless `REGEX`, case-insensitive (Unicode simple case folding)
/// unless `MATCH_CASE`.
pub fn compile(query: &str, flags: u32) -> Result<Regex, QueryError> {
    RegexBuilder::new(&pattern(query, flags))
        .case_insensitive(flags & MATCH_CASE == 0)
        .size_limit(SIZE_LIMIT)
        .dfa_size_limit(SIZE_LIMIT)
        .build()
        .map_err(|error| match error {
            regex::Error::CompiledTooBig(_) => QueryError::TooComplex,
            _ => QueryError::Invalid,
        })
}

/// A cell: absolute row and column.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Pos {
    pub row: u64,
    pub col: u16,
}

/// A match from its first cell to its last (inclusive). Matches never overlap, so a list ordered
/// by `start` is ordered by `end` too.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Match {
    pub start: Pos,
    pub end: Pos,
}

/// What a frame reports about the search.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Summary {
    /// Matches found so far.
    pub total: u32,
    /// 1-based place of the current match counting up from the newest (bottom) one, the order
    /// a terminal's Find next walks; 0 when there is none.
    pub current: u32,
    /// Scrollback is still being read: the counts may grow.
    pub busy: bool,
}

#[derive(Clone, Copy)]
struct CellText {
    /// Where the cell's text starts in `Line::text`.
    byte: u32,
    /// Rows below the line's first row.
    row: u32,
    col: u16,
    width: u16,
}

#[derive(Default)]
struct Line {
    text: String,
    cells: Vec<CellText>,
    rows: u32,
}

/// Reads logical lines out of a terminal's active screen.
struct Reader<'t> {
    terminal: &'t Terminal,
    cols: u16,
    history_rows: u32,
    grapheme: Vec<char>,
}

impl<'t> Reader<'t> {
    fn new(terminal: &'t Terminal) -> Result<Self> {
        Ok(Self {
            terminal,
            cols: terminal.cols()?,
            history_rows: u32::try_from(terminal.scrollback_rows()?)?,
            grapheme: vec!['\0'; 16],
        })
    }

    /// Screen row `y`, column `x`. Active rows resolve from the bottom of the page list, which is
    /// cheaper than walking down from the oldest scrollback page.
    fn at(&self, x: u16, y: u32) -> Result<GridRef<'t>> {
        let point = match y.checked_sub(self.history_rows) {
            Some(active) => Point::Active(PointCoordinate { x, y: active }),
            None => Point::Screen(PointCoordinate { x, y }),
        };
        Ok(self.terminal.grid_ref(point)?)
    }

    fn wrapped(&self, y: u32) -> Result<bool> {
        Ok(self.at(0, y)?.row()?.is_wrapped()?)
    }

    /// The first row of the logical line holding row `y`, no higher than `floor`.
    fn line_start(&self, mut y: u32, floor: u32) -> Result<u32> {
        let bottom = y;
        while y > floor && bottom - y + 1 < MAX_LINE_ROWS && self.wrapped(y - 1)? {
            y -= 1;
        }
        Ok(y)
    }

    /// Read the logical line starting at row `y` into `line`, ending before row `end`.
    fn line(&mut self, y: u32, end: u32, line: &mut Line) -> Result<()> {
        line.text.clear();
        line.cells.clear();
        line.rows = 0;
        loop {
            let row = y + line.rows;
            let mut wrapped = false;
            for x in 0..self.cols {
                let at = self.at(x, row)?;
                if x == 0 {
                    wrapped = at.row()?.is_wrapped()?;
                }
                let cell = at.cell()?;
                let width = match cell.wide()? {
                    CellWide::Narrow => 1,
                    CellWide::Wide => 2,
                    CellWide::SpacerTail | CellWide::SpacerHead => continue,
                };
                let byte = u32::try_from(line.text.len())?;
                if !cell.has_text()? {
                    line.text.push(' ');
                } else if cell.content_tag()? == CellContentTag::CodepointGrapheme {
                    let n = match at.graphemes(&mut self.grapheme) {
                        Err(ghostty_vt::Error::OutOfSpace { required }) => {
                            self.grapheme.resize(required, '\0');
                            at.graphemes(&mut self.grapheme)?
                        }
                        result => result?,
                    };
                    line.text.extend(&self.grapheme[..n]);
                } else {
                    line.text.push(char::from_u32(cell.codepoint()?).unwrap_or(' '));
                }
                line.cells.push(CellText { byte, row: line.rows, col: x, width });
            }
            line.rows += 1;
            if !wrapped || line.rows >= MAX_LINE_ROWS || y + line.rows >= end {
                break;
            }
        }
        // Blank cells after the last text are not part of the line.
        while let Some(last) = line.cells.last()
            && last.row + 1 == line.rows
            && line.text[last.byte as usize..].chars().all(char::is_whitespace)
        {
            line.text.truncate(last.byte as usize);
            line.cells.pop();
        }
        Ok(())
    }
}

/// The matches of `regex` in a line whose first row is the absolute row `first_row`.
fn find(regex: &Regex, line: &Line, first_row: u64, mut out: impl FnMut(Match)) {
    for found in regex.find_iter(&line.text) {
        if found.is_empty() {
            continue;
        }
        // The cell holding the first byte, and the cell holding the last one.
        let first = line.cells[line.cells.partition_point(|c| c.byte as usize <= found.start()) - 1];
        let last = line.cells[line.cells.partition_point(|c| (c.byte as usize) < found.end()) - 1];
        out(Match {
            start: Pos { row: first_row + u64::from(first.row), col: first.col },
            end: Pos { row: first_row + u64::from(last.row), col: last.col + last.width - 1 },
        });
    }
}

/// A cell followed through scrolling, pruning and reflow, on the screen it is on.
pub struct Place {
    cell: TrackedGridRef,
    screen: Screen,
}

/// One pane's search. Drive it with [`Search::step`] while [`Search::busy`]; tell it about every
/// terminal change with [`Search::touch`].
pub struct Search {
    regex: Regex,
    /// Matches in the scrollback rows read so far, top to bottom.
    history: VecDeque<Match>,
    /// Matches in the live region, top to bottom.
    live: Vec<Match>,
    /// Rows pruned off the top of the scrollback since the search (re)started.
    base: u64,
    /// Scrollback rows read so far: `[top, bottom)`, absolute.
    top: u64,
    bottom: u64,
    /// First row of the live region, absolute.
    live_start: u64,
    /// Follows the newest scrollback row read (absolute row `.1`) to measure pruning.
    anchor: Option<(TrackedGridRef, u64)>,
    /// Active screen and size the rows were read with; anything else starts over.
    geometry: Option<(Screen, u16, u16)>,
    /// The terminal changed since the live region was read.
    stale: bool,
    /// Start of the current match.
    current: Option<Pos>,
    /// The current match's first cell; before there is one, the cell the query this one replaced
    /// had its current match on.
    place: Option<Place>,
    /// Scroll to the current match once one is chosen (a new query).
    reveal: bool,
    /// Where the first match is chosen from ([`Search::first_from`]): the place, taken when the
    /// search (re)starts, else the viewport's last row when first needed (all of it: `col` is
    /// `u16::MAX`). So refining a query letter by letter stays where it began, and a resize keeps
    /// the current match.
    origin: Option<Pos>,
    line: Line,
    found: Vec<Match>,
}

impl Search {
    /// `place`: where the query this one replaces was ([`Search::into_place`]).
    pub fn new(regex: Regex, place: Option<Place>) -> Self {
        Self {
            regex,
            history: VecDeque::new(),
            live: Vec::new(),
            base: 0,
            top: 0,
            bottom: 0,
            live_start: 0,
            anchor: None,
            geometry: None,
            stale: true,
            current: None,
            place,
            reveal: true,
            origin: None,
            line: Line::default(),
            found: Vec::new(),
        }
    }

    /// Where a query replacing this one starts from: this one's current match, else the place it
    /// was handed.
    pub fn into_place(self) -> Option<Place> {
        self.place
    }

    /// The terminal's content changed.
    pub fn touch(&mut self) {
        self.stale = true;
    }

    /// The pane has a new terminal (a snapshot): read it from scratch.
    pub fn replace_terminal(&mut self) {
        self.geometry = None;
        self.anchor = None;
        self.place = None;
        self.stale = true;
    }

    /// There is reading to do: a change to catch up with or scrollback not read yet.
    pub fn busy(&self) -> bool {
        self.stale || self.bottom < self.live_start || self.top > self.base
    }

    fn total(&self) -> usize {
        self.history.len() + self.live.len()
    }

    fn get(&self, index: usize) -> Match {
        match index.checked_sub(self.history.len()) {
            Some(live) => self.live[live],
            None => self.history[index],
        }
    }

    /// Where the match starting at `pos` is (`Ok`) or would go (`Err`) in the whole list.
    fn index_of(&self, pos: Pos) -> Result<usize, usize> {
        match self.history.binary_search_by_key(&pos, |m| m.start) {
            Err(i) if i == self.history.len() => {
                let base = self.history.len();
                self.live.binary_search_by_key(&pos, |m| m.start).map(|i| base + i).map_err(|i| base + i)
            }
            found => found,
        }
    }

    /// Whether row `row` has been read, so a match missing there is really gone.
    fn read(&self, row: u64) -> bool {
        row >= self.live_start || (self.top..self.bottom).contains(&row)
    }

    pub fn summary(&self) -> Summary {
        let current = self.current.and_then(|pos| self.index_of(pos).ok()).map_or(0, |i| self.total() - i);
        Summary {
            total: u32::try_from(self.total()).unwrap_or(u32::MAX),
            current: u32::try_from(current).unwrap_or(u32::MAX),
            busy: self.busy(),
        }
    }

    /// Catch up with terminal changes, then read scrollback for a bounded time. Returns whether
    /// what a frame shows (matches, counts, current match) may have changed.
    pub fn step(&mut self, terminal: &mut Terminal) -> Result<bool> {
        self.step_for(terminal, STEP_TIME)
    }

    fn step_for(&mut self, terminal: &mut Terminal, budget: Duration) -> Result<bool> {
        let before = self.fingerprint();
        let started = Instant::now();
        let mut live_changed = false;
        {
            let mut reader = Reader::new(terminal)?;
            if self.stale {
                self.stale = false;
                self.sync(&reader)?;
                live_changed = self.read_live(&mut reader)?;
            }
            // Rows that just scrolled out of the live region first, then older scrollback.
            while self.bottom < self.live_start && started.elapsed() < budget {
                self.read_down(&mut reader)?;
            }
            while self.top > self.base && started.elapsed() < budget {
                self.read_up(&mut reader)?;
            }
            self.place_anchor(reader.terminal)?;
        }
        self.settle(terminal)?;
        Ok(live_changed || self.fingerprint() != before)
    }

    /// Cheap to compare: the counts, the current match and the ends of the scrollback matches.
    fn fingerprint(&self) -> (Summary, usize, Option<Match>, Option<Match>, Option<Pos>) {
        (self.summary(), self.history.len(), self.history.front().copied(), self.history.back().copied(), self.current)
    }

    /// Measure pruning and find the live region; start over when the grid moved wholesale.
    fn sync(&mut self, reader: &Reader<'_>) -> Result<()> {
        let terminal = reader.terminal;
        let screen = terminal.active_screen()?;
        let geometry = Some((screen, terminal.cols()?, terminal.rows()?));
        let mut restart = self.geometry != geometry;
        if !restart && let Some((anchor, row)) = &self.anchor {
            match anchor.point(PointSpace::Screen)? {
                // Rows pruned above the anchor moved it up by as many.
                Some(at) if u64::from(at.y) <= row - self.base => self.base = row - u64::from(at.y),
                _ => restart = true,
            }
        }
        if !restart {
            // A line wrapped across the bottom of what was read stays cut there.
            let floor = u32::try_from(self.bottom - self.base)?.min(reader.history_rows);
            let live_start = self.base + u64::from(reader.line_start(reader.history_rows, floor)?);
            if live_start < self.bottom {
                restart = true;
            } else {
                self.live_start = live_start;
                while self.history.front().is_some_and(|m| m.start.row < self.base) {
                    self.history.pop_front();
                }
                self.top = self.top.max(self.base);
            }
        }
        if restart {
            // Rows are counted afresh (`base` 0): the place's screen row is its absolute row.
            let at = match &self.place {
                Some(place) if place.screen == screen => place.cell.point(PointSpace::Screen)?,
                _ => None,
            };
            self.origin = at.map(|at| Pos { row: u64::from(at.y), col: at.x });
            self.geometry = geometry;
            self.anchor = None;
            self.history.clear();
            self.live.clear();
            self.base = 0;
            self.live_start = u64::from(reader.line_start(reader.history_rows, 0)?);
            self.top = self.live_start;
            self.bottom = self.live_start;
            self.current = None;
        }
        Ok(())
    }

    /// Read the live region again. Returns whether its matches changed.
    fn read_live(&mut self, reader: &mut Reader<'_>) -> Result<bool> {
        let end = reader.history_rows + u32::from(reader.terminal.rows()?);
        let mut y = u32::try_from(self.live_start - self.base)?;
        let mut live = Vec::with_capacity(self.live.len());
        while y < end {
            reader.line(y, end, &mut self.line)?;
            find(&self.regex, &self.line, self.base + u64::from(y), |m| live.push(m));
            y += self.line.rows;
        }
        let changed = live != self.live;
        self.live = live;
        Ok(changed)
    }

    /// Read the logical line below what was read of the scrollback (new history).
    fn read_down(&mut self, reader: &mut Reader<'_>) -> Result<()> {
        let y = u32::try_from(self.bottom - self.base)?;
        let end = u32::try_from(self.live_start - self.base)?;
        reader.line(y, end, &mut self.line)?;
        find(&self.regex, &self.line, self.bottom, |m| self.history.push_back(m));
        self.bottom += u64::from(self.line.rows);
        Ok(())
    }

    /// Read the logical line above what was read of the scrollback (older history).
    fn read_up(&mut self, reader: &mut Reader<'_>) -> Result<()> {
        let end = u32::try_from(self.top - self.base)?;
        let start = reader.line_start(end - 1, 0)?;
        reader.line(start, end, &mut self.line)?;
        let mut found = std::mem::take(&mut self.found);
        find(&self.regex, &self.line, self.base + u64::from(start), |m| found.push(m));
        for m in found.drain(..).rev() {
            self.history.push_front(m);
        }
        self.found = found;
        self.top = self.base + u64::from(start);
        Ok(())
    }

    /// Keep the anchor on the newest scrollback row read (before any, the row above the live
    /// region).
    fn place_anchor(&mut self, terminal: &Terminal) -> Result<()> {
        let Some(row) = self.bottom.checked_sub(1).filter(|row| *row >= self.base) else {
            self.anchor = None;
            return Ok(());
        };
        if self.anchor.as_ref().is_some_and(|(_, at)| *at == row) {
            return Ok(());
        }
        let y = u32::try_from(row - self.base)?;
        self.anchor = Some((terminal.track_grid_ref(Point::Screen(PointCoordinate { x: 0, y }))?, row));
        Ok(())
    }

    /// Where a first match is chosen from: the origin, fixed at the viewport's last row the first
    /// time it is needed.
    fn pivot(&mut self, terminal: &Terminal) -> Result<Pos> {
        if let Some(pos) = self.origin {
            return Ok(pos);
        }
        let viewport = terminal.scrollbar()?;
        let pos = Pos { row: self.base + viewport.offset + viewport.len.max(1) - 1, col: u16::MAX };
        self.origin = Some(pos);
        Ok(pos)
    }

    /// The match a search starting from `from` makes current: the first one on `from`'s row at or
    /// after it (the same match after a reflow, or for a refined query), else the last one before
    /// it. `None` when there is none before it either.
    fn first_from(&self, from: Pos) -> Option<usize> {
        let at = self.index_of(from).unwrap_or_else(|i| i);
        if at < self.total() && self.get(at).start.row == from.row { Some(at) } else { at.checked_sub(1) }
    }

    /// Make the match starting at `pos` current (`None`: there is none), and put the place on it.
    fn set_current(&mut self, terminal: &Terminal, pos: Option<Pos>) -> Result<()> {
        if self.current == pos {
            return Ok(());
        }
        self.current = pos;
        if let Some(pos) = pos {
            let point = Point::Screen(PointCoordinate { x: pos.col, y: u32::try_from(pos.row - self.base)? });
            self.place = Some(Place { cell: terminal.track_grid_ref(point)?, screen: terminal.active_screen()? });
        }
        Ok(())
    }

    /// Keep the current match valid, and choose the first one for a new query (or one that
    /// started over) from the pivot ([`Search::first_from`]), else the first one below it.
    fn settle(&mut self, terminal: &mut Terminal) -> Result<()> {
        if let Some(pos) = self.current {
            if (pos.row < self.base || self.read(pos.row))
                && let Err(next) = self.index_of(pos)
            {
                // Gone (pruned, or its text changed): the match after it, else the last one.
                let index = next.min(self.total().saturating_sub(1));
                let next = (self.total() > 0).then(|| self.get(index).start);
                self.set_current(terminal, next)?;
            }
            return Ok(());
        }
        if self.total() == 0 {
            // A query with no matches has nothing to reveal later.
            self.reveal &= self.busy();
            return Ok(());
        }
        let pivot = self.pivot(terminal)?;
        // Everything between the pivot's row and the matches above it has been read.
        let covered = pivot.row >= self.top && (pivot.row < self.bottom || self.bottom == self.live_start);
        let first = self.first_from(pivot);
        if self.busy() && (!covered || first.is_none()) {
            return Ok(());
        }
        let found = self.get(first.unwrap_or(0));
        self.set_current(terminal, Some(found.start))?;
        if std::mem::take(&mut self.reveal) {
            self.scroll_to(terminal, found)?;
        }
        Ok(())
    }

    /// Move the current match: `direction` > 0 the next one down, < 0 the next one up, wrapping.
    /// Scrolls the viewport to it when it is not entirely in view.
    pub fn select(&mut self, terminal: &mut Terminal, direction: i32) -> Result<()> {
        let total = self.total();
        if total == 0 || direction == 0 {
            return Ok(());
        }
        let index = match self.current.map(|pos| self.index_of(pos)) {
            // Nothing chosen yet (the search is still reading): the one a new query would get.
            None => {
                let pivot = self.pivot(terminal)?;
                self.first_from(pivot).unwrap_or(0)
            }
            Some(Ok(i)) if direction > 0 => (i + 1) % total,
            Some(Ok(i)) => (i + total - 1) % total,
            Some(Err(i)) if direction > 0 => i % total,
            Some(Err(i)) => (i + total - 1) % total,
        };
        let found = self.get(index);
        self.set_current(terminal, Some(found.start))?;
        self.reveal = false;
        self.scroll_to(terminal, found)
    }

    /// Scroll so the match is in view, centred, unless it already is entirely.
    fn scroll_to(&self, terminal: &mut Terminal, found: Match) -> Result<()> {
        let viewport = terminal.scrollbar()?;
        let (first, last) = (found.start.row - self.base, found.end.row - self.base);
        if first >= viewport.offset && last < viewport.offset + viewport.len {
            return Ok(());
        }
        let span = last - first + 1;
        let top = first.saturating_sub(viewport.len.saturating_sub(span) / 2);
        terminal.scroll_viewport(ScrollViewport::Row(usize::try_from(top)?));
        Ok(())
    }

    /// The matches on the viewport whose first row is screen row `viewport_top`, one span per row
    /// and match, into `out`; and the counts.
    pub fn visible(&self, viewport_top: u64, rows: u16, cols: u16, out: &mut Vec<RexMatch>) -> Summary {
        let top = self.base + viewport_top;
        let bottom = top + u64::from(rows);
        let last_col = cols.saturating_sub(1);
        let current = self.current;
        let mut add = |m: &Match| {
            let is_current = u16::from(Some(m.start) == current);
            for row in m.start.row.max(top)..=m.end.row.min(bottom - 1) {
                let col0 = if row == m.start.row { m.start.col } else { 0 };
                let col1 = if row == m.end.row { m.end.col } else { last_col };
                out.push(RexMatch { row: (row - top) as u16, col0: col0.min(last_col), col1: col1.min(last_col), current: is_current });
            }
        };
        if rows > 0 {
            let first = self.history.partition_point(|m| m.end.row < top);
            self.history.range(first..).take_while(|m| m.start.row < bottom).for_each(&mut add);
            let first = self.live.partition_point(|m| m.end.row < top);
            self.live[first..].iter().take_while(|m| m.start.row < bottom).for_each(&mut add);
        }
        self.summary()
    }
}


#[cfg(test)]
mod tests {
    use super::*;
    use ghostty_vt::TerminalOptions;

    fn terminal(cols: u16, rows: u16) -> Terminal {
        Terminal::new(TerminalOptions { cols, rows, max_scrollback: 10_000_000 }).unwrap()
    }

    fn search(terminal: &mut Terminal, query: &str, flags: u32) -> Search {
        let mut search = Search::new(compile(query, flags).unwrap(), None);
        finish(&mut search, terminal);
        search
    }

    fn finish(search: &mut Search, terminal: &mut Terminal) {
        for _ in 0..100_000 {
            search.step_for(terminal, Duration::from_secs(1)).unwrap();
            if !search.busy() {
                return;
            }
        }
        panic!("search did not finish");
    }

    /// Every match as ((start col, start row), (end col, end row)), in screen rows.
    fn all(search: &Search) -> Vec<((u16, u64), (u16, u64))> {
        search
            .history
            .iter()
            .chain(&search.live)
            .map(|m| ((m.start.col, m.start.row - search.base), (m.end.col, m.end.row - search.base)))
            .collect()
    }

    fn viewport(terminal: &Terminal) -> (u64, u16) {
        let s = terminal.scrollbar().unwrap();
        (s.offset, s.len as u16)
    }

    fn visible(search: &Search, terminal: &Terminal) -> (Vec<RexMatch>, Summary) {
        let (top, rows) = viewport(terminal);
        let mut out = Vec::new();
        let summary = search.visible(top, rows, terminal.cols().unwrap(), &mut out);
        (out, summary)
    }

    fn text_at(terminal: &Terminal, col: u16, row: u64) -> char {
        let at = terminal.grid_ref(Point::Screen(PointCoordinate { x: col, y: row as u32 })).unwrap();
        char::from_u32(at.cell().unwrap().codepoint().unwrap()).unwrap()
    }

    fn seq(terminal: &mut Terminal, range: std::ops::RangeInclusive<u32>) {
        for i in range {
            terminal.vt_write(format!("{i}\r\n").as_bytes());
        }
    }

    #[test]
    fn literal_ignores_case_unless_asked() {
        let mut t = terminal(40, 5);
        t.vt_write(b"Hello hello HELLO\r\nworld");
        assert_eq!(all(&search(&mut t, "hello", 0)), vec![((0, 0), (4, 0)), ((6, 0), (10, 0)), ((12, 0), (16, 0))]);
        assert_eq!(all(&search(&mut t, "hello", MATCH_CASE)), vec![((6, 0), (10, 0))]);
        // Regex metacharacters are literal unless REGEX.
        t.vt_write(b"\r\na.c abc");
        assert_eq!(all(&search(&mut t, "a.c", 0)), vec![((0, 2), (2, 2))]);
        assert_eq!(all(&search(&mut t, "a.c", REGEX)).len(), 2);
        // Case folding beyond ASCII.
        t.vt_write("\r\nÉCOLE école".as_bytes());
        assert_eq!(all(&search(&mut t, "école", 0)).len(), 2);
        assert_eq!(all(&search(&mut t, "école", MATCH_CASE)).len(), 1);
    }

    #[test]
    fn regex_errors_are_reported() {
        for bad in ["a(b", "[z-a]", "(?<", r"\p{Nope}"] {
            assert_eq!(check(bad, REGEX), Err(QueryError::Invalid), "{bad}");
            assert_eq!(compile(bad, REGEX).err(), Some(QueryError::Invalid), "{bad}");
            // The same text is fine as a literal.
            assert_eq!(check(bad, 0), Ok(()));
            assert!(compile(bad, MATCH_CASE).is_ok());
        }
        // Valid, but too big to compile: the check passes, compiling refuses it.
        assert_eq!(check(r"\w{200}", REGEX), Ok(()));
        assert_eq!(compile(r"\w{200}", REGEX).err(), Some(QueryError::TooComplex));
        assert!(compile(r"\w{20}", REGEX).is_ok());
    }

    #[test]
    fn regex_anchors_and_classes_work_per_line() {
        let mut t = terminal(30, 6);
        t.vt_write(b"$ ls\r\nfoo.txt  bar.rs\r\n$ echo 42\r\n42");
        assert_eq!(all(&search(&mut t, r"^\$ ", REGEX)), vec![((0, 0), (1, 0)), ((0, 2), (1, 2))]);
        assert_eq!(all(&search(&mut t, r"\w+\.rs", REGEX)), vec![((9, 1), (14, 1))]);
        assert_eq!(all(&search(&mut t, r"\d+$", REGEX)), vec![((7, 2), (8, 2)), ((0, 3), (1, 3))]);
        // Empty matches are not matches.
        assert_eq!(all(&search(&mut t, r"q*", REGEX)), vec![]);
    }

    #[test]
    fn wide_characters_map_to_their_cells() {
        let mut t = terminal(20, 3);
        t.vt_write("a中文b 日本語".as_bytes());
        // a=0, 中=1-2, 文=3-4, b=5, space=6, 日=7-8, 本=9-10, 語=11-12
        assert_eq!(all(&search(&mut t, "文b", 0)), vec![((3, 0), (5, 0))]);
        assert_eq!(all(&search(&mut t, "本語", 0)), vec![((9, 0), (12, 0))]);
        assert_eq!(all(&search(&mut t, "中", 0)), vec![((1, 0), (2, 0))]);
        assert_eq!(all(&search(&mut t, "b 日", 0)), vec![((5, 0), (8, 0))]);
    }

    #[test]
    fn grapheme_clusters_stay_in_one_cell() {
        let mut t = terminal(20, 3);
        t.vt_write("cafe\u{301} ok".as_bytes());
        assert_eq!(all(&search(&mut t, "cafe\u{301}", 0)), vec![((0, 0), (3, 0))]);
        assert_eq!(all(&search(&mut t, "ok", 0)), vec![((5, 0), (6, 0))]);
    }

    #[test]
    fn matches_cross_soft_wraps_but_not_line_breaks() {
        let mut t = terminal(10, 5);
        // "0123456789" fills row 0 and "abcdef" wraps onto row 1.
        t.vt_write(b"0123456789abcdef\r\nxyz");
        assert_eq!(all(&search(&mut t, "89ab", 0)), vec![((8, 0), (1, 1))]);
        // A hard line break is not a wrap.
        assert_eq!(all(&search(&mut t, "efxyz", 0)), vec![]);
        // A wide character that does not fit at the end of a row moves to the next one.
        let mut t = terminal(5, 4);
        t.vt_write("abcd中x".as_bytes());
        assert_eq!(all(&search(&mut t, "d中x", 0)), vec![((3, 0), (2, 1))]);
        // A match across a wrap shows as one span per row.
        let mut t = terminal(10, 5);
        t.vt_write(b"0123456789abcdef");
        let s = search(&mut t, "789abc", 0);
        let (spans, _) = visible(&s, &t);
        let spans: Vec<_> = spans.iter().map(|m| (m.row, m.col0, m.col1, m.current)).collect();
        assert_eq!(spans, vec![(0, 7, 9, 1), (1, 0, 2, 1)]);
    }

    #[test]
    fn scrollback_is_searched_and_counted_top_to_bottom() {
        let mut t = terminal(20, 10);
        seq(&mut t, 1..=5000);
        let s = search(&mut t, "499", 0);
        // 499, 1499, 2499, 3499, 4499, 4990..=4999
        assert_eq!(s.summary().total, 15);
        // The current match is the newest one, which counts first.
        assert_eq!(s.summary().current, 1);
        let rows: Vec<u64> = all(&s).iter().map(|m| m.0.1).collect();
        assert!(rows.windows(2).all(|w| w[0] < w[1]), "ordered top to bottom: {rows:?}");
        for ((col, row), _) in all(&s) {
            assert_eq!(text_at(&t, col, row), '4');
        }
    }

    #[test]
    fn a_new_query_scrolls_to_its_match_and_steps_wrap() {
        let mut t = terminal(20, 10);
        seq(&mut t, 1..=5000);
        let s = search(&mut t, "4999", 0);
        let (top, rows) = viewport(&t);
        let found = all(&s)[0];
        assert!(found.0.1 >= top && found.0.1 < top + u64::from(rows), "4999 at {found:?}, view {top}+{rows}");
        let (spans, summary) = visible(&s, &t);
        assert_eq!(summary, Summary { total: 1, current: 1, busy: false });
        assert_eq!(spans.len(), 1);
        assert_eq!((spans[0].col0, spans[0].col1, spans[0].current), (0, 3, 1));
        assert_eq!(u64::from(spans[0].row) + top, found.0.1);

        t.scroll_viewport(ScrollViewport::Bottom);
        let mut s = search(&mut t, "10", 0);
        let total = s.summary().total;
        assert!(total > 100);
        assert_eq!(s.summary().current, 1, "the newest match");
        s.select(&mut t, 1).unwrap();
        assert_eq!(s.summary().current, total, "down from the newest wraps to the oldest");
        let (top, _) = viewport(&t);
        assert!(top < 10, "scrolled to the oldest match: top {top}");
        let (spans, _) = visible(&s, &t);
        assert_eq!(spans.iter().filter(|m| m.current == 1).count(), 1);
        s.select(&mut t, -1).unwrap();
        assert_eq!(s.summary().current, 1, "up from the oldest wraps to the newest");
        s.select(&mut t, -1).unwrap();
        assert_eq!(s.summary().current, 2);
        s.select(&mut t, 1).unwrap();
        assert_eq!(s.summary().current, 1);
    }

    #[test]
    fn a_new_query_prefers_matches_above_a_scrolled_viewport() {
        let mut t = terminal(20, 10);
        seq(&mut t, 1..=300);
        // Viewport on rows 100..110 (numbers 101..=110).
        t.scroll_viewport(ScrollViewport::Row(100));
        let s = search(&mut t, "5", 0);
        let summary = s.summary();
        let current = s.history.iter().chain(&s.live).nth((summary.total - summary.current) as usize).unwrap().start;
        // The last match at or above the viewport's last row (109: "110") is in "105".
        assert_eq!(current.row - s.base, 104);
        assert_eq!(viewport(&t).0, 100, "already in view: no scroll");
    }

    #[test]
    fn a_refined_query_starts_where_the_last_one_was() {
        let mut t = terminal(20, 10);
        t.vt_write(b"error one\r\n");
        seq(&mut t, 1..=100);
        t.vt_write(b"err two\r\n");
        seq(&mut t, 101..=200);
        t.vt_write(b"error three\r\n");
        seq(&mut t, 201..=230);
        let mut s = search(&mut t, "err", 0);
        // err three (1), err two (2), err one (3): step up to "err two".
        s.select(&mut t, -1).unwrap();
        assert_eq!(s.summary().current, 2);
        // The viewport moves away (the user scrolls), and more output arrives: neither matters.
        t.scroll_viewport(ScrollViewport::Bottom);
        seq(&mut t, 231..=240);
        // Typing on: "erro" starts at "err two", where there is no "erro"; the nearest above it is "error one".
        let mut refined = Search::new(compile("erro", 0).unwrap(), s.into_place());
        finish(&mut refined, &mut t);
        assert_eq!(refined.summary(), Summary { total: 2, current: 2, busy: false });
        // A fresh query starts from the bottom of the viewport: the newest.
        t.scroll_viewport(ScrollViewport::Bottom);
        assert_eq!(search(&mut t, "erro", 0).summary().current, 1);
    }

    #[test]
    fn a_new_query_prefers_a_match_on_the_current_ones_row() {
        let mut t = terminal(30, 6);
        seq(&mut t, 70..=79);
        t.vt_write(b"omega 7 and 7\r\n");
        seq(&mut t, 80..=99);
        let s = search(&mut t, "omega", 0);
        let omega = s.current.unwrap();
        t.scroll_viewport(ScrollViewport::Bottom);
        // "7" after "omega" on its row, not the "7"s on the rows above or below.
        let mut s = Search::new(compile("7", 0).unwrap(), s.into_place());
        finish(&mut s, &mut t);
        assert_eq!(s.current.map(|pos| (pos.row - s.base, pos.col)), Some((omega.row - s.base, 6)));
        // Back to "omega": the last match at or before the "7".
        let mut s = Search::new(compile("omega", 0).unwrap(), s.into_place());
        finish(&mut s, &mut t);
        assert_eq!(s.current.map(|pos| pos.col), Some(0));
    }

    #[test]
    fn a_resize_keeps_the_current_match() {
        let mut t = terminal(30, 6);
        for i in 0..40 {
            t.vt_write(format!("see the tree {i}\r\n").as_bytes());
        }
        let mut s = search(&mut t, "e", 0);
        assert_eq!(s.summary().total, 200);
        // The second 'e' from the bottom: the first in "tree 39".
        s.select(&mut t, -1).unwrap();
        let place = |s: &Search, t: &Terminal| {
            let pos = s.current.unwrap();
            let line: String = (0..t.cols().unwrap()).map(|col| text_at(t, col, pos.row - s.base)).collect();
            (s.summary(), line.trim_end_matches(['\0', ' ']).to_owned(), pos.col)
        };
        let before = place(&s, &t);
        assert_eq!(before, (Summary { total: 200, current: 2, busy: false }, "see the tree 39".to_owned(), 10));
        // Narrower (every line wraps: that 'e' starts the second row) and taller, then back: the
        // same 'e' each time.
        for (cols, rows, line, col) in [(10, 8, "ee 39", 0), (30, 6, "see the tree 39", 10)] {
            t.resize(cols, rows, 8, 16).unwrap();
            s.touch();
            finish(&mut s, &mut t);
            assert_eq!(place(&s, &t), (before.0, line.to_owned(), col), "{cols}x{rows}");
        }
    }

    #[test]
    fn matches_follow_output_and_pruning() {
        // Little scrollback, so output prunes it quickly.
        let mut t = Terminal::new(TerminalOptions { cols: 20, rows: 5, max_scrollback: 64 * 1024 }).unwrap();
        t.vt_write(b"needle one\r\n");
        let mut s = search(&mut t, "needle", 0);
        assert_eq!(s.summary().total, 1);
        for i in 0..200 {
            t.vt_write(format!("line {i}\r\n").as_bytes());
            s.touch();
            finish(&mut s, &mut t);
        }
        t.vt_write(b"needle two\r\n");
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(all(&s).len(), 2);
        for ((col, row), _) in all(&s) {
            assert_eq!(text_at(&t, col, row), 'n');
        }
        // Enough output to prune the first needle away, read in batches as a pane would.
        for i in 0..20_000 {
            t.vt_write(format!("filler {i}\r\n").as_bytes());
            if i % 500 == 0 {
                s.touch();
                finish(&mut s, &mut t);
            }
        }
        s.touch();
        finish(&mut s, &mut t);
        let fresh = search(&mut t, "needle", 0);
        assert_eq!(all(&s), all(&fresh), "incremental results equal a fresh search");
        assert!(s.base > 0, "rows were pruned");
        // The same for a query with matches on every row, all through the pruned scrollback.
        let mut s = search(&mut t, "filler", 0);
        for i in 0..3000 {
            t.vt_write(format!("filler {i}\r\n").as_bytes());
            if i % 97 == 0 {
                s.touch();
                s.step_for(&mut t, Duration::from_micros(200)).unwrap();
            }
        }
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(all(&s), all(&search(&mut t, "filler", 0)));
    }

    #[test]
    fn highlights_stay_on_their_text_while_output_scrolls() {
        let mut t = terminal(20, 5);
        t.vt_write(b"alpha\r\nbeta\r\n");
        let mut s = search(&mut t, "alpha", 0);
        for i in 0..12 {
            t.vt_write(format!("x{i}\r\n").as_bytes());
            s.touch();
            finish(&mut s, &mut t);
            let (spans, summary) = visible(&s, &t);
            assert_eq!(summary.total, 1);
            let (top, _) = viewport(&t);
            match spans.first() {
                Some(m) => assert_eq!(u64::from(m.row) + top, 0, "alpha stays on screen row 0"),
                None => assert!(top > 0, "scrolled out of view"),
            }
        }
    }

    #[test]
    fn the_live_screen_is_read_again_after_changes() {
        let mut t = terminal(20, 5);
        t.vt_write(b"one two");
        let mut s = search(&mut t, "two", 0);
        assert_eq!(s.summary().total, 1);
        t.vt_write(b"\x1b[2K\r three");
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(s.summary(), Summary { total: 0, current: 0, busy: false });
        t.vt_write(b" two two");
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(s.summary().total, 2);
        assert_eq!(s.summary().current, 1);
    }

    #[test]
    fn resize_reset_and_screen_switches_start_over() {
        let mut t = terminal(10, 4);
        t.vt_write(b"0123456789abc\r\n");
        let mut s = search(&mut t, "9a", 0);
        assert_eq!(all(&s), vec![((9, 0), (0, 1))]);
        t.resize(20, 4, 8, 16).unwrap();
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(all(&s), vec![((9, 0), (10, 0))], "reflowed");
        // The alternate screen is searched on its own, and the primary again after it.
        t.vt_write(b"\x1b[?1049h\x1b[Hfull 9a screen");
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(all(&s), vec![((5, 0), (6, 0))]);
        t.vt_write(b"\x1b[?1049l");
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(all(&s), vec![((9, 0), (10, 0))]);
        t.vt_write(b"\x1bc");
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(s.summary().total, 0);
    }

    #[test]
    fn a_cleared_scrollback_drops_its_matches() {
        let mut t = terminal(20, 4);
        seq(&mut t, 1..=50);
        let mut s = search(&mut t, "1", 0);
        assert!(s.summary().total > 10);
        t.vt_write(b"\x1b[H\x1b[2J\x1b[3J7 11");
        s.touch();
        finish(&mut s, &mut t);
        assert_eq!(all(&s), vec![((2, 0), (2, 0)), ((3, 0), (3, 0))]);
    }

    #[test]
    fn reading_ten_megabytes_of_scrollback_is_quick() {
        let mut t = terminal(120, 40);
        let line = "0123456789 abcdefghijklmnopqrstuvwxyz ABCDEFGHIJKLMNOPQRSTUVWXYZ ".repeat(2);
        for i in 0..40_000 {
            t.vt_write(format!("{i:06} {line}\r\n").as_bytes());
        }
        let started = Instant::now();
        let s = search(&mut t, "039999 ", 0);
        let took = started.elapsed();
        assert_eq!(s.summary().total, 1);
        eprintln!("{} scrollback rows read in {took:?}", t.scrollback_rows().unwrap());
    }
}
