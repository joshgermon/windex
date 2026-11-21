// Small ContentDialogs (design §9): close confirmation (§9.6), new session (§9.4) and a one-field
// prompt (session rename when the sidebar is hidden, §9.5). Esc always means Cancel / Keep running.
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Windex.Core;
using Windex.Services;
using Windex.Shell;

namespace Windex.Surfaces;

internal static class DialogKit
{
    public static ContentDialog New(XamlRoot root, string title) => new()
    {
        XamlRoot = root,
        Title = title,
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        RequestedTheme = (root.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"],
    };
}

public static class ConfirmCloseDialog
{
    /// <summary>Ask before ending programs. Returns (close, "don't ask for shells" ticked).</summary>
    public static async Task<(bool Close, bool DontAskForShells)> ShowAsync(
        XamlRoot root, string what, string name, string hostName, IReadOnlyList<PaneModel> panes, bool showShellOption)
    {
        string title = what == "tabs" ? $"Close {name}?" : $"Close {what} \"{name}\"?";
        var dialog = DialogKit.New(root, title);
        var content = new StackPanel { Spacing = 12, MinWidth = 380 };

        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap };
        intro.Inlines.Add(new Run { Text = panes.Count == 1 ? "This ends the program running in it on " : "This ends the programs running in it on " });
        intro.Inlines.Add(new Run { Text = hostName, FontWeight = FontWeights.SemiBold });
        intro.Inlines.Add(new Run { Text = ":" });
        content.Children.Add(intro);

        var list = new StackPanel { Spacing = 6, Margin = new Thickness(4, 0, 0, 0) };
        foreach (var pane in panes.Take(4))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new ProgramTile { IconKey = pane.Icon, Program = pane.Program, TileSize = 16, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = pane.Program ?? "shell", FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrWhiteSpace(pane.Title) && pane.Title != pane.Program)
                row.Children.Add(new TextBlock { Text = pane.Title, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 });
            list.Children.Add(row);
        }
        if (panes.Count > 4) list.Children.Add(new TextBlock { Text = $"and {panes.Count - 4} more", FontSize = 12, Opacity = 0.7 });
        content.Children.Add(list);

        CheckBox? dontAsk = null;
        if (showShellOption)
        {
            // Not a tab stop while the dialog opens, so initial focus lands on "Keep running".
            dontAsk = new CheckBox { Content = "Don't ask when only a shell is running", IsTabStop = false };
            content.Children.Add(dontAsk);
        }

        dialog.Content = content;
        dialog.PrimaryButtonText = what switch { "tabs" => "Close tabs", "session" => "Close session", "pane" => "Close pane", _ => "Close tab" };
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["RexCriticalButtonStyle"];
        dialog.CloseButtonText = "Keep running";
        dialog.CloseButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        dialog.DefaultButton = ContentDialogButton.Close;
        // "Keep running" gets initial focus (not the checkbox), so Enter and Esc both keep programs.
        dialog.Opened += (_, _) =>
        {
            FindButton(dialog, "CloseButton")?.Focus(FocusState.Programmatic);
            if (dontAsk is not null) dontAsk.IsTabStop = true;
        };
        bool close = await dialog.ShowAsync() == ContentDialogResult.Primary;
        return (close, close && dontAsk?.IsChecked == true);
    }

    private static Button? FindButton(DependencyObject root, string name)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button { Name: var n } button && n == name) return button;
            if (FindButton(child, name) is { } found) return found;
        }
        return null;
    }
}

public static class TextPromptDialog
{
    public static async Task<string?> ShowAsync(XamlRoot root, string title, string header, string initial, string primary)
    {
        var dialog = DialogKit.New(root, title);
        var box = new TextBox { Header = header, Text = initial, MinWidth = 320 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        dialog.Content = box;
        dialog.PrimaryButtonText = primary;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}

public static class NewSessionDialog
{
    public sealed record Result(string Host, string? Label, string? Cwd);

    public static async Task<Result?> ShowAsync(XamlRoot root, IReadOnlyList<HostModel> hosts, string? defaultHost, HostRegistry registry)
    {
        var dialog = DialogKit.New(root, "New session");
        var hostBox = new ComboBox { Header = "Host", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var h in hosts)
        {
            var kind = HostKinds.Get(registry.IconFor(h));
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            label.Children.Add(new TextBlock { Text = kind.Glyph, FontFamily = ProgramIcons.FontFor(kind.Source), FontSize = 14, VerticalAlignment = VerticalAlignment.Center }.Decorative());
            label.Children.Add(new TextBlock { Text = h.IsConnected ? h.Name : $"{h.Name} (offline)", VerticalAlignment = VerticalAlignment.Center });
            hostBox.Items.Add(new ComboBoxItem { Content = label, Tag = h, IsEnabled = h.IsConnected });
        }
        var start = hosts.FirstOrDefault(h => h.Id == defaultHost && h.IsConnected) ?? hosts.FirstOrDefault(h => h.IsConnected);
        hostBox.SelectedIndex = start is null ? -1 : hosts.ToList().IndexOf(start);

        var name = new TextBox { Header = "Name", PlaceholderText = "Leave empty to let Rex name it" };
        var cwd = new TextBox { Header = "Start in", PlaceholderText = "Home folder" };
        var form = new StackPanel { Spacing = 12, Width = 400 };
        form.Children.Add(hostBox);
        form.Children.Add(name);
        form.Children.Add(cwd);
        dialog.Content = form;
        dialog.PrimaryButtonText = "Create";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        void Sync()
        {
            var h = (hostBox.SelectedItem as ComboBoxItem)?.Tag as HostModel;
            dialog.Title = h is null ? "New session" : $"New session on {h.Name}";
            dialog.IsPrimaryButtonEnabled = h is not null;
        }
        hostBox.SelectionChanged += (_, _) => Sync();
        name.Loaded += (_, _) => name.Focus(FocusState.Programmatic);
        Sync();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        if ((hostBox.SelectedItem as ComboBoxItem)?.Tag is not HostModel host) return null;
        return new Result(host.Id, name.Text.Trim() is { Length: > 0 } n ? n : null, cwd.Text.Trim() is { Length: > 0 } c ? c : null);
    }
}
