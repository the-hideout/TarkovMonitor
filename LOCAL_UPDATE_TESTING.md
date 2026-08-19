# Local Automatic Update Testing

This document explains how to test TarkovMonitor's complete automatic-update process without Visual Studio and without publishing a GitHub release.

> [!WARNING]
> Version `9999.0.0.0` is intentionally reserved for local testing. Never publish it or use the updated test application as a normal installation. Because it is higher than normal release versions, it will consider every real release older.

## Test layout

Local testing uses a dedicated directory that is safe to recreate between tests:

```text
C:\TarkovMonitor-UpdateTest
├── App
│   └── TarkovMonitor.exe
├── Publish
│   └── TarkovMonitor.exe
└── Releases
    └── TarkovMonitor-9999.0.0.0.zip
```

- `App` is the lower-version standalone installation being updated.
- `Publish` contains the files that will become the update.
- `Releases` acts as the local update feed.

Production and local testing use different asset names:

| Source | Package name | Version source |
|---|---|---|
| GitHub Releases | `TarkovMonitor.zip` | GitHub release name or tag |
| Local test feed | `TarkovMonitor-9999.0.0.0.zip` | Version embedded in filename |

## Recommended: automated test

The repository includes [`Test-LocalUpdate.ps1`](./Test-LocalUpdate.ps1), which prepares, launches, and verifies the test.

### Prerequisites

- Windows PowerShell 5.1 or PowerShell 7+
- The .NET SDK required by TarkovMonitor
- No running TarkovMonitor processes
- `Test-LocalUpdate.ps1` located in the repository root

### Run the test

Open PowerShell in the repository root:

```powershell
Set-Location C:\Data\Source\TarkovMonitor

Unblock-File .\Test-LocalUpdate.ps1
.\Test-LocalUpdate.ps1
```

If local script execution is blocked, allow it only for the current PowerShell process:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\Test-LocalUpdate.ps1
```

The script will:

1. Read the current `AssemblyVersion` from `TarkovMonitor.csproj`.
2. Stop if any TarkovMonitor process is running.
3. Recreate `C:\TarkovMonitor-UpdateTest`.
4. Clear TarkovMonitor's Onova cache.
5. Publish the current version into the `App` directory.
6. Publish version `9999.0.0.0` into the `Publish` directory.
7. Add an `update-test-marker.txt` verification file.
8. Create and validate `TarkovMonitor-9999.0.0.0.zip`.
9. Launch the lower-version application with the local feed configured for that process.
10. Wait for the update to finish and verify the executable version, marker, process, and Onova log.

When the application asks whether to install version `9999.0.0.0`, click **Yes**.

### Script options

Prepare the test without launching TarkovMonitor:

```powershell
.\Test-LocalUpdate.ps1 -PrepareOnly
```

Increase the default five-minute verification timeout:

```powershell
.\Test-LocalUpdate.ps1 -TimeoutSeconds 600
```

Override the automatically detected installed version:

```powershell
.\Test-LocalUpdate.ps1 -InstalledVersion 2.1.0.0
```

## Manual test

Use these steps when diagnosing the script or update service.

### 1. Close TarkovMonitor

```powershell
Get-Process TarkovMonitor -ErrorAction SilentlyContinue |
    Select-Object Id, Path
```

Close every listed process normally before continuing.

### 2. Define the test versions and directories

Run these commands from the repository root:

```powershell
$installedVersion = "2.1.0.0"
$updateVersion = "9999.0.0.0"

$testRoot = "C:\TarkovMonitor-UpdateTest"
$app = Join-Path $testRoot "App"
$publish = Join-Path $testRoot "Publish"
$feed = Join-Path $testRoot "Releases"
$onovaCache = Join-Path $env:LOCALAPPDATA "Onova\TarkovMonitor"
```

Set `$installedVersion` to the version currently defined by `<AssemblyVersion>` in `TarkovMonitor\TarkovMonitor.csproj`.

### 3. Recreate the test environment

The following commands remove only the dedicated test directory and TarkovMonitor's Onova cache:

```powershell
Remove-Item $testRoot -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $onovaCache -Recurse -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Path $app, $publish, $feed |
    Out-Null
```

### 4. Publish the installed baseline

```powershell
dotnet publish .\TarkovMonitor\TarkovMonitor.csproj `
    -c Release `
    --self-contained `
    --runtime win-x64 `
    -p:PublishSingleFile=true `
    -p:Version=$installedVersion `
    -p:AssemblyVersion=$installedVersion `
    -p:FileVersion=$installedVersion `
    --output $app
```

Verify the version:

```powershell
(Get-Item "$app\TarkovMonitor.exe").VersionInfo |
    Select-Object FileVersion, ProductVersion
```

### 5. Publish the local update

```powershell
dotnet publish .\TarkovMonitor\TarkovMonitor.csproj `
    -c Release `
    --self-contained `
    --runtime win-x64 `
    -p:PublishSingleFile=true `
    -p:Version=$updateVersion `
    -p:AssemblyVersion=$updateVersion `
    -p:FileVersion=$updateVersion `
    --output $publish
```

Verify that the published update reports version `9999.0.0.0`:

```powershell
(Get-Item "$publish\TarkovMonitor.exe").VersionInfo |
    Select-Object FileVersion, ProductVersion
```

### 6. Add a marker and create the package

```powershell
Set-Content `
    -Path "$publish\update-test-marker.txt" `
    -Value "Successfully installed local update $updateVersion"

Compress-Archive `
    -Path "$publish\*" `
    -DestinationPath "$feed\TarkovMonitor-$updateVersion.zip" `
    -Force
```

The contents of `Publish` must be compressed—not the `Publish` directory itself. `TarkovMonitor.exe` must therefore appear at the root of the ZIP:

```text
TarkovMonitor.exe
update-test-marker.txt
wwwroot/...
```

This structure is incorrect:

```text
Publish/TarkovMonitor.exe
```

### 7. Configure the local feed and launch the baseline

Set the environment variable and launch the application from the same PowerShell window:

```powershell
$env:TARKOVMONITOR_UPDATE_SOURCE = $feed
Start-Process "$app\TarkovMonitor.exe"
```

Expected behavior:

1. The lower-version application starts from `C:\TarkovMonitor-UpdateTest\App`.
2. An update prompt announces version `9999.0.0.0`.
3. Click **Yes**.
4. TarkovMonitor downloads and extracts the local ZIP.
5. TarkovMonitor closes.
6. Onova copies the new files into the `App` directory.
7. TarkovMonitor restarts automatically.

### 8. Verify the update

```powershell
Get-Process TarkovMonitor |
    Select-Object Id, Path

(Get-Item "$app\TarkovMonitor.exe").VersionInfo |
    Select-Object FileVersion, ProductVersion

Get-Content "$app\update-test-marker.txt"

Get-Content "$env:LOCALAPPDATA\Onova\TarkovMonitor\Log.txt"
```

Expected results:

- The running path is `C:\TarkovMonitor-UpdateTest\App\TarkovMonitor.exe`.
- The installed version is `9999.0.0.0`.
- The marker says the local update installed successfully.
- The Onova log ends with `Update completed successfully.`

### 9. End local test mode

```powershell
Remove-Item Env:\TARKOVMONITOR_UPDATE_SOURCE -ErrorAction SilentlyContinue
```

Close TarkovMonitor before removing the test directory or Onova cache:

```powershell
Remove-Item "C:\TarkovMonitor-UpdateTest" `
    -Recurse -Force -ErrorAction SilentlyContinue

Remove-Item "$env:LOCALAPPDATA\Onova\TarkovMonitor" `
    -Recurse -Force -ErrorAction SilentlyContinue
```

## Troubleshooting

### No update prompt appears

Check the following:

1. `$env:TARKOVMONITOR_UPDATE_SOURCE` points to the `Releases` directory.
2. The local package is named `TarkovMonitor-9999.0.0.0.zip`.
3. `LocalPackageResolver` uses the pattern `TarkovMonitor-*.zip`.
4. The running application version is lower than `9999.0.0.0`.
5. `TarkovMonitor.exe` is at the root of the ZIP.
6. The Onova cache was cleared before the test.
7. No other TarkovMonitor process is running.
8. `updateService.Start()` is reached during application startup.

### The application closes but does not restart

Review the updater log:

```powershell
Get-Content "$env:LOCALAPPDATA\Onova\TarkovMonitor\Log.txt"
```

Look for file-access, elevation, antivirus, or timeout errors.

## Production behavior

- Without `TARKOVMONITOR_UPDATE_SOURCE`, `UpdateService` uses the official `the-hideout/TarkovMonitor` GitHub Releases feed.
- Production uses an asset named exactly `TarkovMonitor.zip`.
- Local testing uses `TarkovMonitor-<version>.zip` because Onova extracts local versions from filenames.
- Draft and prerelease GitHub releases are ignored.
