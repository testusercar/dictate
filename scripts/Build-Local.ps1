<#
.SYNOPSIS
  Builds and installs the Dictate MSIX on this PC, signed with a local dev certificate.
.DESCRIPTION
  Needs Visual Studio 2022 (or Build Tools) with the "WinUI application development" workload
  and the .NET 9 SDK. Run elevated the first time so the certificate can be trusted.
#>
param([ValidateSet('x64', 'ARM64')] [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cer = Join-Path $root 'Dictate-Dev.cer'

# Reuse the dev certificate from earlier runs; MSBuild signs with it from the user store by thumbprint.
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=Dictate Dev' -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=Dictate Dev' -KeyUsage DigitalSignature `
        -FriendlyName 'Dictate Dev' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)")) {
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$out = Join-Path $root 'out\'
& $msbuild (Join-Path $root 'src\Dictate\Dictate.csproj') /restore /m `
    /p:Configuration=Release /p:Platform=$Platform /p:RuntimeIdentifier="win-$($Platform.ToLower())" `
    /p:GenerateAppxPackageOnBuild=true /p:AppxPackageDir=$out `
    /p:AppxPackageSigningEnabled=true /p:PackageCertificateThumbprint=$($cert.Thumbprint)
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$msix = Get-ChildItem -Recurse -Path $out -Filter "*_$($Platform.ToLower()).msix" | Sort-Object LastWriteTime | Select-Object -Last 1
Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion
Write-Host "Installed $($msix.Name)"
