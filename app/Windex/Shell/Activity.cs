// Activity vocabulary (design §3.4, §8.5): which words show, in which colour, with which badge.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windex.Core;

namespace Windex.Shell;

public enum Tone { None, Working, Caution, Critical, Success }

public static class Activity
{
    private static readonly HashSet<string> CautionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Needs input", "Needs approval", "Question", "Needs sign-in", "Bell", "Paused", "Blocked",
    };

    /// <summary>Classify a word/tone pair from the model.</summary>
    public static Tone ToneOf(string? word, string? tone)
    {
        if (string.Equals(word, "Done", StringComparison.OrdinalIgnoreCase)) return Tone.Success;
        if (string.Equals(word, "Error", StringComparison.OrdinalIgnoreCase) || tone == "danger") return Tone.Critical;
        if (tone == "warning" || (word is not null && CautionWords.Contains(word))) return Tone.Caution;
        if (tone == "accent" || string.Equals(word, "Working", StringComparison.OrdinalIgnoreCase)) return Tone.Working;
        return Tone.None;
    }

    /// <summary>The word to show, or null (working and idle show no word).</summary>
    public static string? VisibleWord(string? word, string? tone) =>
        string.IsNullOrWhiteSpace(word) || ToneOf(word, tone) is Tone.Working or Tone.None ? null : word;

    /// <summary>Higher is more pressing (sorting, folding).</summary>
    public static int Priority(Tone tone) => tone switch
    {
        Tone.Caution => 4,
        Tone.Critical => 3,
        Tone.Success => 2,
        Tone.Working => 1,
        _ => 0,
    };

    public static Brush? WordBrush(FrameworkElement scope, Tone tone) => tone switch
    {
        Tone.Caution => ThemeLookup.Brush(scope, "RexCautionBrush"),
        Tone.Critical => ThemeLookup.Brush(scope, "RexCriticalBrush"),
        Tone.Success => ThemeLookup.Brush(scope, "RexSuccessBrush"),
        Tone.Working => ThemeLookup.Brush(scope, "RexTertiaryTextBrush"),
        _ => null,
    };

    /// <summary>The window's state as one (word, tone): its own word, else its most pressing pane, else progress.</summary>
    public static (string? Word, Tone Tone, string? Message) Of(WindowModel w)
    {
        string? word = w.Word; Tone tone = ToneOf(w.Word, w.Tone); string? message = null;
        foreach (var p in w.Panes)
        {
            if (p.Status is not { } s) continue;
            var t = ToneOf(s.Word, s.Tone);
            if (Priority(t) > Priority(tone)) { word = s.Word; tone = t; message = s.Message; }
            else if (message is null && s.Word == word) message = s.Message;
        }
        if (w.Progress is { } progress)
        {
            if (progress.State == "error" && Priority(Tone.Critical) > Priority(tone)) { word = "Error"; tone = Tone.Critical; }
            else if (progress.State == "paused" && Priority(Tone.Caution) > Priority(tone)) { word = "Paused"; tone = Tone.Caution; }
            else if (tone == Tone.None) tone = Tone.Working;
        }
        if (tone == Tone.None && w.Panes.Any(p => p.Paused)) { word = "Paused"; tone = Tone.Caution; }
        return (word, tone, message);
    }

    public static (string? Word, Tone Tone) Of(PaneModel p)
    {
        if (p.Paused) return ("Paused", Tone.Caution);
        if (p.Status is { } s) return (s.Word, ToneOf(s.Word, s.Tone));
        if (p.Progress is { State: "error" }) return ("Error", Tone.Critical);
        if (p.Progress is { State: "paused" }) return ("Paused", Tone.Caution);
        if (p.Progress is not null) return (null, Tone.Working);
        return (null, Tone.None);
    }

    /// <summary>Elapsed-time label: now, 4m, 2h, 3d.</summary>
    public static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes}m" : t.TotalDays < 1 ? $"{(int)t.TotalHours}h" : $"{(int)t.TotalDays}d";
}
