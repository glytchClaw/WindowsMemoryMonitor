# Builds and signs the Memory Monitor widget package (AppPackages\...\MemoryWidget_<version>_x64.msix).
$ErrorActionPreference = 'Stop'
$subject = 'CN=MemoryMonitorWidget'

# Self-signed signing certificate. The private key stays in your personal store and can't be exported;
# only the public part (MemoryMonitorWidget.cer) is written out, for Windows to trust.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter | Select-Object -Last 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'Memory Monitor Widget (local package signing)' `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(5) `
        -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
Export-Certificate -Cert $cert -FilePath (Join-Path $PSScriptRoot 'MemoryMonitorWidget.cer') | Out-Null

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -prerelease -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1

# MakePri and MakeAppx fail on paths over 260 characters, so build through the folder's 8.3 short path.
$root = (New-Object -ComObject Scripting.FileSystemObject).GetFolder($PSScriptRoot).ShortPath

& $msbuild "$root\MemoryWidget.csproj" /restore /nologo /v:minimal `
    /p:Configuration=Release /p:Platform=x64 `
    /p:AppxPackageSigningEnabled=true /p:PackageCertificateThumbprint=$($cert.Thumbprint)
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
