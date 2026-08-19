#Requires -Version 5.1

<#
.SYNOPSIS
    Builds and verifies a complete TarkovMonitor update using a local feed.

.DESCRIPTION
    Creates a lower-version standalone installation and a version 9999.0.0.0
    update package under C:\TarkovMonitor-UpdateTest. It launches the installed
    build with TARKOVMONITOR_UPDATE_SOURCE set only for that process, waits for
    the user to accept the update prompt, and verifies the replaced executable.

.EXAMPLE
    .\Test-LocalUpdate.ps1

.EXAMPLE
    .\Test-LocalUpdate.ps1 -PrepareOnly

.EXAMPLE
    .\Test-LocalUpdate.ps1 -InstalledVersion 2.1.0.0 -TimeoutSeconds 600
#>

[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$InstalledVersion,

    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$UpdateVersion = '9999.0.0.0',

    [string]$TestRoot = 'C:\TarkovMonitor-UpdateTest',

    [ValidateRange(30, 1800)]
    [int]$TimeoutSeconds = 300,

    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step {
    param([string]$Message)

    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-DotNet {
    param([string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE."
    }
}

function Get-ExecutableFileVersion {
    param([Parameter(Mandatory = $true)][string]$Path)

    $versionInfo = (Get-Item -LiteralPath $Path).VersionInfo
    return [Version](
        '{0}.{1}.{2}.{3}' -f
        $versionInfo.FileMajorPart,
        $versionInfo.FileMinorPart,
        $versionInfo.FileBuildPart,
        $versionInfo.FilePrivatePart
    )
}

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $repoRoot 'TarkovMonitor\TarkovMonitor.csproj'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw @"
TarkovMonitor.csproj was not found at:
$projectPath

Place Test-LocalUpdate.ps1 in the repository root and run it from there.
"@
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The dotnet CLI was not found. Install the required .NET SDK and try again.'
}

if ([string]::IsNullOrWhiteSpace($InstalledVersion)) {
    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $assemblyVersionNode = $projectXml.SelectSingleNode(
        '/Project/PropertyGroup/AssemblyVersion'
    )

    if ($null -eq $assemblyVersionNode -or
        [string]::IsNullOrWhiteSpace($assemblyVersionNode.InnerText)) {
        throw @'
The current AssemblyVersion could not be read from TarkovMonitor.csproj.
Pass it explicitly, for example:
  .\Test-LocalUpdate.ps1 -InstalledVersion 2.1.0.0
'@
    }

    $InstalledVersion = $assemblyVersionNode.InnerText.Trim()
}

$installedVersionObject = [Version]$InstalledVersion
$updateVersionObject = [Version]$UpdateVersion

if ($updateVersionObject -le $installedVersionObject) {
    throw "UpdateVersion ($UpdateVersion) must be greater than InstalledVersion ($InstalledVersion)."
}

$testRootFull = [IO.Path]::GetFullPath($TestRoot).TrimEnd('\')
$driveRoot = [IO.Path]::GetPathRoot($testRootFull).TrimEnd('\')
$repoRootFull = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\')

if ($testRootFull -eq $driveRoot -or $testRootFull.Length -lt 10) {
    throw "Unsafe TestRoot rejected: $testRootFull"
}

if ($repoRootFull.Equals($testRootFull, [StringComparison]::OrdinalIgnoreCase) -or
    $repoRootFull.StartsWith(
        $testRootFull + '\',
        [StringComparison]::OrdinalIgnoreCase
    )) {
    throw 'TestRoot cannot be the repository root or one of its parent directories.'
}

$appDirectory = Join-Path $testRootFull 'App'
$publishDirectory = Join-Path $testRootFull 'Publish'
$feedDirectory = Join-Path $testRootFull 'Releases'
$updateZip = Join-Path $feedDirectory "TarkovMonitor-$UpdateVersion.zip"
$onovaCache = Join-Path $env:LOCALAPPDATA 'Onova\TarkovMonitor'
$markerPath = Join-Path $publishDirectory 'update-test-marker.txt'
$installedMarkerPath = Join-Path $appDirectory 'update-test-marker.txt'
$installedExecutable = Join-Path $appDirectory 'TarkovMonitor.exe'
$publishedExecutable = Join-Path $publishDirectory 'TarkovMonitor.exe'
$onovaLog = Join-Path $onovaCache 'Log.txt'

$runningProcesses = @(
    Get-Process TarkovMonitor -ErrorAction SilentlyContinue
)

if ($runningProcesses.Count -gt 0) {
    $processList = $runningProcesses |
        ForEach-Object { "PID $($_.Id): $($_.Path)" }

    throw "Close every TarkovMonitor process before testing:`n$($processList -join "`n")"
}

Write-Host 'TarkovMonitor local automatic-update test' -ForegroundColor Green
Write-Host "Repository:        $repoRootFull"
Write-Host "Installed version: $InstalledVersion"
Write-Host "Update version:    $UpdateVersion"
Write-Host "Test directory:    $testRootFull"

Write-Step 'Cleaning the dedicated test directory and Onova cache'

if (Test-Path -LiteralPath $testRootFull) {
    Remove-Item -LiteralPath $testRootFull -Recurse -Force
}

if (Test-Path -LiteralPath $onovaCache) {
    Remove-Item -LiteralPath $onovaCache -Recurse -Force
}

New-Item -ItemType Directory -Path @(
    $appDirectory,
    $publishDirectory,
    $feedDirectory
) | Out-Null

Write-Step "Publishing the installed baseline ($InstalledVersion)"

$baselineArguments = @(
    'publish',
    $projectPath,
    '-c', 'Release',
    '--self-contained',
    '--runtime', 'win-x64',
    '-p:PublishSingleFile=true',
    "-p:Version=$InstalledVersion",
    "-p:AssemblyVersion=$InstalledVersion",
    "-p:FileVersion=$InstalledVersion",
    '--output', $appDirectory
)

Invoke-DotNet -Arguments $baselineArguments

if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
    throw "Baseline executable was not created: $installedExecutable"
}

$actualInstalledVersion = Get-ExecutableFileVersion -Path $installedExecutable
if ($actualInstalledVersion -ne $installedVersionObject) {
    throw "Baseline version is $actualInstalledVersion; expected $InstalledVersion."
}

Write-Step "Publishing the local update ($UpdateVersion)"

$updateArguments = @(
    'publish',
    $projectPath,
    '-c', 'Release',
    '--self-contained',
    '--runtime', 'win-x64',
    '-p:PublishSingleFile=true',
    "-p:Version=$UpdateVersion",
    "-p:AssemblyVersion=$UpdateVersion",
    "-p:FileVersion=$UpdateVersion",
    '--output', $publishDirectory
)

Invoke-DotNet -Arguments $updateArguments

if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Update executable was not created: $publishedExecutable"
}

$actualUpdateVersion = Get-ExecutableFileVersion -Path $publishedExecutable
if ($actualUpdateVersion -ne $updateVersionObject) {
    throw "Update version is $actualUpdateVersion; expected $UpdateVersion."
}

Set-Content -LiteralPath $markerPath -Value (
    "Successfully installed local update $UpdateVersion"
)

Write-Step 'Creating and validating the local update archive'

Compress-Archive `
    -Path (Join-Path $publishDirectory '*') `
    -DestinationPath $updateZip `
    -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($updateZip)

try {
    $rootExecutable = $archive.Entries |
        Where-Object { $_.FullName -eq 'TarkovMonitor.exe' } |
        Select-Object -First 1

    if ($null -eq $rootExecutable) {
        throw 'TarkovMonitor.exe is not at the root of the update ZIP.'
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Created local package: $updateZip" -ForegroundColor Green

if ($PrepareOnly) {
    Write-Host ''
    Write-Host 'Preparation completed. The application was not launched.' -ForegroundColor Yellow
    Write-Host 'To launch it manually from this PowerShell window:'
    Write-Host "  `$env:TARKOVMONITOR_UPDATE_SOURCE = '$feedDirectory'"
    Write-Host "  Start-Process '$installedExecutable'"
    exit 0
}

Write-Step 'Launching the installed baseline against the local update feed'

$environmentVariableName = 'TARKOVMONITOR_UPDATE_SOURCE'
$previousUpdateSource = [Environment]::GetEnvironmentVariable(
    $environmentVariableName,
    'Process'
)

[Environment]::SetEnvironmentVariable(
    $environmentVariableName,
    $feedDirectory,
    'Process'
)

try {
    $launchedProcess = Start-Process `
        -FilePath $installedExecutable `
        -PassThru
}
finally {
    [Environment]::SetEnvironmentVariable(
        $environmentVariableName,
        $previousUpdateSource,
        'Process'
    )
}

Write-Host "Started PID $($launchedProcess.Id): $installedExecutable"
Write-Host ''
Write-Host 'When the update prompt appears, click Yes.' -ForegroundColor Yellow
Write-Host "Waiting up to $TimeoutSeconds seconds for the update to complete..."

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$updateSucceeded = $false

while ((Get-Date) -lt $deadline) {
    if (Test-Path -LiteralPath $installedMarkerPath -PathType Leaf) {
        try {
            $currentVersion = Get-ExecutableFileVersion -Path $installedExecutable
            if ($currentVersion -eq $updateVersionObject) {
                $updateSucceeded = $true
                break
            }
        }
        catch {
            # The updater may be replacing the executable at this moment.
        }
    }

    Start-Sleep -Seconds 2
}

if (-not $updateSucceeded) {
    Write-Host ''
    Write-Host 'The local update was not verified before the timeout.' -ForegroundColor Red

    if (Test-Path -LiteralPath $onovaLog -PathType Leaf) {
        Write-Host ''
        Write-Host 'Latest Onova log entries:' -ForegroundColor Yellow
        Get-Content -LiteralPath $onovaLog -Tail 30
    }

    throw @"
Local update verification failed.

Confirm that:
- The update prompt appeared and you clicked Yes.
- TARKOVMONITOR_UPDATE_SOURCE was read by UpdateService.
- LocalPackageResolver uses the pattern TarkovMonitor-*.zip.
- No antivirus or file-permission control blocked the updater.
"@
}

Write-Step 'Local automatic update verified successfully'

$finalVersion = Get-ExecutableFileVersion -Path $installedExecutable
$markerText = Get-Content -LiteralPath $installedMarkerPath -Raw

Write-Host "Installed executable: $installedExecutable"
Write-Host "Installed version:    $finalVersion"
Write-Host "Marker:               $($markerText.Trim())"

$updatedProcesses = @(
    Get-Process TarkovMonitor -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $installedExecutable }
)

if ($updatedProcesses.Count -gt 0) {
    Write-Host 'Running process:'
    $updatedProcesses |
        Select-Object Id, Path |
        Format-Table -AutoSize
}

if (Test-Path -LiteralPath $onovaLog -PathType Leaf) {
    Write-Host 'Latest Onova log entries:'
    Get-Content -LiteralPath $onovaLog -Tail 20
}

Write-Host ''
Write-Host "This test installation is version $UpdateVersion." -ForegroundColor Yellow
Write-Host 'Do not use it as a normal installation or publish it.' -ForegroundColor Yellow
Write-Host "Close TarkovMonitor before deleting: $testRootFull"
