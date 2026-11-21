// The hosts this window knows about: the user's saved hosts (persisted in settings.json) when
// running against rexcore.dll, or the demo hosts when running on MockCore. The registry owns
// the UI-side metadata (icon kind, connect-at-launch) and turns edits into core commands.
using Windex.Core;
using Windex.Shell;

namespace Windex.Services;

public sealed class HostRegistry
{
    private readonly ICore _core;
    private readonly AppSettings _settings;
    private readonly List<SavedHost> _hosts;
    private readonly bool _persist;

    public event EventHandler? Changed;

    public HostRegistry(ICore core, AppSettings settings, IReadOnlyList<SavedHost>? demoHosts)
    {
        _core = core;
        _settings = settings;
        _persist = demoHosts is null;
        _hosts = demoHosts is null
            ? settings.Hosts
            : demoHosts.Select(h => new SavedHost { Id = h.Id, Name = h.Name, Endpoint = h.Endpoint, Icon = h.Icon, ConnectAtLaunch = h.ConnectAtLaunch }).ToList();
    }

    public IReadOnlyList<SavedHost> Hosts => _hosts;
    public bool IsDemo => !_persist;

    public SavedHost? Find(string? id) => id is null ? null : _hosts.FirstOrDefault(h => h.Id == id);

    /// <summary>Host-kind icon for a host in the model.</summary>
    public string IconFor(HostModel host) => Find(host.Id)?.Icon ?? HostKinds.Infer(host.Kind, host.Endpoint, null);

    /// <summary>Tell the core about every saved host (startup).</summary>
    public async Task RegisterAllAsync()
    {
        foreach (var h in _hosts.ToList())
            await _core.CommandAsync("add_host", new { id = h.Id, name = h.Name, endpoint = h.Endpoint, connect = h.ConnectAtLaunch });
    }

    public async Task<(SavedHost Host, CommandResult Result)> AddAsync(string name, string endpoint, string icon)
    {
        var host = new SavedHost { Id = NewId(), Name = name, Endpoint = endpoint, Icon = icon };
        _hosts.Add(host);
        Persist();
        var result = await _core.CommandAsync("add_host", new { id = host.Id, name = host.Name, endpoint = host.Endpoint, connect = true });
        return (host, result);
    }

    public async Task<CommandResult> UpdateAsync(string id, string name, string endpoint, string icon)
    {
        var host = Find(id) ?? throw new InvalidOperationException($"unknown host {id}");
        host.Name = name;
        host.Icon = icon;
        bool endpointChanged = host.Endpoint != endpoint;
        host.Endpoint = endpoint;
        Persist();
        return endpointChanged
            ? await _core.CommandAsync("update_host", new { id, name, endpoint })
            : await _core.CommandAsync("update_host", new { id, name });
    }

    public void SetConnectAtLaunch(string id, bool value)
    {
        if (Find(id) is not { } host) return;
        host.ConnectAtLaunch = value;
        Persist();
    }

    public async Task<CommandResult> RemoveAsync(string id)
    {
        _hosts.RemoveAll(h => h.Id == id);
        if (_settings.LastHost == id) { _settings.LastHost = null; _settings.LastSession = null; }
        Persist();
        return await _core.CommandAsync("remove_host", new { id });
    }

    private string NewId()
    {
        string id;
        do id = "h" + Guid.NewGuid().ToString("N")[..8];
        while (Find(id) is not null);
        return id;
    }

    private void Persist()
    {
        if (_persist) _settings.Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
