// The find bar (design §5.7): a floating Acrylic pill at the top-right of the terminal body with a
// borderless query box, the match count, match-case and regular-expression toggles, the two
// match steps and close. TerminalView owns it: the view runs the search on the core and feeds the
// counts back from every frame. Find next (Enter, F3, the up chevron) walks up into older output,
// the way terminals search; Find previous (Shift+Enter, Shift+F3, the down chevron) walks back down.
//
// It is built from XAML text and only WinUI's own theme resources, so it works wherever
// TerminalView does (the app and TerminalLab).
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windex.Interop;
using Windows.System;

namespace Windex.Terminal;

internal sealed partial class TerminalFindBar : UserControl
{
    /// <summary>Distance from the terminal body's edges (epx).</summary>
    public const double Inset = 8;
    private const double PreferredWidth = 380, MinimumWidth = 220, Elevation = 32;
    private static readonly TimeSpan CornerSwap = TimeSpan.FromMilliseconds(167);

    private readonly Border _surface;
    private readonly TextBox _query;
    private readonly TextBlock _count, _error;
    private readonly ToggleButton _matchCase, _regex;
    private readonly Button _next, _previous, _close;
    private readonly ThemeShadow _shadow = new();
    private bool _invalid, _atBottom;
    private (uint Total, uint Current, bool Busy, bool Active, RexSearchError Error) _shown = Unknown;
    private static readonly (uint, uint, bool, bool, RexSearchError) Unknown = (uint.MaxValue, 0, false, false, RexSearchError.None);

    /// <summary>The query or its options changed (TerminalView searches again).</summary>
    public event EventHandler? QueryChanged;
    /// <summary>Find next: Enter, F3 or the up chevron (toward older output).</summary>
    public event EventHandler? NextRequested;
    /// <summary>Find previous: Shift+Enter, Shift+F3 or the down chevron (toward newer output).</summary>
    public event EventHandler? PreviousRequested;
    /// <summary>Esc or the close button.</summary>
    public event EventHandler? CloseRequested;

    public TerminalFindBar()
    {
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(Inset);
        Visibility = Visibility.Collapsed;
        // The terminal's I-beam must not reach the bar's buttons.
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        AutomationProperties.SetName(this, "Find in terminal");
        AutomationProperties.SetLandmarkType(this, AutomationLandmarkType.Search);

        _surface = (Border)XamlReader.Load(Xaml);
        Content = _surface;
        _query = (TextBox)_surface.FindName("Query");
        _count = (TextBlock)_surface.FindName("Count");
        _error = (TextBlock)_surface.FindName("Error");
        _matchCase = (ToggleButton)_surface.FindName("MatchCase");
        _regex = (ToggleButton)_surface.FindName("Regex");
        _next = (Button)_surface.FindName("Next");
        _previous = (Button)_surface.FindName("Previous");
        _close = (Button)_surface.FindName("Close");

        _surface.Shadow = _shadow;
        _surface.Translation = new Vector3(0, 0, (float)Elevation);

        _query.TextChanged += (_, _) => QueryChanged?.Invoke(this, EventArgs.Empty);
        _query.PreviewKeyDown += OnQueryPreviewKeyDown;
        _matchCase.Click += (_, _) => QueryChanged?.Invoke(this, EventArgs.Empty);
        _regex.Click += (_, _) => QueryChanged?.Invoke(this, EventArgs.Empty);
        _next.Click += (_, _) => NextRequested?.Invoke(this, EventArgs.Empty);
        _previous.Click += (_, _) => PreviousRequested?.Invoke(this, EventArgs.Empty);
        _close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        // Clicking a toggle or button leaves keyboard focus in the query (Enter goes on stepping);
        // Tab still reaches them.
        foreach (var control in new Control[] { _matchCase, _regex, _next, _previous, _close })
            control.AllowFocusOnInteraction = false;
        // Esc and F3 anywhere in the bar (the toggles and buttons too); Tab stays in it.
        PreviewKeyDown += OnBarPreviewKeyDown;
        TabFocusNavigation = KeyboardNavigationMode.Cycle;

        // Pointer input over the bar is the bar's: none of it may select text or scroll the terminal underneath.
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        PointerReleased += (_, e) => e.Handled = true;
        PointerMoved += (_, e) => e.Handled = true;
        PointerWheelChanged += (_, e) => e.Handled = true;
        DoubleTapped += (_, e) => e.Handled = true;
        RightTapped += (_, e) => e.Handled = true;
        UpdateCount();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    /// <summary>An element the bar's shadow falls on (an in-tree shadow needs its receivers named).</summary>
    public void CastShadowOn(UIElement receiver) => _shadow.Receivers.Add(receiver);

    public string Query
    {
        get => _query.Text;
        set => _query.Text = value;
    }

    public RexSearchFlags Flags =>
        (_matchCase.IsChecked == true ? RexSearchFlags.MatchCase : 0) | (_regex.IsChecked == true ? RexSearchFlags.Regex : 0);

    /// <summary>Whether keyboard focus is somewhere in the bar.</summary>
    public bool HasFocus => XamlRoot is { } root && Contains(FocusManager.GetFocusedElement(root) as DependencyObject);

    /// <summary>Whether <paramref name="element"/> is the bar or inside it.</summary>
    public bool Contains(DependencyObject? element)
    {
        for (var n = element; n is not null; n = VisualTreeHelper.GetParent(n))
            if (ReferenceEquals(n, this)) return true;
        return false;
    }

    /// <summary>Keyboard focus into the query, its text selected (typing replaces it).</summary>
    public void FocusQuery()
    {
        _query.Focus(FocusState.Programmatic);
        _query.SelectAll();
    }

    /// <summary>Fit a terminal body of this size: the bar's width, and its place when it sits in the bottom corner.</summary>
    public void Fit(Windows.Foundation.Size body)
    {
        Width = Math.Max(Math.Min(PreferredWidth, body.Width - 2 * Inset), Math.Min(MinimumWidth, body.Width));
        if (!_atBottom) return;
        double height = _surface.ActualHeight > 0 ? _surface.ActualHeight : 40;
        _surface.TranslationTransition = null;
        _surface.Translation = new Vector3(0, (float)Math.Max(0, body.Height - height - 2 * Inset), (float)Elevation);
    }

    /// <summary>A pattern the core refused at once (shown until the query or its options change).</summary>
    public void SetInvalid(bool invalid)
    {
        _invalid = invalid;
        _shown = Unknown;
        UpdateCount();
    }

    /// <summary>The latest frame's counts, or why the core could not search for the query.</summary>
    public void SetResults(uint total, uint current, bool busy, bool active, RexSearchError error)
    {
        if (_shown == (total, current, busy, active, error)) return;
        _shown = (total, current, busy, active, error);
        UpdateCount();
    }

    private void UpdateCount()
    {
        var (total, current, busy, active, frameError) = _shown;
        var refused = _invalid ? RexSearchError.Invalid : active ? frameError : RexSearchError.None;
        bool counted = _query.Text.Length > 0 && active && total != uint.MaxValue && refused == RexSearchError.None;
        string error = refused switch
        {
            RexSearchError.Invalid => "Invalid pattern",
            RexSearchError.TooComplex => "Pattern too complex",
            _ => counted && !busy && total == 0 ? "No results" : "",
        };
        string count = !counted || total == 0 ? ""
            : current > 0 ? $"{current} of {total}"
            : $"{total} found";
        Show(_count, count);
        Show(_error, error);
        ToolTipService.SetToolTip(_error, refused switch
        {
            RexSearchError.Invalid => "This regular expression is not valid. Turn off .* to find the text as typed.",
            RexSearchError.TooComplex => "This regular expression is too large to search with. Simplify it, or turn off .* to find the text as typed.",
            _ => null,
        });
        _next.IsEnabled = _previous.IsEnabled = counted && total > 0;

        static void Show(TextBlock block, string text)
        {
            if (block.Text == text) return;
            block.Text = text;
            block.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(block, text);
            if (text.Length > 0 && FrameworkElementAutomationPeer.FromElement(block) is { } peer)
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
    }

    /// <summary>Keep clear of the current match (design §5.7): when it lies under the bar, the bar
    /// moves to the other right-hand corner of the body, if the match covers less of that one (a
    /// match covering both leaves the bar where it is). <paramref name="match"/> is the match's
    /// box in the body's coordinates (epx).</summary>
    public void AvoidMatch(Windows.Foundation.Rect match, Windows.Foundation.Size body, bool animate)
    {
        if (!IsOpen || ActualWidth <= 0) return;
        double height = _surface.ActualHeight > 0 ? _surface.ActualHeight : 40;
        double bottomOffset = body.Height - height - 2 * Inset;
        // Too short a body to have two corners: stay at the top.
        if (bottomOffset < height) return;
        double left = body.Width - Inset - ActualWidth;
        double here = Covered(_atBottom ? bottomOffset : 0), there = Covered(_atBottom ? 0 : bottomOffset);
        if (here == 0 || there >= here) return;
        _atBottom = !_atBottom;
        _surface.TranslationTransition = animate ? new Vector3Transition { Duration = CornerSwap } : null;
        _surface.Translation = new Vector3(0, _atBottom ? (float)bottomOffset : 0, (float)Elevation);

        // How much of the bar's height the match covers with the bar this far down.
        double Covered(double offset)
        {
            double top = Inset + offset;
            return match.Right > left ? Math.Max(0, Math.Min(match.Bottom, top + height) - Math.Max(match.Top, top)) : 0;
        }
    }

    /// <summary>Back to the top-right corner (the bar opening).</summary>
    public void ResetCorner()
    {
        _atBottom = false;
        _surface.TranslationTransition = null;
        _surface.Translation = new Vector3(0, 0, (float)Elevation);
    }

    private void OnQueryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        if (ShiftDown()) PreviousRequested?.Invoke(this, EventArgs.Empty);
        else NextRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A press on the bar outside the query (a button, a toggle, the frame) puts keyboard
    /// focus in the query unless it is in the bar already, so typing and Enter go on working.</summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (!HasFocus) _query.Focus(FocusState.Pointer);
    }

    /// <summary>Esc and F3 before the focused control sees them. A key something else already
    /// handled is not the bar's: Esc that closed a palette opened from here returns focus to the
    /// bar as it is still being routed.</summary>
    private void OnBarPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case VirtualKey.F3:
                e.Handled = true;
                if (ShiftDown()) PreviousRequested?.Invoke(this, EventArgs.Empty);
                else NextRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static bool ShiftDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private const string Xaml = """
        <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                Height="40" CornerRadius="8" BorderThickness="1" Padding="10,0,4,0"
                Background="{ThemeResource AcrylicInAppFillColorDefaultBrush}"
                BorderBrush="{ThemeResource SurfaceStrokeColorFlyoutBrush}">
            <Border.Resources>
                <!-- Subtle 28 x 28 buttons. Templates whose states use ThemeResource (not lightweight-styling
                     aliases, which resolve once against the app's theme rather than this element's), so
                     they follow the bar between light and dark. -->
                <Style x:Key="FindButtonStyle" TargetType="Button">
                    <Setter Property="Width" Value="28" />
                    <Setter Property="Height" Value="28" />
                    <Setter Property="Padding" Value="0" />
                    <Setter Property="CornerRadius" Value="4" />
                    <Setter Property="Background" Value="Transparent" />
                    <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
                    <Setter Property="HorizontalContentAlignment" Value="Center" />
                    <Setter Property="VerticalContentAlignment" Value="Center" />
                    <Setter Property="UseSystemFocusVisuals" Value="True" />
                    <Setter Property="FocusVisualMargin" Value="-2" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="Button">
                                <ContentPresenter x:Name="ContentPresenter" Content="{TemplateBinding Content}"
                                                  Background="{TemplateBinding Background}" Foreground="{TemplateBinding Foreground}"
                                                  CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}"
                                                  HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                                                  VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}"
                                                  AutomationProperties.AccessibilityView="Raw">
                                    <VisualStateManager.VisualStateGroups>
                                        <VisualStateGroup x:Name="CommonStates">
                                            <VisualState x:Name="Normal" />
                                            <VisualState x:Name="PointerOver">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Background" Value="{ThemeResource SubtleFillColorSecondaryBrush}" />
                                                    <Setter Target="ContentPresenter.Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="Pressed">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Background" Value="{ThemeResource SubtleFillColorTertiaryBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="Disabled">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Foreground" Value="{ThemeResource TextFillColorDisabledBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                        </VisualStateGroup>
                                    </VisualStateManager.VisualStateGroups>
                                </ContentPresenter>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
                <Style x:Key="FindToggleStyle" TargetType="ToggleButton">
                    <Setter Property="Width" Value="28" />
                    <Setter Property="Height" Value="28" />
                    <Setter Property="Padding" Value="0" />
                    <Setter Property="CornerRadius" Value="4" />
                    <Setter Property="Background" Value="Transparent" />
                    <Setter Property="Foreground" Value="{ThemeResource TextFillColorSecondaryBrush}" />
                    <Setter Property="HorizontalContentAlignment" Value="Center" />
                    <Setter Property="VerticalContentAlignment" Value="Center" />
                    <Setter Property="UseSystemFocusVisuals" Value="True" />
                    <Setter Property="FocusVisualMargin" Value="-2" />
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="ToggleButton">
                                <ContentPresenter x:Name="ContentPresenter" Content="{TemplateBinding Content}"
                                                  Background="{TemplateBinding Background}" Foreground="{TemplateBinding Foreground}"
                                                  CornerRadius="{TemplateBinding CornerRadius}" Padding="{TemplateBinding Padding}"
                                                  HorizontalContentAlignment="{TemplateBinding HorizontalContentAlignment}"
                                                  VerticalContentAlignment="{TemplateBinding VerticalContentAlignment}"
                                                  AutomationProperties.AccessibilityView="Raw">
                                    <VisualStateManager.VisualStateGroups>
                                        <VisualStateGroup x:Name="CommonStates">
                                            <VisualState x:Name="Normal" />
                                            <VisualState x:Name="PointerOver">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Background" Value="{ThemeResource SubtleFillColorSecondaryBrush}" />
                                                    <Setter Target="ContentPresenter.Foreground" Value="{ThemeResource TextFillColorPrimaryBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="Pressed">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Background" Value="{ThemeResource SubtleFillColorTertiaryBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="Disabled">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Foreground" Value="{ThemeResource TextFillColorDisabledBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="Checked">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Background" Value="{ThemeResource AccentFillColorDefaultBrush}" />
                                                    <Setter Target="ContentPresenter.Foreground" Value="{ThemeResource TextOnAccentFillColorPrimaryBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="CheckedPointerOver">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Background" Value="{ThemeResource AccentFillColorSecondaryBrush}" />
                                                    <Setter Target="ContentPresenter.Foreground" Value="{ThemeResource TextOnAccentFillColorPrimaryBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="CheckedPressed">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Background" Value="{ThemeResource AccentFillColorTertiaryBrush}" />
                                                    <Setter Target="ContentPresenter.Foreground" Value="{ThemeResource TextOnAccentFillColorSecondaryBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                            <VisualState x:Name="CheckedDisabled">
                                                <VisualState.Setters>
                                                    <Setter Target="ContentPresenter.Background" Value="{ThemeResource AccentFillColorDisabledBrush}" />
                                                    <Setter Target="ContentPresenter.Foreground" Value="{ThemeResource TextOnAccentFillColorDisabledBrush}" />
                                                </VisualState.Setters>
                                            </VisualState>
                                        </VisualStateGroup>
                                    </VisualStateManager.VisualStateGroups>
                                </ContentPresenter>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </Border.Resources>
            <Grid ColumnSpacing="2">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                    <ColumnDefinition Width="Auto" />
                </Grid.ColumnDefinitions>
                <FontIcon Glyph="&#xE721;" FontSize="14" VerticalAlignment="Center"
                          Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                <TextBox x:Name="Query" Grid.Column="1" Margin="2,0,0,0" MinWidth="40" MinHeight="0" Height="32"
                         VerticalAlignment="Center" PlaceholderText="Find" FontSize="13" Padding="6,0"
                         IsSpellCheckEnabled="False" IsTextPredictionEnabled="False"
                         AutomationProperties.Name="Find">
                    <!-- Only the text and its placeholder: no border, fill or clear button (the bar has Close). -->
                    <TextBox.Template>
                        <ControlTemplate TargetType="TextBox">
                            <Grid Background="Transparent">
                                <ScrollViewer x:Name="ContentElement" VerticalAlignment="Center" Padding="{TemplateBinding Padding}"
                                              IsTabStop="False" ZoomMode="Disabled" AutomationProperties.AccessibilityView="Raw"
                                              HorizontalScrollMode="Auto" HorizontalScrollBarVisibility="Hidden"
                                              VerticalScrollMode="Disabled" VerticalScrollBarVisibility="Disabled" />
                                <TextBlock x:Name="PlaceholderTextContentPresenter" VerticalAlignment="Center" IsHitTestVisible="False"
                                           Margin="{TemplateBinding Padding}" Text="{TemplateBinding PlaceholderText}" TextWrapping="NoWrap"
                                           Foreground="{ThemeResource TextControlPlaceholderForeground}" />
                            </Grid>
                        </ControlTemplate>
                    </TextBox.Template>
                </TextBox>
                <TextBlock x:Name="Count" Grid.Column="2" Margin="4,0,6,0" VerticalAlignment="Center" FontSize="12"
                           Foreground="{ThemeResource TextFillColorSecondaryBrush}" Visibility="Collapsed"
                           AutomationProperties.LiveSetting="Polite" />
                <TextBlock x:Name="Error" Grid.Column="2" Margin="4,0,6,0" VerticalAlignment="Center" FontSize="12"
                           Foreground="{ThemeResource SystemFillColorCriticalBrush}" Visibility="Collapsed"
                           AutomationProperties.LiveSetting="Assertive" />
                <ToggleButton x:Name="MatchCase" Grid.Column="3" Style="{StaticResource FindToggleStyle}"
                              ToolTipService.ToolTip="Match case" AutomationProperties.Name="Match case">
                    <TextBlock Text="Aa" FontSize="12" FontWeight="SemiBold" />
                </ToggleButton>
                <ToggleButton x:Name="Regex" Grid.Column="4" Style="{StaticResource FindToggleStyle}"
                              ToolTipService.ToolTip="Use regular expression" AutomationProperties.Name="Use regular expression">
                    <TextBlock Text=".*" FontSize="13" FontFamily="Cascadia Mono, Consolas" FontWeight="SemiBold" />
                </ToggleButton>
                <Rectangle Grid.Column="5" Width="1" Height="16" Margin="4,0" VerticalAlignment="Center"
                           Fill="{ThemeResource DividerStrokeColorDefaultBrush}" />
                <Button x:Name="Next" Grid.Column="6" Style="{StaticResource FindButtonStyle}"
                        ToolTipService.ToolTip="Next match, up (Enter)" AutomationProperties.Name="Next match">
                    <FontIcon Glyph="&#xE70E;" FontSize="12" />
                </Button>
                <Button x:Name="Previous" Grid.Column="7" Style="{StaticResource FindButtonStyle}"
                        ToolTipService.ToolTip="Previous match, down (Shift+Enter)" AutomationProperties.Name="Previous match">
                    <FontIcon Glyph="&#xE70D;" FontSize="12" />
                </Button>
                <Button x:Name="Close" Grid.Column="8" Style="{StaticResource FindButtonStyle}"
                        ToolTipService.ToolTip="Close (Esc)" AutomationProperties.Name="Close find">
                    <FontIcon Glyph="&#xE711;" FontSize="12" />
                </Button>
            </Grid>
        </Border>
        """;
}
