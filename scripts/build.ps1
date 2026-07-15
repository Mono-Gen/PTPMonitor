# PTPMonitor Build Script (C# Console Application)

$cscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
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
