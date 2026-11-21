// MockCore: a believable fake Rex world for UI development, demos and screenshots (--mock).
// Hosts are added through add_host like the real core; known demo names get a seeded server
// (sessions, tabs, split layouts, activity), anything else gets a fresh one-session server.
// Mutating commands change the fake model and publish a new snapshot on the UI dispatcher.
// Pane content: when rexcore.dll is present, each demo pane is backed by a local libghostty pane
// (rex_local_pane_new, echoing input) fed its MockScreens screen, so the demo runs through the
// real terminal renderer; without the DLL the panes are blank.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Windex.Interop;
using Windex.Services;

namespace Windex.Core;

public sealed unsafe class MockCore : ICore
{
    /// <summary>The demo host list the app registers on MockCore (not persisted).</summary>
    public static readonly IReadOnlyList<SavedHost> DemoHosts =
    [
        new() { Id = "dasher", Name = "dasher", Endpoint = "ssh://joshua@dasher", Icon = "linux" },
        new() { Id = "comet", Name = "comet", Endpoint = "https://comet.tail4f2e1.ts.net", Icon = "cloud" },
        new() { Id = "optiplex", Name = "optiplex", Endpoint = "ssh://joshua@optiplex", Icon = "desktop" },
        new() { Id = "rex", Name = "rex", Endpoint = "ssh://joshua@rex", Icon = "server" },
    ];

    /// <summary>The session the demo opens at startup.</summary>
    public static readonly (string Host, string Session) DemoStartSession = ("dasher", "amber-sierra");

    private readonly DispatcherQueue _dispatcher;
    private readonly List<MHost> _hosts = new();
    private CurrentRef? _current;
    private long _revision;
    private ulong _nextPane = 1;
    private int _nextId = 1;
    private long _nextReq;
    private bool _publishQueued;
    /// <summary>Real local terminals behind the demo panes (null without rexcore.dll).</summary>
    private readonly NativeCore? _terminals;
    private readonly Dictionary<ulong, ulong> _localOf = new();
    private readonly Dictionary<ulong, ulong> _mockOf = new();
    private string _themeOsc = "";

    public RexModel Model { get; private set; } = RexModel.Empty;
    public event EventHandler<RexModel>? ModelChanged;
    public event EventHandler<IReadOnlyList<DiscoveredMachine>>? DiscoveryUpdated;
    public event EventHandler<ulong>? PaneDirty;
    // MockCore has no programs, so these never fire.
    public event EventHandler<ulong>? Bell { add { } remove { } }
    public event EventHandler<Notice>? NoticeRaised { add { } remove { } }
    public event EventHandler<DesktopNotify>? NotificationRaised { add { } remove { } }
    public event EventHandler<(ulong Pane, string Text)>? ClipboardRequested { add { } remove { } }

    public MockCore(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        try
        {
            _terminals = new NativeCore(dispatcher, Path.Combine(Path.GetTempPath(), "Windex-demo"));
            _terminals.PaneDirty += (_, local) => { if (_mockOf.TryGetValue(local, out var pane)) PaneDirty?.Invoke(this, pane); };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or BadImageFormatException)
        {
            _terminals = null;
        }
    }

    /// <summary>
    /// The local terminal behind a demo pane, created on the pane's first resize: at a generous
    /// size first so the screen lays out unwrapped, then the view's size reflows it.
    /// </summary>
    private ulong LocalFor(ulong pane, bool create)
    {
        if (_localOf.TryGetValue(pane, out var local)) return local;
        if (!create || _terminals is null || FindPane(pane) is null) return 0;
        local = _terminals.LocalPaneNew(160, 50, echo: true);
        if (local == 0) return 0;
        _localOf[pane] = local;
        _mockOf[local] = pane;
        _terminals.LocalPaneFeed(local, Encoding.UTF8.GetBytes(_themeOsc + MockScreens.ToAnsi(MockScreens.Get(ScreenFor(pane)))));
        return local;
    }

    /// <summary>Close the local terminals of panes that are gone.</summary>
    private void DropGoneTerminals()
    {
        if (_terminals is null || _localOf.Count == 0) return;
        var alive = Model.Hosts.SelectMany(h => h.Sessions).SelectMany(s => s.Windows).SelectMany(w => w.Panes).Select(p => p.Pane).ToHashSet();
        foreach (var (pane, local) in _localOf.Where(kv => !alive.Contains(kv.Key)).ToList())
        {
            _localOf.Remove(pane);
            _mockOf.Remove(local);
            _terminals.LocalPaneClose(local);
        }
    }

    /// <summary>The declared theme as OSC 4/10/11/12, applied to every demo terminal.</summary>
    private void ApplyTheme(JsonObject a)
    {
        if (a["theme"] is not JsonObject theme) return;
        static string X(string? hex) => hex is { Length: 7 } ? $"rgb:{hex[1..3]}/{hex[3..5]}/{hex[5..7]}" : "";
        const string Esc = "\u001b", St = "\u001b\\";
        var sb = new StringBuilder();
        if (theme["palette"] is JsonArray palette)
            for (int i = 0; i < palette.Count; i++)
                if (X((string?)palette[i]) is { Length: > 0 } c) sb.Append($"{Esc}]4;{i};{c}{St}");
        foreach (var (key, osc) in new[] { ("foreground", 10), ("background", 11), ("cursor", 12) })
            if (X((string?)theme[key]) is { Length: > 0 } c) sb.Append($"{Esc}]{osc};{c}{St}");
        _themeOsc = sb.ToString();
        if (_terminals is null) return;
        var bytes = Encoding.UTF8.GetBytes(_themeOsc);
        foreach (var local in _localOf.Values) _terminals.LocalPaneFeed(local, bytes);
    }

    /// <summary>Static screen id for the placeholder pane content (see MockScreens).</summary>
    public string? ScreenFor(ulong pane) => FindPane(pane)?.Pane.Screen;

    // ------------------------------------------------------------------ commands

    public Task<CommandResult> CommandAsync(string cmd, object? args = null)
    {
        long req = Interlocked.Increment(ref _nextReq);
        var a = args is null ? new JsonObject() : JsonSerializer.SerializeToNode(args)?.AsObject() ?? new JsonObject();
        try
        {
            if (cmd is "upload" or "download") return StartTransfer(req, cmd, a);
            var (ok, error, data) = Execute(cmd, a);
            if (ok) QueuePublish();
            JsonElement? element = data is null ? null : JsonSerializer.SerializeToElement(data);
            return Task.FromResult(new CommandResult(req, ok, error, element));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CommandResult(req, false, ex.Message, null));
        }
    }

    private (bool Ok, string? Error, object? Data) Execute(string cmd, JsonObject a)
    {
        switch (cmd)
        {
            case "add_host":
            {
                string id = Str(a, "id") ?? throw new ArgumentException("id required");
                if (_hosts.Any(h => h.Id == id)) return Fail("host exists");
                var host = new MHost
                {
                    Id = id,
                    Name = Str(a, "name") ?? id,
                    Endpoint = Str(a, "endpoint") ?? "",
                    State = Bool(a, "connect") ?? true ? "connecting" : "offline",
                };
                host.Kind = KindOf(host.Endpoint);
                _hosts.Add(host);
                if (host.State == "connecting") After(450, () => Seed(host));
                return Ok();
            }
            case "update_host":
            {
                var host = Host(a, "id");
                if (Str(a, "name") is { } name) host.Name = name;
                if (Str(a, "endpoint") is { } endpoint && endpoint != host.Endpoint)
                {
                    host.Endpoint = endpoint;
                    host.Kind = KindOf(endpoint);
                    Reconnect(host);
                }
                return Ok();
            }
            case "remove_host":
            {
                var host = Host(a, "id");
                _hosts.Remove(host);
                if (_current?.Host == host.Id) _current = null;
                return Ok();
            }
            case "connect_host":
                Reconnect(Host(a, "id"));
                return Ok();
            case "disconnect_host":
            {
                var host = Host(a, "id");
                host.State = "offline";
                host.Error = null;
                return Ok();
            }
            case "open_session":
            {
                var host = Host(a, "host");
                var session = Session(host, a);
                session.Attached = true;
                _current = new CurrentRef { Host = host.Id, Session = session.Id };
                return Ok();
            }
            case "new_session":
            {
                var host = Host(a, "host");
                if (host.State != "connected") return Fail($"{host.Name} is not connected");
                string label = Str(a, "label") is { Length: > 0 } l ? l : NextSessionName(host);
                if (host.Sessions.Any(s => s.Label == label)) return Fail($"a session named {label} already exists");
                var session = new MSession { Id = NewId("S"), Label = label };
                string cwd = Str(a, "cwd") is { Length: > 0 } c ? c : "~";
                var w = NewWindow("Shell", Shell(cwd, "zsh-new"));
                session.Windows.Add(w);
                session.ActiveWindow = w.Id;
                host.Sessions.Add(session);
                return Ok(new { session = session.Id });
            }
            case "rename_session":
                Session(Host(a, "host"), a).Label = Str(a, "label") ?? throw new ArgumentException("label required");
                return Ok();
            case "kill_session":
            {
                var host = Host(a, "host");
                var session = Session(host, a);
                host.Sessions.Remove(session);
                if (_current?.Session == session.Id) _current = null;
                return Ok();
            }
            case "new_tab":
            {
                var host = Host(a, "host");
                var session = Session(host, a);
                var focused = session.Windows.FirstOrDefault(w => w.Id == session.ActiveWindow);
                string cwd = Str(a, "cwd") ?? focused?.Panes.FirstOrDefault(p => p.Pane == focused.FocusedPane)?.Cwd ?? "~";
                var w = NewWindow("Shell", Shell(cwd, "zsh-new"));
                session.Windows.Add(w);
                session.ActiveWindow = w.Id;
                return Ok(new { window = w.Id });
            }
            case "select_tab":
            {
                var (_, session, w) = Window(a);
                session.ActiveWindow = w.Id;
                if (w.Word == "Done") { w.Word = null; w.Tone = null; } // seen
                return Ok();
            }
            case "rename_tab":
            {
                var (_, _, w) = Window(a);
                w.Label = Str(a, "label") is { Length: > 0 } label ? label : "Shell";
                return Ok();
            }
            case "close_tab":
            {
                var (_, session, w) = Window(a);
                CloseWindow(session, w);
                return Ok();
            }
            case "move_tab":
            {
                var (_, session, w) = Window(a);
                int index = Math.Clamp(Int(a, "index") ?? 0, 0, session.Windows.Count - 1);
                session.Windows.Remove(w);
                session.Windows.Insert(index, w);
                return Ok();
            }
            case "split":
            {
                var (pane, w, _, _) = PaneArg(a);
                string dir = Str(a, "direction") ?? "right";
                var leaf = Leaf(w.Layout, pane.Pane) ?? throw new InvalidOperationException("pane not in layout");
                var added = Shell(pane.Cwd ?? "~", "zsh-new");
                w.Panes.Add(added);
                bool after = dir is "right" or "down";
                var (oldLeaf, newLeaf) = (new MNode { Pane = leaf.Pane }, new MNode { Pane = added.Pane });
                leaf.Pane = 0;
                leaf.SplitId = "split:" + NewId("s");
                leaf.Axis = dir is "right" or "left" ? "row" : "column";
                leaf.Ratio = 0.5;
                leaf.First = after ? oldLeaf : newLeaf;
                leaf.Second = after ? newLeaf : oldLeaf;
                w.FocusedPane = added.Pane;
                w.Zoomed = null;
                return Ok(new { pane = added.Pane });
            }
            case "close_pane":
            {
                var (pane, w, session, _) = PaneArg(a);
                ClosePane(session, w, pane.Pane);
                return Ok();
            }
            case "focus_pane":
            {
                var (pane, w, session, _) = PaneArg(a);
                w.FocusedPane = pane.Pane;
                session.ActiveWindow = w.Id;
                return Ok();
            }
            case "focus_direction":
            {
                var (pane, w, _, _) = PaneArg(a);
                if (Neighbor(w, pane.Pane, Str(a, "direction") ?? "right") is { } next) w.FocusedPane = next;
                return Ok();
            }
            case "swap_pane":
            {
                var (pane, w, _, _) = PaneArg(a);
                if (Neighbor(w, pane.Pane, Str(a, "direction") ?? "right") is { } other)
                {
                    var (x, y) = (Leaf(w.Layout, pane.Pane)!, Leaf(w.Layout, other)!);
                    (x.Pane, y.Pane) = (y.Pane, x.Pane);
                }
                return Ok();
            }
            case "zoom_pane":
            {
                var (pane, w, _, _) = PaneArg(a);
                bool zoom = a["zoom"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : w.Zoomed != pane.Pane;
                if (w.Panes.Count < 2) zoom = false;
                w.Zoomed = zoom ? pane.Pane : null;
                if (zoom) w.FocusedPane = pane.Pane;
                return Ok();
            }
            case "set_split_ratio":
            {
                var host = Host(a, "host");
                var session = Session(host, a);
                string id = Str(a, "split") ?? throw new ArgumentException("split required");
                double ratio = Math.Clamp(Double(a, "ratio") ?? 0.5, 0.1, 0.9);
                foreach (var w in session.Windows)
                    if (FindSplit(w.Layout, id) is { } node) { node.Ratio = ratio; return Ok(); }
                return Fail("unknown split");
            }
            case "balance":
            {
                var (_, _, w) = Window(a);
                Balance(w.Layout);
                return Ok();
            }
            case "rename_pane":
            {
                var (pane, _, _, _) = PaneArg(a);
                pane.Label = Str(a, "label") is { Length: > 0 } label ? label : null;
                return Ok();
            }
            case "reconnect_pane":
            {
                var (pane, _, _, _) = PaneArg(a);
                pane.Paused = false;
                return Ok();
            }
            case "clear_pane" or "reset_pane" or "send_text":
                PaneArg(a);
                return Ok();
            case "app_focus":
                return Ok();
            case "set_theme":
                ApplyTheme(a);
                return Ok();
            case "discover":
                After(700, () => DiscoveryUpdated?.Invoke(this, Tailnet()));
                return Ok();
            case "list_dir":
                return Ok(new
                {
                    entries = new[] { "src", "docs", "scripts", "target", "node_modules" }.Select(n => new { name = n, dir = true })
                        .Concat(new[] { "README.md", "Cargo.toml", "build.log", "screenshot.png" }.Select(n => new { name = n, dir = false }))
                        .ToArray(),
                });
            case "server_status":
            {
                var host = Host(a, "host");
                return host.Server is null ? Fail($"{host.Name} is not connected") : Ok(new { version = host.Server.Version, api = host.Server.Api, label = host.Server.Label });
            }
            case "cancel_transfer":
            {
                var transfer = _transfers.FirstOrDefault(t => t.Id == Str(a, "id"));
                if (transfer is null) return Fail($"no transfer {Str(a, "id")}");
                EndTransfer(transfer, new CommandResult(transfer.Req, false, "canceled", null));
                return Ok();
            }
            default:
                return Fail("unknown command");
        }

        static (bool, string?, object?) Ok(object? data = null) => (true, null, data);
        static (bool, string?, object?) Fail(string error) => (false, error, null);
    }

    // ------------------------------------------------------------------ hot path (local terminals)

    public int Key(ulong pane, in KeyInput key) => LocalFor(pane, false) is var l and not 0 ? _terminals!.Key(l, key) : 0;
    public int Text(ulong pane, string text) => LocalFor(pane, false) is var l and not 0 ? _terminals!.Text(l, text) : 0;
    public int Paste(ulong pane, string text) => LocalFor(pane, false) is var l and not 0 ? _terminals!.Paste(l, text) : 0;
    public int Mouse(ulong pane, in RexMouse mouse) => 0;
    public int Resize(ulong pane, ushort cols, ushort rows, uint cellWidthPx, uint cellHeightPx) =>
        LocalFor(pane, true) is var l and not 0 ? _terminals!.Resize(l, cols, rows, cellWidthPx, cellHeightPx) : 0;
    public int Focus(ulong pane, bool focused) => LocalFor(pane, false) is var l and not 0 ? _terminals!.Focus(l, focused) : 0;
    public int Scroll(ulong pane, RexScrollOp op, long value) => LocalFor(pane, false) is var l and not 0 ? _terminals!.Scroll(l, op, value) : 0;
    public int Select(ulong pane, RexSelectOp op, int clicks, float x, float y) => LocalFor(pane, false) is var l and not 0 ? _terminals!.Select(l, op, clicks, x, y) : 0;
    public string? SelectionText(ulong pane) => LocalFor(pane, false) is var l and not 0 ? _terminals!.SelectionText(l) : null;
    public string? HyperlinkAt(ulong pane, int col, int row) => LocalFor(pane, false) is var l and not 0 ? _terminals!.HyperlinkAt(l, col, row) : null;
    public int Search(ulong pane, string? query, RexSearchFlags flags) => LocalFor(pane, false) is var l and not 0 ? _terminals!.Search(l, query, flags) : -1;
    public int SearchStep(ulong pane, int direction) => LocalFor(pane, false) is var l and not 0 ? _terminals!.SearchStep(l, direction) : -1;
    public int SetDefaultCursor(ulong pane, RexCursorShape shape) => LocalFor(pane, false) is var l and not 0 ? _terminals!.SetDefaultCursor(l, shape) : -1;
    public RexFrame* LockFrame(ulong pane) => LocalFor(pane, false) is var l and not 0 ? _terminals!.LockFrame(l) : null;
    public void UnlockFrame(ulong pane) { if (LocalFor(pane, false) is var l and not 0) _terminals!.UnlockFrame(l); }
    public ulong LocalPaneNew(ushort cols, ushort rows, bool echo) => _terminals?.LocalPaneNew(cols, rows, echo) ?? 0;
    public void LocalPaneFeed(ulong pane, ReadOnlySpan<byte> bytes) => _terminals?.LocalPaneFeed(pane, bytes);
    public void LocalPaneClose(ulong pane) => _terminals?.LocalPaneClose(pane);
    public void Dispose() => _terminals?.Dispose();

    // ------------------------------------------------------------------ publishing

    private void QueuePublish()
    {
        if (_publishQueued) return;
        _publishQueued = true;
        _dispatcher.TryEnqueue(Publish);
    }

    private void Publish()
    {
        _publishQueued = false;
        Model = Snapshot();
        DropGoneTerminals();
        ModelChanged?.Invoke(this, Model);
    }

    private void After(int ms, Action action)
    {
        _ = Task.Delay(ms).ContinueWith(_ => _dispatcher.TryEnqueue(() => { action(); QueuePublish(); }));
    }

    private RexModel Snapshot() => new()
    {
        Revision = ++_revision,
        Current = _current is null ? null : new CurrentRef { Host = _current.Host, Session = _current.Session },
        Transfers = _transfers.Select(t => new TransferModel
        {
            Id = t.Id, Kind = t.Kind, Host = t.Host, Pane = t.Pane, Name = t.Names[t.Index], Index = t.Index, Count = t.Names.Count,
            Bytes = t.Bytes, Total = t.Sizes.Sum(), State = t.Bytes == 0 ? "starting" : t.Bytes >= t.Sizes.Sum() ? "finishing" : "sending",
            Remote = t.Remote, Local = t.Local,
        }).ToList(),
        Hosts = _hosts.Select(h => new HostModel
        {
            Id = h.Id, Name = h.Name, Endpoint = h.Endpoint, Kind = h.Kind, State = h.State, Error = h.Error,
            Server = h.Server, CanUpload = h.Server is { Api: >= 11 }, CanDownload = h.Server is { Api: >= 13 },
            Sessions = h.Sessions.Select(s =>
            {
                var (word, tone) = SessionWord(s);
                return new SessionModel
                {
                    Id = s.Id, Label = s.Label, Attached = s.Attached, Word = word, Tone = tone,
                    ActiveWindow = s.ActiveWindow,
                    Windows = s.Windows.Select((w, i) => new WindowModel
                    {
                        Id = w.Id, Label = w.Label, Index = i,
                        Title = w.Label != "Shell" ? w.Label : w.Panes.FirstOrDefault(p => p.Pane == w.FocusedPane)?.DisplayTitle ?? w.Label,
                        FocusedPane = w.FocusedPane, ZoomedPane = w.Zoomed,
                        Word = w.Word ?? WindowWord(w).Word, Tone = w.Word is not null ? w.Tone : WindowWord(w).Tone,
                        Progress = w.Panes.Select(p => p.Progress).FirstOrDefault(p => p is not null),
                        Layout = ToLayout(w.Layout),
                        Panes = w.Panes.Select(p => new PaneModel
                        {
                            Pane = p.Pane, Block = p.Block, Title = p.DisplayTitle, Program = p.Program, Cwd = p.Cwd, Icon = p.Icon,
                            Status = p.Status, Progress = p.Progress, Paused = p.Paused || h.State != "connected",
                            Owned = true, ServerCols = 120, ServerRows = 36,
                        }).ToList(),
                    }).ToList(),
                };
            }).ToList(),
        }).ToList(),
    };

    private static (string? Word, string? Tone) WindowWord(MWindow w)
    {
        var pressing = w.Panes.Where(p => p.Status?.Word is not null)
            .OrderByDescending(p => p.Status!.Tone == "warning" ? 3 : p.Status!.Tone == "danger" ? 2 : 1)
            .FirstOrDefault();
        return pressing is null ? (null, null) : (pressing.Status!.Word, pressing.Status!.Tone);
    }

    /// <summary>The most pressing word among the session's tabs that are out of sight.</summary>
    private (string? Word, string? Tone) SessionWord(MSession s)
    {
        (string? Word, string? Tone) best = (null, null);
        int bestRank = 0;
        foreach (var w in s.Windows)
        {
            if (w.Id == s.ActiveWindow && _current?.Session == s.Id) continue;
            var (word, tone) = w.Word is not null ? (w.Word, w.Tone) : WindowWord(w);
            int rank = tone == "warning" ? 3 : tone == "danger" ? 2 : word == "Done" ? 1 : 0;
            if (rank > bestRank) { best = (word, tone); bestRank = rank; }
        }
        return best;
    }

    private static LayoutNode ToLayout(MNode n) => n.IsSplit
        ? new LayoutNode
        {
            Kind = "split", Id = n.SplitId, Axis = n.Axis, Ratio = n.Ratio,
            Children = [new LayoutChild { Weight = n.Ratio, Node = ToLayout(n.First!) }, new LayoutChild { Weight = 1 - n.Ratio, Node = ToLayout(n.Second!) }],
        }
        : new LayoutNode { Kind = "pane", Pane = n.Pane };

    // ------------------------------------------------------------------ fake transfers (demo)

    private readonly List<MTransfer> _transfers = new();

    private sealed class MTransfer
    {
        public string Id = "", Kind = "upload", Host = "";
        public long Req;
        public ulong Pane;
        public List<string> Names = new();
        public List<long> Sizes = new();
        public List<string> Temporary = new();
        public long Bytes;
        public int Index;
        public string? Remote, Local;
        public TaskCompletionSource<CommandResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DispatcherQueueTimer? Timer;
    }

    /// <summary>
    /// A transfer that moves its bytes over about three seconds, or at 8 MB/s for big files: an upload
    /// then types demo paths at the pane's prompt (as the server does), a download writes a small demo
    /// file where it was asked.
    /// </summary>
    private Task<CommandResult> StartTransfer(long req, string cmd, JsonObject a)
    {
        var (pane, _, _, host) = PaneArg(a);
        if (host.State != "connected") return Task.FromResult(new CommandResult(req, false, $"{host.Name} is not connected", null));
        var t = new MTransfer { Id = Str(a, "id") ?? $"transfer:{req}", Kind = cmd, Host = host.Id, Pane = pane.Pane, Req = req };
        if (cmd == "upload")
        {
            foreach (var f in a["files"]?.AsArray() ?? [])
            {
                string path = (string?)f?["path"] ?? "";
                if ((bool?)f?["temporary"] == true) t.Temporary.Add(path);
                if (Directory.Exists(path)) return Refuse($"{Path.GetFileName(path)} is a folder; only files can be uploaded");
                if (!File.Exists(path)) return Refuse($"{Path.GetFileName(path)} no longer exists");
                t.Names.Add((string?)f?["name"] ?? Path.GetFileName(path));
                t.Sizes.Add(new FileInfo(path).Length);
            }
            if (t.Names.Count == 0) return Refuse("missing \"files\"");
        }
        else
        {
            t.Remote = Str(a, "path") ?? "";
            t.Local = Str(a, "dest") ?? "";
            t.Names.Add(t.Remote.TrimEnd('/').Split('/')[^1]);
            t.Sizes.Add(DemoDownload(t.Remote).Length);
        }
        _transfers.Add(t);
        long total = Math.Max(1, t.Sizes.Sum());
        t.Timer = _dispatcher.CreateTimer();
        t.Timer.Interval = TimeSpan.FromMilliseconds(100);
        t.Timer.Tick += (_, _) =>
        {
            t.Bytes = Math.Min(total, t.Bytes + Math.Clamp(total / 30, 1, 800_000));
            long sum = 0;
            t.Index = t.Sizes.FindIndex(s => (sum += s) > t.Bytes) is var i and >= 0 ? i : t.Sizes.Count - 1;
            if (t.Bytes >= total) After(400, () => FinishTransfer(t));
            QueuePublish();
            if (t.Bytes >= total) t.Timer!.Stop();
        };
        t.Timer.Start();
        QueuePublish();
        return t.Done.Task;

        Task<CommandResult> Refuse(string error)
        {
            foreach (var temp in t.Temporary) try { File.Delete(temp); } catch (IOException) { }
            return Task.FromResult(new CommandResult(req, false, error, null));
        }
    }

    private void FinishTransfer(MTransfer t)
    {
        if (!_transfers.Contains(t)) return;
        try
        {
            if (t.Kind == "upload")
            {
                // The server types the batch's paths, shell-quoted, as one paste.
                string folder = Guid.NewGuid().ToString("N")[..12];
                string typed = string.Join(" ", t.Names.Select(n => $"~/.local/state/rex/uploads/{folder}/{n.Replace(" ", "\\ ")}"));
                Paste(t.Pane, typed);
                EndTransfer(t, new CommandResult(t.Req, true, null, JsonSerializer.SerializeToElement(new { id = t.Id, files = t.Names, bytes = t.Sizes.Sum() })));
            }
            else
            {
                var bytes = DemoDownload(t.Remote!);
                File.WriteAllBytes(t.Local!, bytes);
                EndTransfer(t, new CommandResult(t.Req, true, null, JsonSerializer.SerializeToElement(new { id = t.Id, path = t.Local, name = t.Names[0], remote = t.Remote, bytes = bytes.Length })));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EndTransfer(t, new CommandResult(t.Req, false, ex.Message, null));
        }
    }

    private void EndTransfer(MTransfer t, CommandResult result)
    {
        t.Timer?.Stop();
        _transfers.Remove(t);
        foreach (var temp in t.Temporary) try { File.Delete(temp); } catch (IOException) { }
        QueuePublish();
        t.Done.TrySetResult(result);
    }

    /// <summary>What a demo download contains.</summary>
    private static byte[] DemoDownload(string remote) =>
        Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat($"Downloaded from the Windex demo: {remote}\n", 20000)));

    // ------------------------------------------------------------------ the fake world

    private void Reconnect(MHost host)
    {
        host.State = "connecting";
        host.Error = null;
        After(900, () => Seed(host, reconnect: true));
    }

    /// <summary>Bring a host "online": demo names get their seeded server, others a fresh one.</summary>
    private void Seed(MHost host, bool reconnect = false)
    {
        if (!_hosts.Contains(host)) return;
        switch (host.Name)
        {
            case "rex":
                host.State = "offline";
                host.Error = "ssh: connect to host rex port 22: Connection timed out";
                return;
            case "optiplex" when !reconnect:
                host.State = "reconnecting";
                host.Error = "ssh: Connection to optiplex closed by remote host";
                host.Server = new ServerInfo { Version = "0.0.176", Api = 13, Label = "optiplex" };
                if (host.Sessions.Count == 0) SeedOptiplex(host);
                return;
        }
        host.State = "connected";
        host.Error = null;
        host.Server ??= new ServerInfo { Version = "0.0.179", Api = 13, Instance = Guid.NewGuid().ToString("N")[..12], Label = host.Name };
        foreach (var p in host.Sessions.SelectMany(s => s.Windows).SelectMany(w => w.Panes))
        {
            p.Paused = false;
            if (p.Progress?.State == "paused") p.Progress = p.Progress with { State = "working" };
        }
        if (host.Sessions.Count > 0) return;
        switch (host.Name)
        {
            case "dasher": SeedDasher(host); break;
            case "comet": SeedComet(host); break;
            case "optiplex": SeedOptiplex(host); break;
            default:
            {
                var s = new MSession { Id = NewId("S"), Label = "main" };
                var w = NewWindow("Shell", Shell("~", "zsh-new"));
                s.Windows.Add(w); s.ActiveWindow = w.Id;
                host.Sessions.Add(s);
                break;
            }
        }
    }

    private void SeedDasher(MHost host)
    {
        var amber = new MSession { Id = NewId("S"), Label = "amber-sierra" };
        {
            var codex = Pane("Fix reconnect backoff", "codex", "codex", "~/src/windex", "codex",
                status: new StatusModel { Word = "Needs input", Tone = "warning", Message = "Allow Codex to run `cargo build --release`?" });
            var zsh = Pane("zsh ~/src/windex", "zsh", "shell", "~/src/windex", "zsh-git");
            var w = Window("codex", Split("row", 0.58, Leaf(codex), Leaf(zsh)), codex, zsh);
            w.FocusedPane = codex.Pane;
            amber.Windows.Add(w);
        }
        {
            var bun = Pane("bun dev ~/src/site", "bun", "bun", "~/src/site", "bun-dev",
                progress: new ProgressModel { State = "working", Percent = 62 });
            var git = Pane("git log ~/src/site", "git", "git", "~/src/site", "git-log");
            var htop = Pane("htop", "htop", "monitor", "~", "htop");
            var w = Window("bun dev", Split("row", 0.55, Leaf(bun), Split("column", 0.5, Leaf(git), Leaf(htop))), bun, git, htop);
            w.FocusedPane = bun.Pane;
            amber.Windows.Add(w);
        }
        {
            var fish = Pane("fish ~/src/site", "fish", "shell", "~/src/site", "fish");
            var w = Window("fish", Leaf(fish), fish);
            w.Word = "Done"; w.Tone = "accent";
            amber.Windows.Add(w);
        }
        amber.ActiveWindow = amber.Windows[0].Id;
        host.Sessions.Add(amber);

        var snake = new MSession { Id = NewId("S"), Label = "rex-snake" };
        {
            var node = Pane("Stress-test Snake demo", "node", "node", "~/Sites/rex-snake", "snake");
            var w = Window("Shell", Leaf(node), node);
            snake.Windows.Add(w);
            snake.ActiveWindow = w.Id;
        }
        host.Sessions.Add(snake);
    }

    private void SeedComet(MHost host)
    {
        var owl = new MSession { Id = NewId("S"), Label = "night-owl" };
        var claude = Pane("Refactor settings loader", "claude", "claude", "~/work/api", "claude",
            status: new StatusModel { Word = "Working", Tone = "accent", Message = "Refactoring settings loader" },
            progress: new ProgressModel { State = "indeterminate" });
        var w1 = Window("claude", Leaf(claude), claude);
        var deploy = Pane("deploy.sh ~/work/infra", "bash", "shell", "~/work/infra", "deploy",
            progress: new ProgressModel { State = "error", Percent = 80 });
        var w2 = Window("deploy", Leaf(deploy), deploy);
        owl.Windows.AddRange([w1, w2]);
        owl.ActiveWindow = w1.Id;
        host.Sessions.Add(owl);
    }

    private void SeedOptiplex(MHost host)
    {
        var build = new MSession { Id = NewId("S"), Label = "build" };
        var cargo = Pane("cargo build --release", "cargo", "rust", "~/src/engine", "cargo",
            progress: new ProgressModel { State = "paused", Percent = 45 });
        cargo.Paused = true;
        var w = Window("cargo", Leaf(cargo), cargo);
        build.Windows.Add(w);
        build.ActiveWindow = w.Id;
        host.Sessions.Add(build);
    }

    private static List<DiscoveredMachine> Tailnet() =>
    [
        Machine("dasher", "linux", true, rex: "https://dasher.tail4f2e1.ts.net", tsSsh: true),
        Machine("comet", "linux", true, rex: "https://comet.tail4f2e1.ts.net", tsSsh: false),
        Machine("nas", "linux", true, rex: "http://nas.tail4f2e1.ts.net:7297", tsSsh: true),
        Machine("optiplex", "windows", true, rex: null, tsSsh: true),
        Machine("studio", "macOS", true, rex: null, tsSsh: false),
        Machine("pixel-9", "android", true, rex: null, tsSsh: null),
        Machine("rex", "linux", false, rex: null, tsSsh: true),
        Machine("josh-desktop", "windows", true, rex: null, tsSsh: null, self: true),
    ];

    private static DiscoveredMachine Machine(string name, string os, bool online, string? rex, bool? tsSsh, bool self = false) => new()
    {
        Name = name, Dns = $"{name}.tail4f2e1.ts.net", Os = os, Online = online, Self = self,
        Ips = [$"100.{64 + name.Length}.{name[0] % 200}.{name.Length * 7}"],
        Rex = rex is null ? null : new DiscoveredRex { Endpoint = rex, Reachable = true },
        Ssh = tsSsh is null ? null : new DiscoveredSsh { TailscaleSsh = tsSsh.Value },
    };

    private string NextSessionName(MHost host)
    {
        string[] a = ["quiet", "amber", "lunar", "still", "coral", "misty", "silver", "rapid"];
        string[] b = ["meadow", "basin", "atoll", "ridge", "harbor", "sierra", "canyon", "delta"];
        for (int i = 0; ; i++)
        {
            string name = $"{a[(i * 3 + host.Sessions.Count) % a.Length]}-{b[(i * 5 + host.Sessions.Count * 3) % b.Length]}";
            if (host.Sessions.All(s => s.Label != name)) return name;
        }
    }

    // ------------------------------------------------------------------ model helpers

    private sealed class MHost
    {
        public string Id = "", Name = "", Endpoint = "", Kind = "ssh", State = "offline";
        public string? Error;
        public ServerInfo? Server;
        public readonly List<MSession> Sessions = new();
    }

    private sealed class MSession
    {
        public string Id = "", Label = "";
        public bool Attached;
        public string? ActiveWindow;
        public readonly List<MWindow> Windows = new();
    }

    private sealed class MWindow
    {
        public string Id = "", Label = "Shell";
        public string? Word, Tone;
        public ulong FocusedPane;
        public ulong? Zoomed;
        public MNode Layout = new();
        public readonly List<MPane> Panes = new();
    }

    private sealed class MPane
    {
        public ulong Pane;
        public string Block = "", Title = "", Icon = "shell", Screen = "zsh-new";
        public string? Label, Program, Cwd;
        public StatusModel? Status;
        public ProgressModel? Progress;
        public bool Paused;
        public string DisplayTitle => Label ?? Title;
    }

    private sealed class MNode
    {
        public ulong Pane;
        public string? SplitId;
        public string Axis = "row";
        public double Ratio = 0.5;
        public MNode? First, Second;
        public bool IsSplit => First is not null;
    }

    private string NewId(string prefix) => $"{prefix}_{_nextId++:x3}";

    private MPane Pane(string title, string program, string icon, string cwd, string screen, StatusModel? status = null, ProgressModel? progress = null) => new()
    {
        Pane = _nextPane++, Block = NewId("B"), Title = title, Program = program, Icon = icon, Cwd = cwd, Screen = screen,
        Status = status, Progress = progress,
    };

    private MPane Shell(string cwd, string screen) => Pane($"zsh {cwd}", "zsh", "shell", cwd, screen);

    private MWindow NewWindow(string label, MPane pane) => Window(label, Leaf(pane), pane);

    private MWindow Window(string label, MNode layout, params MPane[] panes)
    {
        var w = new MWindow { Id = NewId("W"), Label = label, Layout = layout, FocusedPane = panes[0].Pane };
        w.Panes.AddRange(panes);
        return w;
    }

    private static MNode Leaf(MPane pane) => new() { Pane = pane.Pane };

    private MNode Split(string axis, double ratio, MNode first, MNode second) =>
        new() { SplitId = "split:" + NewId("s"), Axis = axis, Ratio = ratio, First = first, Second = second };

    private static MNode? Leaf(MNode node, ulong pane) =>
        node.IsSplit ? Leaf(node.First!, pane) ?? Leaf(node.Second!, pane) : node.Pane == pane ? node : null;

    private static MNode? FindSplit(MNode node, string id) =>
        !node.IsSplit ? null : node.SplitId == id ? node : FindSplit(node.First!, id) ?? FindSplit(node.Second!, id);

    private static void Balance(MNode node)
    {
        if (!node.IsSplit) return;
        node.Ratio = 0.5;
        Balance(node.First!);
        Balance(node.Second!);
    }

    private void ClosePane(MSession session, MWindow w, ulong pane)
    {
        if (!w.Layout.IsSplit) { CloseWindow(session, w); return; }
        Remove(w.Layout, pane);
        w.Panes.RemoveAll(p => p.Pane == pane);
        if (w.Zoomed == pane) w.Zoomed = null;
        if (w.FocusedPane == pane) w.FocusedPane = w.Panes[0].Pane;

        static bool Remove(MNode node, ulong pane)
        {
            if (!node.IsSplit) return false;
            MNode? keep = node.First is { IsSplit: false } f && f.Pane == pane ? node.Second
                : node.Second is { IsSplit: false } s && s.Pane == pane ? node.First : null;
            if (keep is null) return Remove(node.First!, pane) || Remove(node.Second!, pane);
            (node.Pane, node.SplitId, node.Axis, node.Ratio, node.First, node.Second) = (keep.Pane, keep.SplitId, keep.Axis, keep.Ratio, keep.First, keep.Second);
            return true;
        }
    }

    private static void CloseWindow(MSession session, MWindow w)
    {
        int index = session.Windows.IndexOf(w);
        session.Windows.Remove(w);
        if (session.ActiveWindow == w.Id)
            session.ActiveWindow = session.Windows.Count == 0 ? null : session.Windows[Math.Min(index, session.Windows.Count - 1)].Id;
    }

    /// <summary>The pane whose rectangle is nearest in the given direction (unit-square geometry).</summary>
    private static ulong? Neighbor(MWindow w, ulong pane, string direction)
    {
        var rects = new Dictionary<ulong, (double X, double Y, double W, double H)>();
        Rects(w.Layout, 0, 0, 1, 1, rects);
        if (!rects.TryGetValue(pane, out var from)) return null;
        ulong? best = null;
        double bestDistance = double.MaxValue;
        foreach (var (id, r) in rects)
        {
            if (id == pane) continue;
            bool overlapsY = r.Y < from.Y + from.H - 1e-6 && r.Y + r.H > from.Y + 1e-6;
            bool overlapsX = r.X < from.X + from.W - 1e-6 && r.X + r.W > from.X + 1e-6;
            double distance = direction switch
            {
                "right" when overlapsY && r.X >= from.X + from.W - 1e-6 => r.X - (from.X + from.W),
                "left" when overlapsY && r.X + r.W <= from.X + 1e-6 => from.X - (r.X + r.W),
                "down" when overlapsX && r.Y >= from.Y + from.H - 1e-6 => r.Y - (from.Y + from.H),
                "up" when overlapsX && r.Y + r.H <= from.Y + 1e-6 => from.Y - (r.Y + r.H),
                _ => double.MaxValue,
            };
            if (distance < bestDistance) { bestDistance = distance; best = id; }
        }
        return best;

        static void Rects(MNode n, double x, double y, double width, double height, Dictionary<ulong, (double, double, double, double)> into)
        {
            if (!n.IsSplit) { into[n.Pane] = (x, y, width, height); return; }
            if (n.Axis == "row")
            {
                Rects(n.First!, x, y, width * n.Ratio, height, into);
                Rects(n.Second!, x + width * n.Ratio, y, width * (1 - n.Ratio), height, into);
            }
            else
            {
                Rects(n.First!, x, y, width, height * n.Ratio, into);
                Rects(n.Second!, x, y + height * n.Ratio, width, height * (1 - n.Ratio), into);
            }
        }
    }

    // ------------------------------------------------------------------ argument helpers

    private MHost Host(JsonObject a, string key)
    {
        string id = Str(a, key) ?? throw new ArgumentException($"{key} required");
        return _hosts.FirstOrDefault(h => h.Id == id) ?? throw new ArgumentException($"unknown host {id}");
    }

    private static MSession Session(MHost host, JsonObject a)
    {
        string id = Str(a, "session") ?? throw new ArgumentException("session required");
        return host.Sessions.FirstOrDefault(s => s.Id == id) ?? host.Sessions.FirstOrDefault(s => s.Label == id)
            ?? throw new ArgumentException($"unknown session {id}");
    }

    private (MHost Host, MSession Session, MWindow Window) Window(JsonObject a)
    {
        var host = Host(a, "host");
        var session = Session(host, a);
        string id = Str(a, "window") ?? throw new ArgumentException("window required");
        var w = session.Windows.FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException($"unknown window {id}");
        return (host, session, w);
    }

    private (MPane Pane, MWindow Window, MSession Session, MHost Host) PaneArg(JsonObject a)
    {
        ulong pane = (ulong?)a["pane"] ?? throw new ArgumentException("pane required");
        return FindPane(pane) ?? throw new ArgumentException($"unknown pane {pane}");
    }

    private (MPane Pane, MWindow Window, MSession Session, MHost Host)? FindPane(ulong pane)
    {
        foreach (var h in _hosts)
            foreach (var s in h.Sessions)
                foreach (var w in s.Windows)
                    foreach (var p in w.Panes)
                        if (p.Pane == pane) return (p, w, s, h);
        return null;
    }

    private static string KindOf(string endpoint) =>
        endpoint.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? "https"
        : endpoint.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ? "http"
        : endpoint.StartsWith("unix:", StringComparison.OrdinalIgnoreCase) ? "unix"
        : "ssh";

    private static string? Str(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool? Bool(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static int? Int(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    private static double? Double(JsonObject a, string key) => a[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
