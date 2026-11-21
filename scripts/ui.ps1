# UI test helpers: SendInput keyboard/mouse, window activation, UIA lookups. Dot-source me.
# The user may be working on this PC: Focus-Window first waits until there has been no keyboard
# or mouse input for 60 s (Wait-UserIdle), and every key/click is refused unless the app under
# test is in front. Prefer screenshot.ps1 -Mode Print for captures (no foreground needed).
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('RexUi' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class RexUi {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [StructLayout(LayoutKind.Sequential)] public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
  public static double IdleSeconds() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return unchecked((uint)Environment.TickCount - i.dwTime) / 1000.0; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  public static uint PidAt(int x, int y) { var p = new POINT(); p.X = x; p.Y = y; var h = GetAncestor(WindowFromPoint(p), 2); uint pid; GetWindowThreadProcessId(h, out pid); return pid; }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder sb, int n);
  public static System.Collections.Generic.List<IntPtr> WindowsFor(uint pid) {
    var list = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  public static string Title(IntPtr h) { var sb = new System.Text.StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
  static INPUT Key(ushort vk, bool up) { var i = new INPUT(); i.type = 1; i.u.ki.wVk = vk; i.u.ki.dwFlags = up ? 2u : 0u;
    if (vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28 || vk == 0x2E || vk == 0x2D || vk == 0x24 || vk == 0x23 || vk == 0x21 || vk == 0x22) i.u.ki.dwFlags |= 1; return i; }
  public static void Chord(ushort[] mods, ushort vk) {
    var list = new System.Collections.Generic.List<INPUT>();
    foreach (var m in mods) list.Add(Key(m, false));
    list.Add(Key(vk, false)); list.Add(Key(vk, true));
    for (int k = mods.Length - 1; k >= 0; k--) list.Add(Key(mods[k], true));
    SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Text(string s) {
    var list = new System.Collections.Generic.List<INPUT>();
    foreach (char c in s) { var d = new INPUT(); d.type = 1; d.u.ki.wScan = c; d.u.ki.dwFlags = 4; var u = d; u.u.ki.dwFlags = 6; list.Add(d); list.Add(u); }
    SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Mouse(uint flags) { var i = new INPUT(); i.type = 0; i.u.mi.dwFlags = flags; SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))); }
  public static void Wheel(int delta) { var i = new INPUT(); i.type = 0; i.u.mi.dwFlags = 0x0800; i.u.mi.mouseData = (uint)delta; SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT))); }
}
"@
}

$VK = @{ Ctrl = 0x11; Shift = 0x10; Alt = 0x12; Enter = 0x0D; Esc = 0x1B; Tab = 0x09; F2 = 0x71; F6 = 0x75; F11 = 0x7A; Left = 0x25; Up = 0x26; Right = 0x27; Down = 0x28; Back = 0x08; Del = 0x2E; Comma = 0xBC }

function Get-MainHwnd([int]$ProcId) {
    $best = [IntPtr]::Zero; $area = -1
    foreach ($h in [RexUi]::WindowsFor([uint32]$ProcId)) {
        $r = New-Object RexUi+RECT; [RexUi]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null
        $a = [long]($r.R - $r.L) * ($r.B - $r.T); if ($a -gt $area) { $area = $a; $best = $h }
    }
    $best
}
function Wait-UserIdle([int]$Seconds = 60, [int]$TimeoutSeconds = 1800) {
    # Never take the foreground while someone is using the PC.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ([RexUi]::IdleSeconds() -lt $Seconds) {
        if ((Get-Date) -gt $deadline) { throw "the user has been active for $TimeoutSeconds s; skipping foreground UI steps" }
        Start-Sleep -Seconds 5
    }
}
function Focus-Window([IntPtr]$h) {
    if ([RexUi]::GetForegroundWindow() -ne $h) { Wait-UserIdle }
    $global:RevTarget = $h; $tp = 0; [RexUi]::GetWindowThreadProcessId($h, [ref]$tp) | Out-Null; $global:RevPid = $tp
    [RexUi]::ShowWindow($h, 9) | Out-Null
    [RexUi]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [RexUi]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [RexUi]::SetForegroundWindow($h) | Out-Null; [RexUi]::BringWindowToTop($h) | Out-Null
    Start-Sleep -Milliseconds 400
    ([RexUi]::GetForegroundWindow() -eq $h)
}
function Win-Rect([IntPtr]$h) { $r = New-Object RexUi+RECT; [RexUi]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) | Out-Null; $r }
function Send-Chord([string[]]$mods, $key) {
    if ($global:RevTarget -and [RexUi]::GetForegroundWindow() -ne $global:RevTarget) {
        $fgPid = 0; [RexUi]::GetWindowThreadProcessId([RexUi]::GetForegroundWindow(), [ref]$fgPid) | Out-Null
        if ($fgPid -ne $global:RevPid) { throw "foreground is not the app (pid $fgPid); refusing to send keys" }
    }
    $m = [uint16[]]@($mods | Where-Object { $_ } | ForEach-Object { $VK[$_] })
    if ($null -eq $m) { $m = [uint16[]]@() }
    $k = if ($key -is [string]) { if ($VK.ContainsKey($key)) { $VK[$key] } else { [int][char]$key.ToUpper() } } else { $key }
    [RexUi]::Chord($m, [uint16]$k); Start-Sleep -Milliseconds 350
}
function Click-At([int]$x, [int]$y, [switch]$Right, [switch]$Double) {
    if ($global:RevPid -and [RexUi]::PidAt($x, $y) -ne $global:RevPid) { throw "point $x,$y is not over the app; refusing to click" }
    [RexUi]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 120
    $down = if ($Right) { 0x0008 } else { 0x0002 }; $up = if ($Right) { 0x0010 } else { 0x0004 }
    [RexUi]::Mouse($down); [RexUi]::Mouse($up)
    if ($Double) { Start-Sleep -Milliseconds 60; [RexUi]::Mouse($down); [RexUi]::Mouse($up) }
    Start-Sleep -Milliseconds 350
}
function Drag([int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$steps = 12) {
    if ($global:RevPid -and [RexUi]::PidAt($x1, $y1) -ne $global:RevPid) { throw "drag start is not over the app" }
    [RexUi]::SetCursorPos($x1, $y1) | Out-Null; Start-Sleep -Milliseconds 300
    [RexUi]::Mouse(0x0002); Start-Sleep -Milliseconds 80
    for ($i = 1; $i -le $steps; $i++) { [RexUi]::SetCursorPos([int]($x1 + ($x2 - $x1) * $i / $steps), [int]($y1 + ($y2 - $y1) * $i / $steps)) | Out-Null; [RexUi]::Mouse(0x0001); Start-Sleep -Milliseconds 30 }
}
function Drag-Release { [RexUi]::Mouse(0x0004); Start-Sleep -Milliseconds 300 }
function Shot([IntPtr]$h, [string]$out) {
    $r = Win-Rect $h; $w = $r.R - $r.L; $hh = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $hh; $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $hh)); $g.Dispose()
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); "shot $out"
}
function Uia-Root([IntPtr]$h) { [System.Windows.Automation.AutomationElement]::FromHandle($h) }
function Uia-Find($root, [string]$name, [switch]$All) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    if ($All) { $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c) } else { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c) }
}
function Uia-Center($el) { $b = $el.Current.BoundingRectangle; @([int]($b.X + $b.Width / 2), [int]($b.Y + $b.Height / 2)) }
function Uia-Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }


function With-Lock([scriptblock]$body) {
    $m = New-Object System.Threading.Mutex($false, 'Global\WindexScreenshot'); $have = $false
    try { $have = $m.WaitOne([TimeSpan]::FromSeconds(120)); & $body } finally { if ($have) { $m.ReleaseMutex() }; $m.Dispose() }
}
function Tabs-Now([IntPtr]$h) {
    $root = Uia-Root $h
    ($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem))) | ForEach-Object { $_.Current.Name.Split(',')[0] + '@' + [int]$_.Current.BoundingRectangle.X }) -join ' | '
}

function Move-Abs([int]$x, [int]$y) {
    Add-Type -AssemblyName System.Windows.Forms
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $nx = [int](($x - $vs.Left) * 65535 / ($vs.Width - 1)); $ny = [int](($y - $vs.Top) * 65535 / ($vs.Height - 1))
    $i = New-Object RexUi+INPUT; $i.type = 0; $i.u.mi.dx = $nx; $i.u.mi.dy = $ny; $i.u.mi.dwFlags = 0x0001 -bor 0x8000 -bor 0x4000
    [RexUi]::SendInput(1, @($i), [System.Runtime.InteropServices.Marshal]::SizeOf([type]'RexUi+INPUT')) | Out-Null
}
function Drag2([int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$steps = 15) {
    if ($global:RevPid -and [RexUi]::PidAt($x1, $y1) -ne $global:RevPid) { throw "drag start is not over the app" }
    Move-Abs $x1 $y1; Start-Sleep -Milliseconds 300
    [RexUi]::Mouse(0x0002); Start-Sleep -Milliseconds 80
    for ($i = 1; $i -le $steps; $i++) { Move-Abs ([int]($x1 + ($x2 - $x1) * $i / $steps)) ([int]($y1 + ($y2 - $y1) * $i / $steps)); Start-Sleep -Milliseconds 30 }
}

if (-not ('RevMouse2' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RevMouse2 {
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  public static uint Send(int x, int y, uint extra) {
    int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
    var i = new INPUT(); i.type = 0;
    i.mi.dx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)); i.mi.dy = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
    i.mi.dwFlags = 0x0001 | 0x8000 | 0x4000 | extra;
    return SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
  }
}
"@
}
function Drag3([int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$steps = 15) {
    if ($global:RevPid -and [RexUi]::PidAt($x1, $y1) -ne $global:RevPid) { throw "drag start is not over the app" }
    [RevMouse2]::Send($x1, $y1, 0) | Out-Null; Start-Sleep -Milliseconds 300
    [RevMouse2]::Send($x1, $y1, 0x0002) | Out-Null; Start-Sleep -Milliseconds 80
    for ($i = 1; $i -le $steps; $i++) { [RevMouse2]::Send([int]($x1 + ($x2 - $x1) * $i / $steps), [int]($y1 + ($y2 - $y1) * $i / $steps), 0) | Out-Null; Start-Sleep -Milliseconds 30 }
}
