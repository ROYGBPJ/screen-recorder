$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $repoRoot "NovaScreenRecorder.sln") -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
Write-Host "Build succeeded."
