# PTPMonitor Build Script (C# Console Application)

# Search common .NET Framework install locations rather than hardcoding one path/version, so the
# script also works on 32-bit installs or systems with a different Framework version installed.
$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v*\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v*\csc.exe"
)
$cscPath = $cscCandidates | ForEach-Object { Get-ChildItem -Path $_ -ErrorAction SilentlyContinue } |
    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $cscPath) {
    Write-Host "csc.exe not found under $env:WINDIR\Microsoft.NET\Framework(64)\v*. Install .NET Framework or edit `$cscCandidates in this script." -ForegroundColor Red
    exit 1
}
$outDir = "bin"
$outFile = "$outDir\PTPMonitor.exe"
$srcFile = "src\PTPMonitor.cs"

if (!(Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force
}

Write-Host "Building PTPMonitor..." -ForegroundColor Cyan

$iconFile = "assets\app_icon.ico"

# Web dashboard (HTML/CSS/JS) is embedded as manifest resources so the .exe stays a single
# dependency-free file while the JS/CSS remain plain files that external tools (e.g. `node --check`)
# can lint outside the C# compiler.
$webResources = @(
    "assets\web\index.html,PTPMonitor.web.index.html"
    "assets\web\style.css,PTPMonitor.web.style.css"
    "assets\web\app.js,PTPMonitor.web.app.js"
)
$resourceArgs = $webResources | ForEach-Object { "/resource:$_" }

& $cscPath /out:$outFile /win32icon:$iconFile /r:System.dll,System.Core.dll @resourceArgs $srcFile

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build Successful: $outFile" -ForegroundColor Green
} else {
    Write-Host "Build Failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}
