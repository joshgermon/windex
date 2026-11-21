// Chrome motion (design §10). Snappy uses the WinUI durations (83 / 167 / 250 ms), Natural
// stretches them by 1.5, and Off (or Windows' "Animation effects" off) turns custom transitions
// off. Terminal content never animates, whatever this says.
namespace Windex.Services;

public static class Motion
{
    public const double Faster = 83, Fast = 167, Normal = 250;

    public static bool Enabled => WindowMaterial.AnimationsEnabled(App.Settings);

    /// <summary>A chrome duration for the Animations setting, given its Snappy length in ms.</summary>
    public static TimeSpan Duration(double snappyMs) =>
        TimeSpan.FromMilliseconds(App.Settings.Animations == "natural" ? snappyMs * 1.5 : snappyMs);
}
