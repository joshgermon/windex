# Build Windex: the Rust core (rexcore.dll) and the WinUI 3 app.
#   .\scripts\build.ps1 [-Core] [-App] [-Release] [-Run]
# With neither -Core nor -App, builds both.
param([switch]$Core, [switch]$App, [switch]$Release, [switch]$Run)
. "$PSScriptRoot\env.ps1"
$root = Resolve-Path "$PSScriptRoot\.."
if (-not $Core -and -not $App) { $Core = $true; $App = $true }
if ($Core) {
    Push-Location "$root\core"
    cmd /c "cargo build --release 2>&1"
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { Write-Error "core build failed ($code)"; exit $code }
}
if ($App) {
    $cfg = if ($Release) { 'Release' } else { 'Debug' }
    Push-Location "$root\app\Windex"
    cmd /c "dotnet build -c $cfg -nologo -v:minimal 2>&1"
    $code = $LASTEXITCODE
    Pop-Location
    if ($code -ne 0) { Write-Error "app build failed ($code)"; exit $code }
    $exe = "$root\app\Windex\bin\x64\$cfg\net10.0-windows10.0.26100.0\win-x64\Windex.exe"
    "APP: $exe"
    if ($Run) { Start-Process $exe }
}
