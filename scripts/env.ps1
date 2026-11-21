# Dot-source this to get the Windex toolchain on PATH:  . .\scripts\env.ps1
# (Deliberately does not set $ErrorActionPreference: cargo/dotnet write progress to stderr.)
$tools = Join-Path $env:LOCALAPPDATA 'windex-tools'
$zig = Get-ChildItem (Join-Path $tools 'zig-extract') -Directory -ErrorAction SilentlyContinue | Select-Object -First 1
if ($zig) { $env:PATH = "$($zig.FullName);$env:PATH" }
$dotnetRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $dotnetRoot 'dotnet.exe')) {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
}
$env:RUSTUP_TOOLCHAIN = '1.96.1-x86_64-pc-windows-msvc'
# ghostty-vt-sys cannot fetch its tarball through native-tls here; use the verified local copy.
$ghosttySrc = Join-Path $tools 'ghostty-src-83fb956'
if (Test-Path $ghosttySrc) { $env:GHOSTTY_SOURCE_DIR = $ghosttySrc }

# Import the MSVC x64 developer environment once per shell (needed by zig/cc for the Windows SDK).
if (-not $env:VSCMD_VER) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    $vs =& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    if (Test-Path $vcvars) {
        cmd /c "`"$vcvars`" >nul && set" | ForEach-Object {
            if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($matches[1])" -Value $matches[2] }
        }
    }
}
