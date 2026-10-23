$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$xamlPath = Join-Path $root 'src/NovaScreenRecorder.App/MainWindow.xaml'
$codePath = Join-Path $root 'src/NovaScreenRecorder.App/MainWindow.xaml.cs'
$projectPath = Join-Path $root 'src/NovaScreenRecorder.App/NovaScreenRecorder.App.csproj'
$manifestPath = Join-Path $root 'src/NovaScreenRecorder.App/app.manifest'

[xml]$xaml = Get-Content -LiteralPath $xamlPath -Raw
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$code = Get-Content -LiteralPath $codePath -Raw

$frameworks = @($project.Project.PropertyGroup | ForEach-Object { [string]$_.TargetFramework })
if (!($frameworks | Where-Object { $_ -like 'net8.0-windows*' })) {
    throw 'Project target framework must remain a .NET 8 Windows target.'
}

$eventNames = @('Click','Checked','Unchecked','SelectionChanged','MouseDoubleClick','KeyDown')
$handlers = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($eventName in $eventNames) {
    foreach ($match in [regex]::Matches($xaml.OuterXml, "\b$eventName=`"([A-Za-z_][A-Za-z0-9_]*)`"")) {
        [void]$handlers.Add($match.Groups[1].Value)
    }
}
foreach ($handler in $handlers) {
    if ($code -notmatch ("\b" + [regex]::Escape($handler) + "\s*\(")) {
        throw "XAML event handler '$handler' has no matching C# method."
    }
}

if ($code -notmatch 'CreateProcessStartInfo' -or $code -notmatch 'ArgumentList\.Add') {
    throw 'Safe FFmpeg argument-list launching is missing.'
}
if ($code -notmatch 'MinimumFreeBytes' -or $code -notmatch 'EmergencyFreeBytes') {
    throw 'Disk-space safeguards are missing.'
}
if (!(Test-Path (Join-Path $root 'src/NovaScreenRecorder.App/WindowsGraphicsCaptureService.cs'))) { throw 'Native Windows Graphics Capture service is missing.' }
if (!(Test-Path (Join-Path $root 'tests/NovaScreenRecorder.Tests/RecordingUtilitiesTests.cs'))) { throw 'Automated tests are missing.' }
if (!(Test-Path (Join-Path $root 'scripts/Test-Windows11-Hardware.ps1'))) { throw 'Windows 11 hardware validation script is missing.' }
if (!(Test-Path (Join-Path $root '.github/workflows/build.yml'))) {
    throw 'GitHub Actions build workflow is missing.'
}
Write-Host "Static validation passed: $($handlers.Count) XAML event handlers checked; project, manifest, and safety guards are present."
