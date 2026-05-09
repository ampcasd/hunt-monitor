# Export the TibiaSquareTest public cert (.cer) for sharing with other machines.
# Run: powershell -File desktop/msix/export-cert.ps1

$ErrorActionPreference = "Stop"

$subject = "CN=TibiaSquareTest"
$msixPath = Join-Path $PSScriptRoot "..\..\TibiaSquareHuntMonitor.msix"

# Find the exact cert that signed the current MSIX (there may be multiple
# CN=TibiaSquareTest certs in the trust store from past sideload runs).
if (-not (Test-Path $msixPath)) {
    Write-Error "MSIX not found at $msixPath. Run restart.ps1 first."
    exit 1
}
$signerThumb = (Get-AuthenticodeSignature $msixPath).SignerCertificate.Thumbprint
if (-not $signerThumb) {
    Write-Error "MSIX at $msixPath is not signed."
    exit 1
}

$cert = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
    Where-Object { $_.Subject -eq $subject -and $_.Thumbprint -eq $signerThumb } |
    Select-Object -First 1

if (-not $cert) {
    Write-Error "Signer cert $signerThumb not found in LocalMachine\TrustedPeople. Run msix/test-sideload.ps1 once as admin first."
    exit 1
}

$out = Join-Path ([Environment]::GetFolderPath("Desktop")) "TibiaSquareTest.cer"
Export-Certificate -Cert $cert -FilePath $out | Out-Null

Write-Host "Exported: $out" -ForegroundColor Green
Write-Host "Thumbprint: $($cert.Thumbprint)" -ForegroundColor DarkGray
Write-Host "NotAfter:   $($cert.NotAfter)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Copy this .cer + the .msix to the target machine, then on that machine (as admin):" -ForegroundColor Yellow
Write-Host '  Import-Certificate -FilePath "C:\path\TibiaSquareTest.cer" -CertStoreLocation Cert:\LocalMachine\TrustedPeople' -ForegroundColor White
Write-Host '  Add-AppxPackage    -Path     "C:\path\TibiaSquareHuntMonitor.msix"' -ForegroundColor White
