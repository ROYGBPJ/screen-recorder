[CmdletBinding()]
param(
    [string]$FfmpegPath = (Join-Path $PSScriptRoot '..\artifacts\publish\win-x64\ffmpeg.exe'),
    [string]$ReportPath = (Join-Path $env:TEMP ("Nova-Hardware-Report-{0:yyyyMMdd-HHmmss}.txt" -f (Get-Date)))
)
$ErrorActionPreference = 'Stop'
$FfmpegPath = [System.IO.Path]::GetFullPath($FfmpegPath)
$report = [System.Collections.Generic.List[string]]::new()
$os = Get-CimInstance Win32_OperatingSystem
$report.Add("Nova Screen Recorder hardware report")
$report.Add("Generated: $(Get-Date -Format o)")
$report.Add("OS: $($os.Caption) | Version $($os.Version) | Build $($os.BuildNumber)")
$report.Add("Architecture: $env:PROCESSOR_ARCHITECTURE")
if ($os.Caption -notmatch 'Windows 11' -or [int]$os.BuildNumber -lt 22000) {
    $report.Add('WARNING: This host does not identify as Windows 11 build 22000 or later. Continue only if you intentionally test a supported Windows 10 configuration.')
}

Add-Type -AssemblyName System.Windows.Forms
$report.Add('')
$report.Add('DISPLAY TOPOLOGY (physical desktop bounds reported by WinForms)')
foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
    $b = $screen.Bounds
    $report.Add("Name=$($screen.DeviceName); Primary=$($screen.Primary); Bounds=$($b.X),$($b.Y),$($b.Width),$($b.Height)")
}

if (!(Test-Path -LiteralPath $FfmpegPath)) {
    $command = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($command) { $FfmpegPath = $command.Source }
}
if (!(Test-Path -LiteralPath $FfmpegPath)) {
    $report.Add('ERROR: ffmpeg.exe not found. Pass -FfmpegPath or place it in PATH.')
} else {
    $report.Add('')
    $report.Add("FFmpeg: $FfmpegPath")
    $report.Add((& $FfmpegPath -hide_banner -version 2>&1 | Select-Object -First 1).ToString())
    $report.Add('')
    $report.Add('AVAILABLE VIDEO ENCODERS')
    $report.AddRange([string[]](& $FfmpegPath -hide_banner -encoders 2>&1 | ForEach-Object { $_.ToString() }))
    $report.Add('')
    $report.Add('DIRECTSHOW DEVICES (FFmpeg may return a non-zero code for the dummy input; output is still useful)')
    $report.AddRange([string[]](& $FfmpegPath -hide_banner -list_devices true -f dshow -i dummy 2>&1 | ForEach-Object { $_.ToString() }))
}

$report.Add('')
$report.Add('AUTOMATED TESTS')
$repoRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($dotnet -and (Test-Path (Join-Path $repoRoot 'tests\NovaScreenRecorder.Tests\NovaScreenRecorder.Tests.csproj'))) {
    Push-Location $repoRoot
    try {
        $testOutput = & dotnet test '.\tests\NovaScreenRecorder.Tests\NovaScreenRecorder.Tests.csproj' --configuration Release 2>&1
        $testExit = $LASTEXITCODE
        $report.AddRange([string[]]($testOutput | ForEach-Object { $_.ToString() }))
        $report.Add("dotnet test exit code: $testExit")
    } finally { Pop-Location }
} else {
    $report.Add('SKIPPED: .NET SDK or repository test project was not found. Run dotnet test from the repository on this PC.')
}
$report.Add('')
$report.Add('NOTE: This report inventories local devices and runs helper/API smoke tests. It does not replace manual recording tests for the native picker, real GPU encoding, microphone/webcam sync, mixed-DPI region selection, sleep/resume, and recovery.')
$report | Set-Content -LiteralPath $ReportPath -Encoding UTF8
Write-Host "Hardware report saved to: $ReportPath"
Write-Host 'Open the report and follow docs-Windows-Hardware-Validation.md for interactive tests.'
