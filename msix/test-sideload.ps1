# test-sideload.ps1 - Build and sideload MSIX for local testing
# Run from the repository root: powershell -File msix/test-sideload.ps1
# Requires: .NET 8 SDK, Windows 10 SDK (for makeappx.exe / signtool.exe)

#Requires -RunAsAdministrator

param(
    [switch]$SkipBuild,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$repoRoot     = (Resolve-Path "$PSScriptRoot/..").Path
$csproj       = Join-Path $repoRoot "src/TibiaSquare.HuntMonitor/TibiaSquare.HuntMonitor.csproj"
$msixDir      = Join-Path $repoRoot "msix"
$stagingDir   = Join-Path $repoRoot "msix-staging"
$certPath     = Join-Path $msixDir "test-cert.pfx"
$certPasswordBytes = New-Object byte[] 32
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($certPasswordBytes)
$certPassword = [Convert]::ToBase64String($certPasswordBytes)
$certSubject  = "CN=TibiaSquareTest"
$msixOutput   = Join-Path $repoRoot "TibiaSquareHuntMonitor.msix"
$packageName  = "Ampcasd.TibiaSquareHuntUploader"

# Find Windows SDK tools
$sdkBin = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\10.0.*\x64" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "makeappx.exe") } |
    Sort-Object { [version]($_.Parent.Name) } -Descending | Select-Object -First 1
if (-not $sdkBin) {
    Write-Error "Windows 10 SDK not found. Install it from Visual Studio Installer."
    exit 1
}
$makeappx = Join-Path $sdkBin.FullName "makeappx.exe"
$signtool = Join-Path $sdkBin.FullName "signtool.exe"

# --- Uninstall ---
if ($Uninstall) {
    Write-Host "Removing sideloaded package..." -ForegroundColor Yellow
    Get-AppxPackage -Name $packageName | Remove-AppxPackage
    Write-Host "Done." -ForegroundColor Green
    exit 0
}

# --- Step 1: Build ---
if (-not $SkipBuild) {
    Write-Host "Building app..." -ForegroundColor Cyan
    dotnet publish $csproj -c Release -r win-x64 --self-contained true -o (Join-Path $repoRoot "publish")
    if ($LASTEXITCODE -ne 0) { exit 1 }

    # Bundle OBS if available
    $obsBundle = Join-Path $repoRoot "obs-portable"
    if (Test-Path $obsBundle) {
        Copy-Item $obsBundle -Destination (Join-Path $repoRoot "publish/obs-portable") -Recurse -Force
    } else {
        Write-Warning "obs-portable not found at $obsBundle - skipping OBS bundle"
    }
}

# --- Step 2: Stage MSIX content ---
Write-Host "Staging MSIX content..." -ForegroundColor Cyan

if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
Copy-Item (Join-Path $repoRoot "publish") -Destination $stagingDir -Recurse

# Read version from csproj
$versionMatch = Select-String -Path $csproj -Pattern '<Version>([^<]+)<'
$version = $versionMatch.Matches[0].Groups[1].Value
$msixVersion = "$version.0"  # MSIX requires 4-part version

# Copy and patch manifest
$manifest = Get-Content (Join-Path $msixDir "AppxManifest.xml") -Raw
$manifest = $manifest -replace "VERSION_PLACEHOLDER", $msixVersion
$manifest = $manifest -replace "CN=PUBLISHER_PLACEHOLDER", $certSubject
$manifest | Set-Content (Join-Path $stagingDir "AppxManifest.xml") -Encoding UTF8

# Copy logo assets
$stagingAssets = Join-Path $stagingDir "Assets"
if (-not (Test-Path $stagingAssets)) { New-Item $stagingAssets -ItemType Directory | Out-Null }
Copy-Item (Join-Path $msixDir "Assets\*") -Destination $stagingAssets -Force

# --- Step 3: Create self-signed certificate ---
Write-Host "Creating self-signed certificate..." -ForegroundColor Cyan

# Remove old cert if present
Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $certSubject } | Remove-Item -Force -ErrorAction SilentlyContinue

$cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject $certSubject `
    -KeyUsage DigitalSignature `
    -FriendlyName "Tibia Square Test Signing" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

# Export to PFX
$pwd = ConvertTo-SecureString -String $certPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $certPath -Password $pwd | Out-Null

# Trust the cert (needed for sideloading)
Export-Certificate -Cert $cert -FilePath (Join-Path $msixDir "test-cert.cer") | Out-Null
Import-Certificate -FilePath (Join-Path $msixDir "test-cert.cer") -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Remove-Item (Join-Path $msixDir "test-cert.cer") -Force

# --- Step 4: Pack MSIX ---
Write-Host "Packing MSIX..." -ForegroundColor Cyan

if (Test-Path $msixOutput) { Remove-Item $msixOutput -Force }
& $makeappx pack /d $stagingDir /p $msixOutput /nv
if ($LASTEXITCODE -ne 0) { exit 1 }

# --- Step 5: Sign MSIX ---
Write-Host "Signing MSIX..." -ForegroundColor Cyan

& $signtool sign /fd SHA256 /a /f $certPath /p $certPassword $msixOutput
if ($LASTEXITCODE -ne 0) { exit 1 }

# --- Done ---
Write-Host ""
Write-Host "MSIX package ready: $msixOutput" -ForegroundColor Green
Write-Host ""
Write-Host "To install, run:" -ForegroundColor Yellow
Write-Host ('  Add-AppxPackage -Path "' + $msixOutput + '"') -ForegroundColor White
Write-Host ""
Write-Host "To uninstall later:" -ForegroundColor Yellow
Write-Host '  powershell -File msix/test-sideload.ps1 -Uninstall' -ForegroundColor White
