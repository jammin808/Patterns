<#
.SYNOPSIS
  Prepares a Windows machine for a show run by Patterns: no Windows Update restart during the
  show, updates paused, toasts off machine-wide, the error-reporting dialog off.
.DESCRIPTION
  Run once, as an administrator, before the tour. Everything here is a policy a user cannot set
  from inside Patterns; everything the show lock does by itself (notifications, sounds, other
  apps' audio, the shortcut keys, sleep, the Windows key) needs nothing from this script.
  -Undo puts the policies back to "not configured".
.PARAMETER PauseDays
  How many days to pause Windows Update from today (1-35). Default 35.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\show-machine.ps1
  powershell -ExecutionPolicy Bypass -File tools\show-machine.ps1 -PauseDays 10
  powershell -ExecutionPolicy Bypass -File tools\show-machine.ps1 -Undo
#>
param(
    [int]$PauseDays = 35,
    [switch]$Undo
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Run this as an administrator (right-click PowerShell, 'Run as administrator')." -ForegroundColor Yellow
    exit 1
}

$au = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU'
$wu = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate'
$ux = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
$toast = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Explorer'
$wer = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting'

function Set-Dword($path, $name, $value) {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    New-ItemProperty -Path $path -Name $name -Value $value -PropertyType DWord -Force | Out-Null
    Write-Host "  $path\$name = $value"
}
function Set-Text($path, $name, $value) {
    if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    New-ItemProperty -Path $path -Name $name -Value $value -PropertyType String -Force | Out-Null
    Write-Host "  $path\$name = $value"
}
function Remove-Value($path, $name) {
    if (Test-Path $path) { Remove-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue }
    Write-Host "  $path\$name removed"
}

if ($Undo) {
    Write-Host "Putting the show-machine policies back to 'not configured':"
    Remove-Value $au 'NoAutoRebootWithLoggedOnUsers'
    Remove-Value $au 'AUOptions'
    Remove-Value $wu 'SetActiveHours'
    Remove-Value $wu 'ActiveHoursStart'
    Remove-Value $wu 'ActiveHoursEnd'
    Remove-Value $ux 'PauseUpdatesExpiryTime'
    Remove-Value $ux 'PauseFeatureUpdatesStartTime'
    Remove-Value $ux 'PauseFeatureUpdatesEndTime'
    Remove-Value $ux 'PauseQualityUpdatesStartTime'
    Remove-Value $ux 'PauseQualityUpdatesEndTime'
    Remove-Value $toast 'NoToastApplicationNotification'
    Remove-Value $wer 'DontShowUI'
    Write-Host "Done. Windows Update behaves as before."
    exit 0
}

if ($PauseDays -lt 1 -or $PauseDays -gt 35) { Write-Host "PauseDays must be 1-35."; exit 1 }
$start = (Get-Date).ToUniversalTime()
$end = $start.AddDays($PauseDays)
$fmt = 'yyyy-MM-ddTHH:mm:ssZ'

Write-Host "Windows Update: no restart while a user is signed in, notify only, active hours 06:00-00:00, paused $PauseDays days:"
Set-Dword $au 'NoAutoRebootWithLoggedOnUsers' 1
Set-Dword $au 'AUOptions' 2
Set-Dword $wu 'SetActiveHours' 1
Set-Dword $wu 'ActiveHoursStart' 6
Set-Dword $wu 'ActiveHoursEnd' 0
Set-Text $ux 'PauseUpdatesExpiryTime' $end.ToString($fmt)
Set-Text $ux 'PauseFeatureUpdatesStartTime' $start.ToString($fmt)
Set-Text $ux 'PauseFeatureUpdatesEndTime' $end.ToString($fmt)
Set-Text $ux 'PauseQualityUpdatesStartTime' $start.ToString($fmt)
Set-Text $ux 'PauseQualityUpdatesEndTime' $end.ToString($fmt)

Write-Host "Toasts off for every user on this machine:"
Set-Dword $toast 'NoToastApplicationNotification' 1

Write-Host "The Windows Error Reporting dialog off:"
Set-Dword $wer 'DontShowUI' 1

Write-Host ""
Write-Host "Done. Pending restart right now: " -NoNewline
$pending = (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')
if ($pending) { Write-Host "YES - restart the machine now, before doors, so it is not waiting during the show." -ForegroundColor Yellow } else { Write-Host "no." }
Write-Host "Quit Teams and Outlook before doors, or set Teams to Do not disturb; the show lock mutes their audio either way."
