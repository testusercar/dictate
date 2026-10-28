<#
.SYNOPSIS
  Installs the Dictate key-test MSIX from a CI build artifact.
.DESCRIPTION
  Run from the unzipped artifact folder in an elevated PowerShell (needed once to trust the
  signing certificate). Each CI run signs with a fresh throwaway certificate, so re-run this
  script for every new build.
#>
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$cer = Get-ChildItem -Recurse -Filter 'Dictate-Dev.cer' | Select-Object -First 1
$msix = Get-ChildItem -Recurse -Filter "*_$arch.msix" | Select-Object -First 1
if (-not $cer -or -not $msix) { throw "Could not find Dictate-Dev.cer and a $arch .msix under $PSScriptRoot" }

Write-Host "Trusting $($cer.Name) for package signing (LocalMachine\TrustedPeople)"
Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

$deps = Get-ChildItem -Recurse -Path $msix.DirectoryName -Filter '*.appx' |
    Where-Object { $_.FullName -match "\\Dependencies\\$arch\\" }
foreach ($d in $deps) {
    Write-Host "Installing dependency $($d.Name)"
    Add-AppxPackage -Path $d.FullName -ErrorAction SilentlyContinue
}

Write-Host "Installing $($msix.Name)"
Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion

Write-Host ""
Write-Host "Done. Next: Settings > Personalization > Text input > Customize Copilot key > choose 'Dictate'."
Write-Host "Then open 'Dictate (key test)' from Start and try tap, hold and double tap."
