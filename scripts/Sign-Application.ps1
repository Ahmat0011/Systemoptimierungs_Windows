<#
.SYNOPSIS
    Erstellt ein lokales Code-Signing-Zertifikat und signiert die SystemOptimierer-Binaries,
    um Blockaden durch Windows Smart App Control (SAC) oder Windows Defender zu verhindern.
#>

param(
    [string]$TargetDir = "$PSScriptRoot\..\bin\Debug\net10.0-windows"
)

$CertSubject = "CN=SystemOptimizer PRO Developer"
$Cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $CertSubject } | Select-Object -First 1

if (-not $Cert) {
    Write-Host "Erstelle lokales Code-Signing-Zertifikat in Cert:\CurrentUser\My..." -ForegroundColor Cyan
    $Cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $CertSubject `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -HashAlgorithm "SHA256" `
        -KeyLength 2048 `
        -NotAfter (Get-Date).AddYears(5)
}

if (-not $Cert) {
    Write-Error "Zertifikat konnte nicht erstellt oder gefunden werden."
    exit 1
}

Write-Host "Verwende Zertifikat: $($Cert.Thumbprint)" -ForegroundColor Green

$resolvedTarget = (Resolve-Path $TargetDir -ErrorAction SilentlyContinue)
if (-not $resolvedTarget) {
    Write-Host "Zielordner '$TargetDir' nicht gefunden." -ForegroundColor Yellow
    exit 0
}

$FilesToSign = Get-ChildItem -Path $resolvedTarget.Path -Include "*.exe", "*.dll" -Recurse -File | Where-Object {
    $_.Name -like "SystemOptimierer*"
}

foreach ($file in $FilesToSign) {
    Write-Host "Signiere: $($file.Name)" -ForegroundColor Cyan
    Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $Cert -HashAlgorithm SHA256 -ErrorAction SilentlyContinue
    $sig = Get-AuthenticodeSignature $file.FullName
    $color = if ($sig.Status -eq "Valid") { "Green" } else { "Yellow" }
    Write-Host "Status fuer $($file.Name): $($sig.Status)" -ForegroundColor $color
}

Write-Host "Signierung abgeschlossen." -ForegroundColor Green
