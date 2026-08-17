# dsh-launcher build script
# Produces dist\dsh-launcher.exe using the .NET Framework compiler already
# present on every Windows 10/11 machine - no SDK or NuGet needed.
param(
    [string]$Output = "dist\dsh-launcher.exe"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path "$fw\csc.exe")) {
    $fw = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319"
}
$csc = "$fw\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found - .NET Framework 4.x is required." }

$outFull = Join-Path $root $Output
$outDir = Split-Path -Parent $outFull
if ($outDir) { New-Item -ItemType Directory -Force -Path $outDir | Out-Null }

$manifestPath = Join-Path $root "src\app.manifest"
$iconPath     = Join-Path $root "assets\icon.ico"
$sourcePath   = Join-Path $root "src\Program.cs"

Write-Host "Compiling with $csc"
$cscArgs = @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/win32manifest:$manifestPath",
    "/win32icon:$iconPath",
    "/r:System.Windows.Forms.dll",
    "/r:System.Drawing.dll",
    "/r:$fw\System.Web.Extensions.dll",
    "/r:$fw\System.Management.dll",
    "/out:$outFull",
    $sourcePath
)
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

# Ship the default config alongside the exe so users can tweak it.
Copy-Item (Join-Path $root "config.json") (Join-Path $outDir "config.json") -Force

Write-Host ""
Write-Host "Build OK: $outFull"
