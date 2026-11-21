// The core's Model JSON (docs/contract.md), deserialized. Immutable snapshots; diff by Id.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windex.Core;

public sealed record RexModel
{
    [JsonPropertyName("revision")] public long Revision { get; init; }
    [JsonPropertyName("current")] public CurrentRef? Current { get; init; }
    [JsonPropertyName("hosts")] public List<HostModel> Hosts { get; init; } = new();
    /// <summary>File transfers in flight (docs/contract.md "Transfers").</summary>
    [JsonPropertyName("transfers")] public List<TransferModel> Transfers { get; init; } = new();

    public static readonly RexModel Empty = new();

    public HostModel? Host(string? id) => id is null ? null : Hosts.FirstOrDefault(h => h.Id == id);

    public SessionModel? CurrentSession =>
        Current is null ? null : Host(Current.Host)?.Sessions.FirstOrDefault(s => s.Id == Current.Session);

    public PaneModel? FindPane(ulong pane, out WindowModel? window, out SessionModel? session, out HostModel? host)
    {
        foreach (var h in Hosts)
            foreach (var s in h.Sessions)
                foreach (var w in s.Windows)
                    foreach (var p in w.Panes)
                        if (p.Pane == pane) { window = w; session = s; host = h; return p; }
        window = null; session = null; host = null;
        return null;
    }
}

public sealed record CurrentRef
{
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("session")] public string Session { get; init; } = "";
}

public sealed record HostModel
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("endpoint")] public string Endpoint { get; init; } = "";
    /// <summary>ssh | https | http | unix | local</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "ssh";
    /// <summary>connecting | connected | reconnecting | offline | error</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "offline";
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("server")] public ServerInfo? Server { get; init; }
    [JsonPropertyName("sessions")] public List<SessionModel> Sessions { get; init; } = new();
    /// <summary>The server's API revision takes uploads (11+) / downloads (13+).</summary>
    [JsonPropertyName("can_upload")] public bool CanUpload { get; init; }
    [JsonPropertyName("can_download")] public bool CanDownload { get; init; }

    [JsonIgnore] public bool IsConnected => State == "connected";
}

/// <summary>An upload batch or a download in flight.</summary>
public sealed record TransferModel
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    /// <summary>upload | download</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "upload";
    [JsonPropertyName("host")] public string Host { get; init; } = "";
    [JsonPropertyName("pane")] public ulong Pane { get; init; }
    /// <summary>The file being moved now.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; } = 1;
    [JsonPropertyName("bytes")] public long Bytes { get; init; }
    /// <summary>Bytes expected (a download's is the size when the server opened the file).</summary>
    [JsonPropertyName("total")] public long Total { get; init; }
    /// <summary>starting | sending | finishing</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "starting";
    [JsonPropertyName("remote")] public string? Remote { get; init; }
    [JsonPropertyName("local")] public string? Local { get; init; }
}

public sealed record ServerInfo
{
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("api")] public int Api { get; init; }
    [JsonPropertyName("instance")] public string? Instance { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
}

public sealed record SessionModel
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("attached")] public bool Attached { get; init; }
    [JsonPropertyName("word")] public string? Word { get; init; }
    [JsonPropertyName("tone")] public string? Tone { get; init; }
    [JsonPropertyName("active_window")] public string? ActiveWindow { get; init; }
    [JsonPropertyName("windows")] public List<WindowModel> Windows { get; init; } = new();
}

public sealed record WindowModel
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("focused_pane")] public ulong FocusedPane { get; init; }
    [JsonPropertyName("zoomed_pane")] public ulong? ZoomedPane { get; init; }
    [JsonPropertyName("word")] public string? Word { get; init; }
    [JsonPropertyName("tone")] public string? Tone { get; init; }
    [JsonPropertyName("progress")] public ProgressModel? Progress { get; init; }
    [JsonPropertyName("layout")] public LayoutNode? Layout { get; init; }
    [JsonPropertyName("panes")] public List<PaneModel> Panes { get; init; } = new();
}

public sealed record PaneModel
{
    [JsonPropertyName("pane")] public ulong Pane { get; init; }
    [JsonPropertyName("block")] public string Block { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("program")] public string? Program { get; init; }
    [JsonPropertyName("cwd")] public string? Cwd { get; init; }
    [JsonPropertyName("icon")] public string Icon { get; init; } = "shell";
    [JsonPropertyName("status")] public StatusModel? Status { get; init; }
    [JsonPropertyName("progress")] public ProgressModel? Progress { get; init; }
    [JsonPropertyName("paused")] public bool Paused { get; init; }
    [JsonPropertyName("owned")] public bool Owned { get; init; } = true;
    [JsonPropertyName("server_cols")] public int ServerCols { get; init; }
    [JsonPropertyName("server_rows")] public int ServerRows { get; init; }
    [JsonPropertyName("exited")] public bool Exited { get; init; }
}

public sealed record StatusModel
{
    [JsonPropertyName("word")] public string? Word { get; init; }
    /// <summary>accent | warning | danger</summary>
    [JsonPropertyName("tone")] public string? Tone { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed record ProgressModel
{
    /// <summary>working | paused | error | indeterminate</summary>
    [JsonPropertyName("state")] public string State { get; init; } = "working";
    [JsonPropertyName("percent")] public int? Percent { get; init; }
}

public sealed record LayoutNode
{
    /// <summary>"pane" or "split"</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "pane";
    [JsonPropertyName("pane")] public ulong Pane { get; init; }
    /// <summary>Server split id (for set_split_ratio).</summary>
    [JsonPropertyName("id")] public string? Id { get; init; }
    /// <summary>"row" (children left→right) or "column" (top→bottom)</summary>
    [JsonPropertyName("axis")] public string? Axis { get; init; }
    /// <summary>Share of the first child (0..1).</summary>
    [JsonPropertyName("ratio")] public double Ratio { get; init; } = 0.5;
    [JsonPropertyName("children")] public List<LayoutChild>? Children { get; init; }

    [JsonIgnore] public bool IsSplit => Kind == "split" && Children is { Count: > 0 };

    public IEnumerable<ulong> PaneIds()
    {
        if (!IsSplit) { yield return Pane; yield break; }
        foreach (var child in Children!)
            foreach (var id in child.Node.PaneIds())
                yield return id;
    }
}

public sealed record LayoutChild
{
    [JsonPropertyName("weight")] public double Weight { get; init; } = 1;
    [JsonPropertyName("node")] public LayoutNode Node { get; init; } = new();
}

/// <summary>A tailnet machine from a DISCOVERY event.</summary>
public sealed record DiscoveredMachine
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("dns")] public string? Dns { get; init; }
    [JsonPropertyName("ips")] public List<string> Ips { get; init; } = new();
    [JsonPropertyName("os")] public string? Os { get; init; }
    [JsonPropertyName("online")] public bool Online { get; init; }
    [JsonPropertyName("self")] public bool Self { get; init; }
    [JsonPropertyName("rex")] public DiscoveredRex? Rex { get; init; }
    [JsonPropertyName("ssh")] public DiscoveredSsh? Ssh { get; init; }
}

public sealed record DiscoveredRex
{
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
    [JsonPropertyName("reachable")] public bool Reachable { get; init; }
}

public sealed record DiscoveredSsh
{
    [JsonPropertyName("tailscale_ssh")] public bool TailscaleSsh { get; init; }
}

public sealed record CommandResult(long Req, bool Ok, string? Error, JsonElement? Data);

public sealed record Notice(string Level, string Title, string Message, string? Host);

public sealed record DesktopNotify(string Kind, string Title, string Body, string? Host, string? Session, string? Window, ulong Pane);

[JsonSerializable(typeof(RexModel))]
[JsonSerializable(typeof(List<DiscoveredMachine>))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ModelJsonContext : JsonSerializerContext { }
