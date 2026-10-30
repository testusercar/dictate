<#
.SYNOPSIS
  Installs the Copilot Key Launcher (package FlowKey) MSIX from a CI build artifact.
.DESCRIPTION
  Run from the unzipped artifact folder in an elevated PowerShell (needed to trust the signing
  certificate). Each CI run signs with a fresh throwaway certificate, so re-run this script for
  every new build. Removes Dictate and the old key test app so only Flow Key offers the Copilot key.
#>
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$cer = Get-ChildItem -Recurse -Filter 'Dictate-Dev.cer' | Select-Object -First 1
$msix = Get-ChildItem -Recurse -Filter "FlowKey_*_$arch.msix" | Select-Object -First 1
if (-not $cer -or -not $msix) { throw "Could not find Dictate-Dev.cer and a FlowKey $arch .msix under $PSScriptRoot" }

Write-Host "Trusting $($cer.Name) for package signing (LocalMachine\TrustedPeople)"
Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null

$deps = Get-ChildItem -Recurse -Path $msix.DirectoryName -Filter '*.appx' |
    Where-Object { $_.FullName -match "\\Dependencies\\$arch\\" }
foreach ($d in $deps) {
    Write-Host "Installing dependency $($d.Name)"
    Add-AppxPackage -Path $d.FullName -ErrorAction SilentlyContinue
}

# Other Copilot key providers from this repo; only Flow Key should be offered.
foreach ($name in 'Dictate', 'Dictate.KeySpike') {
    Get-AppxPackage -Name $name | ForEach-Object {
        Write-Host "Removing $name"
        Get-Process -Name ($name -replace '\..*$', '') -ErrorAction SilentlyContinue | Stop-Process -Force
        Remove-AppxPackage -Package $_.PackageFullName
    }
}

# A running Flow Key blocks the update (0x80073D02).
Get-Process -Name FlowKey -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Closing the running Flow Key"
    Stop-Process -Id $_.Id -Force
}

Write-Host "Installing $($msix.Name)"
Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion

Write-Host ""
Write-Host "Done. Open Copilot Key Launcher from Start to choose the app the key opens, and make sure"
Write-Host "Settings > Personalization > Text input > Customize Copilot key is set to 'Copilot Key Launcher'."
