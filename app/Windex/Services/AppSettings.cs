// Preferences and saved hosts, persisted as JSON in %APPDATA%\Windex\settings.json
// (or the folder named by --settings-dir / WINDEX_SETTINGS_DIR, for tests).
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windex.Services;

public sealed class AppSettings
{
    // ---- Hosts (only persisted when running against rexcore.dll; demo mode keeps its own list) ----
    public List<SavedHost> Hosts { get; set; } = new();

    // ---- General ----
    public bool RestoreLastSession { get; set; } = true;
    public string? LastHost { get; set; }
    public string? LastSession { get; set; }
    public bool ConfirmClose { get; set; } = true;
    public bool SkipConfirmWhenShellOnly { get; set; }

    // ---- Appearance ----
    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";
    /// <summary>mica-alt | mica | acrylic | solid</summary>
    public string Material { get; set; } = "mica-alt";
    /// <summary>Acrylic only, 40–100.</summary>
    public double GlassOpacity { get; set; } = 70;
    public bool KeepGlassWhenInactive { get; set; }
    /// <summary>aurora | accent | none</summary>
    public string Tint { get; set; } = "aurora";
    /// <summary>off | surface | content | both</summary>
    public string DimInactive { get; set; } = "both";
    /// <summary>An accent outline on the focused card (Rex Mac's "Highlight active pane edges", off there too).</summary>
    public bool HighlightFocused { get; set; }
    /// <summary>always | hover | hidden</summary>
    public string PaneHeaders { get; set; } = "always";
    /// <summary>comfortable (floating glass cards) | compact (tiles in one frame, hairline dividers)</summary>
    public string PaneStyle { get; set; } = "comfortable";
    /// <summary>0–5 program tiles per tab.</summary>
    public int TabIconCount { get; set; } = 3;
    /// <summary>always | active | never</summary>
    public string ColorfulIcons { get; set; } = "always";
    /// <summary>left | right</summary>
    public string SidebarPosition { get; set; } = "left";
    public bool SidebarOpen { get; set; } = true;
    public double SidebarWidth { get; set; } = 260;
    /// <summary>snappy | natural | off</summary>
    public string Animations { get; set; } = "snappy";

    // ---- Terminal (read by the terminal view) ----
    /// <summary>
    /// 10 pt (13.3 epx) sits just above the 13 epx chrome text, the proportion Rex Mac's 12 px
    /// terminal has under its 13 pt chrome, so panes read as airy rather than dense.
    /// </summary>
    public const double DefaultTerminalFontSize = 10;

    public string TerminalFontFamily { get; set; } = "Cascadia Mono";
    public double TerminalFontSize { get; set; } = DefaultTerminalFontSize;
    public double TerminalLineHeight { get; set; } = 1.25;
    /// <summary>block | bar | underline | hollow</summary>
    public string CursorShape { get; set; } = "block";
    public bool CursorBlink { get; set; } = true;
    public bool CopyOnSelect { get; set; }
    public bool WarnMultilinePaste { get; set; } = true;

    // ---- Shortcuts ----
    public bool CtrlKSwitcher { get; set; }

    // ---- Notifications ----
    public bool NotifyNeedsInput { get; set; } = true;
    public bool NotifyError { get; set; } = true;
    public bool NotifyDone { get; set; } = true;
    public bool NotifyBell { get; set; }
    public bool NotifyProgramMessages { get; set; } = true;
    public bool FlashTaskbar { get; set; } = true;
    /// <summary>The taskbar button shows the current session's progress and a badge when a tab needs you.</summary>
    public bool TaskbarProgress { get; set; } = true;

    // ---- One-time tips ----
    public bool SeenAddHostTip { get; set; }
    public bool SeenSwitcherTip { get; set; }

    // ------------------------------------------------------------------------------------------

    [JsonIgnore] public string FilePath { get; private set; } = "";

    /// <summary>Raised on the UI thread after any change made through <see cref="Update"/>.</summary>
    public event EventHandler<string>? Changed;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("WINDEX_SETTINGS_DIR");
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--settings-dir");
        if (i >= 0 && i + 1 < args.Length) overrideDir = args[i + 1];
        if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Windex");
        // Windex was called RexWin before it went public: carry its settings (hosts, theme) over once.
        var legacy = Path.Combine(appData, "RexWin", "settings.json");
        try
        {
            if (!File.Exists(Path.Combine(dir, "settings.json")) && File.Exists(legacy))
            {
                Directory.CreateDirectory(dir);
                File.Copy(legacy, Path.Combine(dir, "settings.json"));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return dir;
    }

    public static AppSettings Load(string directory)
    {
        var path = Path.Combine(directory, "settings.json");
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new()
                : new();
        }
        catch (Exception ex)
        {
            // A damaged file must not stop the app; keep it aside so nothing is silently lost.
            System.Diagnostics.Debug.WriteLine($"[settings] unreadable {path}: {ex.Message}");
            try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
            settings = new();
        }
        settings.FilePath = path;
        settings.Normalize();
        return settings;
    }

    private void Normalize()
    {
        GlassOpacity = Math.Clamp(GlassOpacity, 40, 100);
        SidebarWidth = Math.Clamp(SidebarWidth, 200, 360);
        TabIconCount = Math.Clamp(TabIconCount, 0, 5);
        TerminalFontSize = Math.Clamp(TerminalFontSize, 6, 36);
        TerminalLineHeight = Math.Clamp(TerminalLineHeight, 1.0, 2.0);
        if (PaneHeaders is not ("always" or "hover" or "hidden")) PaneHeaders = "always";
        if (PaneStyle is not ("comfortable" or "compact")) PaneStyle = "comfortable";
        if (Animations is not ("snappy" or "natural" or "off")) Animations = "snappy";
        Hosts ??= new();
    }

    /// <summary>Apply a change, persist, and notify listeners with the changed key.</summary>
    public void Update(string key, Action<AppSettings> change)
    {
        change(this);
        Normalize();
        Save();
        Changed?.Invoke(this, key);
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(FilePath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[settings] save failed: {ex.Message}");
        }
    }
}

/// <summary>A host the user added. The core knows it by <see cref="Id"/>.</summary>
public sealed class SavedHost
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    /// <summary>Host-kind icon (see HostKinds): pc, desktop, laptop, server, cloud, web, linux, mini.</summary>
    public string Icon { get; set; } = "server";
    public bool ConnectAtLaunch { get; set; } = true;
}
