# Capture a top-level window of a process to a PNG.
#
#   .\scripts\screenshot.ps1 -Launch <exe> [-Arguments '...'] -Out artifacts\x.png [-Width 1280 -Height 800] [-WaitSeconds 4] [-Close]
#   .\scripts\screenshot.ps1 -ProcessId 1234 -Out artifacts\x.png
#   .\scripts\screenshot.ps1 -ProcessName Windex -Out artifacts\x.png -Mode Print
#
# Modes:
#   Screen (default)  brings the window to the front and copies the screen: true pixels incl. Mica/Acrylic.
#                     Serialized machine-wide by a named mutex so parallel agents don't capture each other.
#   Print             PrintWindow(PW_RENDERFULLCONTENT): works while occluded; backdrop may render black.
param(
    [string]$ProcessName = 'Windex',
    [int]$ProcessId = 0,
    [string]$Out = "$PSScriptRoot\..\artifacts\screenshot.png",
    [string]$Launch,
    [string]$Arguments = '',
    [int]$WaitSeconds = 4,
    [int]$Width = 0,
    [int]$Height = 0,
    [ValidateSet('Screen', 'Print')][string]$Mode = 'Screen',
    [switch]$Close
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('RexShot' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class RexShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public static IntPtr FindFor(uint pid) {
    IntPtr found = IntPtr.Zero; long best = -1;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
      if (p == pid && IsWindowVisible(h)) { RECT r; DwmGetWindowAttribute(h, 9, out r, 16); long a = (long)(r.R-r.L)*(r.B-r.T); if (a > best) { best = a; found = h; } }
      return true; }, IntPtr.Zero);
    return found;
  }
}
"@
}

$mutex = New-Object System.Threading.Mutex($false, 'Global\WindexScreenshot')
$haveLock = $false
try {
    if ($Mode -eq 'Screen' -or $Launch) { $haveLock = $mutex.WaitOne([TimeSpan]::FromSeconds(120)) }
    if ($Launch) {
        if ($Arguments) { $p = Start-Process -FilePath $Launch -ArgumentList $Arguments -PassThru }
        else { $p = Start-Process -FilePath $Launch -PassThru }
        Start-Sleep -Seconds $WaitSeconds
    } elseif ($ProcessId -gt 0) {
        $p = Get-Process -Id $ProcessId -ErrorAction Stop
    } else {
        $p = Get-Process -Name $ProcessName -ErrorAction Stop | Select-Object -First 1
    }
    $h = [IntPtr]::Zero
    for ($i = 0; $i -lt 30 -and $h -eq [IntPtr]::Zero; $i++) { $h = [RexShot]::FindFor([uint32]$p.Id); if ($h -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 500 } }
    if ($h -eq [IntPtr]::Zero) { throw "no visible window for pid $($p.Id)" }
    if ($Width -gt 0 -and $Height -gt 0) { [RexShot]::SetWindowPos($h, [IntPtr]::Zero, 80, 60, $Width, $Height, 0x0004) | Out-Null; Start-Sleep -Milliseconds 600 }
    $r = New-Object RexShot+RECT
    if ($Mode -eq 'Screen') {
        [RexShot]::ShowWindow($h, 9) | Out-Null
        [RexShot]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [RexShot]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [RexShot]::SetForegroundWindow($h) | Out-Null
        [RexShot]::BringWindowToTop($h) | Out-Null
        Start-Sleep -Milliseconds 900
        [RexShot]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
        $w = $r.R - $r.L; $hgt = $r.B - $r.T
        $bmp = New-Object System.Drawing.Bitmap $w, $hgt
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hgt))
        $g.Dispose()
    } else {
        [RexShot]::GetWindowRect($h, [ref]$r) | Out-Null
        $w = $r.R - $r.L; $hgt = $r.B - $r.T
        $bmp = New-Object System.Drawing.Bitmap $w, $hgt
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [RexShot]::PrintWindow($h, $hdc, 2) | Out-Null
        $g.ReleaseHdc($hdc); $g.Dispose()
    }
    $dir = Split-Path -Parent $Out; if ($dir) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "saved $Out ($w x $hgt) pid=$($p.Id) mode=$Mode"
    if ($Close) { Stop-Process -Id $p.Id -Force }
} finally {
    if ($haveLock) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
