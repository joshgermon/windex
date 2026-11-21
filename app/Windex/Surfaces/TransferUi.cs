// File transfer surfaces (design §9.10 notices, docs/contract.md "Transfers"):
// - TransferBars: one Fluent InfoBar per upload batch or download in the notices area (at most
//   three bars there in all), with a progress bar fed by the model's `transfers` and Cancel; it
//   turns into the outcome (where the file went, Show in folder for a download) when the
//   command's RESULT arrives.
// - RemotePathDialog: Download file… asks for the remote path, prefilled with the pane's folder
//   and completing names with list_dir; a path that names no file is refused in the dialog,
//   before the Save As picker.
// - MultilinePasteDialog: the warning before text with line breaks reaches a program that has
//   not asked for bracketed paste.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Services;
using Windex.Shell;

namespace Windex.Surfaces;

public sealed class TransferBars
{
    /// <summary>The notices area shows at most this many bars (design §9.10).</summary>
    private const int MaxNotices = 3;

    private readonly ICore _core;
    private readonly Panel _area;
    private readonly FrameworkElement _scope;
    /// <summary>Running transfers in the order they started.</summary>
    private readonly OrderedDictionary<string, TransferBar> _running = new();

    public TransferBars(ICore core, Panel area, FrameworkElement scope)
    {
        _core = core;
        _area = area;
        _scope = scope;
        core.ModelChanged += (_, model) => Update(model);
    }

    /// <summary>Show a transfer that is starting; <paramref name="id"/> is the one sent with the command.</summary>
    public TransferBar Begin(string id, bool upload, string title, string message)
    {
        var bar = new TransferBar(this, id, upload, title, message);
        _running[id] = bar;
        Present(bar.InfoBar);
        return bar;
    }

    private void Update(RexModel model)
    {
        foreach (var bar in _running.Values)
            bar.Show(model.Transfers.FirstOrDefault(t => t.Id == bar.Id));
        // Running transfers that newer notices pushed out come back once there is room, above the
        // notices that came after them (the newest of them first, so the oldest ends on top).
        foreach (var bar in _running.Values.Reverse().Where(b => !_area.Children.Contains(b.InfoBar)))
        {
            if (_area.Children.Count >= MaxNotices) break;
            bar.InfoBar.Background = ThemeLookup.Brush(_scope, "RexOverlayBrush");
            _area.Children.Insert(0, bar.InfoBar);
        }
    }

    /// <summary>
    /// Add a bar as the newest notice, keeping the area to <see cref="MaxNotices"/>: the oldest other
    /// notice goes first, a running transfer only when nothing else is left (it returns when there is room).
    /// </summary>
    private void Present(InfoBar bar)
    {
        bar.Background = ThemeLookup.Brush(_scope, "RexOverlayBrush");
        _area.Children.Add(bar);
        while (_area.Children.Count > MaxNotices)
        {
            var others = _area.Children.Where(c => !ReferenceEquals(c, bar)).ToList();
            _area.Children.Remove(others.FirstOrDefault(c => !IsRunning(c)) ?? others[0]);
        }
    }

    /// <summary>Whether a notices-area element is a running transfer's bar (plain notices push those out last).</summary>
    public bool IsRunning(UIElement element) => _running.Values.Any(r => ReferenceEquals(r.InfoBar, element));

    internal void Cancel(string id) => _ = _core.CommandAsync("cancel_transfer", new { id });

    internal void Ended(TransferBar bar, TimeSpan? linger)
    {
        _running.Remove(bar.Id);
        // A transfer that ended while pushed out of the area still says how it ended. Only an
        // error may push a running transfer (and its Cancel) out to say so.
        bool room = _area.Children.Count < MaxNotices || _area.Children.Any(c => !IsRunning(c));
        if (!_area.Children.Contains(bar.InfoBar) && (room || bar.InfoBar.Severity == InfoBarSeverity.Error)) Present(bar.InfoBar);
        if (linger is not { } wait) return;
        var timer = _scope.DispatcherQueue.CreateTimer();
        timer.Interval = wait;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _area.Children.Remove(bar.InfoBar);
            _lingering.Remove(timer);
        };
        // Held until it fires: a timer nothing references can be collected before it ticks.
        _lingering.Add(timer);
        timer.Start();
    }

    private readonly HashSet<Microsoft.UI.Dispatching.DispatcherQueueTimer> _lingering = new();

    internal void Remove(InfoBar bar) => _area.Children.Remove(bar);
}

/// <summary>One transfer's InfoBar: progress while it runs, then its outcome.</summary>
public sealed class TransferBar
{
    private readonly TransferBars _owner;
    private readonly bool _upload;
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, IsIndeterminate = true, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _detail = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = "Cancel", Padding = new Thickness(11, 3, 11, 4), FontSize = 12 };
    private bool _canceling;

    public string Id { get; }
    public InfoBar InfoBar { get; }

    internal TransferBar(TransferBars owner, string id, bool upload, string title, string message)
    {
        _owner = owner;
        _upload = upload;
        Id = id;
        _detail.Foreground = ThemeLookup.Brush(ThemeLookup.Theme, "RexSecondaryTextBrush");
        _detail.Text = upload ? "Preparing…" : "Opening the file…";
        // Progress under the message; the numbers and Cancel share the row below it.
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_detail);
        Grid.SetColumn(_cancel, 1);
        row.Children.Add(_cancel);
        var body = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 16, 14) };
        body.Children.Add(_progress);
        body.Children.Add(row);
        _cancel.Click += (_, _) =>
        {
            _canceling = true;
            _cancel.IsEnabled = false;
            _detail.Text = "Canceling…";
            _owner.Cancel(Id);
        };
        AutomationProperties.SetName(_cancel, upload ? "Cancel upload" : "Cancel download");
        InfoBar = new InfoBar
        {
            Title = title,
            Message = message,
            IsOpen = true,
            IsClosable = false,
            Severity = InfoBarSeverity.Informational,
            IconSource = new FontIconSource { Glyph = upload ? "\uE898" : "\uE896" },
            Content = body,
        };
        AutomationProperties.SetName(InfoBar, title);
        AutomationProperties.SetLiveSetting(InfoBar, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
    }

    /// <summary>The transfer's state from the latest model (null: not listed yet, or already over).</summary>
    internal void Show(TransferModel? t)
    {
        if (t is null || _canceling) return;
        string ofFiles = t.Count > 1 ? $" · {t.Index + 1} of {t.Count}: {t.Name}" : "";
        switch (t.State)
        {
            case "finishing" when _upload:
                _progress.IsIndeterminate = true;
                _detail.Text = $"Sent {TransferFiles.Size(t.Bytes)}. Waiting for the server to type the {(t.Count > 1 ? "paths" : "path")}…";
                return;
            case "finishing":
                _progress.IsIndeterminate = true;
                _detail.Text = $"Saving {TransferFiles.Size(t.Bytes)}…";
                return;
            case "starting" when t.Bytes == 0:
                _progress.IsIndeterminate = true;
                _detail.Text = (_upload ? "Preparing…" : "Opening the file…") + ofFiles;
                return;
        }
        if (t.Total > 0)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Clamp((double)t.Bytes / t.Total, 0, 1);
            _detail.Text = $"{TransferFiles.Size(t.Bytes)} of {TransferFiles.Size(t.Total)}{ofFiles}";
        }
        else
        {
            _progress.IsIndeterminate = true;
            _detail.Text = TransferFiles.Size(t.Bytes) + ofFiles;
        }
    }

    /// <summary>The upload is done: its paths are typed in the terminal.</summary>
    public void Uploaded(string title, string message) =>
        Finish(InfoBarSeverity.Success, title, message, null, TimeSpan.FromSeconds(6));

    /// <summary>The download is in place: name it, and offer to show it in Explorer.</summary>
    public void Downloaded(string title, string path)
    {
        var show = new Button { Content = "Show in folder" };
        show.Click += (_, _) =>
        {
            ShowInFolder(path);
            _owner.Remove(InfoBar);
        };
        Finish(InfoBarSeverity.Success, title, path, show, TimeSpan.FromSeconds(15));
    }

    public void Failed(string title, string error)
    {
        if (error == "canceled") Finish(InfoBarSeverity.Informational, _upload ? "Upload canceled" : "Download canceled", "Nothing was kept.", null, TimeSpan.FromSeconds(4));
        else Finish(InfoBarSeverity.Error, title, error, null, null);
    }

    private void Finish(InfoBarSeverity severity, string title, string message, ButtonBase? action, TimeSpan? linger)
    {
        InfoBar.IconSource = null;
        InfoBar.Severity = severity;
        InfoBar.Title = title;
        InfoBar.Message = message;
        InfoBar.Content = null;
        InfoBar.ActionButton = action;
        InfoBar.IsClosable = true;
        InfoBar.Closed += (_, _) => _owner.Remove(InfoBar);
        AutomationProperties.SetName(InfoBar, title);
        _owner.Ended(this, linger);
    }

    /// <summary>Open the file's folder in Explorer with the file selected (the shell's own call for it).</summary>
    public static void ShowInFolder(string path)
    {
        nint item = ILCreateFromPathW(path);
        if (item != 0)
        {
            try
            {
                if (SHOpenFolderAndSelectItems(item, 0, 0, 0) >= 0) return;
            }
            finally { ILFree(item); }
        }
        // The file is gone (or the shell refused): open its folder if that still exists.
        if (Path.GetDirectoryName(path) is not { } folder || !Directory.Exists(folder)) return;
        try { Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* The path is on screen in the notice. */ }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint item);
}

/// <summary>Download file…: the remote path, completed from the server's listing (list_dir).</summary>
public static class RemotePathDialog
{
    private sealed record Suggestion(string Name, bool Dir, string Path)
    {
        public override string ToString() => Dir ? Name + "/" : Name;
    }

    public static async Task<string?> ShowAsync(XamlRoot root, ICore core, ulong pane, string hostName, string? cwd)
    {
        var dialog = DialogKit.New(root, $"Download from {hostName}");
        string start = string.IsNullOrEmpty(cwd) ? "~/" : TransferFiles.ShortPath(cwd).TrimEnd('/') + "/";
        var box = new AutoSuggestBox
        {
            Header = "File on " + hostName,
            Text = start,
            PlaceholderText = "~/path/to/file",
            UpdateTextOnSelect = false,
            MinWidth = 440,
        };
        var hint = new TextBlock
        {
            Text = "Folders and names complete as you type. Next, choose where to save it on this PC.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeLookup.Brush(ThemeLookup.Theme, "RexSecondaryTextBrush"),
        };
        // Why the path can't be downloaded, in the dialog's own theme.
        var problem = (TextBlock)XamlReader.Load("""
            <TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" FontSize="12"
                       TextWrapping="Wrap" Visibility="Collapsed" Foreground="{ThemeResource SystemFillColorCriticalBrush}" />
            """);
        AutomationProperties.SetLiveSetting(problem, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Assertive);
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(box);
        form.Children.Add(problem);
        form.Children.Add(hint);
        dialog.Content = form;
        dialog.PrimaryButtonText = "Choose where to save…";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        var listings = new Dictionary<string, List<Suggestion>?>();
        int generation = 0;

        async Task Suggest()
        {
            int mine = ++generation;
            string text = box.Text;
            int slash = text.LastIndexOf('/');
            string dir = slash < 0 ? "" : text[..(slash + 1)];
            string prefix = text[(slash + 1)..];
            if (!listings.TryGetValue(dir, out var entries))
            {
                listings[dir] = entries = await List(core, pane, dir);
                if (mine != generation) return;
            }
            var matches = (entries ?? [])
                .Where(e => e.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (prefix.StartsWith('.') || !e.Name.StartsWith('.')))
                .OrderByDescending(e => e.Dir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();
            box.ItemsSource = matches;
            box.IsSuggestionListOpen = matches.Count > 0 && box.FocusState != FocusState.Unfocused;
        }

        void Sync() => dialog.IsPrimaryButtonEnabled = box.Text.Trim() is { Length: > 0 } t && !t.EndsWith('/');

        // The server is asked whether the path names a file before the Save As picker opens.
        async Task<bool> Confirm()
        {
            string? why = await Problem(core, pane, hostName, box.Text.Trim());
            problem.Text = why ?? "";
            problem.Visibility = why is null ? Visibility.Collapsed : Visibility.Visible;
            if (why is not null) box.Focus(FocusState.Programmatic);
            return why is null;
        }

        box.TextChanged += (sender, args) =>
        {
            Sync();
            problem.Visibility = Visibility.Collapsed;
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) _ = Suggest();
        };
        // Arrowing through the list leaves the text alone; Enter or a click takes the entry: a
        // folder opens (its entries are offered next), a file fills the box.
        bool submitted = false;
        box.QuerySubmitted += async (sender, args) =>
        {
            if (args.ChosenSuggestion is Suggestion s)
            {
                box.Text = s.Dir ? s.Path + "/" : s.Path;
                CaretToEnd(box);
                if (s.Dir) _ = Suggest();
                return;
            }
            if (!dialog.IsPrimaryButtonEnabled || !await Confirm()) return;
            submitted = true;
            dialog.Hide();
        };
        dialog.PrimaryButtonClick += async (sender, args) =>
        {
            var deferral = args.GetDeferral();
            try { args.Cancel = !await Confirm(); }
            finally { deferral.Complete(); }
        };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            // Ready to type a name in the pane's folder.
            CaretToEnd(box);
            _ = Suggest();
        };
        Sync();

        var result = await dialog.ShowAsync();
        string path = box.Text.Trim();
        return (result == ContentDialogResult.Primary || submitted) && path.Length > 0 && !path.EndsWith('/') ? path : null;
    }

    private static void CaretToEnd(AutoSuggestBox box)
    {
        if (Descendant<TextBox>(box) is { } text) text.Select(text.Text.Length, 0);

        static T? Descendant<T>(DependencyObject node) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is T match) return match;
                if (Descendant<T>(child) is { } deeper) return deeper;
            }
            return null;
        }
    }

    /// <summary>
    /// Why a remote path can't be downloaded, from a listing of its folder narrowed to its name:
    /// the folder or the file is missing, or it is a folder. Null when it names a file, or when the
    /// listing can't tell (the download then gives the server's reason).
    /// </summary>
    private static async Task<string?> Problem(ICore core, ulong pane, string hostName, string path)
    {
        int slash = path.LastIndexOf('/');
        string dir = slash < 0 ? "" : path[..(slash + 1)];
        string name = path[(slash + 1)..];
        if (name is "." or ".." || (dir.Length == 0 && name == "~")) return $"{path} is a folder. Only files can be downloaded.";
        string folder = dir.Length == 0 ? "this pane's folder" : dir.Length == 1 ? dir : dir.TrimEnd('/');
        var result = await core.CommandAsync("list_dir", new { pane, path = dir, prefix = name });
        if (!result.Ok || result.Data is not { } data) return null;
        switch (data.TryGetProperty("status", out var status) ? status.GetString() : "ok")
        {
            case "missing": return $"{folder} doesn't exist on {hostName}.";
            case "not_dir": return $"{folder} isn't a folder on {hostName}.";
            case "denied": return $"{hostName} doesn't let you read {folder}.";
        }
        if (!data.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in entries.EnumerateArray())
            if (e.TryGetProperty("name", out var n) && n.GetString() == name)
                return e.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.True ? $"{name} is a folder. Only files can be downloaded." : null;
        bool truncated = data.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
        return truncated ? null : $"There's no {name} in {folder} on {hostName}.";
    }

    /// <summary>A folder's entries (null when it can't be listed).</summary>
    private static async Task<List<Suggestion>?> List(ICore core, ulong pane, string dir)
    {
        var result = await core.CommandAsync("list_dir", new { pane, path = dir });
        if (!result.Ok || result.Data is not { } data || !data.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<Suggestion>();
        foreach (var e in entries.EnumerateArray())
        {
            if (!e.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;
            bool isDir = e.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.True;
            list.Add(new Suggestion(name, isDir, dir + name));
        }
        return list;
    }
}

/// <summary>Before text with line breaks reaches a program without bracketed paste (each line may run).</summary>
public static class MultilinePasteDialog
{
    private const int PreviewLines = 6;

    /// <summary>(paste, "don't ask again" ticked).</summary>
    public static async Task<(bool Paste, bool DontAsk)> ShowAsync(XamlRoot root, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
        var dialog = DialogKit.New(root, "Paste multiple lines?");
        var content = new StackPanel { Spacing = 12, MaxWidth = 520 };
        content.Children.Add(new TextBlock
        {
            Text = lines.Length > 1
                ? $"This text has {lines.Length} lines. The program in this pane takes a pasted line break as Enter, so each line may run as a command."
                : "This text ends with a line break. The program in this pane takes it as Enter, so it may run as a command.",
            TextWrapping = TextWrapping.Wrap,
        });

        var preview = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };
        foreach (var (line, i) in lines.Take(PreviewLines).Select((l, i) => (l, i)))
        {
            if (i > 0) preview.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            preview.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = line.Length > 120 ? line[..120] + "…" : line });
        }
        var well = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = ThemeLookup.Brush(ThemeLookup.Theme, "RexPaneStrokeBrush"),
            Background = ThemeLookup.Brush(ThemeLookup.Theme, "RexPreviewWellBrush"),
            Child = new ScrollViewer
            {
                Content = preview,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        content.Children.Add(well);
        if (lines.Length > PreviewLines)
            content.Children.Add(new TextBlock { Text = $"and {lines.Length - PreviewLines} more lines", FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, -6, 0, 0) });

        var dontAsk = new CheckBox { Content = "Don't ask again", IsTabStop = false };
        content.Children.Add(dontAsk);
        dialog.Content = content;
        dialog.PrimaryButtonText = "Paste";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Opened += (_, _) => dontAsk.IsTabStop = true;
        bool paste = await dialog.ShowAsync() == ContentDialogResult.Primary;
        return (paste, paste && dontAsk.IsChecked == true);
    }
}
