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

& $cscPath /out:$outFile /win32icon:$iconFile /r:System.dll,System.Core.dll $srcFile

if ($LASTEXITCODE -eq 0) {
    Write-Host "Build Successful: $outFile" -ForegroundColor Green
} else {
    Write-Host "Build Failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}
