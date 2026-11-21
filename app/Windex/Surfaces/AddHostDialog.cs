// Add host (design §9.2): pick a machine on the tailnet (from the core's DISCOVERY) or enter an
// address; then connect and report the server's own reason on failure. Also serves Edit host.
using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windex.Core;
using Windex.Services;
using Windex.Shell;

namespace Windex.Surfaces;

public sealed class AddHostDialog
{
    private readonly ICore _core;
    private readonly HostRegistry _registry;
    private readonly SavedHost? _editing;
    private readonly ContentDialog _dialog;
    private IReadOnlyList<DiscoveredMachine> _machines;
    private bool _discovered;

    // Shell
    private readonly SelectorBar _selector = new();
    private readonly SelectorBarItem _tailnetTab = new() { Text = "On your tailnet", Icon = new FontIcon { Glyph = "\uE968" } };
    private readonly SelectorBarItem _addressTab = new() { Text = "Enter address", Icon = new FontIcon { Glyph = "\uE71B" } };
    private readonly Grid _body = new() { Width = 592, MinHeight = 420 };   // 640 dialog less its 24 epx padding each side
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly InfoBar _failure = new() { Severity = InfoBarSeverity.Error, IsClosable = false, Title = "Couldn't connect" };

    // Tailnet view
    private readonly AutoSuggestBox _search = new() { PlaceholderText = "Search machines", QueryIcon = new SymbolIcon(Symbol.Find) };
    private readonly ListView _list = new() { Height = 340, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
    private readonly InfoBar _noTailscale = new()
    {
        Severity = InfoBarSeverity.Informational, IsClosable = false, IsOpen = false,
        Title = "Tailscale isn't running on this PC",
        Message = "Sign in to Tailscale to find your machines, or enter an address.",
    };
    private readonly StackPanel _looking = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(4, 12, 0, 0) };

    // Step 2 / address form
    private readonly TextBox _endpoint = new() { Header = "Address", IsReadOnly = true };
    private readonly TextBox _address = new() { Header = "Address", PlaceholderText = "ssh://user@machine  or  https://machine.tailnet.ts.net" };
    private readonly TextBox _user = new() { Header = "User" };
    private readonly TextBox _socket = new() { Header = "Rex socket", PlaceholderText = "Detect automatically" };
    private readonly InfoBar _sshInfo = new() { Severity = InfoBarSeverity.Informational, IsClosable = false, IsOpen = true };
    private readonly TextBox _name = new() { Header = "Name" };
    private readonly ComboBox _icon = new() { Header = "Icon", HorizontalAlignment = HorizontalAlignment.Stretch };
    private DiscoveredMachine? _picked;
    private bool _nameEdited;
    private SavedHost? _added;

    private AddHostDialog(XamlRoot root, ICore core, HostRegistry registry, IReadOnlyList<DiscoveredMachine> machines, SavedHost? editing)
    {
        _core = core;
        _registry = registry;
        _machines = machines;
        _editing = editing;
        _dialog = DialogKit.New(root, editing is null ? "Add host" : "Edit host");
        _dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        _dialog.CloseButtonText = "Cancel";
        _dialog.DefaultButton = ContentDialogButton.Primary;
        _dialog.PrimaryButtonText = editing is null ? "Add host" : "Save";

        foreach (var kind in HostKinds.All)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock { Text = kind.Glyph, FontFamily = ProgramIcons.FontFor(kind.Source), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Width = 16 }.Decorative());
            row.Children.Add(new TextBlock { Text = kind.Label, VerticalAlignment = VerticalAlignment.Center });
            _icon.Items.Add(new ComboBoxItem { Content = row, Tag = kind.Key });
        }
        _name.TextChanged += (_, _) => { if (_name.FocusState != FocusState.Unfocused) _nameEdited = true; Validate(); };
        _address.TextChanged += (_, _) => { if (!_nameEdited && HostOf(_address.Text) is { } h) _name.Text = h; Validate(); };
        _user.TextChanged += (_, _) => { if (_picked is not null) _endpoint.Text = SshEndpoint(_picked); Validate(); };
        _socket.TextChanged += (_, _) => { if (_picked is not null) _endpoint.Text = SshEndpoint(_picked); };
        _search.TextChanged += (_, _) => FillList();
        _list.ItemClick += (_, e) => { if ((e.ClickedItem as ListViewItem)?.Tag is DiscoveredMachine m) Pick(m); };
        _selector.Items.Add(_tailnetTab);
        _selector.Items.Add(_addressTab);
        _selector.SelectionChanged += (_, _) => { _picked = null; ShowView(); };
        AutomationProperties.SetName(_list, "Machines on your tailnet");

        var open = new HyperlinkButton { Content = "Open Tailscale" };
        open.Click += (_, _) => OpenTailscale();
        _noTailscale.ActionButton = open;
        _looking.Children.Add(new ProgressRing { IsActive = true, Width = 16, Height = 16, MinWidth = 16, MinHeight = 16 });
        _looking.Children.Add(new TextBlock { Text = "Looking for machines…", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });

        var layout = new StackPanel { Spacing = 8 };
        layout.Children.Add(_busy);
        layout.Children.Add(_failure);
        if (editing is null) layout.Children.Add(_selector);
        layout.Children.Add(_body);
        _dialog.Content = layout;
        _dialog.PrimaryButtonClick += OnPrimary;
        _dialog.SecondaryButtonClick += OnSecondary;
        _dialog.CloseButtonClick += (_, _) => { if (_added is not null) _ = _registry.RemoveAsync(_added.Id); };
    }

    public static async Task<bool> ShowAsync(XamlRoot root, ICore core, HostRegistry registry, RexModel model,
        IReadOnlyList<DiscoveredMachine> tailnet, DiscoveredMachine? machine, SavedHost? editing)
    {
        var flow = new AddHostDialog(root, core, registry, tailnet, editing);
        void OnDiscovery(object? s, IReadOnlyList<DiscoveredMachine> m) { flow._machines = m; flow._discovered = true; flow.FillList(); }
        core.DiscoveryUpdated += OnDiscovery;
        try
        {
            if (editing is not null) flow.ShowEdit(editing);
            else if (machine is not null) { flow._selector.SelectedItem = flow._tailnetTab; flow.Pick(machine); }
            else
            {
                flow._selector.SelectedItem = flow._tailnetTab;
                flow._discovered = tailnet.Count > 0;
                _ = core.CommandAsync("discover");
                _ = flow.GiveUpLookingAsync();
            }
            return await flow._dialog.ShowAsync() == ContentDialogResult.Primary || flow._added is not null;
        }
        finally { core.DiscoveryUpdated -= OnDiscovery; }
    }

    private async Task GiveUpLookingAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        _dialog.DispatcherQueue.TryEnqueue(() => { _discovered = true; FillList(); });
    }

    // ------------------------------------------------------------------ views

    private void ShowView()
    {
        _body.Children.Clear();
        _failure.IsOpen = false;
        _dialog.SecondaryButtonText = "";
        if (_picked is not null) { ShowMachineForm(_picked); return; }
        _dialog.Title = "Add host";
        if (_selector.SelectedItem == _addressTab) ShowAddressForm();
        else ShowTailnet();
        Validate();
    }

    private void ShowTailnet()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_noTailscale);
        panel.Children.Add(_search);
        panel.Children.Add(_looking);
        panel.Children.Add(_list);
        _body.Children.Add(panel);
        FillList();
    }

    private void FillList()
    {
        string q = _search.Text.Trim();
        var machines = _machines.Where(m => !m.Self && (q.Length == 0 || RexPalette.Matches(m.Name, q) || RexPalette.Matches(m.Dns ?? "", q))).ToList();
        _list.Items.Clear();
        void Group(string header, IEnumerable<DiscoveredMachine> items)
        {
            var list = items.OrderBy(m => m.Name).ToList();
            if (list.Count == 0) return;
            _list.Items.Add(new ListViewItem
            {
                Content = new TextBlock { Text = header, FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.8 },
                IsHitTestVisible = false, IsTabStop = false, MinHeight = 28, Padding = new Thickness(12, 8, 12, 0),
            });
            foreach (var m in list) _list.Items.Add(MachineRow(m));
        }
        Group("Rex servers", machines.Where(m => m.Online && m.Rex is not null));
        Group("Reachable over SSH", machines.Where(m => m.Online && m.Rex is null));
        Group("Offline", machines.Where(m => !m.Online));
        bool none = _machines.Count(m => !m.Self) == 0;
        _looking.Visibility = none && !_discovered ? Visibility.Visible : Visibility.Collapsed;
        _noTailscale.IsOpen = none && _discovered;
        _list.Visibility = none ? Visibility.Collapsed : Visibility.Visible;
    }

    private ListViewItem MachineRow(DiscoveredMachine m)
    {
        var kind = HostKinds.Get(HostKinds.Infer(null, m.Rex?.Endpoint, m.Os));
        var grid = new Grid { Height = 52, ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = kind.Glyph, FontFamily = ProgramIcons.FontFor(kind.Source), FontSize = 20, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }.Decorative());

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = m.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { m.Dns?.TrimEnd('.'), m.Os }.Where(s => !string.IsNullOrEmpty(s))), FontSize = 12, Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        string check; string? brush = null;
        if (!m.Online) { check = "Offline"; brush = "RexTertiaryTextBrush"; }
        else if (m.Rex is { } rex) check = rex.Endpoint?.StartsWith("https", StringComparison.OrdinalIgnoreCase) == true ? "Rex · HTTPS" : "Rex · HTTP";
        else if (m.Ssh is { TailscaleSsh: true }) check = "Tailscale SSH";
        else if (m.Ssh is not null) check = "SSH";
        else { check = "No Rex found"; brush = "RexCautionBrush"; }
        var checkText = new TextBlock { Text = check, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        if (brush is not null) checkText.Foreground = ThemeLookup.Brush(ThemeLookup.Theme, brush);
        Grid.SetColumn(checkText, 2);
        grid.Children.Add(checkText);

        var saved = _registry.Hosts.Where(h => string.Equals(h.Name, m.Name, StringComparison.OrdinalIgnoreCase)
                                               || h.Endpoint.Contains(m.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (saved.Count > 0)
        {
            var tag = new Border
            {
                Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center,
                Background = ThemeLookup.Brush(ThemeLookup.Theme, "RexSubtleHoverBrush"),
                Child = new TextBlock { Text = "Added", FontSize = 11 },
            };
            ToolTipService.SetToolTip(tag, string.Join(", ", saved.Select(s => s.Name)));
            Grid.SetColumn(tag, 3);
            grid.Children.Add(tag);
        }

        var item = new ListViewItem { Content = grid, Tag = m, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsEnabled = m.Online };
        AutomationProperties.SetName(item, $"{m.Name}, {check}{(saved.Count > 0 ? ", added" : "")}");
        return item;
    }

    /// <summary>Step 2: connect to a picked machine (Rex server or SSH).</summary>
    private void Pick(DiscoveredMachine m)
    {
        _picked = m;
        _nameEdited = false;
        ShowView();
    }

    private void ShowMachineForm(DiscoveredMachine m)
    {
        _dialog.Title = $"Connect to {m.Name}";
        _dialog.SecondaryButtonText = "Back";
        var form = new StackPanel { Spacing = 12 };
        form.Transitions = [new EntranceThemeTransition { FromHorizontalOffset = 24, FromVerticalOffset = 0 }];
        if (m.Rex is { Endpoint: { } rex })
        {
            _endpoint.Text = rex;
            form.Children.Add(_endpoint);
        }
        else
        {
            if (string.IsNullOrEmpty(_user.Text)) _user.Text = Environment.UserName.ToLowerInvariant();
            _sshInfo.Title = m.Ssh is { TailscaleSsh: true } ? $"Tailscale SSH is on for {m.Name}. No keys needed." : "Uses your SSH keys and ssh-agent.";
            _endpoint.Text = SshEndpoint(m);
            form.Children.Add(_sshInfo);
            form.Children.Add(_user);
            form.Children.Add(_socket);
            form.Children.Add(_endpoint);
        }
        _name.Text = m.Name;
        SelectIcon(HostKinds.Infer(null, m.Rex?.Endpoint, m.Os));
        form.Children.Add(_name);
        form.Children.Add(_icon);
        _body.Children.Add(form);
        Validate();
    }

    private void ShowAddressForm()
    {
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(_address);
        form.Children.Add(_name);
        if (_icon.SelectedIndex < 0) SelectIcon("server");
        form.Children.Add(_icon);
        form.Children.Add(new TextBlock
        {
            Text = "Rex doesn't authenticate connections. Use a trusted network such as Tailscale, or SSH.",
            FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
        });
        _body.Children.Add(form);
        _address.Loaded += (_, _) => _address.Focus(FocusState.Programmatic);
    }

    private void ShowEdit(SavedHost host)
    {
        _address.Text = host.Endpoint;
        _name.Text = host.Name;
        _nameEdited = true;
        SelectIcon(host.Icon);
        _body.Children.Add(new StackPanel { Spacing = 12, Children = { _address, _name, _icon } });
        Validate();
    }

    private void SelectIcon(string key)
    {
        for (int i = 0; i < _icon.Items.Count; i++)
            if ((_icon.Items[i] as ComboBoxItem)?.Tag as string == key) { _icon.SelectedIndex = i; return; }
    }

    // ------------------------------------------------------------------ validation and submit

    private string? CurrentEndpoint() =>
        _editing is not null || (_picked is null && _selector.SelectedItem == _addressTab) ? _address.Text.Trim()
        : _picked is { Rex.Endpoint: { } rex } ? rex
        : _picked is not null ? SshEndpoint(_picked)
        : null;

    private void Validate()
    {
        var endpoint = CurrentEndpoint();
        bool addressMode = _editing is not null || (_picked is null && _selector.SelectedItem == _addressTab);
        string? problem = endpoint is null ? null : EndpointProblem(endpoint);
        if (addressMode)
        {
            _address.Description = _address.Text.Length > 0 && problem is not null
                ? new TextBlock { Text = problem, Foreground = ThemeLookup.Brush(ThemeLookup.Theme, "RexCriticalBrush"), FontSize = 12 }
                : null;
        }
        _dialog.IsPrimaryButtonEnabled = endpoint is { Length: > 0 } && problem is null && _name.Text.Trim().Length > 0
                                         && (_picked is null || _picked.Rex is not null || _user.Text.Trim().Length > 0);
    }

    private static string? EndpointProblem(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ssh" or "https" or "http"))
            return "Use ssh://, https:// or http://";
        if (string.IsNullOrEmpty(uri.Host)) return "Add the machine's name after the scheme";
        return null;
    }

    private static string? HostOf(string endpoint) =>
        Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host) ? uri.Host.Split('.')[0] : null;

    private string SshEndpoint(DiscoveredMachine m)
    {
        string user = _user.Text.Trim();
        string socket = _socket.Text.Trim();
        string at = user.Length > 0 ? user + "@" : "";
        return $"ssh://{at}{m.Name}{(socket.Length > 0 ? "/" + socket.TrimStart('/') : "")}";
    }

    private void OnSecondary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_added is not null)
        {
            // "Add anyway": keep the host; the core keeps retrying in the background.
            _added = null;
            return;
        }
        args.Cancel = true;
        _picked = null;
        ShowView();
    }

    private async void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? endpoint = CurrentEndpoint();
        string name = _name.Text.Trim();
        string icon = (_icon.SelectedItem as ComboBoxItem)?.Tag as string ?? "server";
        if (endpoint is null) { args.Cancel = true; return; }
        var deferral = args.GetDeferral();
        try
        {
            if (_editing is not null)
            {
                await _registry.UpdateAsync(_editing.Id, name, endpoint, icon);
                return;
            }
            _busy.Visibility = Visibility.Visible;
            _failure.IsOpen = false;
            _dialog.IsPrimaryButtonEnabled = false;
            string? error;
            if (_added is { } existing)
            {
                // Retry after a failure: the host is already saved.
                long since = _core.Model.Revision;
                await _core.CommandAsync("connect_host", new { id = existing.Id });
                error = await WaitForConnectionAsync(existing.Id, since);
            }
            else
            {
                long since = _core.Model.Revision;
                var (host, result) = await _registry.AddAsync(name, endpoint, icon);
                _added = host;
                error = result.Ok ? await WaitForConnectionAsync(host.Id, since) : result.Error ?? "The core refused the host.";
            }
            _busy.Visibility = Visibility.Collapsed;
            if (error is null) { _added = null; return; } // connected: close
            args.Cancel = true;
            _failure.Message = error;
            _failure.IsOpen = true;
            _dialog.SecondaryButtonText = "Add anyway";
            _dialog.PrimaryButtonText = "Retry";
            _dialog.IsPrimaryButtonEnabled = true;
        }
        finally { deferral.Complete(); }
    }

    /// <summary>Wait for the new host to connect (null) or fail (the server's reason, verbatim).</summary>
    private async Task<string?> WaitForConnectionAsync(string id, long since)
    {
        var done = new TaskCompletionSource<string?>();
        void Check(object? s, RexModel model)
        {
            var host = model.Host(id);
            if (host is null || model.Revision <= since) return;
            if (host.IsConnected) done.TrySetResult(null);
            else if (host.State is "offline" or "error") done.TrySetResult(host.Error ?? $"{host.Name} is offline.");
        }
        _core.ModelChanged += Check;
        try
        {
            Check(null, _core.Model); // only states published after the request count
            var finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            return finished == done.Task ? done.Task.Result : "No answer after 20 seconds. The host stays in the list and keeps retrying.";
        }
        finally { _core.ModelChanged -= Check; }
    }

    private static void OpenTailscale()
    {
        var gui = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale-ipn.exe");
        try
        {
            Process.Start(new ProcessStartInfo(File.Exists(gui) ? gui : "https://tailscale.com/download/windows") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[add-host] open tailscale: {ex.Message}");
        }
    }
}
