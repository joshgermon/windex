// Window actions: everything the title row, sidebar, pane cards, menus, palette and shortcuts
// can do, as core commands (docs/contract.md) guarded by the confirmations in design §9.6.
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Interop;
using Windex.Panes;
using Windex.Services;
using Windex.Shell;
using Windex.Surfaces;
using Windex.Terminal;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Windex;

public sealed partial class MainWindow : IPaneHost, ISidebarHost, IPaneDropTarget, IPasteHandler
{
    private bool _dialogOpen;

    /// <summary>Run a core command; failures surface as an in-app notice.</summary>
    private async Task<CommandResult> Command(string cmd, object? args = null, bool quiet = false)
    {
        // Acting on the panes and tabs on screen is working in that session.
        if (cmd is "split" or "close_pane" or "zoom_pane" or "focus_pane" or "focus_direction" or "swap_pane"
            or "rename_pane" or "clear_pane" or "reset_pane" or "reconnect_pane" or "set_split_ratio" or "balance"
            or "new_tab" or "close_tab" or "rename_tab" or "move_tab")
            AdoptCurrent();
        // Commands that move or remove the focused terminal hand keyboard focus back to the pane.
        if (cmd is "split" or "close_pane" or "zoom_pane" or "focus_pane" or "focus_direction" or "new_tab"
            or "select_tab" or "close_tab" or "open_session" or "kill_session")
            _refocusPane = true;
        var result = await _core.CommandAsync(cmd, args);
        if (!result.Ok && !quiet) ShowNotice("error", $"Couldn't {Describe(cmd)}", result.Error ?? "The server refused the request.");
        return result;
    }

    private static string Describe(string cmd) => cmd switch
    {
        "open_session" => "open the session",
        "new_session" => "create the session",
        "new_tab" => "open a new tab",
        "split" => "split the pane",
        "close_pane" => "close the pane",
        "close_tab" => "close the tab",
        "kill_session" => "close the session",
        "rename_tab" or "rename_pane" or "rename_session" => "rename",
        _ => cmd.Replace('_', ' '),
    };

    private async Task<bool> DialogAsync(Func<XamlRoot, Task<bool>> show)
    {
        if (_dialogOpen || Content.XamlRoot is not { } root) return false;
        _dialogOpen = true;
        try { return await show(root); }
        finally { _dialogOpen = false; }
    }

    // ------------------------------------------------------------------ sessions and tabs

    public async Task OpenSessionAsync(string hostId, string sessionId, bool quiet = false)
    {
        var target = new CurrentRef { Host = hostId, Session = sessionId };
        var previous = (_chosen, _chosenShown);
        Choose(target);
        // Sent even when the session is already on screen: a session the core picked by itself is
        // only claimed (sized to this window) once open_session says the user chose it.
        var result = await Command("open_session", new { host = hostId, session = sessionId }, quiet);
        if (!result.Ok && _chosen == target)
        {
            (_chosen, _chosenShown) = previous;
            SyncAppFocus();
        }
    }

    private async Task SelectTabAsync(string hostId, string sessionId, string windowId)
    {
        await OpenSessionAsync(hostId, sessionId);
        await Command("select_tab", new { host = hostId, session = sessionId, window = windowId });
    }

    /// <summary>Switch host, session and tab, then focus the pane (toasts, activity rows).</summary>
    private async Task LandOnAsync(string hostId, string sessionId, string? windowId, ulong pane)
    {
        await OpenSessionAsync(hostId, sessionId);
        if (windowId is not null) await Command("select_tab", new { host = hostId, session = sessionId, window = windowId });
        if (pane != 0) await Command("focus_pane", new { pane }, quiet: true);
    }

    public async Task NewTabAsync()
    {
        if (CurrentHost is not { } host || CurrentSession is not { } session) return;
        var cwd = CurrentWindow?.Panes.FirstOrDefault(p => p.Pane == FocusedPane)?.Cwd;
        var result = cwd is null
            ? await Command("new_tab", new { host = host.Id, session = session.Id })
            : await Command("new_tab", new { host = host.Id, session = session.Id, cwd });
        if (result.Ok && result.Data?.TryGetProperty("window", out var w) == true && w.GetString() is { } window)
            await Command("select_tab", new { host = host.Id, session = session.Id, window }, quiet: true);
    }

    public async Task NewSessionAsync(string? hostId)
    {
        var hosts = _model.Hosts;
        if (hosts.Count == 0) { ShowAddHost(null); return; }
        NewSessionDialog.Result? request = null;
        await DialogAsync(async root =>
        {
            request = await NewSessionDialog.ShowAsync(root, hosts, hostId ?? CurrentHost?.Id, _hosts);
            return request is not null;
        });
        if (request is { } r) await CreateSessionAsync(r.Host, r.Label, r.Cwd);
    }

    private async Task CreateSessionAsync(string hostId, string? label, string? cwd)
    {
        object args = (label, cwd) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => new { host = hostId, label, cwd },
            ({ Length: > 0 }, _) => new { host = hostId, label },
            (_, { Length: > 0 }) => new { host = hostId, cwd },
            _ => new { host = hostId },
        };
        var result = await Command("new_session", args);
        if (result.Ok && result.Data?.TryGetProperty("session", out var s) == true && s.GetString() is { } session)
            await OpenSessionAsync(hostId, session);
    }

    private Task RenameTabAsync(string hostId, string sessionId, string windowId, string label) =>
        Command("rename_tab", new { host = hostId, session = sessionId, window = windowId, label });

    public void RenameSession(string hostId, string sessionId, string label) =>
        _ = Command("rename_session", new { host = hostId, session = sessionId, label });

    /// <summary>Rename the current tab inline in its pill.</summary>
    private void BeginRenameTab()
    {
        if (Tabs.SelectedItem is TabViewItem { Header: TabPillHeader header }) header.BeginRename();
    }

    private async Task BeginRenameSessionAsync()
    {
        if (CurrentHost is not { } host || CurrentSession is not { } session) return;
        if (SidebarHost.Visibility == Visibility.Visible && SidebarView.BeginSessionRename(host.Id, session.Id)) return;
        string? label = null;
        await DialogAsync(async root =>
        {
            label = await TextPromptDialog.ShowAsync(root, "Rename session", "Name", session.Label, "Rename");
            return label is not null;
        });
        if (label is { Length: > 0 } && label != session.Label) RenameSession(host.Id, session.Id, label);
    }

    private async Task CloseTabAsync(string hostId, string sessionId, string windowId)
    {
        var host = _model.Host(hostId);
        var window = host?.Sessions.FirstOrDefault(s => s.Id == sessionId)?.Windows.FirstOrDefault(w => w.Id == windowId);
        if (host is null || window is null) return;
        if (await ConfirmCloseAsync("tab", TabLabel(window), host.Name, window.Panes))
            await Command("close_tab", new { host = hostId, session = sessionId, window = windowId });
    }

    private async Task CloseOtherTabsAsync(string hostId, string sessionId, string windowId)
    {
        var host = _model.Host(hostId);
        var others = host?.Sessions.FirstOrDefault(s => s.Id == sessionId)?.Windows.Where(w => w.Id != windowId).ToList();
        if (host is null || others is null || others.Count == 0) return;
        string name = others.Count == 1 ? TabLabel(others[0]) : $"{others.Count} other tabs";
        if (!await ConfirmCloseAsync("tabs", name, host.Name, others.SelectMany(w => w.Panes).ToList())) return;
        foreach (var w in others) await Command("close_tab", new { host = hostId, session = sessionId, window = w.Id });
    }

    private async Task KillSessionAsync(string hostId, string sessionId)
    {
        var host = _model.Host(hostId);
        var session = host?.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (host is null || session is null) return;
        if (!await ConfirmCloseAsync("session", session.Label, host.Name, session.Windows.SelectMany(w => w.Panes).ToList(), always: true)) return;
        // Closing the session on screen: the core then shows one of its own choosing, which the
        // window must not claim, so stop reporting focus before the session goes.
        var target = new CurrentRef { Host = hostId, Session = sessionId };
        if (_model.Current == target && _chosen == target)
        {
            _chosen = null;
            SyncAppFocus();
        }
        await Command("kill_session", new { host = hostId, session = sessionId });
    }

    private async Task MoveTabAsync(string hostId, string sessionId, string windowId, int delta)
    {
        var session = _model.Host(hostId)?.Sessions.FirstOrDefault(s => s.Id == sessionId);
        var window = session?.Windows.FirstOrDefault(w => w.Id == windowId);
        if (session is null || window is null) return;
        int index = Math.Clamp(window.Index + delta, 0, session.Windows.Count - 1);
        if (index != window.Index) await Command("move_tab", new { host = hostId, session = sessionId, window = windowId, index });
    }

    private void CycleTab(int delta)
    {
        if (CurrentHost is not { } host || CurrentSession is not { Windows.Count: > 1 } session || CurrentWindow is not { } window) return;
        var ordered = session.Windows.OrderBy(w => w.Index).ToList();
        int at = ordered.FindIndex(w => w.Id == window.Id);
        var next = ordered[(at + delta + ordered.Count) % ordered.Count];
        _ = SelectTabAsync(host.Id, session.Id, next.Id);
    }

    private void GoToTab(int number)
    {
        if (CurrentHost is not { } host || CurrentSession is not { Windows.Count: > 0 } session) return;
        var ordered = session.Windows.OrderBy(w => w.Index).ToList();
        var target = number >= 9 ? ordered[^1] : number <= ordered.Count ? ordered[number - 1] : null;
        if (target is not null) _ = SelectTabAsync(host.Id, session.Id, target.Id);
    }

    /// <summary>Next / previous session in sidebar order across connected hosts.</summary>
    private void CycleSession(int delta)
    {
        var all = _model.Hosts.Where(h => h.IsConnected).SelectMany(h => h.Sessions.Select(s => (Host: h.Id, Session: s.Id))).ToList();
        if (all.Count == 0) return;
        int at = all.FindIndex(x => x.Host == _model.Current?.Host && x.Session == _model.Current?.Session);
        var next = all[((at < 0 ? (delta > 0 ? -1 : 0) : at) + delta + all.Count) % all.Count];
        _ = OpenSessionAsync(next.Host, next.Session);
    }

    // ------------------------------------------------------------------ close confirmation (§9.6)

    /// <summary>Ask before ending programs. "Keep running" is the default; shells alone may skip.</summary>
    private async Task<bool> ConfirmCloseAsync(string what, string name, string hostName, IReadOnlyList<PaneModel> panes, bool always = false)
    {
        bool shellOnly = panes.All(p => ProgramIcons.Resolve(p.Icon, p.Program).Key is "shell" or "pwsh" or "cmd");
        if (!always && (!_settings.ConfirmClose || (shellOnly && _settings.SkipConfirmWhenShellOnly))) return true;
        bool confirmed = false;
        await DialogAsync(async root =>
        {
            var (ok, dontAsk) = await ConfirmCloseDialog.ShowAsync(root, what, name, hostName, panes, showShellOption: !always);
            confirmed = ok;
            if (dontAsk) _settings.Update("confirm", s => s.SkipConfirmWhenShellOnly = true);
            return ok;
        });
        return confirmed;
    }

    // ------------------------------------------------------------------ panes (IPaneHost)

    /// <summary>The user clicked or tabbed into a pane on screen: make it the server's focused pane.</summary>
    public void FocusPane(ulong pane)
    {
        // Only a pane on screen: focus_pane on another tab's pane makes the server switch tabs.
        if (!PaneArea.IsShown(pane)) return;
        AdoptCurrent();
        // One click reaches here from the card and from its content; the model catches up later.
        bool requested = _focusRequest is { } r && r.Pane == pane && DateTime.UtcNow - r.At < TimeSpan.FromSeconds(1);
        if (pane != FocusedPane && !requested)
        {
            _focusRequest = (pane, DateTime.UtcNow);
            _ = Command("focus_pane", new { pane }, quiet: true);
        }
        PaneArea.FocusContent(pane);
    }

    public void SplitPane(ulong pane, string direction) => _ = Command("split", new { pane, direction });

    private void SplitFocused(string direction)
    {
        if (FocusedPane is { } pane) SplitPane(pane, direction);
    }

    public void ToggleZoom(ulong pane)
    {
        if (CurrentWindow is { Panes.Count: > 1 }) _ = Command("zoom_pane", new { pane, zoom = "toggle" });
    }

    public async void ClosePane(ulong pane)
    {
        var model = _model.FindPane(pane, out var window, out var session, out var host);
        if (model is null || host is null) return;
        if (await ConfirmCloseAsync("pane", model.Title, host.Name, [model]))
            await Command("close_pane", new { pane });
    }

    public void RenamePane(ulong pane, string label) => _ = Command("rename_pane", new { pane, label });

    public void ReconnectPane(ulong pane) => _ = Command("reconnect_pane", new { pane });

    public void SetSplitRatio(string splitId, double ratio)
    {
        if (CurrentHost is { } host && CurrentSession is { } session)
            _ = Command("set_split_ratio", new { host = host.Id, session = session.Id, split = splitId, ratio = Math.Round(ratio, 4) }, quiet: true);
    }

    /// <summary>Move focus to the neighbouring pane; false (the key goes to the program) when there is none.</summary>
    private bool FocusDirection(string direction)
    {
        if (FocusedPane is not { } pane || !PaneArea.HasNeighbor(pane, direction)) return false;
        _ = Command("focus_direction", new { pane, direction }, quiet: true);
        return true;
    }

    /// <summary>Move the nearest divider on that axis; false (the key goes to the program) when there is none.</summary>
    private bool ResizeFocused(string direction)
    {
        if (FocusedPane is not { } pane || PaneArea.ResizeTarget(pane, direction) is not { } target) return false;
        SetSplitRatio(target.SplitId, target.Ratio);
        return true;
    }

    private void BeginRenamePane()
    {
        if (FocusedPane is { } pane) PaneArea.Card(pane)?.BeginRename();
    }

    /// <summary>The pane menu (§9.7): a CommandBarFlyout like Windows' text context menus.</summary>
    public void ShowPaneMenu(ulong pane, FrameworkElement anchor, Windows.Foundation.Point? position)
    {
        var model = _model.FindPane(pane, out var window, out _, out _);
        if (model is null || window is null) return;
        bool multi = window.Panes.Count > 1;
        var flyout = new CommandBarFlyout();

        string? selection = _core.SelectionText(pane);
        flyout.PrimaryCommands.Add(Bar("Copy", "\uE8C8", () => CopyText(selection!), selection is { Length: > 0 }));
        flyout.PrimaryCommands.Add(Bar("Paste", "\uE77F", () => PasteInto(pane)));
        flyout.PrimaryCommands.Add(Bar("Select all", "\uE8B3", () => _core.Select(pane, RexSelectOp.All, 0, 0, 0)));
        flyout.PrimaryCommands.Add(Bar("Search terminal", "\uE721", () => OpenFind(pane), keys: "find"));

        flyout.SecondaryCommands.Add(Bar("Split right", null, () => SplitPane(pane, "right"), keys: "split-right", path: "RexSplitRightPath"));
        flyout.SecondaryCommands.Add(Bar("Split left", null, () => SplitPane(pane, "left"), path: "RexSplitRightPath"));
        flyout.SecondaryCommands.Add(Bar("Split down", null, () => SplitPane(pane, "down"), keys: "split-down", path: "RexSplitDownPath"));
        flyout.SecondaryCommands.Add(Bar("Split up", null, () => SplitPane(pane, "up"), path: "RexSplitDownPath"));
        flyout.SecondaryCommands.Add(Bar(window.ZoomedPane == pane ? "Restore pane" : "Zoom pane", window.ZoomedPane == pane ? "\uE73F" : "\uE740",
            () => ToggleZoom(pane), multi, keys: "zoom-pane"));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        flyout.SecondaryCommands.Add(Bar("Rename pane…", "\uE8AC", () => PaneArea.Card(pane)?.BeginRename()));
        flyout.SecondaryCommands.Add(Bar("Copy pane ID", "\uE8C8", () => CopyText(model.Block)));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        string? noUpload = UploadRefusal(pane), noDownload = DownloadRefusal(pane);
        flyout.SecondaryCommands.Add(Bar("Upload files…", "\uE898", () => _ = PickAndUploadAsync(pane), noUpload is null,
            tooltip: noUpload ?? "Send files to this pane's machine; their paths are typed at the prompt"));
        flyout.SecondaryCommands.Add(Bar("Download file…", "\uE896", () => _ = DownloadAsync(pane), noDownload is null,
            tooltip: noDownload ?? "Save a file from this pane's machine on this PC"));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        flyout.SecondaryCommands.Add(Bar("Clear terminal", "\uE75C", () => _ = Command("clear_pane", new { pane })));
        flyout.SecondaryCommands.Add(Bar("Reset terminal", "\uE72C", () => _ = Command("reset_pane", new { pane })));
        flyout.SecondaryCommands.Add(Bar("Reconnect", "\uE895", () => ReconnectPane(pane), model.Paused));
        flyout.SecondaryCommands.Add(new AppBarSeparator());
        flyout.SecondaryCommands.Add(Bar("Close pane…", "\uE711", () => ClosePane(pane), keys: "close-pane"));

        // At the pointer it may extend past the window like any context menu; from More it hangs under the button.
        flyout.ShouldConstrainToRootBounds = false;
        var options = new FlyoutShowOptions
        {
            ShowMode = FlyoutShowMode.Standard,
            Position = position,
            Placement = position is null ? FlyoutPlacementMode.BottomEdgeAlignedRight : FlyoutPlacementMode.Auto,
        };
        flyout.ShowAt(anchor, options);
    }

    private static AppBarButton Bar(string label, string? glyph, Action click, bool enabled = true, string? keys = null, string? path = null, string? tooltip = null)
    {
        var button = new AppBarButton { Label = label, IsEnabled = enabled };
        if (glyph is not null) button.Icon = new FontIcon { Glyph = glyph };
        else if (path is not null)
            button.Icon = new PathIcon { Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), Application.Current.Resources[path]) };
        if (keys is not null && Shortcuts.Text(keys) is { } text) button.KeyboardAcceleratorTextOverride = text;
        ToolTipService.SetToolTip(button, tooltip ?? label);
        button.Click += (_, _) => click();
        return button;
    }

    private static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>The pane menu's Paste: the terminal's own paste (files, pictures, the multi-line check).</summary>
    private void PasteInto(ulong pane)
    {
        if (PaneArea.Card(pane)?.PaneContent is TerminalView view) view.Paste();
    }

    // ------------------------------------------------------------------ file transfers (docs/contract.md "Transfers")

    private TransferBars? _transferBars;
    private TransferBars TransferBars => _transferBars ??= new TransferBars(_core, Notices, Root);

    /// <summary>Why files can't be sent to the pane now (null: they can).</summary>
    private string? UploadRefusal(ulong pane) => TransferRefusal(pane, upload: true);

    private string? DownloadRefusal(ulong pane) => TransferRefusal(pane, upload: false);

    private string? TransferRefusal(ulong pane, bool upload)
    {
        if (_model.FindPane(pane, out _, out _, out var host) is not { } model || host is null) return "This pane is gone.";
        if (!host.IsConnected) return $"{host.Name} is not connected.";
        if (model.Paused) return $"{host.Name} is reconnecting.";
        if (upload ? !host.CanUpload : !host.CanDownload)
            return $"The Rex server on {host.Name} is too old to {(upload ? "receive" : "send")} files. Update Rex there.";
        return null;
    }

    public DropOffer OfferFor(ulong pane)
    {
        if (UploadRefusal(pane) is { } why) return new DropOffer(false, "Can't upload here", why);
        var model = _model.FindPane(pane, out _, out _, out var host)!;
        return new DropOffer(true, $"Upload to {host!.Name}", TransferFiles.ShortPath(model.Cwd));
    }

    public void DropItems(ulong pane, IReadOnlyList<IStorageItem> items) => UploadItems(pane, items);

    public void DropPicture(ulong pane, RandomAccessStreamReference picture) => _ = UploadPictureAsync(pane, picture);

    /// <summary>Ctrl+V and friends: files copied in Explorer, or a picture without text (Win+Shift+S), are uploaded.</summary>
    public async Task<bool> PasteDataAsync(ulong pane, DataPackageView content)
    {
        if (content.Contains(StandardDataFormats.StorageItems))
        {
            UploadItems(pane, await content.GetStorageItemsAsync());
            return true;
        }
        if (!content.Contains(StandardDataFormats.Text) && content.Contains(StandardDataFormats.Bitmap))
        {
            _ = UploadPictureAsync(pane, await content.GetBitmapAsync());
            return true;
        }
        return false;
    }

    public async Task<bool> ConfirmMultilinePasteAsync(ulong pane, string text)
    {
        if (!_settings.WarnMultilinePaste) return true;
        bool paste = false;
        await DialogAsync(async root =>
        {
            var (ok, dontAsk) = await MultilinePasteDialog.ShowAsync(root, text);
            paste = ok;
            if (dontAsk) _settings.Update("terminal", s => s.WarnMultilinePaste = false);
            return ok;
        });
        PaneArea.FocusContent(pane);
        return paste;
    }

    private void UploadItems(ulong pane, IReadOnlyList<IStorageItem> items)
    {
        var (files, refusal) = TransferFiles.FromItems(items);
        if (refusal is not null) ShowNotice("error", files.Count == 0 && items.Any(i => i.IsOfType(StorageItemTypes.Folder)) ? "Folders can't be uploaded" : "Can't upload these", refusal);
        else _ = UploadAsync(pane, files);
    }

    private async Task UploadPictureAsync(ulong pane, RandomAccessStreamReference picture)
    {
        if (UploadRefusal(pane) is { } why) { ShowNotice("error", "Can't upload the picture", why); return; }
        UploadFile file;
        try { file = await TransferFiles.SavePictureAsync(picture); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowNotice("error", "Can't read the picture", ex.Message);
            return;
        }
        await UploadAsync(pane, [file]);
    }

    private async Task PickAndUploadAsync(ulong pane)
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var picked = await picker.PickMultipleFilesAsync();
        if (picked is { Count: > 0 }) UploadItems(pane, picked.ToList());
    }

    /// <summary>Send files to the pane's machine; the server types their paths at the prompt.</summary>
    private async Task UploadAsync(ulong pane, IReadOnlyList<UploadFile> files)
    {
        if (UploadRefusal(pane) is { } why)
        {
            foreach (var temp in files.Where(f => f.Temporary)) try { File.Delete(temp.Path); } catch (IOException) { }
            ShowNotice("error", "Can't upload here", why);
            return;
        }
        var model = _model.FindPane(pane, out _, out _, out var host)!;
        string hostName = host!.Name;
        string what = files.Count == 1 ? files[0].Name : $"{files.Count} files";
        string id = $"upload:{Guid.NewGuid():N}";
        var bar = TransferBars.Begin(id, upload: true, $"Uploading {what}", $"to {hostName} · {TransferFiles.ShortPath(model.Cwd)}");
        var result = await _core.CommandAsync("upload", new
        {
            pane,
            id,
            files = files.Select(f => new { path = f.Path, name = f.Name, temporary = f.Temporary }).ToArray(),
        });
        if (result.Ok)
            bar.Uploaded($"Uploaded {what} to {hostName}", files.Count == 1 ? "Its path is typed at the prompt." : "Their paths are typed at the prompt.");
        else bar.Failed($"Couldn't upload {what}", result.Error ?? "The server refused the upload.");
    }

    /// <summary>Download file…: the remote path (completed from the server), then where to save it.</summary>
    private async Task DownloadAsync(ulong pane)
    {
        if (DownloadRefusal(pane) is { } why) { ShowNotice("error", "Can't download from here", why); return; }
        var model = _model.FindPane(pane, out _, out _, out var host)!;
        string hostName = host!.Name;
        string? remote = null;
        await DialogAsync(async root =>
        {
            remote = await RemotePathDialog.ShowAsync(root, _core, pane, hostName, model.Cwd);
            return remote is not null;
        });
        if (remote is null) return;

        string name = TransferFiles.LocalName(remote);
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads, SuggestedFileName = name };
        string extension = Path.GetExtension(name);
        if (extension.Length > 1) picker.FileTypeChoices.Add($"{extension[1..].ToUpperInvariant()} file", [extension]);
        picker.FileTypeChoices.Add("All files", ["."]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var target = await picker.PickSaveFileAsync();
        if (target is null) return;
        string dest = target.Path;
        // The picker may leave an empty file where the download goes; a failed download removes it.
        bool placeholder = File.Exists(dest) && new FileInfo(dest).Length == 0;

        string id = $"download:{Guid.NewGuid():N}";
        var bar = TransferBars.Begin(id, upload: false, $"Downloading {name}", $"from {hostName} · {remote}");
        var result = await _core.CommandAsync("download", new { pane, id, path = remote, dest });
        if (result.Ok)
        {
            string saved = result.Data?.TryGetProperty("path", out var p) == true ? p.GetString() ?? dest : dest;
            bar.Downloaded($"Downloaded {Path.GetFileName(saved)} from {hostName}", saved);
            return;
        }
        if (placeholder && File.Exists(dest) && new FileInfo(dest).Length == 0)
            try { File.Delete(dest); } catch (IOException) { }
        bar.Failed($"Couldn't download {name}", result.Error ?? "The server refused the download.");
    }

    // ------------------------------------------------------------------ menus (§6.5, §9.7)

    public MenuFlyout TabMenu(string hostId, string sessionId, string windowId)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var session = _model.Host(hostId)?.Sessions.FirstOrDefault(s => s.Id == sessionId);
            var window = session?.Windows.FirstOrDefault(w => w.Id == windowId);
            if (session is null || window is null) return;
            menu.Items.Add(MenuItem("Rename tab", "\uE8AC", "rename-tab", () =>
            {
                if (_tabItems.TryGetValue($"{hostId}/{sessionId}/{windowId}", out var item) && item.Header is TabPillHeader h) h.BeginRename();
            }));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("Move left", "\uE72B", null, () => _ = MoveTabAsync(hostId, sessionId, windowId, -1), () => window.Index > 0));
            menu.Items.Add(MenuItem("Move right", "\uE72A", null, () => _ = MoveTabAsync(hostId, sessionId, windowId, +1), () => window.Index < session.Windows.Count - 1));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("New tab", "\uE710", "new-tab", () => _ = NewTabAsync()));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("Close other tabs…", "\uE711", null, () => _ = CloseOtherTabsAsync(hostId, sessionId, windowId), () => session.Windows.Count > 1));
            menu.Items.Add(MenuItem("Close tab…", "\uE711", "close-tab", () => _ = CloseTabAsync(hostId, sessionId, windowId)));
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
                if (item.Tag is Func<bool> enabled) item.IsEnabled = enabled();
        };
        return menu;
    }

    public MenuFlyout HostMenu(string hostId)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var host = _model.Host(hostId);
            var saved = _hosts.Find(hostId);
            if (host is null) return;
            bool live = host.State is "connected" or "connecting" or "reconnecting";
            menu.Items.Add(live
                ? MenuItem("Disconnect", "\uE8CD", null, () => _ = Command("disconnect_host", new { id = hostId }))
                : MenuItem("Connect", "\uE703", null, () => RetryHost(hostId)));
            menu.Items.Add(MenuItem("Reconnect", "\uE72C", null, () => RetryHost(hostId)));
            menu.Items.Add(MenuItem($"New session on {host.Name}…", "\uE710", null, () => _ = NewSessionAsync(hostId), () => host.IsConnected));
            menu.Items.Add(new MenuFlyoutSeparator());
            if (saved is not null) menu.Items.Add(MenuItem("Edit host…", "\uE70F", null, () => _ = EditHostAsync(hostId)));
            menu.Items.Add(MenuItem("Copy address", "\uE8C8", null, () => CopyText(host.Endpoint)));
            if (SshTarget(host.Endpoint) is { } target)
                menu.Items.Add(MenuItem("Open in Windows Terminal", "\uE756", null, () => OpenInTerminal(target)));
            if (saved is not null)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                menu.Items.Add(MenuItem("Remove host…", "\uE74D", null, () => _ = RemoveHostAsync(hostId)));
            }
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
                if (item.Tag is Func<bool> enabled) item.IsEnabled = enabled();
        };
        return menu;
    }

    public MenuFlyout SessionMenu(string hostId, string sessionId, Action rename)
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var host = _model.Host(hostId);
            if (host is null) return;
            menu.Items.Add(MenuItem("Open", "\uE8A7", null, () => _ = OpenSessionAsync(hostId, sessionId), () => host.IsConnected));
            menu.Items.Add(MenuItem("New tab", "\uE710", null, async () =>
            {
                await OpenSessionAsync(hostId, sessionId);
                await NewTabAsync();
            }, () => host.IsConnected));
            menu.Items.Add(MenuItem("Rename session…", "\uE8AC", null, rename, () => host.IsConnected));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(MenuItem("Close session…", "\uE711", null, () => _ = KillSessionAsync(hostId, sessionId), () => host.IsConnected));
            foreach (var item in menu.Items.OfType<MenuFlyoutItem>())
                if (item.Tag is Func<bool> enabled) item.IsEnabled = enabled();
        };
        return menu;
    }

    /// <summary>"user@host" from ssh://user@host[:port][/socket], for Windows Terminal.</summary>
    private static (string Destination, int? Port)? SshTarget(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "ssh") return null;
        string destination = string.IsNullOrEmpty(uri.UserInfo) ? uri.Host : $"{uri.UserInfo}@{uri.Host}";
        return (destination, uri.IsDefaultPort || uri.Port <= 0 ? null : uri.Port);
    }

    private void OpenInTerminal((string Destination, int? Port) target)
    {
        try
        {
            string args = target.Port is { } port ? $"ssh -p {port} {target.Destination}" : $"ssh {target.Destination}";
            Process.Start(new ProcessStartInfo("wt.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowNotice("error", "Couldn't open Windows Terminal", ex.Message);
        }
    }

    // ------------------------------------------------------------------ hosts (ISidebarHost)

    public void OpenSession(string hostId, string sessionId) => _ = OpenSessionAsync(hostId, sessionId);

    public void SelectTab(string hostId, string sessionId, string windowId) => _ = SelectTabAsync(hostId, sessionId, windowId);

    public void ShowHostTrouble(string hostId)
    {
        _troubleHost = hostId;
        _dismissedTrouble = null;
        RenderHostInfoBar();
    }

    public void RetryHost(string hostId)
    {
        _dismissedTrouble = null;
        _ = Command("connect_host", new { id = hostId });
    }

    public void RefreshHosts()
    {
        foreach (var host in _model.Hosts.Where(h => h.State is "offline" or "error"))
            RetryHost(host.Id);
        _ = _core.CommandAsync("discover");
    }

    public void OpenSettings(string? page) => App.ShowSettings(page);

    public void ShowAddHost(DiscoveredMachine? machine) => _ = AddHostAsync(machine);

    private async Task AddHostAsync(DiscoveredMachine? machine)
    {
        await DialogAsync(root => AddHostDialog.ShowAsync(root, _core, _hosts, _model, _tailnet, machine, editing: null));
        _ = _core.CommandAsync("discover");
    }

    private async Task EditHostAsync(string hostId)
    {
        if (_hosts.Find(hostId) is not { } saved) return;
        await DialogAsync(root => AddHostDialog.ShowAsync(root, _core, _hosts, _model, _tailnet, null, editing: saved));
    }

    private async Task RemoveHostAsync(string hostId)
    {
        if (_hosts.Find(hostId) is not { } saved) return;
        bool remove = false;
        await DialogAsync(async root =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = $"Remove {saved.Name}?",
                Content = new TextBlock
                {
                    Text = $"Windex forgets this host. Nothing changes on {saved.Name}: its sessions keep running.",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Remove host",
                PrimaryButtonStyle = (Style)Application.Current.Resources["RexCriticalButtonStyle"],
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                RequestedTheme = Root.ActualTheme,
            };
            remove = await dialog.ShowAsync() == ContentDialogResult.Primary;
            return remove;
        });
        if (remove) await _hosts.RemoveAsync(hostId);
    }
}
