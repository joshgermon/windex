//! What a terminal block's programs report about themselves, and what the UI shows for it:
//! titles (terminal title or a generated `program - ~/dir`), program name and icon key, working
//! directory, OSC 7501 status records, OSC 9;4 progress, bell and exit (docs/protocol.md §8).
//!
//! The parsing and title rules are ported from Rex Rune's `src/connection/activity.rs`,
//! `src/connection/titles.rs` and `src/app/activity.rs` (MIT). Icon keys follow
//! docs/design.md §8.3.
use serde_json::{Value, json};
use std::collections::HashMap;

pub const TERMINAL_CREATOR: &str = "com.superlogical.terminal";

/// A status record's state, in priority order (a tab shows its highest).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord)]
pub enum State {
    #[default]
    Idle,
    Done,
    Working,
    Error,
    Blocked,
}

impl State {
    fn parse(value: &str) -> Self {
        match value {
            "working" => Self::Working,
            "done" => Self::Done,
            "blocked" => Self::Blocked,
            "error" => Self::Error,
            _ => Self::Idle,
        }
    }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Record {
    pub state: State,
    /// `permission`, `question` or `auth` while blocked.
    pub kind: Option<String>,
    pub app: Option<String>,
    pub message: Option<String>,
    pub progress: Option<u8>,
}

impl Record {
    pub fn parse(value: &Value) -> Self {
        let text = |key: &str| value[key].as_str().filter(|s| !s.is_empty()).map(str::to_owned);
        Self {
            state: State::parse(value["state"].as_str().unwrap_or_default()),
            kind: text("kind"),
            app: text("effective_app").or_else(|| text("app")),
            message: text("msg").or_else(|| text("title")),
            progress: percent(&value["progress"]),
        }
    }
}

/// An OSC 9;4 report.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Progress {
    Set(Option<u8>),
    Indeterminate,
    Paused(Option<u8>),
    Error(Option<u8>),
}

impl Progress {
    fn rank(self) -> u8 {
        match self {
            Self::Error(_) => 2,
            Self::Paused(_) => 1,
            Self::Set(_) | Self::Indeterminate => 0,
        }
    }

    pub fn json(self) -> Value {
        let (state, percent) = match self {
            Self::Set(p) => ("working", p),
            Self::Indeterminate => ("indeterminate", None),
            Self::Paused(p) => ("paused", p),
            Self::Error(p) => ("error", p),
        };
        json!({ "state": state, "percent": percent })
    }
}

fn percent(value: &Value) -> Option<u8> {
    value.as_u64().filter(|p| *p <= 100).map(|p| p as u8)
}

/// Everything known about one terminal block's programs.
#[derive(Clone, Debug, Default)]
pub struct Activity {
    /// Terminal title and the pid that set it.
    pub title: Option<(String, Option<u64>)>,
    /// Foreground program name, its executable and script path.
    pub program: Option<String>,
    pub executable: Option<String>,
    pub script: Option<String>,
    /// Foreground pid, then its ancestors.
    pub pids: Vec<u64>,
    pub cwd: Option<String>,
    pub home: Option<String>,
    /// The terminal's own shell, when its process is a shell.
    pub shell_pid: Option<u64>,
    /// OSC 7 directory (`pwd_changed`), as a path.
    pub pwd: Option<String>,
    pub records: HashMap<String, Record>,
    pub progress: Option<Progress>,
    /// The bell rang while the block was out of sight.
    pub bell_unseen: bool,
    /// A program finished (working/blocked → done/idle) while out of sight.
    pub done_unseen: bool,
    pub exited: bool,
}

/// What a `block_event` changed that the host should act on beyond the model.
#[derive(Debug, PartialEq)]
pub enum Effect {
    None,
    /// A program went from busy to finished.
    Finished { app: Option<String> },
    /// A program now waits for the user.
    NeedsInput { word: &'static str, message: Option<String>, app: Option<String> },
    Bell,
    Notification { title: String, body: String },
}

impl Activity {
    /// The highest-priority status record.
    pub fn deciding(&self) -> Option<&Record> {
        self.records.values().max_by_key(|r| r.state)
    }

    /// Apply a terminal `block_event` (name + payload).
    pub fn apply_event(&mut self, name: &str, body: &Value) -> Effect {
        match name {
            "title_changed" => self.set_title(body),
            "process_changed" => self.set_process(body),
            "pwd_changed" => {
                self.pwd = body["pwd"].as_str().and_then(file_url_path);
            }
            "program_status_changed" => {
                let before = self.deciding().map(|r| r.state).unwrap_or_default();
                let id = body["id"].as_str().unwrap_or_default().to_owned();
                let record = Record::parse(&body["record"]);
                self.records.insert(id, record);
                return self.transition(before);
            }
            "program_status_removed" => {
                let before = self.deciding().map(|r| r.state).unwrap_or_default();
                for id in body["ids"].as_array().into_iter().flatten().filter_map(Value::as_str) {
                    self.records.remove(id);
                }
                return self.transition(before);
            }
            "progress_report" => {
                let p = percent(&body["progress"]);
                self.progress = match body["state"].as_str() {
                    Some("set") => Some(Progress::Set(p)),
                    Some("indeterminate") => Some(Progress::Indeterminate),
                    Some("pause") => Some(Progress::Paused(p)),
                    Some("error") => Some(Progress::Error(p)),
                    Some("remove") => None,
                    _ => self.progress,
                };
            }
            "bell" => return Effect::Bell,
            "child_exited" => self.exited = true,
            "desktop_notification" => {
                return Effect::Notification {
                    title: body["title"].as_str().unwrap_or_default().to_owned(),
                    body: body["body"].as_str().unwrap_or_default().to_owned(),
                };
            }
            _ => {}
        }
        Effect::None
    }

    fn transition(&self, before: State) -> Effect {
        let top = self.deciding();
        let now = top.map(|r| r.state).unwrap_or_default();
        let app = top.and_then(|r| r.app.clone());
        match top {
            Some(r) if r.state == State::Blocked && before != State::Blocked => {
                Effect::NeedsInput { word: blocked_word(r.kind.as_deref()), message: r.message.clone(), app }
            }
            _ if matches!(before, State::Working | State::Blocked) && matches!(now, State::Done | State::Idle) => Effect::Finished { app },
            _ => Effect::None,
        }
    }

    pub fn set_title(&mut self, body: &Value) {
        let text = body["title"].as_str().unwrap_or_default().trim().to_owned();
        self.title = (!text.is_empty()).then(|| (text, body["owner"]["pid"].as_u64()));
    }

    /// A `process_changed` payload or a `…terminal.process` reply.
    pub fn set_process(&mut self, body: &Value) {
        let foreground = &body["foreground"];
        let pids: Vec<u64> = std::iter::once(foreground)
            .chain(body["ancestors"].as_array().into_iter().flatten())
            .filter_map(|p| p["pid"].as_u64())
            .collect();
        let program = basename_of(foreground, &["script", "argv0", "name"]);
        // A progress report belongs to the program that made it.
        let left = self.pids.first().is_some_and(|pid| !pids.contains(pid));
        if left || program != self.program {
            self.progress = None;
        }
        self.program = program;
        self.executable = basename_of(foreground, &["name", "argv0"]);
        self.script = foreground["script"].as_str().filter(|s| !s.is_empty()).map(|s| s.replace('\\', "/"));
        self.pids = pids;
        let place = place(body);
        self.cwd = place.0;
        if place.1.is_some() {
            self.home = place.1;
        }
        self.shell_pid = place.2;
    }

    /// The terminal title while the program that set it is still in the foreground chain.
    fn current_title(&self) -> Option<&str> {
        let (text, owner) = self.title.as_ref()?;
        match owner {
            Some(pid) if !self.pids.is_empty() && !self.pids.contains(pid) => None,
            _ => Some(text),
        }
    }

    fn shell_titled(&self) -> bool {
        let owner = self.title.as_ref().and_then(|(_, owner)| *owner);
        owner.is_some() && owner == self.shell_pid
    }

    /// The directory a title shows (`~/src/rex`), from the foreground's cwd or OSC 7.
    pub fn directory(&self) -> Option<String> {
        let cwd = self.cwd.as_deref().or(self.pwd.as_deref())?;
        Some(directory(cwd, self.home.as_deref()))
    }

    /// What the terminal is called while it has no user label: a title a program set for
    /// itself; else `program - ~/dir`; else whatever title there is.
    pub fn shown_title(&self) -> Option<String> {
        let title = self.current_title();
        if title.is_some() && !self.shell_titled() {
            return title.map(str::to_owned);
        }
        let generated = self.program.as_ref().map(|program| match self.directory() {
            Some(dir) => format!("{program} - {dir}"),
            None => program.clone(),
        });
        generated.or(title.map(str::to_owned))
    }

    /// The working directory the UI shows and new splits inherit.
    pub fn working_directory(&self) -> Option<&str> {
        self.cwd.as_deref().or(self.pwd.as_deref())
    }

    /// The program the pane shows an icon for: a status report's app, else the foreground.
    pub fn icon_program(&self) -> Option<&str> {
        self.deciding().and_then(|r| r.app.as_deref()).or(self.program.as_deref())
    }

    pub fn icon(&self) -> &'static str {
        let reporting_agent = self.deciding().is_some_and(|r| r.app.is_some());
        let key = self
            .icon_program()
            .and_then(icon_key)
            .or_else(|| self.executable.as_deref().and_then(icon_key));
        match key {
            Some(key) => key,
            None if reporting_agent => "agent",
            None => "shell",
        }
    }

    /// The pane's status word: trouble, then work, then an unseen finish.
    pub fn status(&self) -> Option<(&'static str, &'static str, Option<String>)> {
        match self.deciding() {
            Some(r) if r.state == State::Blocked => Some((blocked_word(r.kind.as_deref()), "warning", r.message.clone())),
            Some(r) if r.state == State::Error => Some(("Error", "danger", r.message.clone())),
            Some(r) if r.state == State::Working => Some(("Working", "accent", r.message.clone())),
            _ if self.done_unseen => Some(("Done", "accent", None)),
            _ => None,
        }
    }

    /// The progress bar: the program's OSC 9;4 report, else a working record's percentage.
    pub fn shown_progress(&self) -> Option<Progress> {
        self.progress.or_else(|| {
            self.deciding()
                .filter(|r| r.state == State::Working)
                .and_then(|r| r.progress)
                .map(|p| Progress::Set(Some(p)))
        })
    }
}

/// A tab's word and tone from its panes (Rex Rune `tab_word`): trouble, an unseen bell, a
/// paused/failed progress report, then an unseen finish.
pub fn tab_word<'a>(panes: impl Iterator<Item = &'a Activity> + Clone) -> Option<(String, &'static str)> {
    let top = panes.clone().filter_map(Activity::deciding).max_by_key(|r| r.state);
    match top.map(|r| r.state) {
        Some(State::Blocked) => return Some((blocked_word(top.and_then(|r| r.kind.as_deref())).into(), "warning")),
        Some(State::Error) => return Some(("Error".into(), "danger")),
        _ => {}
    }
    if panes.clone().any(|a| a.bell_unseen) {
        return Some(("Bell".into(), "warning"));
    }
    match tab_progress(panes.clone()) {
        Some(Progress::Error(_)) => return Some(("Error".into(), "danger")),
        Some(Progress::Paused(Some(n))) => return Some((format!("Paused {n}%"), "warning")),
        Some(Progress::Paused(None)) => return Some(("Paused".into(), "warning")),
        _ => {}
    }
    panes.clone().any(|a| a.done_unseen).then(|| ("Done".into(), "accent"))
}

/// A tab's progress bar: the most pressing report among its panes; none while trouble shows.
pub fn tab_progress<'a>(panes: impl Iterator<Item = &'a Activity> + Clone) -> Option<Progress> {
    if panes.clone().filter_map(Activity::deciding).any(|r| matches!(r.state, State::Blocked | State::Error)) {
        return None;
    }
    panes.filter_map(Activity::shown_progress).max_by_key(|p| p.rank())
}

fn blocked_word(kind: Option<&str>) -> &'static str {
    match kind {
        Some("permission") => "Needs approval",
        Some("question") => "Question",
        Some("auth") => "Needs sign-in",
        _ => "Needs input",
    }
}

fn basename_of(process: &Value, keys: &[&str]) -> Option<String> {
    keys.iter()
        .filter_map(|key| process[*key].as_str())
        .find(|s| !s.is_empty())
        .map(|s| s.rsplit(['/', '\\']).next().unwrap_or(s).trim_start_matches('-'))
        .filter(|s| !s.is_empty())
        .map(str::to_owned)
}

const SHELLS: &[&str] = &["sh", "bash", "zsh", "fish", "dash", "ash", "ksh", "mksh", "csh", "tcsh", "nu", "xonsh", "elvish", "pwsh"];

/// (foreground cwd, home, own shell pid) from a process payload (Rex Rune `Place::parse`).
fn place(process: &Value) -> (Option<String>, Option<String>, Option<u64>) {
    let chain: Vec<&Value> = std::iter::once(&process["foreground"])
        .chain(process["ancestors"].as_array().into_iter().flatten())
        .filter(|p| p.is_object())
        .collect();
    let path = |value: &Value| value.as_str().filter(|dir| dir.starts_with('/')).map(str::to_owned);
    let child = &process["child"];
    let own = if child.is_object() && child["name"] != "login" {
        Some(child)
    } else {
        match chain.as_slice() {
            [.., shell, login] if login["name"] == "login" => Some(*shell),
            [.., own] if own["name"] != "login" => Some(*own),
            _ => None,
        }
    };
    let shell = own
        .filter(|p| basename_of(p, &["script", "argv0", "name"]).is_some_and(|n| SHELLS.contains(&n.as_str())))
        .and_then(|p| p["pid"].as_u64());
    (chain.iter().find_map(|p| path(&p["cwd"])), path(&process["home"]), shell)
}

/// `file://host/path` (OSC 7) → `/path`.
fn file_url_path(url: &str) -> Option<String> {
    if url.starts_with('/') {
        return Some(url.to_owned());
    }
    let parsed = url::Url::parse(url).ok()?;
    let path = percent_encoding::percent_decode_str(parsed.path()).decode_utf8_lossy().into_owned();
    (!path.is_empty()).then_some(path)
}

/// The most characters a title gives its directory.
const DIRECTORY_ROOM: usize = 24;

/// A directory as a title shows it: home as `~`, and of a long path only its last folders.
pub fn directory(cwd: &str, home: Option<&str>) -> String {
    let home = home.map(|h| h.trim_end_matches('/')).filter(|h| !h.is_empty());
    let dir = match home.and_then(|h| cwd.strip_prefix(h)) {
        Some("") => "~".to_owned(),
        Some(rest) if rest.starts_with('/') => format!("~{rest}"),
        _ => cwd.to_owned(),
    };
    if dir.chars().count() <= DIRECTORY_ROOM {
        return dir;
    }
    let mut kept = Vec::new();
    let mut used = 1;
    for folder in dir.rsplit('/').filter(|f| !f.is_empty()) {
        used += 1 + folder.chars().count();
        if !kept.is_empty() && used > DIRECTORY_ROOM {
            break;
        }
        kept.push(folder);
    }
    kept.reverse();
    format!("…/{}", kept.join("/"))
}

/// docs/design.md §8.3: program → icon key.
const ICONS: &[(&str, &[&str])] = &[
    ("shell", &["sh", "bash", "zsh", "fish", "dash", "ash", "ksh", "mksh", "csh", "tcsh", "nu", "xonsh", "elvish", "login"]),
    ("pwsh", &["pwsh", "powershell"]),
    ("cmd", &["cmd"]),
    ("wsl", &["wsl", "wslhost"]),
    ("node", &["node", "npm", "npx", "tsx", "ts-node", "nodemon", "pnpm", "yarn", "vite"]),
    ("bun", &["bun", "bunx"]),
    ("deno", &["deno"]),
    ("python", &["python", "ipython", "pip", "pytest", "uv", "uvx", "poetry", "jupyter"]),
    ("ruby", &["ruby", "irb", "bundle", "rails", "rake", "gem"]),
    ("go", &["go", "gopls"]),
    ("rust", &["cargo", "rustc", "rustup", "rust-analyzer"]),
    ("dotnet", &["dotnet", "msbuild"]),
    ("java", &["java", "gradle", "gradlew", "mvn", "kotlin"]),
    ("build", &["make", "cmake", "ninja", "gcc", "g++", "clang", "cl", "bazel", "just"]),
    ("test", &["jest", "vitest", "playwright", "mocha"]),
    ("vim", &["vim", "vi", "gvim", "view", "vimdiff"]),
    ("neovim", &["nvim", "nvim-qt", "neovide"]),
    ("helix", &["hx", "helix"]),
    ("emacs", &["emacs", "emacsclient"]),
    ("editor", &["nano", "micro", "pico", "edit"]),
    ("vscode", &["code", "code-insiders"]),
    ("git", &["git", "lazygit", "gitui", "tig", "jj"]),
    ("github", &["gh"]),
    ("monitor", &["htop", "btop", "top", "btm", "glances", "nvtop", "atop"]),
    ("container", &["docker", "docker-compose", "lazydocker", "podman"]),
    ("kube", &["kubectl", "k9s", "helm", "kubectx"]),
    ("ssh", &["ssh", "mosh", "et", "autossh"]),
    ("tailscale", &["tailscale"]),
    ("pager", &["less", "more", "bat", "most"]),
    ("manual", &["man", "tldr", "info"]),
    ("logs", &["tail", "journalctl", "lnav", "multitail"]),
    ("wait", &["sleep", "watch", "timeout"]),
    ("database", &["psql", "pgcli", "mysql", "sqlite3", "redis-cli", "duckdb", "mongosh"]),
    ("http", &["curl", "wget", "http", "xh"]),
    ("infra", &["terraform", "tofu", "pulumi", "ansible", "az", "aws", "gcloud"]),
    ("claude", &["claude", "claude-code"]),
    ("codex", &["codex"]),
    ("gemini", &["gemini"]),
    ("opencode", &["opencode"]),
    ("copilot", &["copilot", "gh-copilot"]),
    ("cursor", &["cursor-agent"]),
    ("aider", &["aider"]),
    ("amp", &["amp"]),
    ("llm", &["ollama", "llama-server", "llama-cli"]),
    ("fun", &["sl", "cmatrix", "cowsay", "lolcat", "asciiquarium"]),
];

/// The icon key for a command name: case-insensitive, without a path, `.exe`/`.cmd`/`.bat`/
/// `.ps1`, a login shell's `-`, or a version suffix (`python3.12`).
pub fn icon_key(command: &str) -> Option<&'static str> {
    let name = command.rsplit(['/', '\\']).next().unwrap_or(command).trim_start_matches('-').to_ascii_lowercase();
    let name = [".exe", ".cmd", ".bat", ".ps1"].iter().find_map(|ext| name.strip_suffix(ext)).unwrap_or(&name);
    let find = |n: &str| ICONS.iter().find(|(_, names)| names.contains(&n)).map(|(key, _)| *key);
    find(name).or_else(|| {
        let bare = name.trim_end_matches(|c: char| c.is_ascii_digit() || c == '.' || c == '-');
        (!bare.is_empty() && bare != name).then(|| find(bare)).flatten()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn with_process(shell: &str, program: Option<(&str, &str)>, cwd: &str) -> Activity {
        let root = json!({"pid": 1, "name": shell, "argv0": format!("-{shell}"), "cwd": cwd});
        let body = match program {
            Some((name, argv0)) => json!({"home": "/home/u", "ancestors": [root], "foreground": {"pid": 7, "name": name, "argv0": argv0, "cwd": cwd}}),
            None => json!({"home": "/home/u", "foreground": root}),
        };
        let mut a = Activity::default();
        a.set_process(&body);
        a
    }

    #[test]
    fn generated_titles_replace_shell_titles() {
        let mut a = with_process("zsh", Some(("nvim", "nvim")), "/home/u/rex");
        assert_eq!(a.shown_title().as_deref(), Some("nvim - ~/rex"));
        a.set_title(&json!({"title": "joshua@dasher:~/rex", "owner": {"pid": 1, "name": "zsh"}}));
        assert_eq!(a.shown_title().as_deref(), Some("nvim - ~/rex"), "the shell's own title gives way");
        a.set_title(&json!({"title": "main.rs - NVIM", "owner": {"pid": 7, "name": "nvim"}}));
        assert_eq!(a.shown_title().as_deref(), Some("main.rs - NVIM"), "a program's title stays");
        let shell = with_process("zsh", None, "/home/u");
        assert_eq!(shell.shown_title().as_deref(), Some("zsh - ~"));
        assert_eq!(shell.program.as_deref(), Some("zsh"), "login dash stripped");
    }

    #[test]
    fn titles_go_stale_with_their_program() {
        let mut a = with_process("zsh", Some(("htop", "htop")), "/home/u");
        a.set_title(&json!({"title": "htop", "owner": {"pid": 7}}));
        a.set_process(&json!({"home": "/home/u", "foreground": {"pid": 1, "name": "zsh", "argv0": "-zsh", "cwd": "/home/u"}}));
        assert_eq!(a.shown_title().as_deref(), Some("zsh - ~"));
    }

    #[test]
    fn long_directories_keep_their_last_folders() {
        assert_eq!(directory("/home/u", Some("/home/u/")), "~");
        assert_eq!(directory("/home/u/src", Some("/home/u")), "~/src");
        assert_eq!(directory("/srv/a/very/long/path/to/project", None), "…/long/path/to/project");
        assert_eq!(directory("/x/a-folder-name-longer-than-room-allows", None), "…/a-folder-name-longer-than-room-allows");
    }

    #[test]
    fn icon_keys() {
        for (cmd, key) in [
            ("zsh", "shell"),
            ("-bash", "shell"),
            ("nvim", "neovim"),
            ("vim", "vim"),
            ("node", "node"),
            ("python3.12", "python"),
            ("python3", "python"),
            ("/usr/bin/git", "git"),
            ("claude", "claude"),
            ("codex", "codex"),
            ("ssh", "ssh"),
            ("htop", "monitor"),
            ("docker", "container"),
            ("PWSH.EXE", "pwsh"),
            ("sleep", "wait"),
        ] {
            assert_eq!(icon_key(cmd), Some(key), "{cmd}");
        }
        assert_eq!(icon_key("mystery"), None);
        assert_eq!(icon_key("x264"), None);
    }

    #[test]
    fn icons_follow_status_app_then_program_then_interpreter() {
        let mut a = with_process("zsh", Some(("node", "claude")), "/home/u");
        assert_eq!((a.program.as_deref(), a.icon()), (Some("claude"), "claude"));
        a = with_process("zsh", Some(("node", "my-script")), "/home/u");
        assert_eq!(a.icon(), "node", "unknown script shows its interpreter");
        a = with_process("zsh", Some(("mystery", "mystery")), "/home/u");
        assert_eq!(a.icon(), "shell");
        a.apply_event("program_status_changed", &json!({"id": "", "record": {"state": "working", "app": "some-agent"}}));
        assert_eq!(a.icon(), "agent");
        a.apply_event("program_status_changed", &json!({"id": "", "record": {"state": "working", "app": "codex"}}));
        assert_eq!(a.icon(), "codex");
    }

    #[test]
    fn status_records_and_transitions() {
        let mut a = Activity::default();
        assert_eq!(a.apply_event("program_status_changed", &json!({"id": "", "record": {"state": "working", "app": "claude", "progress": 40}})), Effect::None);
        assert_eq!(a.status().map(|s| s.0), Some("Working"));
        assert_eq!(a.shown_progress(), Some(Progress::Set(Some(40))));
        let effect = a.apply_event("program_status_changed", &json!({"id": "", "record": {"state": "blocked", "kind": "permission", "app": "claude"}}));
        assert_eq!(effect, Effect::NeedsInput { word: "Needs approval", message: None, app: Some("claude".into()) });
        assert_eq!(tab_word([&a].into_iter()), Some(("Needs approval".into(), "warning")));
        let effect = a.apply_event("program_status_removed", &json!({"ids": [""], "reason": "prompt"}));
        assert_eq!(effect, Effect::Finished { app: None });
        assert_eq!(a.status(), None);
    }

    #[test]
    fn tab_words_and_progress() {
        let mut a = Activity::default();
        a.apply_event("progress_report", &json!({"state": "pause", "progress": 30}));
        assert_eq!(tab_word([&a].into_iter()), Some(("Paused 30%".into(), "warning")));
        assert_eq!(tab_progress([&a].into_iter()).map(Progress::json), Some(json!({"state": "paused", "percent": 30})));
        let mut b = Activity { bell_unseen: true, ..Activity::default() };
        assert_eq!(tab_word([&a, &b].into_iter()), Some(("Bell".into(), "warning")));
        a.apply_event("progress_report", &json!({"state": "remove"}));
        b.bell_unseen = false;
        b.done_unseen = true;
        assert_eq!(tab_word([&a, &b].into_iter()), Some(("Done".into(), "accent")));
        assert_eq!(tab_word([&Activity::default()].into_iter()), None);
    }

    #[test]
    fn pwd_events() {
        let mut a = Activity::default();
        a.apply_event("pwd_changed", &json!({"pwd": "file://dasher/home/joshua/my%20dir"}));
        assert_eq!(a.working_directory(), Some("/home/joshua/my dir"));
    }
}
