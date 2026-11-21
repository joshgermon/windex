// ICore over rexcore.dll. Events arrive on core threads and are marshalled to the UI dispatcher.
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Windex.Interop;

namespace Windex.Core;

public sealed unsafe class NativeCore : ICore
{
    private readonly DispatcherQueue _dispatcher;
    private readonly RexEventCallback _callback; // kept alive for the core's lifetime
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CommandResult>> _pending = new();
    private readonly ConcurrentDictionary<ulong, byte> _dirtyQueued = new();
    private long _nextReq;
    private nint _core;
    private int _modelPosted;
    private string? _latestModelJson;

    public RexModel Model { get; private set; } = RexModel.Empty;
    public event EventHandler<RexModel>? ModelChanged;
    public event EventHandler<ulong>? PaneDirty;
    public event EventHandler<ulong>? Bell;
    public event EventHandler<(ulong Pane, string Text)>? ClipboardRequested;
    public event EventHandler<Notice>? NoticeRaised;
    public event EventHandler<DesktopNotify>? NotificationRaised;
    public event EventHandler<IReadOnlyList<DiscoveredMachine>>? DiscoveryUpdated;
    public event EventHandler<string>? Log;

    public NativeCore(DispatcherQueue dispatcher, string? stateDir = null)
    {
        _dispatcher = dispatcher;
        _callback = OnEvent;
        var options = new JsonObject
        {
            ["client_name"] = "Windex",
            ["client_version"] = typeof(NativeCore).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
            ["label"] = Environment.MachineName,
            ["state_dir"] = stateDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Windex"),
            ["ssh_path"] = FindOnPath("ssh.exe") ?? @"C:\Windows\System32\OpenSSH\ssh.exe",
            ["tailscale_path"] = FindOnPath("tailscale.exe") ?? @"C:\Program Files\Tailscale\tailscale.exe",
            ["log_level"] = "info",
        };
        if (RexCoreNative.CoreAbiVersion() != RexCoreNative.AbiVersion)
            throw new InvalidOperationException($"rexcore.dll ABI {RexCoreNative.CoreAbiVersion()} != {RexCoreNative.AbiVersion}");
        _core = RexCoreNative.CoreNew(options.ToJsonString(), Marshal.GetFunctionPointerForDelegate(_callback), 0);
        if (_core == 0) throw new InvalidOperationException("rex_core_new failed");
    }

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    private void OnEvent(nint user, int kind, ulong pane, nint json, nuint len)
    {
        try
        {
            string text = len == 0 || json == 0 ? "" : Encoding.UTF8.GetString((byte*)json, checked((int)len));
            switch ((RexEventKind)kind)
            {
                case RexEventKind.Model:
                    // Coalesce bursts: only the newest snapshot is parsed on the UI thread.
                    Volatile.Write(ref _latestModelJson, text);
                    if (Interlocked.Exchange(ref _modelPosted, 1) == 0)
                        _dispatcher.TryEnqueue(DispatchModel);
                    break;
                case RexEventKind.PaneDirty:
                    if (_dirtyQueued.TryAdd(pane, 0))
                        _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () => { _dirtyQueued.TryRemove(pane, out _); PaneDirty?.Invoke(this, pane); });
                    break;
                case RexEventKind.Result:
                    var result = ParseResult(text);
                    if (_pending.TryRemove(result.Req, out var tcs)) tcs.TrySetResult(result);
                    break;
                case RexEventKind.Notice:
                    var n = JsonNode.Parse(text);
                    var notice = new Notice((string?)n?["level"] ?? "info", (string?)n?["title"] ?? "", (string?)n?["message"] ?? "", (string?)n?["host"]);
                    _dispatcher.TryEnqueue(() => NoticeRaised?.Invoke(this, notice));
                    break;
                case RexEventKind.Bell:
                    _dispatcher.TryEnqueue(() => Bell?.Invoke(this, pane));
                    break;
                case RexEventKind.Clipboard:
                    var clip = (string?)JsonNode.Parse(text)?["text"] ?? "";
                    _dispatcher.TryEnqueue(() => ClipboardRequested?.Invoke(this, (pane, clip)));
                    break;
                case RexEventKind.Notify:
                    var m = JsonNode.Parse(text);
                    var note = new DesktopNotify((string?)m?["kind"] ?? "osc9", (string?)m?["title"] ?? "", (string?)m?["body"] ?? "",
                        (string?)m?["host"], (string?)m?["session"], (string?)m?["window"], pane);
                    _dispatcher.TryEnqueue(() => NotificationRaised?.Invoke(this, note));
                    break;
                case RexEventKind.Discovery:
                    var machines = JsonNode.Parse(text)?["machines"]?.Deserialize(ModelJsonContext.Default.ListDiscoveredMachine) ?? new();
                    _dispatcher.TryEnqueue(() => DiscoveryUpdated?.Invoke(this, machines));
                    break;
                case RexEventKind.Log:
                    System.Diagnostics.Debug.WriteLine($"[rexcore] {text}");
                    Log?.Invoke(this, text);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[rexcore] event {kind} failed: {ex}");
        }
    }

    private void DispatchModel()
    {
        Interlocked.Exchange(ref _modelPosted, 0);
        var json = Volatile.Read(ref _latestModelJson);
        if (json is null) return;
        try
        {
            var model = JsonSerializer.Deserialize(json, ModelJsonContext.Default.RexModel);
            if (model is null) return;
            Model = model;
            ModelChanged?.Invoke(this, model);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[rexcore] bad model: {ex.Message}");
        }
    }

    private static CommandResult ParseResult(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        long req = root.TryGetProperty("req", out var r) ? r.GetInt64() : 0;
        bool ok = root.TryGetProperty("ok", out var o) && o.GetBoolean();
        string? error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        JsonElement? data = root.TryGetProperty("data", out var d) ? d.Clone() : null;
        return new CommandResult(req, ok, error, data);
    }

    public Task<CommandResult> CommandAsync(string cmd, object? args = null)
    {
        var req = Interlocked.Increment(ref _nextReq);
        var node = args is null ? new JsonObject() : JsonSerializer.SerializeToNode(args)?.AsObject() ?? new JsonObject();
        node["cmd"] = cmd;
        node["req"] = req;
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[req] = tcs;
        if (RexCoreNative.CoreCommand(_core, node.ToJsonString()) < 0)
        {
            _pending.TryRemove(req, out _);
            tcs.TrySetResult(new CommandResult(req, false, "malformed command", null));
        }
        return tcs.Task;
    }

    public int Key(ulong pane, in KeyInput key)
    {
        nint code = Marshal.StringToCoTaskMemUTF8(key.Code);
        nint k = key.Key is null ? 0 : Marshal.StringToCoTaskMemUTF8(key.Key);
        nint t = key.Text is null ? 0 : Marshal.StringToCoTaskMemUTF8(key.Text);
        try
        {
            var native = new RexKey { Code = code, Key = k, Text = t, Mods = key.Mods, Action = key.Action };
            return RexCoreNative.PaneKey(_core, pane, &native);
        }
        finally
        {
            Marshal.FreeCoTaskMem(code);
            if (k != 0) Marshal.FreeCoTaskMem(k);
            if (t != 0) Marshal.FreeCoTaskMem(t);
        }
    }

    public int Text(ulong pane, string text) => RexCoreNative.PaneText(_core, pane, text);
    public int Paste(ulong pane, string text) => RexCoreNative.PanePaste(_core, pane, text);
    public int Mouse(ulong pane, in RexMouse mouse) { var m = mouse; return RexCoreNative.PaneMouse(_core, pane, &m); }
    public int Resize(ulong pane, ushort cols, ushort rows, uint cw, uint ch) => RexCoreNative.PaneResize(_core, pane, cols, rows, cw, ch);
    public int Focus(ulong pane, bool focused) => RexCoreNative.PaneFocus(_core, pane, focused ? 1 : 0);
    public int Scroll(ulong pane, RexScrollOp op, long value) => RexCoreNative.PaneScroll(_core, pane, (int)op, value);
    public int Select(ulong pane, RexSelectOp op, int clicks, float x, float y) => RexCoreNative.PaneSelect(_core, pane, (int)op, clicks, x, y);
    public string? SelectionText(ulong pane) => RexCoreNative.TakeString(RexCoreNative.PaneSelectionText(_core, pane));
    public string? HyperlinkAt(ulong pane, int col, int row) => RexCoreNative.TakeString(RexCoreNative.PaneHyperlinkAt(_core, pane, col, row));
    public int Search(ulong pane, string? query, RexSearchFlags flags) => RexCoreNative.PaneSearch(_core, pane, query, (uint)flags);
    public int SearchStep(ulong pane, int direction) => RexCoreNative.PaneSearchStep(_core, pane, direction);
    public int SetDefaultCursor(ulong pane, RexCursorShape shape) => RexCoreNative.PaneSetDefaultCursor(_core, pane, (int)shape);
    public RexFrame* LockFrame(ulong pane) => RexCoreNative.PaneFrameLock(_core, pane);
    public void UnlockFrame(ulong pane) => RexCoreNative.PaneFrameUnlock(_core, pane);
    public ulong LocalPaneNew(ushort cols, ushort rows, bool echo) => RexCoreNative.LocalPaneNew(_core, cols, rows, echo ? 1 : 0);

    public void LocalPaneFeed(ulong pane, ReadOnlySpan<byte> bytes)
    {
        fixed (byte* p = bytes) RexCoreNative.LocalPaneFeed(_core, pane, p, (nuint)bytes.Length);
    }

    public void LocalPaneClose(ulong pane) => RexCoreNative.LocalPaneClose(_core, pane);

    public void Dispose()
    {
        var core = Interlocked.Exchange(ref _core, 0);
        if (core != 0) RexCoreNative.CoreFree(core);
        foreach (var tcs in _pending.Values) tcs.TrySetCanceled();
        _pending.Clear();
    }
}
