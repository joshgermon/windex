# Drives a running TerminalLab window for verification: finds the terminal through UI Automation
# and sends real mouse and keyboard input with SendInput. Input steals focus and the pointer, so
# every gesture holds the same machine-wide mutex scripts\screenshot.ps1 uses.
#
#   Import-Module .\app\TerminalLab\scripts\LabDriver.psm1
#   $lab = Start-Lab -Arguments '--demo 1 --screenshot-friendly' -Width 1400 -Height 900
#   Invoke-Drag $lab -From (Get-CellPoint $lab 0 1) -To (Get-CellPoint $lab 20 1)
#   Send-Chord $lab @(0x11, 0x10, 0x43)       # Ctrl+Shift+C
#   Stop-Lab $lab

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('LabInput' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class LabInput {
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public static int Metric(int i) { return GetSystemMetrics(i); }
  // Physical pixels everywhere on this thread (per-monitor v2), whatever the monitor scale.
  public static void PerMonitor() { SetThreadDpiAwarenessContext((IntPtr)(-4)); }
  static void Send(INPUT i) { SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))); }
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  public static void Mouse(uint flags, int data) { var i = new INPUT { type = 0 }; i.u.mi.dwFlags = flags; i.u.mi.mouseData = (uint)data; Send(i); }
  [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
  static void MoveAbsolute(int x, int y) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    var i = new INPUT { type = 0 }; i.u.mi.dx = (int)((x - vx) * 65535.0 / (vw - 1)); i.u.mi.dy = (int)((y - vy) * 65535.0 / (vh - 1));
    i.u.mi.dwFlags = 0x0001 | 0x8000 | 0x4000; Send(i);
  }
  // A real pointer move (absolute SendInput) to a physical screen point. Normalised coordinates do
  // not map linearly across monitors of different scale, so it corrects against the cursor it reads back.
  public static void MoveTo(int x, int y) {
    int tx = x, ty = y;
    for (int attempt = 0; attempt < 6; attempt++) {
      MoveAbsolute(tx, ty);
      POINT p; GetCursorPos(out p);
      if (p.X == x && p.Y == y) return;
      tx += x - p.X; ty += y - p.Y;
    }
  }
  public static void Key(ushort vk, bool up, bool extended) {
    var i = new INPUT { type = 1 }; i.u.ki.wVk = vk; i.u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
    i.u.ki.dwFlags = (up ? 2u : 0u) | (extended ? 1u : 0u); Send(i);
  }
  public static void Unicode(char c, bool up) { var i = new INPUT { type = 1 }; i.u.ki.wScan = c; i.u.ki.dwFlags = 4u | (up ? 2u : 0u); Send(i); }
}
"@
}
[LabInput]::PerMonitor()

$script:Exe = Join-Path $PSScriptRoot '..\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\TerminalLab.exe'

function Use-ScreenLock([scriptblock]$Body) {
    [LabInput]::PerMonitor()
    $mutex = New-Object System.Threading.Mutex($false, 'Global\WindexScreenshot')
    try { $owned = $mutex.WaitOne([TimeSpan]::FromSeconds(120)) }
    catch [System.Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { $mutex.Dispose(); throw "the screen is busy (another capture holds the lock)" }
    try { & $Body } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
}

function Start-Lab([string]$Arguments, [int]$Width = 1400, [int]$Height = 900, [int]$X = 80, [int]$Y = 60, [int]$WaitSeconds = 4) {
    $p = Start-Process -FilePath $script:Exe -ArgumentList "$Arguments --size $Width,$Height --pos $X,$Y" -PassThru
    Start-Sleep -Seconds $WaitSeconds
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $window = $null
    for ($i = 0; $i -lt 20 -and -not $window; $i++) { $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond); if (-not $window) { Start-Sleep -Milliseconds 300 } }
    [pscustomobject]@{ Process = $p; Window = $window }
}

function Stop-Lab($Lab) { if (-not $Lab.Process.HasExited) { Stop-Process -Id $Lab.Process.Id -Force } }

# The TerminalViews ("Terminal" panes) in document order.
function Get-Terminals($Lab) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Terminal')
    @($Lab.Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))
}

# Screen point (physical pixels) at the centre of cell (col,row) of a terminal. Padding 10,2 epx; cell size from the lab log or given.
# -Fraction places the point across the cell (0 left edge, 0.5 centre).
function Get-CellPoint($Lab, [int]$Col, [int]$Row, [int]$Index = 0, [double]$CellW = 9, [double]$CellH = 19, [double]$Scale = 1, [double]$Fraction = 0.5) {
    [LabInput]::PerMonitor()
    $r = (Get-Terminals $Lab)[$Index].Current.BoundingRectangle
    [pscustomobject]@{ X = [int]($r.Left + [Math]::Round(10 * $Scale) + ($Col + $Fraction) * $CellW); Y = [int]($r.Top + [Math]::Round(2 * $Scale) + ($Row + 0.5) * $CellH) }
}

# Input must never land in another window: bring the lab forward and check, or stop.
function Set-LabForeground($Lab) {
    $h = $Lab.Process.MainWindowHandle
    for ($i = 0; $i -lt 10; $i++) {
        if ([LabInput]::GetForegroundWindow() -eq $h) { Start-Sleep -Milliseconds 150; return }
        # An Alt tap lets this process move the foreground (the same step scripts\screenshot.ps1 takes).
        [LabInput]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [LabInput]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [LabInput]::SetForegroundWindow($h) | Out-Null
        [LabInput]::BringWindowToTop($h) | Out-Null
        Start-Sleep -Milliseconds 300
    }
    throw "TerminalLab is not the foreground window; refusing to send input"
}

# Clicks (Count times); -Hold keeps modifier keys (e.g. 0x11 Ctrl) down for the click, released in any case.
function Invoke-Click($Lab, $At, [int]$Count = 1, [int[]]$Hold = @()) {
    Use-ScreenLock {
        Set-LabForeground $Lab
        try {
            foreach ($k in $Hold) { [LabInput]::Key($k, $false, $false); Start-Sleep -Milliseconds 40 }
            [LabInput]::MoveTo($At.X, $At.Y)
            for ($i = 0; $i -lt $Count; $i++) { [LabInput]::Mouse(2, 0); [LabInput]::Mouse(4, 0); Start-Sleep -Milliseconds 60 }
            Start-Sleep -Milliseconds 300
        } finally { foreach ($k in $Hold) { [LabInput]::Key($k, $true, $false) } }
    }
}
function Invoke-Drag($Lab, $From, $To) {
    Use-ScreenLock {
        Set-LabForeground $Lab
        [LabInput]::MoveTo($From.X, $From.Y)
        Start-Sleep -Milliseconds 100
        [LabInput]::Mouse(2, 0)
        for ($i = 1; $i -le 10; $i++) {
            [LabInput]::MoveTo([int]($From.X + ($To.X - $From.X) * $i / 10), [int]($From.Y + ($To.Y - $From.Y) * $i / 10))
            Start-Sleep -Milliseconds 30
        }
        [LabInput]::Mouse(4, 0)
        Start-Sleep -Milliseconds 300
    }
}

# Wheel notches (positive: away from the user); -Hold keeps modifier keys down for them.
function Invoke-Wheel($Lab, $At, [int]$Notches, [int[]]$Hold = @()) {
    Use-ScreenLock {
        Set-LabForeground $Lab
        try {
            foreach ($k in $Hold) { [LabInput]::Key($k, $false, $false); Start-Sleep -Milliseconds 40 }
            [LabInput]::MoveTo($At.X, $At.Y)
            for ($i = 0; $i -lt [Math]::Abs($Notches); $i++) { [LabInput]::Mouse(0x800, 120 * [Math]::Sign($Notches)); Start-Sleep -Milliseconds 40 }
            Start-Sleep -Milliseconds 300
        } finally { foreach ($k in $Hold) { [LabInput]::Key($k, $true, $false) } }
    }
}

# Presses virtual keys in order and releases them in reverse (a chord), e.g. @(0x11, 0x10, 0x43).
function Send-Chord($Lab, [int[]]$Keys, [int[]]$Extended = @()) {
    Use-ScreenLock {
        Set-LabForeground $Lab
        foreach ($k in $Keys) { [LabInput]::Key($k, $false, $Extended -contains $k); Start-Sleep -Milliseconds 40 }
        [array]::Reverse($Keys)
        foreach ($k in $Keys) { [LabInput]::Key($k, $true, $Extended -contains $k); Start-Sleep -Milliseconds 40 }
        Start-Sleep -Milliseconds 300
    }
}

# Presses keys one after another (virtual-key codes, so the current layout decides what they type).
# A stroke given as an array holds the leading keys as modifiers, e.g. @(0x10, 0xDE) is Shift+Quote.
function Send-Keys($Lab, [object[]]$Strokes) {
    Use-ScreenLock {
        Set-LabForeground $Lab
        foreach ($stroke in $Strokes) {
            $keys = @($stroke)
            $mods = @(if ($keys.Count -gt 1) { $keys[0..($keys.Count - 2)] })
            $vk = $keys[-1]
            foreach ($m in $mods) { [LabInput]::Key($m, $false, $false) }
            [LabInput]::Key($vk, $false, $false); Start-Sleep -Milliseconds 15; [LabInput]::Key($vk, $true, $false)
            [array]::Reverse($mods)
            foreach ($m in $mods) { [LabInput]::Key($m, $true, $false) }
            Start-Sleep -Milliseconds 30
        }
        Start-Sleep -Milliseconds 300
    }
}

# Types text as Unicode packets (VK_PACKET), the way the touch keyboard and IMEs inject text.
function Send-UnicodeText($Lab, [string]$Text) {
    Use-ScreenLock {
        Set-LabForeground $Lab
        foreach ($c in $Text.ToCharArray()) { [LabInput]::Unicode($c, $false); [LabInput]::Unicode($c, $true) }
        Start-Sleep -Milliseconds 300
    }
}

# Types ASCII text with real key presses (virtual keys + scan codes) on the current layout.
function Send-KeyText($Lab, [string]$Text) {
    Use-ScreenLock {
        Set-LabForeground $Lab
        foreach ($c in $Text.ToCharArray()) {
            $vk = [int][char]::ToUpperInvariant($c)
            if ($c -eq ' ') { $vk = 0x20 }
            $shift = [char]::IsUpper($c)
            if ($shift) { [LabInput]::Key(0x10, $false, $false) }
            [LabInput]::Key($vk, $false, $false); [LabInput]::Key($vk, $true, $false)
            if ($shift) { [LabInput]::Key(0x10, $true, $false) }
        }
        Start-Sleep -Milliseconds 300
    }
}

# Screen capture of the lab window in physical pixels (correct on any monitor scale).
# -Print renders the window with PrintWindow (no activation, unaffected by what covers or dims the screen).
function Save-LabCapture($Lab, [string]$Out, [switch]$Print) {
    Use-ScreenLock { Save-Window $Lab $Out -Print:$Print }
}

function Save-Window($Lab, [string]$Out, [switch]$Print) {
    Add-Type -AssemblyName System.Drawing
    & {
        $h = $Lab.Process.MainWindowHandle
        $r = New-Object LabInput+RECT
        if ($Print) { [LabInput]::GetWindowRect($h, [ref]$r) | Out-Null }
        else { Set-LabForeground $Lab; Start-Sleep -Milliseconds 600; [LabInput]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null }
        $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        if ($Print) { $hdc = $g.GetHdc(); [LabInput]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc) }
        else { $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size) }
        $g.Dispose(); $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        "saved $Out ($($r.R - $r.L) x $($r.B - $r.T))"
    }
}

# The window's top-left corner in physical screen pixels, as a -Print capture frames it (for
# finding a screen point's pixel in that capture).
function Get-PrintOrigin($Lab) {
    [LabInput]::PerMonitor()
    $r = New-Object LabInput+RECT
    [LabInput]::GetWindowRect($Lab.Process.MainWindowHandle, [ref]$r) | Out-Null
    [pscustomobject]@{ X = $r.L; Y = $r.T }
}

# Rests the pointer on a point, optionally with modifiers held, and optionally captures the window
# (PrintWindow) before letting go.
function Invoke-Hover($Lab, $At, [int[]]$Hold = @(), [string]$CaptureTo) {
    Use-ScreenLock {
        Set-LabForeground $Lab
        try {
            foreach ($k in $Hold) { [LabInput]::Key($k, $false, $false); Start-Sleep -Milliseconds 40 }
            [LabInput]::MoveTo($At.X - 4, $At.Y); Start-Sleep -Milliseconds 80
            [LabInput]::MoveTo($At.X, $At.Y); Start-Sleep -Milliseconds 400
            if ($CaptureTo) { Save-Window $Lab $CaptureTo -Print }
        } finally { foreach ($k in $Hold) { [LabInput]::Key($k, $true, $false) } }
    }
}
# Moves the window (physical pixels; may cross onto a monitor with another scale).
function Move-LabWindow($Lab, [int]$X, [int]$Y) {
    [LabInput]::PerMonitor()
    [LabInput]::SetWindowPos($Lab.Process.MainWindowHandle, [IntPtr]::Zero, $X, $Y, 0, 0, 0x0001 -bor 0x0004) | Out-Null
    Start-Sleep -Milliseconds 800
}

Export-ModuleMember -Function Get-PrintOrigin, Invoke-Hover, Move-LabWindow, Save-LabCapture, Start-Lab, Stop-Lab, Get-Terminals, Get-CellPoint, Invoke-Click, Invoke-Drag, Invoke-Wheel, Send-Chord, Send-Keys, Send-UnicodeText, Send-KeyText, Use-ScreenLock
