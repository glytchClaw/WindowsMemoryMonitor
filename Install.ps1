# Installs (or updates) the Memory Monitor widget for the current user.
# Usage: powershell -ExecutionPolicy Bypass -File Install.ps1
$ErrorActionPreference = 'Stop'

$package = Get-ChildItem -Path $PSScriptRoot -Recurse -Filter 'MemoryWidget_*_x64.msix' |
    Where-Object { $_.FullName -notmatch '\\Dependencies\\' } |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $package) { throw "No MemoryWidget_*_x64.msix found under $PSScriptRoot. Run Build.ps1 first." }

# Windows only installs the package if its signing certificate is trusted on this machine.
$cer = Join-Path $PSScriptRoot 'MemoryMonitorWidget.cer'
$thumbprint = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $cer).Thumbprint
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint")) {
    Write-Host 'Windows needs to trust this package''s self-signed certificate (CN=MemoryMonitorWidget) before it will install.' -ForegroundColor Yellow
    Write-Host 'This adds it to the machine''s Trusted People store and needs administrator approval.'
    $answer = Read-Host 'Trust the certificate now? (Y/N)'
    if ($answer -notmatch '^[Yy]') {
        Write-Host "Not installed. To trust it yourself, run this in an elevated PowerShell, then run Install.ps1 again:"
        Write-Host "  Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
        exit 1
    }
    $import = "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $import
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$thumbprint")) { throw 'The certificate was not trusted, so the package cannot be installed.' }
}

# The deployment service can't open packages from very long paths, so install from a short temp copy.
$staged = Join-Path ([IO.Path]::GetTempPath()) $package.Name
Copy-Item -LiteralPath $package.FullName -Destination $staged -Force
try {
    Add-AppxPackage -Path $staged -ForceApplicationShutdown -ForceUpdateFromAnyVersion
}
finally {
    Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue
}

# The Widgets board only discovers new providers when it starts, so restart it. It relaunches on demand.
Get-Process -Name 'Widgets', 'WidgetBoard' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Installed $($package.Name)."
Write-Host "Open Widgets (Win+W), click '+', choose 'Memory Monitor', and pin it."
