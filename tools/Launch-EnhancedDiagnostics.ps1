$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "TarkovMonitor\TarkovMonitor.csproj"
$bundledDotnet = "C:\Users\anden\.codex\toolchains\dotnet-10.0.302\dotnet.exe"

if (Test-Path -LiteralPath $bundledDotnet) {
    $dotnetPath = $bundledDotnet
}
else {
    $dotnetCommand = Get-Command dotnet -ErrorAction Stop
    $dotnetPath = $dotnetCommand.Source
}

Write-Host "Launching TarkovMonitor from: $repoRoot"
Write-Host "Branch-local diagnostics build; installed TarkovMonitor is not modified."

Push-Location $repoRoot
try {
    & $dotnetPath run --project $projectPath --no-restore
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
