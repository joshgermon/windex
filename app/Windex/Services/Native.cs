// Small Win32 helpers the shell needs (taskbar flash, foreground, window icon).
using System.Runtime.InteropServices;

namespace Windex.Services;

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x2;
    private const uint FLASHW_TIMERNOFG = 0xC;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FLASHWINFO info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    /// <summary>
    /// Whether another app holds this chord as a system-wide hotkey, so Windows never delivers it
    /// to Windex. Probed by registering it for this thread and releasing it at once.
    /// </summary>
    public static bool HotkeyTakenElsewhere(KeyChord chord)
    {
        const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
        const int ErrorHotkeyAlreadyRegistered = 1409, Id = 0x5EED;
        uint mods = ModNoRepeat;
        if (chord.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu)) mods |= ModAlt;
        if (chord.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) mods |= ModControl;
        if (chord.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) mods |= ModShift;
        if (RegisterHotKey(0, Id, mods, (uint)chord.Key))
        {
            UnregisterHotKey(0, Id);
            return false;
        }
        return Marshal.GetLastPInvokeError() == ErrorHotkeyAlreadyRegistered;
    }

    /// <summary>Flash the taskbar button until the window comes to the foreground.</summary>
    public static void FlashTaskbar(nint hwnd)
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 0,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }
}
