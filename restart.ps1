# restart.ps1 — Kill running app, build, package MSIX, and install
# No admin required if test certificate is already trusted in LocalMachine\TrustedPeople.
# Run from anywhere: powershell -File C:\code\tibia-square\desktop\restart.ps1

$ErrorActionPreference = "Stop"

$desktopDir   = $PSScriptRoot
$repoRoot     = (Resolve-Path "$desktopDir/..").Path
$csproj       = Join-Path $desktopDir "src/TibiaSquare.HuntMonitor/TibiaSquare.HuntMonitor.csproj"
$msixDir      = Join-Path $desktopDir "msix"
$publishDir   = Join-Path $repoRoot "publish"
$stagingDir   = Join-Path $repoRoot "msix-staging"
$msixOutput   = Join-Path $repoRoot "TibiaSquareHuntMonitor.msix"
$certSubject  = "CN=TibiaSquareTest"
$packageName  = "Ampcasd.TibiaSquareHuntUploader"

# --- Find signing cert (must be trusted + have private key) ---
$trustedThumbs = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object { $_.Subject -eq $certSubject } |
    Select-Object -ExpandProperty Thumbprint

$signingCert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $certSubject -and $_.HasPrivateKey -and ($trustedThumbs -contains $_.Thumbprint) } |
    Select-Object -First 1

if (-not $signingCert) {
    Write-Error @"
No trusted signing certificate found. Run once with admin to set up:
  powershell -File desktop/msix/test-sideload.ps1
"@
    exit 1
}
Write-Host "Using cert: $($signingCert.Thumbprint)" -ForegroundColor DarkGray

# --- Find Windows SDK tools ---
$sdkBin = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\10.0.*\x64" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "makeappx.exe") } |
    Sort-Object { [version]($_.Parent.Name) } -Descending | Select-Object -First 1
if (-not $sdkBin) { Write-Error "Windows 10 SDK not found."; exit 1 }
$makeappx = Join-Path $sdkBin.FullName "makeappx.exe"
$signtool = Join-Path $sdkBin.FullName "signtool.exe"

# --- Kill running app + OBS ---
$procs = Get-Process -Name "TibiaSquare.HuntMonitor" -ErrorAction SilentlyContinue
if ($procs) {
    Write-Host "Stopping Hunt Monitor..." -ForegroundColor Yellow
    $procs | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}
# Kill OBS instances spawned from our portable directory (orphaned by the force-kill above)
Get-Process -Name "obs64" -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $exePath = $_.MainModule.FileName
        if ($exePath -like "*obs-portable*") {
            Write-Host "Stopping orphaned OBS (PID: $($_.Id))..." -ForegroundColor Yellow
            $_ | Stop-Process -Force
        }
    } catch {}
}
Start-Sleep -Milliseconds 500

# --- Build (Debug intentionally — local dev script only, not CI) ---
Write-Host "Building..." -ForegroundColor Cyan
dotnet publish $csproj -c Debug -r win-x64 --self-contained true -o $publishDir -v quiet
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "Building processing DLL..." -ForegroundColor Cyan
powershell -ExecutionPolicy Bypass -File (Join-Path $repoRoot "scripts/build-processing-dll.ps1") -Configuration Debug -OutputDir (Join-Path $publishDir "lib")
if ($LASTEXITCODE -ne 0) { exit 1 }

# Bundle OBS if available
$obsBundle = Join-Path $desktopDir "obs-portable"
$obsDest = Join-Path $publishDir "obs-portable"
if ((Test-Path $obsBundle) -and -not (Test-Path $obsDest)) {
    Copy-Item $obsBundle -Destination $obsDest -Recurse -Force
}

# --- Stage MSIX ---
Write-Host "Staging MSIX..." -ForegroundColor Cyan
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
Copy-Item $publishDir -Destination $stagingDir -Recurse

# Read version from csproj
$versionMatch = Select-String -Path $csproj -Pattern '<Version>([^<]+)<'
$versionProps = Join-Path $desktopDir "Version.props"
if (-not $versionMatch -and (Test-Path $versionProps)) {
    $versionMatch = Select-String -Path $versionProps -Pattern '<Version>([^<]+)<'
}
$version = $versionMatch.Matches[0].Groups[1].Value
$msixVersion = "$version.0"

# Patch manifest
$manifest = Get-Content (Join-Path $msixDir "AppxManifest.xml") -Raw
$manifest = $manifest -replace "VERSION_PLACEHOLDER", $msixVersion
$manifest = $manifest -replace "CN=PUBLISHER_PLACEHOLDER", $certSubject
$manifest | Set-Content (Join-Path $stagingDir "AppxManifest.xml") -Encoding UTF8

# Copy logo assets
$stagingAssets = Join-Path $stagingDir "Assets"
if (-not (Test-Path $stagingAssets)) { New-Item $stagingAssets -ItemType Directory | Out-Null }
Copy-Item (Join-Path $msixDir "Assets\*") -Destination $stagingAssets -Force

# --- Pack MSIX ---
Write-Host "Packing MSIX..." -ForegroundColor Cyan
if (Test-Path $msixOutput) { Remove-Item $msixOutput -Force }
& $makeappx pack /d $stagingDir /p $msixOutput /nv /o 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    & $makeappx pack /d $stagingDir /p $msixOutput /nv /o
    exit 1
}

# --- Sign MSIX ---
Write-Host "Signing..." -ForegroundColor Cyan
& $signtool sign /fd SHA256 /sha1 $signingCert.Thumbprint $msixOutput 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    & $signtool sign /fd SHA256 /sha1 $signingCert.Thumbprint $msixOutput
    exit 1
}

# --- Remove old package (same version + changed content blocks reinstall) ---
$existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing old package v$($existing.Version)..." -ForegroundColor DarkGray
    Remove-AppxPackage -Package $existing.PackageFullName
}

# --- Install ---
Write-Host "Installing v$version..." -ForegroundColor Cyan
Add-AppxPackage -Path $msixOutput -ForceUpdateFromAnyVersion
if ($LASTEXITCODE -ne 0) { exit 1 }

# --- Cleanup ---
Remove-Item $stagingDir -Recurse -Force -ErrorAction SilentlyContinue

# --- Launch ---
Write-Host "Launching..." -ForegroundColor Cyan
$pkg = Get-AppxPackage -Name $packageName
$familyName = $pkg.PackageFamilyName
explorer.exe "shell:AppsFolder\${familyName}!HuntMonitor"

Write-Host "Done - Hunt Monitor v$version installed and running." -ForegroundColor Green
