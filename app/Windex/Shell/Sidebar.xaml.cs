// Sidebar: syncs rows from the model by key, routes clicks and context menus to the window.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windex.Core;
using Windex.Services;

namespace Windex.Shell;

/// <summary>What the sidebar asks of its window.</summary>
public interface ISidebarHost
{
    void OpenSession(string hostId, string sessionId);
    void SelectTab(string hostId, string sessionId, string windowId);
    void ShowHostTrouble(string hostId);
    void RetryHost(string hostId);
    void ShowAddHost(DiscoveredMachine? machine);
    void RefreshHosts();
    void OpenSettings(string page);
    MenuFlyout HostMenu(string hostId);
    MenuFlyout SessionMenu(string hostId, string sessionId, Action rename);
    MenuFlyout TabMenu(string hostId, string sessionId, string windowId);
    void RenameSession(string hostId, string sessionId, string label);
}

public sealed partial class Sidebar : UserControl
{
    private readonly Dictionary<string, ListViewItem> _items = new();
    private ISidebarHost? _host;
    private bool _tailnetExpanded;
    private RexModel _model = RexModel.Empty;
    private HostRegistry? _registry;
    private IReadOnlyList<DiscoveredMachine> _tailnet = [];
    private string _colorful = "always";

    public Sidebar()
    {
        InitializeComponent();
    }

    public void Attach(ISidebarHost host) => _host = host;

    /// <summary>Keyboard focus into the list (Ctrl+F6 cycling).</summary>
    public void FocusList() => Rows.Focus(FocusState.Keyboard);

    /// <summary>The footer's Add host button (the first-run tip points at it).</summary>
    public FrameworkElement AddHostTarget => AddHostButton;

    public void Update(RexModel model, HostRegistry registry, IReadOnlyList<DiscoveredMachine> tailnet, string colorful)
    {
        _model = model;
        _registry = registry;
        _tailnet = tailnet;
        _colorful = colorful;
        Render();
    }

    private void Render()
    {
        if (_registry is null) return;
        var model = _model;
        var wanted = new List<(string Key, Func<SidebarRow> Create, Action<SidebarRow> Update)>();
        var current = model.Current;

        foreach (var host in model.Hosts)
        {
            var h = host;
            wanted.Add(("host:" + h.Id,
                () =>
                {
                    var menu = _host!.HostMenu(h.Id);
                    return new HostRow(h.Id, () => _host?.RetryHost(h.Id), _ => menu) { ContextFlyout = menu };
                },
                row => ((HostRow)row).Update(h, HostKinds.Get(_registry.IconFor(h)))));
            foreach (var session in h.Sessions)
            {
                var s = session;
                bool isCurrent = current?.Host == h.Id && current.Session == s.Id;
                wanted.Add(($"session:{h.Id}/{s.Id}",
                    () => NewSessionRow(h.Id, s.Id),
                    row =>
                    {
                        var r = (SessionRow)row;
                        r.Update(h, s, isCurrent, _colorful);
                        r.IsSelected = isCurrent && s.Windows.Count == 0;
                    }));
                if (!isCurrent) continue;
                foreach (var window in s.Windows)
                {
                    var w = window;
                    bool selected = s.ActiveWindow == w.Id;
                    wanted.Add(($"tab:{h.Id}/{s.Id}/{w.Id}",
                        () =>
                        {
                            var r = new TabRow(h.Id, s.Id, w.Id);
                            r.ContextFlyout = _host!.TabMenu(h.Id, s.Id, w.Id);
                            return r;
                        },
                        row =>
                        {
                            var r = (TabRow)row;
                            r.Update(w, _colorful, selected);
                            r.IsSelected = selected;
                        }));
                }
            }
        }

        // Tailnet machines that are online, not this PC and not already added.
        var added = model.Hosts.Select(h => h.Name.ToLowerInvariant()).ToHashSet();
        var machines = _tailnet.Where(m => m.Online && !m.Self && !added.Contains(m.Name.ToLowerInvariant()))
            .OrderBy(m => m.Rex is null).ThenBy(m => m.Name).ToList();
        if (machines.Count > 0)
        {
            wanted.Add(("tailnet", () => new TailnetRow(), row => ((TailnetRow)row).Update(machines.Count, _tailnetExpanded)));
            if (_tailnetExpanded)
                foreach (var machine in machines)
                {
                    var m = machine;
                    wanted.Add(("machine:" + m.Name, () => new MachineRow(m, x => _host?.ShowAddHost(x)), row => ((MachineRow)row).Update(m)));
                }
        }

        Sync(wanted);
        EmptyState.Visibility = model.Hosts.Count == 0 && _registry.Hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private SessionRow NewSessionRow(string hostId, string sessionId)
    {
        var row = new SessionRow(hostId, sessionId);
        row.ContextFlyout = _host!.SessionMenu(hostId, sessionId, () => BeginSessionRename(row));
        return row;
    }

    private void Sync(List<(string Key, Func<SidebarRow> Create, Action<SidebarRow> Update)> wanted)
    {
        var keys = wanted.Select(w => w.Key).ToHashSet();
        foreach (var stale in _items.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            Rows.Items.Remove(_items[stale]);
            _items.Remove(stale);
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            var (key, create, update) = wanted[i];
            if (!_items.TryGetValue(key, out var item))
            {
                var row = create();
                item = new ListViewItem { Content = row };
                if (row is HostRow or TailnetRow) item.Margin = new Thickness(0, row is HostRow && i > 0 ? 8 : 1, 0, 1);
                _items[key] = item;
                Rows.Items.Insert(Math.Min(i, Rows.Items.Count), item);
            }
            else
            {
                int at = Rows.Items.IndexOf(item);
                if (at != i)
                {
                    Rows.Items.RemoveAt(at);
                    Rows.Items.Insert(Math.Min(i, Rows.Items.Count), item);
                }
                if (item.Content is HostRow) item.Margin = new Thickness(0, i > 0 ? 8 : 1, 0, 1);
            }
            update((SidebarRow)item.Content);
        }
    }

    private void Rows_ItemClick(object sender, ItemClickEventArgs e)
    {
        var row = (e.ClickedItem as ListViewItem)?.Content as SidebarRow ?? e.ClickedItem as SidebarRow;
        switch (row)
        {
            case HostRow h:
                _host?.ShowHostTrouble(h.HostId);
                break;
            case SessionRow s when s.Available:
                _host?.OpenSession(s.HostId, s.SessionId);
                break;
            case SessionRow s:
                _host?.ShowHostTrouble(s.HostId);
                break;
            case TabRow t:
                _host?.SelectTab(t.HostId, t.SessionId, t.WindowId);
                break;
            case TailnetRow:
                _tailnetExpanded = !_tailnetExpanded;
                Render();
                break;
            case MachineRow m:
                _host?.ShowAddHost(m.Machine);
                break;
        }
    }

    // ------------------------------------------------------------------ inline session rename (§9.5)

    /// <summary>Rename the current session inline; false when it is not listed.</summary>
    public bool BeginSessionRename(string hostId, string sessionId)
    {
        if (!_items.TryGetValue($"session:{hostId}/{sessionId}", out var item) || item.Content is not SessionRow row) return false;
        BeginSessionRename(row);
        return true;
    }

    private void BeginSessionRename(SessionRow row)
    {
        var session = _model.Host(row.HostId)?.Sessions.FirstOrDefault(s => s.Id == row.SessionId);
        if (session is null) return;
        var box = new TextBox
        {
            Text = session.Label,
            Style = (Style)Application.Current.Resources["RexInlineRenameTextBoxStyle"],
            FontSize = 13,
            Margin = new Thickness(38, 0, 6, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Session name");
        row.Children.Add(box);
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            row.Children.Remove(box);
            var label = box.Text.Trim();
            if (commit && label.Length > 0 && label != session.Label) _host?.RenameSession(row.HostId, row.SessionId, label);
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Finish(true); }
            else if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Finish(false); }
        };
        box.LostFocus += (_, _) => Finish(true);
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
    }

    private void AddHost_Click(object sender, RoutedEventArgs e) => _host?.ShowAddHost(null);
    private void Refresh_Click(object sender, RoutedEventArgs e) => _host?.RefreshHosts();
    private void Manage_Click(object sender, RoutedEventArgs e) => _host?.OpenSettings("hosts");
}
