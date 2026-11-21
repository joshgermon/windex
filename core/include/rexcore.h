/*
 * rexcore.h — the C ABI between the Windex UI (C#/WinUI 3) and rexcore.dll (Rust).
 *
 * Threading
 *   - Every function is safe to call from any thread unless noted.
 *   - The event callback is invoked on core worker threads, never re-entrantly for the
 *     same event; the UI must marshal to its dispatcher and return quickly. RESULTs for
 *     commands answered without the network (ping, add_host, app_focus, ...) arrive on the
 *     calling thread, inside rex_core_command. No callback starts after rex_core_free returns.
 *   - rex_pane_frame_lock/unlock must be paired on the same thread (the UI render thread).
 *
 * Strings: UTF-8, NUL-terminated. Strings returned as `char*` are owned by the caller
 * and must be released with rex_string_free. `const char*` passed to the callback is
 * valid only for the duration of the callback.
 *
 * Panes: every terminal the UI can show is a "pane", identified by a non-zero 64-bit
 * handle allocated by the core. The model JSON maps server blocks to pane handles.
 * Handles are never reused within a process.
 */
#ifndef REXCORE_H
#define REXCORE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define REXCORE_ABI_VERSION 2

typedef struct rex_core rex_core;

/* ---------------------------------------------------------------- events */

enum rex_event_kind {
    REX_EV_MODEL         = 1, /* json: full Model snapshot (see docs/contract.md)        */
    REX_EV_PANE_DIRTY    = 2, /* pane: new frame available; sent once until next lock    */
    REX_EV_RESULT        = 3, /* json: {"req":n,"ok":bool,"error":str?,"data":any?}      */
    REX_EV_NOTICE        = 4, /* json: {"level":"info|warning|error","title","message","host"?} */
    REX_EV_BELL          = 5, /* pane                                                    */
    REX_EV_CLIPBOARD     = 6, /* pane, json: {"text":str} program asked to copy (OSC 52)  */
    REX_EV_NOTIFY        = 7, /* json: {"title","body","host","session","window","pane","kind"} desktop toast */
    REX_EV_LOG           = 8, /* json: {"level","target","message"}                       */
    REX_EV_DISCOVERY     = 9  /* json: {"machines":[...]} tailnet discovery results       */
};

typedef void (*rex_event_fn)(void *user, int32_t kind, uint64_t pane,
                             const char *json, size_t json_len);

/* ------------------------------------------------------------- lifecycle */

/* options_json: {"client_name":"Windex","client_version":"x","label":"<hostname>",
 *                "state_dir":"C:\\...","ssh_path":"C:\\Windows\\System32\\OpenSSH\\ssh.exe",
 *                "log_level":"info"}
 * Returns NULL on failure (details via REX_EV_LOG before returning). */
rex_core *rex_core_new(const char *options_json, rex_event_fn callback, void *user);
void      rex_core_free(rex_core *core);
uint32_t  rex_core_abi_version(void);
void      rex_string_free(char *s);

/* Asynchronous JSON command (see docs/contract.md "Commands"). The JSON must carry
 * "req": <u64>; its outcome arrives as REX_EV_RESULT with the same req.
 * Returns 0 if queued, <0 if the JSON is malformed. */
int32_t rex_core_command(rex_core *core, const char *command_json);

/* --------------------------------------------------------------- input */

enum rex_mods {
    REX_MOD_SHIFT = 1, REX_MOD_CTRL = 2, REX_MOD_ALT = 4, REX_MOD_SUPER = 8,
    REX_MOD_CAPS  = 16, REX_MOD_NUM = 32
};

enum rex_key_action { REX_KEY_RELEASE = 0, REX_KEY_PRESS = 1, REX_KEY_REPEAT = 2 };

typedef struct rex_key {
    const char *code;   /* W3C KeyboardEvent.code: "KeyA", "Digit1", "Enter", "ArrowLeft", "F5", "Numpad1", "Backquote", ... */
    const char *key;    /* W3C KeyboardEvent.key for the current layout, or NULL ("a", "A", "Enter", "ArrowLeft") */
    const char *text;   /* UTF-8 text this press produces after layout/dead keys, or NULL */
    uint32_t    mods;   /* rex_mods */
    uint32_t    action; /* rex_key_action */
} rex_key;

/* All return 0 on success, <0 if the pane is unknown, 1 if input is paused (disconnected). */
int32_t rex_pane_key(rex_core *core, uint64_t pane, const rex_key *key);
int32_t rex_pane_text(rex_core *core, uint64_t pane, const char *utf8);  /* committed IME / injected text */
int32_t rex_pane_paste(rex_core *core, uint64_t pane, const char *utf8); /* bracketed when the program asked */

enum rex_mouse_action { REX_MOUSE_PRESS = 1, REX_MOUSE_RELEASE = 2, REX_MOUSE_MOVE = 3, REX_MOUSE_WHEEL = 4 };
enum rex_mouse_button { REX_BTN_NONE = 0, REX_BTN_LEFT = 1, REX_BTN_RIGHT = 2, REX_BTN_MIDDLE = 3 };

typedef struct rex_mouse {
    uint32_t action;   /* rex_mouse_action */
    uint32_t button;   /* rex_mouse_button */
    uint32_t mods;     /* rex_mods */
    float    x, y;     /* pixels relative to the top-left of the cell grid */
    float    wheel_dy; /* wheel notches, + = away from user (scroll up) */
} rex_mouse;

/* Mouse reporting to the program (only meaningful when frame->mouse_tracking). */
int32_t rex_pane_mouse(rex_core *core, uint64_t pane, const rex_mouse *mouse);

/* The UI computed this pane's grid from its pixel size and font metrics. */
int32_t rex_pane_resize(rex_core *core, uint64_t pane, uint16_t cols, uint16_t rows,
                        uint32_t cell_width_px, uint32_t cell_height_px);
int32_t rex_pane_focus(rex_core *core, uint64_t pane, int32_t focused);

enum rex_scroll_op { REX_SCROLL_DELTA = 0, REX_SCROLL_TOP = 1, REX_SCROLL_BOTTOM = 2,
                     REX_SCROLL_PROMPT = 3, REX_SCROLL_ROW = 4 };
/* DELTA/PROMPT: value is rows/prompts, negative = toward history. ROW: absolute row from top. */
int32_t rex_pane_scroll(rex_core *core, uint64_t pane, int32_t op, int64_t value);

enum rex_select_op { REX_SEL_CLEAR = 0, REX_SEL_PRESS = 1, REX_SEL_DRAG = 2,
                     REX_SEL_RELEASE = 3, REX_SEL_ALL = 4 };
/* Selection gestures, driven by libghostty-vt (word/path/line rules, wide cells, autoscroll).
 * x/y: pixels relative to the grid origin (may be outside the grid while dragging).
 * clicks: 1 = cell, 2 = word, 3 = line (PRESS only). Uses the cell size from rex_pane_resize. */
int32_t rex_pane_select(rex_core *core, uint64_t pane, int32_t op, int32_t clicks, float x, float y);
char   *rex_pane_selection_text(rex_core *core, uint64_t pane); /* NULL if none; free with rex_string_free */
char   *rex_pane_hyperlink_at(rex_core *core, uint64_t pane, int32_t col, int32_t row); /* OSC 8 or detected URL */

/* ------------------------------------------------------ find in terminal */

enum rex_search_flags { REX_SEARCH_MATCH_CASE = 1, REX_SEARCH_REGEX = 2 };
enum rex_search_error { REX_SEARCH_INVALID = 1, REX_SEARCH_TOO_COMPLEX = 2 };

/* Search the pane's screen and scrollback (on its worker thread: never blocks), and keep the
 * results up to date as output arrives. query NULL or "" stops searching and clears the
 * highlights. Literal text unless REX_SEARCH_REGEX (Rust regex syntax); case-insensitive unless
 * REX_SEARCH_MATCH_CASE. Matches never span lines but do span soft wraps. A new query makes the
 * last match at or above the viewport's bottom row current (else the first below it) and
 * scrolls it into view; a query replacing another starts where that one's current match was.
 * Results arrive in frames: matches / match_count, search_*.
 * Returns 0, -1 unknown pane, -3 invalid regular expression (the pane's search is cleared).
 * Only the syntax is checked here; the worker compiles the pattern, and one too big to compile
 * shows as search_error = REX_SEARCH_TOO_COMPLEX in frames. */
int32_t rex_pane_search(rex_core *core, uint64_t pane, const char *query, uint32_t flags);
/* Move the current match: direction > 0 the next one down (toward newer output), < 0 the next
 * one up; wraps around. Scrolls the viewport to it unless it is already entirely in view. */
int32_t rex_pane_search_step(rex_core *core, uint64_t pane, int32_t direction);

/* The cursor shape (rex_cursor_shape) used while the program has not chosen one with DECSCUSR
 * (and after it resets it with CSI 0 SP q). Kept across snapshots. Default: block. */
int32_t rex_pane_set_default_cursor(rex_core *core, uint64_t pane, int32_t shape);

/* --------------------------------------------------------------- frames */

enum rex_cell_flags {
    REX_CELL_BOLD      = 1 << 0,
    REX_CELL_ITALIC    = 1 << 1,
    REX_CELL_FAINT     = 1 << 2,
    REX_CELL_BLINK     = 1 << 3,
    REX_CELL_STRIKE    = 1 << 4,
    REX_CELL_OVERLINE  = 1 << 5,
    REX_CELL_UL_SINGLE = 1 << 6,
    REX_CELL_UL_DOUBLE = 1 << 7,
    REX_CELL_UL_CURLY  = 1 << 8,
    REX_CELL_UL_DOTTED = 1 << 9,
    REX_CELL_UL_DASHED = 1 << 10,
    REX_CELL_WIDE      = 1 << 11, /* glyph spans this cell and the next          */
    REX_CELL_SPACER    = 1 << 12, /* tail of a wide glyph: draw nothing          */
    REX_CELL_SELECTED  = 1 << 13,
    REX_CELL_LINK      = 1 << 14, /* OSC 8 hyperlink                              */
    REX_CELL_DEFAULT_BG= 1 << 15  /* bg is the terminal default (UI may let the card show through) */
};

typedef struct rex_cell {
    uint32_t fg;        /* 0x00RRGGBB, inverse already applied */
    uint32_t bg;        /* 0x00RRGGBB, inverse already applied */
    uint32_t ul_color;  /* 0x00RRGGBB, or 0xFFFFFFFF = use fg */
    uint32_t text_off;  /* offset (UTF-16 units) into rex_frame.text */
    uint16_t text_len;  /* UTF-16 units of this cell's grapheme cluster; 0 = blank */
    uint16_t flags;     /* rex_cell_flags */
} rex_cell;             /* 20 bytes, packed naturally */

enum rex_cursor_shape { REX_CURSOR_BLOCK = 0, REX_CURSOR_BAR = 1, REX_CURSOR_UNDERLINE = 2, REX_CURSOR_HOLLOW = 3 };

/* One viewport row's part of a find-in-terminal match (a match across a wrap has one per row). */
typedef struct rex_match {
    uint16_t row;           /* viewport row */
    uint16_t col0, col1;    /* first and last cell on that row, inclusive (a wide glyph's 2 cells) */
    uint16_t current;       /* 1: part of the current match */
} rex_match;                /* 8 bytes */

typedef struct rex_frame {
    uint32_t        abi;            /* REXCORE_ABI_VERSION */
    uint16_t        cols, rows;
    const rex_cell *cells;          /* rows * cols, row-major */
    const uint16_t *text;           /* UTF-16 pool referenced by cells */
    uint32_t        text_len;
    uint32_t        fg, bg;         /* terminal default colors 0x00RRGGBB */
    uint32_t        cursor_color;   /* 0x00RRGGBB */
    int16_t         cursor_x, cursor_y; /* viewport cell, -1 when hidden/off-screen */
    uint8_t         cursor_shape;   /* rex_cursor_shape */
    uint8_t         cursor_blink;
    uint8_t         mouse_tracking; /* program wants mouse reports */
    uint8_t         alt_screen;
    uint8_t         paused;         /* input paused: transport down / reconnecting */
    uint8_t         bracketed_paste;
    uint8_t         focus_reporting;
    uint8_t         _pad;
    uint64_t        generation;     /* increments on every new frame */
    uint64_t        total_rows;     /* scrollback + screen rows */
    uint64_t        viewport_top;   /* row index (from top of scrollback) of viewport row 0 */
    const uint8_t  *wrapped;        /* rows entries: row soft-wraps into the next */
    const char     *title;          /* UTF-8 terminal title (OSC 0/2), may be "" */
    const char     *pwd;            /* UTF-8 OSC 7 working directory, may be "" */
    /* Find in terminal (rex_pane_search); all zero when not searching. */
    const rex_match *matches;       /* match_count spans on the viewport, top to bottom */
    uint32_t        match_count;
    uint32_t        search_total;   /* matches found so far (screen and scrollback) */
    uint32_t        search_current; /* 1-based place of the current match counting from the newest
                                     * (bottom) one, as Find next walks: 1 = newest; 0 none */
    uint8_t         search_active;  /* a query is set */
    uint8_t         search_busy;    /* scrollback still being read: counts may grow */
    uint8_t         search_error;   /* rex_search_error: the query cannot be searched for; 0 none */
    uint8_t         _pad2;
} rex_frame;

/* Lock the latest frame of a pane for reading. Returns NULL if the pane is unknown or
 * has no terminal yet. The pointer and everything it references stay valid and
 * unchanged until rex_pane_frame_unlock. Locking also re-arms REX_EV_PANE_DIRTY. */
const rex_frame *rex_pane_frame_lock(rex_core *core, uint64_t pane);
void             rex_pane_frame_unlock(rex_core *core, uint64_t pane);

/* ------------------------------------------------- local panes (no server) */
/* A standalone libghostty-vt terminal for previews, tests and demo mode.
 * Bytes fed are parsed as if they came from a PTY; input to it is discarded
 * (or echoed when echo != 0). Close with rex_local_pane_close. */
uint64_t rex_local_pane_new(rex_core *core, uint16_t cols, uint16_t rows, int32_t echo);
int32_t  rex_local_pane_feed(rex_core *core, uint64_t pane, const uint8_t *bytes, size_t len);
void     rex_local_pane_close(rex_core *core, uint64_t pane);

#ifdef __cplusplus
}
#endif
#endif /* REXCORE_H */
