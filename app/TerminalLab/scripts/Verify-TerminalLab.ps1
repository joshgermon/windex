# End-to-end checks of TerminalView in TerminalLab with real input (SendInput) and real captures,
# plus the lifecycle check (--leak-check) and the device-loss recovery (--lose-device).
# Prints PASS/FAIL per check and writes screenshots to artifacts\terminal\.
#
#   . .\scripts\env.ps1; dotnet build app\TerminalLab -c Debug
#   powershell -NoProfile -File app\TerminalLab\scripts\Verify-TerminalLab.ps1 [-Skip150]
#
# It moves the pointer and types into the lab window only after confirming that window is in the
# foreground, holds the machine-wide screenshot lock while doing so, and changes the clipboard.
# The 150% checks need a monitor at 150% scale; pass -Monitor150 'x,y' (its physical top-left).
param([switch]$Skip150, [string]$Monitor150 = '-3840,-352')
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LabDriver.psm1') -Force
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$shots = Join-Path $repo 'artifacts\terminal'
New-Item -ItemType Directory -Force $shots | Out-Null
$logDir = Join-Path $env:TEMP 'TerminalLab'
New-Item -ItemType Directory -Force $logDir | Out-Null
$script:failures = 0

function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { "PASS  $Name  $Detail" } else { $script:failures++; "FAIL  $Name  $Detail" }
}

function Read-Log([string]$Path) { if (Test-Path $Path) { Get-Content $Path -Encoding UTF8 } else { @() } }

Add-Type -AssemblyName System.Drawing
if (-not ('CaptureScan' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
public static class CaptureScan {
  // Bounding box (x0, y0, x1, y1) of the pixels of exactly one colour; x1 < 0 when there are none.
  public static int[] Bounds(string path, int r, int g, int b) {
    using (var bmp = new Bitmap(path)) {
      int x0 = bmp.Width, y0 = bmp.Height, x1 = -1, y1 = -1;
      for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) {
        var c = bmp.GetPixel(x, y);
        if (c.R == r && c.G == g && c.B == b) { x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y); }
      }
      return new[] { x0, y0, x1, y1 };
    }
  }
  // The brightest pixel in a box, as its darkest channel (255: pure white).
  public static int Brightest(string path, int x0, int y0, int x1, int y1) {
    using (var bmp = new Bitmap(path)) {
      int best = 0;
      for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) {
        var c = bmp.GetPixel(x, y);
        best = Math.Max(best, Math.Min(c.R, Math.Min(c.G, c.B)));
      }
      return best;
    }
  }
}
"@
}
# The colour of a capture's pixel at a screen point (captures taken with -Print, framed by Get-PrintOrigin).
function Get-CapturePixel([string]$Png, $Origin, $At) {
    $bmp = [System.Drawing.Bitmap]::FromFile($Png)
    try { $bmp.GetPixel($At.X - $Origin.X, $At.Y - $Origin.Y) } finally { $bmp.Dispose() }
}

# Every printable key of the US layout that is not a letter: virtual key, W3C code, unshifted and shifted text.
$UsSymbolKeys = @(
    @(0xC0, 'Backquote', '`', '~'), @(0x31, 'Digit1', '1', '!'), @(0x32, 'Digit2', '2', '@'), @(0x33, 'Digit3', '3', '#'),
    @(0x34, 'Digit4', '4', '$'), @(0x35, 'Digit5', '5', '%'), @(0x36, 'Digit6', '6', '^'), @(0x37, 'Digit7', '7', '&'),
    @(0x38, 'Digit8', '8', '*'), @(0x39, 'Digit9', '9', '('), @(0x30, 'Digit0', '0', ')'), @(0xBD, 'Minus', '-', '_'),
    @(0xBB, 'Equal', '=', '+'), @(0xDB, 'BracketLeft', '[', '{'), @(0xDD, 'BracketRight', ']', '}'), @(0xDC, 'Backslash', '\', '|'),
    @(0xBA, 'Semicolon', ';', ':'), @(0xDE, 'Quote', "'", '"'), @(0xBC, 'Comma', ',', '<'), @(0xBE, 'Period', '.', '>'), @(0xBF, 'Slash', '/', '?'))

function With-Lab([string]$Arguments, [scriptblock]$Body, [int]$Width = 1400, [int]$Height = 900, [int]$X = 80, [int]$Y = 60) {
    $log = Join-Path $logDir ("verify-" + [guid]::NewGuid().ToString('n').Substring(0, 8) + '.log')
    $lab = Start-Lab -Arguments "$Arguments --log $log" -Width $Width -Height $Height -X $X -Y $Y -WaitSeconds 5
    try { & $Body $lab $log } finally { Stop-Lab $lab }
}

"== 100% scale (9x19 px cells)"
With-Lab '--demo 1 --screenshot-friendly' {
    param($lab, $log)
    Save-LabCapture $lab (Join-Path $shots 'showcase-100.png') | Out-Null
    # Drag from the left edge of row 8 col 0 to col 20 (inside "shades"), copy with Ctrl+Insert.
    Set-Clipboard -Value 'before'
    Invoke-Drag $lab (Get-CellPoint $lab 0 8 -Fraction 0.1) (Get-CellPoint $lab 20 8 -Fraction 0.9)
    Save-LabCapture $lab (Join-Path $shots 'selection-100.png') -Print | Out-Null
    Send-Chord $lab @(0x11, 0x2D) -Extended @(0x2D)
    $clip = Get-Clipboard -Raw
    Check 'drag selection + Ctrl+Insert copies the dragged text' ($clip -eq 'Truecolor text shades') "[$clip]"
    Invoke-Click $lab (Get-CellPoint $lab 25 8) -Count 2
    Send-Chord $lab @(0x11, 0x43)
    $clip = Get-Clipboard -Raw
    $sentCtrlC = @(Read-Log $log | Where-Object { $_ -like 'key code=KeyC*Ctrl*Press' }).Count
    Check 'double-click selects a word; Ctrl+C copies it instead of sending ^C' ($clip -eq 'smoothly' -and $sentCtrlC -eq 0) "[$clip]"
    Invoke-Click $lab (Get-CellPoint $lab 5 17) -Count 3
    Send-Chord $lab @(0x11, 0x2D) -Extended @(0x2D)
    $clip = Get-Clipboard -Raw
    Check 'triple-click selects the line (box drawing copies as text)' ($clip -like '├──────┼──────┤ ╠══════╬══════╣*') "[$($clip.Substring(0, [Math]::Min(30, $clip.Length)))…]"
    Invoke-Click $lab (Get-CellPoint $lab 60 30)
    Send-Chord $lab @(0x11, 0x43)
    Check 'Ctrl+C without a selection reaches the program' (@(Read-Log $log | Where-Object { $_ -like 'key code=KeyC*Ctrl*Press' }).Count -eq 1)
    $url = Get-CellPoint $lab 40 26
    Invoke-Hover $lab $url -Hold @(0x11) -CaptureTo (Join-Path $shots 'link-hover-100.png')
    Invoke-Click $lab (Get-CellPoint $lab 5 26) -Hold @(0x11)
    Invoke-Click $lab $url -Hold @(0x11)
    $links = @(Read-Log $log | Where-Object { $_ -like 'link *' })
    Check 'Ctrl+click opens OSC 8 and plain-text links' ($links -contains 'link https://github.com/ghostty-org/ghostty' -and $links -contains 'link https://example.com/rex?x=1') ($links -join ', ')
    Check 'program title (OSC 2) is reported' $true '(see the card header in showcase-100.png)'
}

With-Lab '--demo 2 --screenshot-friendly' {
    param($lab, $log)
    Save-LabCapture $lab (Join-Path $shots 'scrollback-100.png') | Out-Null
    Invoke-Wheel $lab (Get-CellPoint $lab 40 10) 10
    $wheel = @(Read-Log $log | Where-Object { $_ -eq 'scroll Delta -3' }).Count
    Check 'mouse wheel scrolls 3 rows per notch' ($wheel -eq 10) "($wheel of 10 notches)"
    Save-LabCapture $lab (Join-Path $shots 'scrollback-wheel-100.png') -Print | Out-Null
    $r = (Get-Terminals $lab)[0].Current.BoundingRectangle
    $bottom = [pscustomobject]@{ X = [int]($r.Right - 5); Y = [int]($r.Bottom - 30) }
    $top = [pscustomobject]@{ X = [int]($r.Right - 5); Y = [int]($r.Top - 60) }  # past the top: clamps to row 0
    Invoke-Hover $lab $bottom -CaptureTo (Join-Path $shots 'scrollbar-hover-100.png')
    Invoke-Drag $lab $bottom $top
    $rows = @(Read-Log $log | Where-Object { $_ -like 'scroll Row *' })
    Check 'dragging the scrollbar thumb scrolls to rows' ($rows.Count -gt 0 -and $rows[-1] -eq 'scroll Row 0') "(last: $($rows[-1]))"
    Save-LabCapture $lab (Join-Path $shots 'scrollbar-top-100.png') -Print | Out-Null
}

With-Lab '--demo 4 --screenshot-friendly' {
    param($lab, $log)
    Send-KeyText $lab 'hello World'
    Send-Chord $lab @(0x09)
    Send-KeyText $lab 'x'
    Send-Chord $lab @(0x25) -Extended @(0x25)
    Send-UnicodeText $lab ' café € 中文 😀'
    Set-Clipboard -Value "pasted`r`nline two"
    Send-Chord $lab @(0x10, 0x2D) -Extended @(0x2D)
    Send-Chord $lab @(0x0D)
    Send-Chord $lab @(0x12, 0x46)
    Send-Chord $lab @(0x11, 0x12, 0x51)
    Send-KeyText $lab 'done'
    Save-LabCapture $lab (Join-Path $shots 'typing-pad-100.png') | Out-Null
    $l = Read-Log $log
    Check 'typed keys carry W3C code, key and text' (($l -contains 'key code=KeyH key="h" text="h" mods=None action=Press') -and ($l -contains 'key code=KeyW key="W" text="W" mods=Shift action=Press'))
    Check 'Tab and arrows reach the terminal (no focus navigation)' (($l -contains 'key code=Tab key="Tab" text=null mods=None action=Press') -and ($l -contains 'key code=KeyX key="x" text="x" mods=None action=Press') -and ($l -contains 'key code=ArrowLeft key="ArrowLeft" text=null mods=None action=Press'))
    Check 'composed / injected text arrives as text (incl. a surrogate pair)' (($l -contains 'text "é"') -and ($l -contains 'text "€"') -and ($l -contains 'text "中"') -and ($l -contains 'text "😀"'))
    Check 'Shift+Insert pastes with CRLF as CR' ($l -contains 'paste "pasted\x0dline two"')
    Check 'Alt chords carry the character; Ctrl+Alt (AltGr on US) is a chord' (($l -contains 'key code=KeyF key="f" text="f" mods=Alt action=Press') -and ($l -contains 'key code=KeyQ key="q" text=null mods=Ctrl, Alt action=Press'))
    $strokes = @()
    foreach ($k in $UsSymbolKeys) { $strokes += ,$k[0] }
    foreach ($k in $UsSymbolKeys) { $strokes += ,@(0x10, $k[0]) }
    Send-Chord $lab @(0x0D)
    Send-Keys $lab $strokes
    Set-Clipboard -Value 'via ctrl-v'
    Send-Chord $lab @(0x11, 0x56)
    Save-LabCapture $lab (Join-Path $shots 'typing-symbols-100.png') | Out-Null
    $l = Read-Log $log
    $missing = foreach ($k in $UsSymbolKeys) {
        if ($l -notcontains "key code=$($k[1]) key=""$($k[2])"" text=""$($k[2])"" mods=None action=Press") { $k[2] }
        if ($l -notcontains "key code=$($k[1]) key=""$($k[3])"" text=""$($k[3])"" mods=Shift action=Press") { $k[3] }
    }
    Check 'every US symbol key types its character, unshifted and shifted (incl. ` [ ] \ '' ")' (@($missing).Count -eq 0) "(missing: [$($missing -join ' ')] of $($UsSymbolKeys.Count * 2); echo in typing-symbols-100.png)"
    Check 'Ctrl+V pastes (and is not sent as ^V)' (($l -contains 'paste "via ctrl-v"') -and -not ($l | Where-Object { $_ -like 'key code=KeyV *mods=Ctrl action=Press' }))
    $press = @($l | ForEach-Object { if ($_ -match '^key code=(\S+) .*action=Press$') { $Matches[1] } })
    $release = @($l | ForEach-Object { if ($_ -match '^key code=(\S+) .*action=Release$') { $Matches[1] } })
    $unpaired = Compare-Object ($press | Sort-Object) ($release | Sort-Object)
    Check 'every press is followed by its release' ($press.Count -gt 0 -and -not $unpaired) "($($press.Count) presses, $($release.Count) releases)"
}

With-Lab '--demo 4 --screenshot-friendly --mouse-tracking' {
    param($lab, $log)
    $pt = Get-CellPoint $lab 5 6
    Invoke-Click $lab $pt
    Invoke-Wheel $lab $pt 2
    Invoke-Click $lab (Get-CellPoint $lab 6 6) -Hold @(0x10)
    $l = Read-Log $log
    Check 'mouse reports go to the program while it tracks the mouse' ((@($l | Where-Object { $_ -like 'mouse Press Left*' }).Count -eq 1) -and (@($l | Where-Object { $_ -like 'mouse Wheel*' }).Count -eq 2))
    Check 'Shift overrides mouse tracking and selects' (@($l | Where-Object { $_ -like 'select Press*' }).Count -eq 1)
}

With-Lab '--demo 2 --screenshot-friendly' {
    param($lab, $log)
    Invoke-Wheel $lab (Get-CellPoint $lab 40 10) 2 -Hold @(0x11)
    $l = Read-Log $log
    $resizes = @($l | Where-Object { $_ -like 'resize *' })
    Check 'Ctrl+wheel reaches the host (font zoom) and does not scroll the terminal' (($l -contains 'zoom 13') -and -not ($l | Where-Object { $_ -like 'scroll *' }) -and $resizes[-1] -notlike '*cell=9x19') "(last: $($resizes[-1]))"
}

With-Lab '--demo 1 --screenshot-friendly --non-owner' {
    param($lab, $log)
    $png = Join-Path $shots 'non-owner-100.png'
    Save-LabCapture $lab $png -Print | Out-Null
    $origin = Get-PrintOrigin $lab
    # The frame is 100x30: its green first row and red last row end at column 100 and row 29.
    $inside = Get-CapturePixel $png $origin (Get-CellPoint $lab 99 0)
    $right = Get-CapturePixel $png $origin (Get-CellPoint $lab 103 0)
    $lastRow = Get-CapturePixel $png $origin (Get-CellPoint $lab 60 29)
    $below = Get-CapturePixel $png $origin (Get-CellPoint $lab 60 31)
    $green = { param($c) $c.G -gt 150 -and $c.R -lt 140 }
    $red = { param($c) $c.R -gt 200 -and $c.G -lt 150 }
    Check 'a frame smaller than the view stays in its grid: edge colours do not spread across the rest' ((& $green $inside) -and -not (& $green $right) -and (& $red $lastRow) -and -not (& $red $below)) "(row 0 at col 99 $($inside.Name), col 103 $($right.Name); row 29 $($lastRow.Name), row 31 $($below.Name))"
}

With-Lab '--demo 1 --screenshot-friendly --theme light' {
    param($lab, $log)
    # Put the block cursor over a glyph: paste x and a cursor-left sequence (the pane echoes pastes).
    Set-Clipboard -Value ('x' + [char]27 + '[D')
    Send-Chord $lab @(0x11, 0x56)
    $png = Join-Path $shots 'cursor-text-light-100.png'
    Save-LabCapture $lab $png -Print | Out-Null
    # The cursor block is the only Rex Dawn cursor blue (#0067C0); the glyph on it must be the theme's cursor text, #FFFFFF.
    $x0, $y0, $x1, $y1 = [CaptureScan]::Bounds($png, 0x00, 0x67, 0xC0)
    $brightest = if ($x1 -ge 0) { [CaptureScan]::Brightest($png, $x0, $y0, $x1, $y1) } else { 0 }
    Check 'the glyph under a block cursor uses the theme''s cursor text colour (Rex Dawn #FFFFFF)' ($x1 -ge 0 -and ($x1 - $x0) -lt 20 -and $brightest -eq 255) "(cursor $($x1 - $x0 + 1)x$($y1 - $y0 + 1) px, brightest glyph pixel $brightest)"
}

With-Lab '--demo 3 --screenshot-friendly --lose-device 2' {
    param($lab, $log)
    Start-Sleep -Seconds 2
    Save-LabCapture $lab (Join-Path $shots 'device-loss-100.png') -Print | Out-Null
    $l = Read-Log $log
    Check 'after a device loss every terminal rebuilds its GPU resources and redraws' (($l -contains 'device lost (simulated)') -and ($l | Where-Object { $_ -like 'rendered after device loss*' })) '(device-loss-100.png)'
}

"== lifecycle"
$leakOut = Join-Path $logDir 'leak-check.txt'
Remove-Item $leakOut -ErrorAction SilentlyContinue
$leak = Start-Process (Join-Path $PSScriptRoot '..\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64\TerminalLab.exe') -ArgumentList "--leak-check 40 --out $leakOut --size 1300,850 --pos 80,60" -PassThru
if (-not $leak.WaitForExit(240000)) { Stop-Process -Id $leak.Id -Force }
$report = Read-Log $leakOut
Check 'closed panes are collected and give their GPU memory back (40 open/close cycles)' (@($report | Where-Object { $_ -like 'FAIL*' }).Count -eq 0 -and @($report | Where-Object { $_ -like 'PASS*' }).Count -ge 1) "($(($report | Select-Object -Skip 1) -join '; '))"

With-Lab '--demo 1 --screenshot-friendly --font-size 7' {
    param($lab, $log)
    Save-LabCapture $lab (Join-Path $shots 'drift-7pt-100.png') | Out-Null
    $cols = @(Read-Log $log | ForEach-Object { if ($_ -match '^resize (\d+)x') { [int]$Matches[1] } })[-1]
    Check 'a 7 pt grid is wider than 200 columns (alignment rows in drift-7pt-100.png)' ($cols -ge 200) "($cols columns)"
}
With-Lab '--demo 3 --screenshot-friendly' { param($lab, $log) Save-LabCapture $lab (Join-Path $shots 'split-100.png') | Out-Null }
With-Lab '--demo 3 --screenshot-friendly --theme light' { param($lab, $log) Save-LabCapture $lab (Join-Path $shots 'split-light-100.png') | Out-Null }
With-Lab '--demo 1 --screenshot-friendly --theme light' { param($lab, $log) Save-LabCapture $lab (Join-Path $shots 'showcase-light-100.png') | Out-Null }
With-Lab '--demo 1 --screenshot-friendly --opaque' { param($lab, $log) Save-LabCapture $lab (Join-Path $shots 'showcase-opaque-100.png') | Out-Null }
With-Lab '--demo 5 --screenshot-friendly' { param($lab, $log) Start-Sleep 2; Save-LabCapture $lab (Join-Path $shots 'stress-100.png') | Out-Null }

if (-not $Skip150) {
    "== 150% scale (13x28 px cells)"
    $m = $Monitor150.Split(',') | ForEach-Object { [int]$_ }
    With-Lab '--demo 1 --screenshot-friendly' {
        param($lab, $log)
        Save-LabCapture $lab (Join-Path $shots 'showcase-150.png') -Print | Out-Null
        Set-Clipboard -Value 'before'
        Invoke-Drag $lab (Get-CellPoint $lab 0 8 -CellW 13 -CellH 28 -Scale 1.5 -Fraction 0.1) (Get-CellPoint $lab 20 9 -CellW 13 -CellH 28 -Scale 1.5 -Fraction 0.9)
        Send-Chord $lab @(0x11, 0x2D) -Extended @(0x2D)
        $clip = Get-Clipboard -Raw
        Check 'two-row drag selection at 150% copies both rows' ($clip -eq "Truecolor text shades smoothly from one end of the line to the other end`n▌ Styles") "[$($clip -replace "`n", '\n')]"
        Save-LabCapture $lab (Join-Path $shots 'selection-150.png') -Print | Out-Null
        Check 'cells measure 13x28 px at 150%' (@(Read-Log $log | Where-Object { $_ -like 'resize *cell=13x28' }).Count -ge 1)
    } -Width 2100 -Height 1350 -X ($m[0] + 140) -Y ($m[1] + 100)
    With-Lab '--demo 3 --screenshot-friendly' { param($lab, $log) Save-LabCapture $lab (Join-Path $shots 'split-150.png') -Print | Out-Null } -Width 2100 -Height 1350 -X ($m[0] + 140) -Y ($m[1] + 100)
    With-Lab '--demo 1 --screenshot-friendly' {
        param($lab, $log)
        Move-LabWindow $lab ($m[0] + 140) ($m[1] + 100)
        $sizes = @(Read-Log $log | Where-Object { $_ -like 'resize *' })
        Check 'moving to a 150% monitor re-measures the cells' ($sizes[0] -like '*cell=9x19' -and $sizes[-1] -like '*cell=13x28') "($($sizes[0]) -> $($sizes[-1]))"
    }
}

""
if ($script:failures -eq 0) { "ALL CHECKS PASSED" } else { "$($script:failures) CHECK(S) FAILED" }
