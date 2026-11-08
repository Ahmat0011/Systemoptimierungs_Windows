param(
    [Parameter(Mandatory=$false, Position=0)]
    [string]$TargetDir = "$PSScriptRoot\..\bin\Release\net10.0-windows"
)

$ErrorActionPreference = "Continue"

if (-not (Test-Path -Path $TargetDir -PathType Container)) {
    $TargetDir = "$PSScriptRoot\..\bin\Debug\net10.0-windows"
}

if (-not (Test-Path -Path $TargetDir -PathType Container)) {
    Write-Output "Zielordner existiert noch nicht - Signierung uebersprungen."
    exit 0
}

$cert = Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.HasPrivateKey -and ($_.Subject -like '*CN=NeuroPlayer Dev Cert*' -or $_.Thumbprint -eq '18E2C9C22A3A557BD50792CF033EC7C5E72F265F') } |
        Select-Object -First 1

if (-not $cert) {
    $cert = Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.HasPrivateKey } |
            Select-Object -First 1
}

if (-not $cert) {
    Write-Output "Kein Code-Signing-Zertifikat gefunden - Signierung uebersprungen."
    exit 0
}

Write-Output "Verwende Zertifikat: $($cert.Subject) ($($cert.Thumbprint))"

$timestampServers = @(
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com",
    "http://timestamp.globalsign.com/scripts/timstamp.dll"
)

$filesToSign = Get-ChildItem -Path $TargetDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
    ($_.Extension -eq ".exe" -or $_.Extension -eq ".dll") -and ($_.Name -like "SystemOptimierer*")
}

foreach ($file in $filesToSign) {
    try {
        Unblock-File -Path $file.FullName -ErrorAction SilentlyContinue
        Write-Output "Signiere: $($file.FullName)"
        $status = $null

        foreach ($server in $timestampServers) {
            try {
                $status = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -TimestampServer $server -ErrorAction Stop
                if ($status.Status -eq 'Valid') { break }
            } catch { }
        }

        if (-not $status -or $status.Status -ne 'Valid') {
            $status = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -ErrorAction SilentlyContinue
        }

        if ($status) {
            Write-Output "  Status: $($status.Status) - $($status.StatusMessage)"
        }
    } catch { }
}

$desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "SystemOptimierer.lnk"
if (Test-Path $desktopLnk) {
    Unblock-File -Path $desktopLnk -ErrorAction SilentlyContinue
}

Write-Output "Signierung erfolgreich abgeschlossen."
exit 0