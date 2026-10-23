$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\NovaScreenRecorder.App\NovaScreenRecorder.App.csproj"
$output = Join-Path $repoRoot "publish\win-x64"

dotnet restore $project
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }

dotnet publish $project -c Release -r win-x64 --self-contained true `
  -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Host "Published application to $output"
