#Requires -Version 5.1
<#
.SYNOPSIS
Installs and launches the validation APK on a connected Galaxy S25 Ultra only.
.EXAMPLE
.\tools\AndroidFlat\Install-AndroidFlat.ps1
.EXAMPLE
.\tools\AndroidFlat\Install-AndroidFlat.ps1 -Device PHONE_SERIAL -Apk Build\android-flat-dev.apk
#>
[CmdletBinding()]
param(
    [string]$Apk,
    [string]$Device,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)+$')]
    [string]$Package = 'com.zantetsuken.androidflat',
    [string]$Adb = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!(Test-Path -LiteralPath $Adb -PathType Leaf)) {
    $command = Get-Command $Adb -CommandType Application -ErrorAction SilentlyContinue
    if (!$command) { throw "adb not found: $Adb. Pass -Adb with the Android SDK adb.exe path." }
    $Adb = $command.Source
}

function Test-S25UltraModel([string]$Model) {
    # adb devices -l replaces punctuation/spaces with underscores; getprop keeps the original model string.
    $compact = ($Model -replace '[\s_-]', '').ToUpperInvariant()
    return $compact -match '^SMS938[A-Z0-9]*$|^(SAMSUNG)?GALAXYS25ULTRA$'
}

$listing = @(& $Adb devices -l)
if ($LASTEXITCODE -ne 0) { throw 'adb devices -l failed. Nothing was installed or launched.' }
foreach ($line in $listing) { Write-Host $line }
$connected = @(
    foreach ($line in $listing) {
        if ($line -match '^\s*(\S+)\s+(device|offline|unauthorized|recovery|sideload|bootloader)\b') {
            $serial = $Matches[1]
            $state = $Matches[2]
            $model = if ($line -match '\bmodel:(\S+)') { $Matches[1] } else { '' }
            [PSCustomObject]@{ Serial = $serial; State = $state; Model = $model }
        }
    }
)
if ($Device) {
    $selected = @($connected | Where-Object { $_.Serial -eq $Device })
    if ($selected.Count -ne 1 -or $selected[0].State -ne 'device') {
        throw "Device '$Device' is not connected and authorized. Nothing was installed or launched."
    }
    if (!(Test-S25UltraModel $selected[0].Model)) {
        throw "Device '$Device' reports model '$($selected[0].Model)', not a Galaxy S25 Ultra. Nothing was installed or launched."
    }
} else {
    $selected = @($connected | Where-Object { $_.State -eq 'device' -and (Test-S25UltraModel $_.Model) })
    if ($selected.Count -eq 0) {
        throw 'Galaxy S25 Ultra is not connected and authorized. Connect an SM-S938* phone with USB debugging enabled. Nothing was installed or launched on any connected device.'
    }
    if ($selected.Count -gt 1) {
        throw "Multiple Galaxy S25 Ultra devices are connected. Choose one with -Device SERIAL: $($selected.Serial -join ', '). Nothing was installed or launched."
    }
}
$serial = $selected[0].Serial
# Query only the matching phone. A connected Quest never receives an install, launch or getprop command.
$modelLines = @(& $Adb -s $serial shell getprop ro.product.model)
if ($LASTEXITCODE -ne 0) { throw "Could not verify model of '$serial'. Nothing was installed or launched." }
$model = ($modelLines -join '').Trim()
if (!(Test-S25UltraModel $model)) {
    throw "Device '$serial' reports model '$model', not a Galaxy S25 Ultra. Nothing was installed or launched."
}
if (!$Apk) { $Apk = Join-Path $project 'Build\android-flat.apk' }
$Apk = [IO.Path]::GetFullPath($Apk)
if (!(Test-Path -LiteralPath $Apk -PathType Leaf)) { throw "APK not found: $Apk" }

Write-Host "Installing $Apk on $serial ($model)"
& $Adb -s $serial install -r $Apk
if ($LASTEXITCODE -ne 0) { throw "APK installation failed on '$serial'." }
$resolved = @(& $Adb -s $serial shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER $Package)
if ($LASTEXITCODE -ne 0) { throw "Installed APK, but could not resolve the launcher for '$Package'." }
$activity = @($resolved | ForEach-Object { $_.Trim() } | Where-Object {
    $_ -match '^[A-Za-z0-9_.$]+/[A-Za-z0-9_.$]+$' -and $_.StartsWith($Package + '/')
}) | Select-Object -Last 1
if (!$activity) { throw "Installed APK, but no launcher activity was found for '$Package': $($resolved -join ' ')" }
$launched = @(& $Adb -s $serial shell am start -W -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -n $activity)
$launchExitCode = $LASTEXITCODE
foreach ($line in $launched) { Write-Host $line }
if ($launchExitCode -ne 0 -or ($launched | Where-Object { $_ -match '^\s*(Error\b|Exception\b)' })) {
    throw "Installed APK, but launching '$activity' failed on '$serial'."
}
Write-Output "Android phone launched: $Package on $serial ($model)."
