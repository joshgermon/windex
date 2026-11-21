// Command line:
//   TerminalLab.exe [--demo 1..5] [--screenshot-friendly] [--theme dark|light] [--font-size <pt>]
//                   [--size <w>,<h>] [--pos <x>,<y>] [--opaque] [--select-all] [--mouse-tracking]
//                   [--log <file>] [--bench <seconds> [--bench-mode full|stream] --out <file>]
//                   [--leak-check <cycles> --out <file>] [--non-owner] [--lose-device <seconds>]
//   --screenshot-friendly  deterministic content, a steady cursor, keyboard focus in the terminal.
//   --mouse-tracking       the programs ask for mouse reports (SGR, button events): pointer input goes to Core.Mouse.
//   --log                  every input call the views make, one line each (RecordingCore).
//   --bench                runs the stress demo (full-screen truecolor frames, or streaming lines), writes timings, exits.
//   --leak-check           opens and closes a pane card <cycles> times, reports views still alive after GC, exits.
//   --non-owner            the core ignores the views' resizes, like a pane another client owns: the frame
//                          keeps a 100x30 grid, smaller than the view (green first row, red last row).
//   --lose-device          runs the GPU device-loss recovery after <seconds>; the views must rebuild and redraw.
//   Ctrl+wheel over a terminal changes the demo's font size (the host's zoom, as Windex does it).
using System.Globalization;

namespace TerminalLab;

internal sealed record LabOptions
{
    public int Demo { get; init; } = 1;
    public bool ScreenshotFriendly { get; init; }
    public bool? Dark { get; init; }
    public double FontSize { get; init; } = 11;
    public (int W, int H)? Size { get; init; }
    public (int X, int Y)? Position { get; init; }
    public bool Opaque { get; init; }
    public bool SelectAll { get; init; }
    public bool MouseTracking { get; init; }
    public string? LogPath { get; init; }
    public double BenchSeconds { get; init; }
    /// <summary>Benchmark input: full-screen truecolor frames (default) or streaming lines.</summary>
    public bool BenchStreaming { get; init; }
    public string? OutPath { get; init; }
    public int LeakCycles { get; init; }
    public bool NonOwner { get; init; }
    public double LoseDeviceAfter { get; init; }

    public static LabOptions Parse(string[] args)
    {
        var o = new LabOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            (int, int) Pair() { var p = Next().Split(','); return (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture)); }
            o = args[i] switch
            {
                "--demo" => o with { Demo = Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 1, Demos.Count) },
                "--screenshot-friendly" => o with { ScreenshotFriendly = true },
                "--theme" => o with { Dark = Next() != "light" },
                "--font-size" => o with { FontSize = double.Parse(Next(), CultureInfo.InvariantCulture) },
                "--size" => o with { Size = Pair() },
                "--pos" => o with { Position = Pair() },
                "--opaque" => o with { Opaque = true },
                "--select-all" => o with { SelectAll = true },
                "--mouse-tracking" => o with { MouseTracking = true },
                "--log" => o with { LogPath = Next() },
                "--bench" => o with { BenchSeconds = double.Parse(Next(), CultureInfo.InvariantCulture), Demo = 5 },
                "--bench-mode" => o with { BenchStreaming = Next() == "stream" },
                "--out" => o with { OutPath = Next() },
                "--leak-check" => o with { LeakCycles = int.Parse(Next(), CultureInfo.InvariantCulture) },
                "--non-owner" => o with { NonOwner = true },
                "--lose-device" => o with { LoseDeviceAfter = double.Parse(Next(), CultureInfo.InvariantCulture) },
                _ => o,
            };
        }
        return o;
    }
}
