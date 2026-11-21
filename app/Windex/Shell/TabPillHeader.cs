// The contents of a pill tab (design §3.4 with amendment 1): the program deck (the focused
// pane's tile in front, the tab's other programs peeking out behind it, as on Rex Mac) with the
// status badge, the label (inline-renamable), the activity word and an OSC 9;4 progress bar
// along the pill's inside bottom edge.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windex.Core;
using Windows.System;

namespace Windex.Shell;

public sealed partial class TabPillHeader : Grid
{
    /// <summary>The front tile; tiles behind it are smaller and peek out by <see cref="DeckStep"/>.</summary>
    private const double FrontTile = 18, BackTile = 14, DeckStep = 4;

    private readonly Grid _deck = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 9, 0) };
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _rename = new() { Visibility = Visibility.Collapsed, MinWidth = 80 };
    private readonly TextBlock _word = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 2, 0), Visibility = Visibility.Collapsed };
    // No track under the bar: along a pill's edge a full-width hairline reads as an underline.
    private readonly ProgressBar _progress = new()
    {
        Height = 2, MinHeight = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2),
        CornerRadius = new CornerRadius(1), Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    private string _currentLabel = "";
    private bool _renaming;
    private Tone _tone;

    /// <summary>Raised when an inline rename is committed with a new label.</summary>
    public event EventHandler<string>? Renamed;

    public TabPillHeader()
    {
        _label.Style = (Style)Application.Current.Resources["RexTabLabelTextStyle"];
        _word.Style = (Style)Application.Current.Resources["RexActivityWordTextStyle"];
        _rename.Style = (Style)Application.Current.Resources["RexInlineRenameTextBoxStyle"];
        AutomationProperties.SetName(_rename, "Tab name");
        AutomationProperties.SetLiveSetting(_word, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);

        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Children.Add(_deck);
        SetColumn(_label, 1); Children.Add(_label);
        SetColumn(_rename, 1); Children.Add(_rename);
        SetColumn(_word, 2); Children.Add(_word);
        SetColumnSpan(_progress, 3); Children.Add(_progress);

        DoubleTapped += (_, e) => { e.Handled = true; BeginRename(); };
        _rename.KeyDown += OnRenameKey;
        _rename.LostFocus += (_, _) => EndRename(commit: true);
        ActualThemeChanged += (_, _) => _word.Foreground = Activity.WordBrush(this, _tone);
    }

    /// <summary>
    /// The activity word is never trimmed: when the pill is too narrow for the deck, word and a
    /// little label, the word steps aside (the tile's badge and the tooltip still carry it).
    /// </summary>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        // A narrow pill keeps only the front tile (the focused pane's program) and gives the label the room.
        for (int i = 1; i < _deck.Children.Count; i++)
            _deck.Children[i].Visibility = availableSize.Width >= 120 ? Visibility.Visible : Visibility.Collapsed;
        if (_word.Text.Length > 0)
        {
            var unbounded = new Windows.Foundation.Size(double.PositiveInfinity, availableSize.Height);
            _word.Visibility = Visibility.Visible;
            _word.Measure(unbounded);
            _deck.Measure(unbounded);
            double needed = _deck.DesiredSize.Width + _word.DesiredSize.Width + 24;
            _word.Visibility = availableSize.Width >= needed ? Visibility.Visible : Visibility.Collapsed;
        }
        return base.MeasureOverride(availableSize);
    }

    public void Update(WindowModel w, string sessionLabel, string hostName, int iconCount, string colorful, bool selected)
    {
        _currentLabel = string.IsNullOrWhiteSpace(w.Title) ? w.Label : w.Title;
        _label.Text = _currentLabel;

        // Program deck: the focused pane's program in front, then the tab's other distinct programs.
        var (word, tone, message) = Activity.Of(w);
        _tone = tone;
        var keys = w.Panes.OrderByDescending(p => p.Pane == w.FocusedPane)
            .Select(p => ProgramIcons.Resolve(p.Icon, p.Program).Key)
            .Distinct().Take(iconCount).ToList();
        _deck.Children.Clear();
        bool monochrome = colorful == "never" || (colorful == "active" && !selected);
        for (int i = 0; i < keys.Count; i++)
        {
            var tile = new ProgramTile
            {
                IconKey = keys[i],
                TileSize = i == 0 ? FrontTile : BackTile,
                Badge = i == 0 ? tone : Tone.None,
                Monochrome = monochrome,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                // Each tile behind shows a DeckStep sliver to the right of the one in front.
                Margin = new Thickness(i == 0 ? 0 : FrontTile - BackTile + i * DeckStep, 0, 0, 0),
            };
            Canvas.SetZIndex(tile, keys.Count - i);
            _deck.Children.Add(tile);
        }
        _deck.Visibility = keys.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var visible = Activity.VisibleWord(word, tone == Tone.Critical ? "danger" : tone == Tone.Caution ? "warning" : null);
        _word.Text = visible ?? "";
        _word.Visibility = visible is null ? Visibility.Collapsed : Visibility.Visible;
        _word.Foreground = Activity.WordBrush(this, tone);

        if (w.Progress is { } progress)
        {
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = progress.Percent is null && progress.State is "working" or "indeterminate";
            _progress.Value = progress.Percent ?? 100;
            _progress.ShowPaused = progress.State == "paused";
            _progress.ShowError = progress.State == "error";
        }
        else _progress.Visibility = Visibility.Collapsed;

        var tip = $"{_currentLabel}\n{sessionLabel} · {hostName}";
        if (!string.IsNullOrWhiteSpace(message)) tip += "\n" + message;
        ToolTipService.SetToolTip(this, tip);

        string name = _currentLabel;
        if (visible is not null) name += ", " + visible;
        if (w.Progress?.Percent is { } pct) name += $", {pct}%";
        AutomationName = name;
    }

    /// <summary>"{label}, {activity word}, {progress}%" for the tab's automation name.</summary>
    public string AutomationName { get; private set; } = "";

    public void BeginRename()
    {
        if (_renaming) return;
        _renaming = true;
        _rename.Text = _currentLabel;
        _label.Visibility = Visibility.Collapsed;
        _rename.Visibility = Visibility.Visible;
        _rename.Focus(FocusState.Programmatic);
        _rename.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (!_renaming) return;
        _renaming = false;
        _rename.Visibility = Visibility.Collapsed;
        _label.Visibility = Visibility.Visible;
        var text = _rename.Text.Trim();
        if (commit && text.Length > 0 && text != _currentLabel) Renamed?.Invoke(this, text);
    }

    private void OnRenameKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { EndRename(commit: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { EndRename(commit: false); e.Handled = true; }
    }
}
