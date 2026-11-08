$ErrorActionPreference = "Continue"

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$TargetDirs = @(
    (Join-Path $ProjectRoot "bin\Release\net10.0-windows"),
    (Join-Path $ProjectRoot "bin\Debug\net10.0-windows")
)

Write-Host "[1/4] Entferne Internetsperren (Zone.Identifier)..." -ForegroundColor Cyan
Get-ChildItem -Path $ProjectRoot -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
    Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
}

Write-Host "[2/4] Suche Code-Signing-Zertifikat..." -ForegroundColor Cyan
$cert = Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.HasPrivateKey -and ($_.Subject -like '*CN=NeuroPlayer Dev Cert*' -or $_.Thumbprint -eq '18E2C9C22A3A557BD50792CF033EC7C5E72F265F') } |
        Select-Object -First 1

if (-not $cert) {
    $cert = Get-ChildItem -Path Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue |
            Where-Object { $_.HasPrivateKey } |
            Select-Object -First 1
}

if (-not $cert) {
    Write-Host "[FEHLER] Kein gueltiges Code-Signing-Zertifikat gefunden." -ForegroundColor Red
    exit 1
}

Write-Host "Verwende Zertifikat: $($cert.Subject) ($($cert.Thumbprint))" -ForegroundColor Green

try {
    $publicCertBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    $publicCert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(,$publicCertBytes)

    $stores = @(
        @{ Name = "Root";             Location = "CurrentUser" },
        @{ Name = "TrustedPublisher"; Location = "CurrentUser" },
        @{ Name = "Root";             Location = "LocalMachine" },
        @{ Name = "TrustedPublisher"; Location = "LocalMachine" }
    )

    foreach ($s in $stores) {
        try {
            $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($s.Name, $s.Location)
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $publicCert.Thumbprint })) {
                $store.Add($publicCert)
                Write-Host "  -> Eingetragen in $($s.Location)\$($s.Name)" -ForegroundColor DarkGray
            }
            $store.Close()
        } catch { }
    }
} catch { }

Write-Host "[3/4] Signiere Anwendungsdateien..." -ForegroundColor Cyan
$timestampServers = @(
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com",
    "http://timestamp.globalsign.com/scripts/timstamp.dll"
)

$signedCount = 0
foreach ($dir in $TargetDirs) {
    if (-not (Test-Path -Path $dir -PathType Container)) { continue }

    $files = Get-ChildItem -Path $dir -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -eq "SystemOptimierer.exe" -or $_.Name -eq "SystemOptimierer.dll"
    }

    foreach ($file in $files) {
        try {
            Unblock-File -Path $file.FullName -ErrorAction SilentlyContinue
            $sig = $null

            foreach ($ts in $timestampServers) {
                try {
                    $sig = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert -TimestampServer $ts -ErrorAction Stop
                    if ($sig.Status -eq 'Valid') { break }
                } catch { }
            }

            if (-not $sig -or $sig.Status -ne 'Valid') {
                $sig = Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $cert
            }

            $configName = Split-Path -Leaf (Split-Path -Parent $dir)
            if ($sig.Status -eq 'Valid') {
                Write-Host "  [OK] $($file.Name) ($configName): Signatur gueltig" -ForegroundColor Green
            } else {
                Write-Host "  [WARNUNG] $($file.Name) ($configName): $($sig.Status) - $($sig.StatusMessage)" -ForegroundColor Yellow
            }
            $signedCount++
        } catch {
            Write-Host "  [FEHLER] $($file.Name): $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

Write-Host "[4/4] Aktualisiere Desktop-Verknuepfung..." -ForegroundColor Cyan
$exePath = Join-Path $ProjectRoot "bin\Release\net10.0-windows\SystemOptimierer.exe"
if (-not (Test-Path $exePath)) {
    $exePath = Join-Path $ProjectRoot "bin\Debug\net10.0-windows\SystemOptimierer.exe"
}

if (Test-Path $exePath) {
    $desktopPath = [Environment]::GetFolderPath('Desktop')
    $shortcutPath = Join-Path $desktopPath "SystemOptimierer.lnk"
    $iconPath = Join-Path $ProjectRoot "icon.ico"

    $wshShell = New-Object -ComObject WScript.Shell
    $shortcut = $wshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = Split-Path -Parent $exePath
    if (Test-Path $iconPath) {
        $shortcut.IconLocation = "$iconPath,0"
    } else {
        $shortcut.IconLocation = "$exePath,0"
    }
    $shortcut.Description = "SystemOptimierer - Windows Wartungs- und Optimierungstool"
    $shortcut.Save()

    Unblock-File -Path $shortcutPath -ErrorAction SilentlyContinue
    Write-Host "  [OK] Desktop-Verknuepfung aktualisiert: $shortcutPath" -ForegroundColor Green
}

Write-Host ""
Write-Host "Fertig! Insgesamt $signedCount Datei(en) signiert und freigegeben." -ForegroundColor Green
exit 0