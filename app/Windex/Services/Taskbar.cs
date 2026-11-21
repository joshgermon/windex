// The window's taskbar button (design §3.6), through ITaskbarList3, which works unpackaged:
// progress for the session on screen (indeterminate, a percentage, paused in yellow, error in red)
// and an overlay badge while a tab out of sight needs you (the shell's warning icon) or has failed
// (its error icon). Every call is skipped when nothing changed, and a taskbar that is not there
// (Explorer restarting, a server SKU) just leaves the button plain.
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Windex.Services;

public enum TaskbarState { None, Indeterminate, Normal, Paused, Error }

public enum TaskbarBadge { None, NeedsYou, Error }

internal sealed partial class Taskbar
{
    private static readonly Guid TaskbarListClass = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    private readonly nint _hwnd;
    private readonly ITaskbarList3? _list;
    private (TaskbarState State, int Percent)? _progress;
    private TaskbarBadge? _badge;
    private string? _badgeText;
    private nint _badgeIcon;

    public Taskbar(nint hwnd)
    {
        _hwnd = hwnd;
        try
        {
            var iid = typeof(ITaskbarList3).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(TaskbarListClass, 0, ClsctxInprocServer, iid, out var unknown));
            try
            {
                _list = (ITaskbarList3)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None);
            }
            finally { Marshal.Release(unknown); }
            _list.HrInit();
        }
        catch (Exception ex)
        {
            App.Log($"taskbar unavailable: {ex.Message}");
            _list = null;
        }
    }

    public void SetProgress(TaskbarState state, int percent)
    {
        if (_list is null || _progress == (state, percent)) return;
        _progress = (state, percent);
        try
        {
            _list.SetProgressState(_hwnd, state switch
            {
                TaskbarState.Indeterminate => TbpfIndeterminate,
                TaskbarState.Normal => TbpfNormal,
                TaskbarState.Paused => TbpfPaused,
                TaskbarState.Error => TbpfError,
                _ => TbpfNoProgress,
            });
            if (state is TaskbarState.Normal or TaskbarState.Paused or TaskbarState.Error)
                _list.SetProgressValue(_hwnd, (ulong)Math.Clamp(percent, 0, 100), 100);
        }
        catch (COMException ex) { App.Log($"taskbar progress: {ex.Message}"); }
    }

    public void SetBadge(TaskbarBadge badge, string description)
    {
        if (_list is null || (_badge == badge && _badgeText == description)) return;
        _badgeText = description;
        if (_badge == badge && badge != TaskbarBadge.None)
        {
            // Same mark, new words ("2 tabs need you"): keep the icon, refresh the description.
            try { _list.SetOverlayIcon(_hwnd, _badgeIcon, description); }
            catch (COMException ex) { App.Log($"taskbar badge: {ex.Message}"); }
            return;
        }
        _badge = badge;
        nint previous = _badgeIcon;
        _badgeIcon = badge == TaskbarBadge.None ? 0 : StockIcon(badge == TaskbarBadge.Error ? SiidError : SiidWarning);
        try { _list.SetOverlayIcon(_hwnd, _badgeIcon, badge == TaskbarBadge.None ? null : description); }
        catch (COMException ex) { App.Log($"taskbar badge: {ex.Message}"); }
        if (previous != 0) DestroyIcon(previous);
    }

    /// <summary>A small icon from the shell's stock set (the same warning and error marks Windows uses).</summary>
    private static nint StockIcon(uint id)
    {
        var info = new StockIconInfo { Size = (uint)Marshal.SizeOf<StockIconInfo>() };
        return SHGetStockIconInfo(id, ShgsiIcon | ShgsiSmallIcon, ref info) == 0 ? info.Icon : 0;
    }

    // ------------------------------------------------------------------ interop

    private const uint ClsctxInprocServer = 1;
    private const int TbpfNoProgress = 0, TbpfIndeterminate = 1, TbpfNormal = 2, TbpfError = 4, TbpfPaused = 8;
    private const uint SiidWarning = 78, SiidError = 80;
    private const uint ShgsiIcon = 0x100, ShgsiSmallIcon = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StockIconInfo
    {
        public uint Size;
        public nint Icon;
        public int SystemImageIndex;
        public int IconIndex;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetStockIconInfo(uint id, uint flags, ref StockIconInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}

/// <summary>ITaskbarList3 (shobjidl_core.h), every slot up to SetOverlayIcon in vtable order.</summary>
[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
internal partial interface ITaskbarList3
{
    void HrInit();
    void AddTab(nint hwnd);
    void DeleteTab(nint hwnd);
    void ActivateTab(nint hwnd);
    void SetActiveAlt(nint hwnd);
    void MarkFullscreenWindow(nint hwnd, int fullscreen);
    void SetProgressValue(nint hwnd, ulong completed, ulong total);
    void SetProgressState(nint hwnd, int flags);
    void RegisterTab(nint tab, nint main);
    void UnregisterTab(nint tab);
    void SetTabOrder(nint tab, nint insertBefore);
    void SetTabActive(nint tab, nint main, uint reserved);
    void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);
    void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);
    void ThumbBarSetImageList(nint hwnd, nint imageList);
    void SetOverlayIcon(nint hwnd, nint icon, string? description);
}
