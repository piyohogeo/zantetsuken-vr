param(
    [string]$Player = "Builds/BuildingAnchorScenario/Player/CutWorldSandbox.exe",
    [string]$Output = ("Library/BuildingAnchorScenario/run-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$playerPath = [IO.Path]::GetFullPath((Join-Path $repo $Player))
$outputPath = [IO.Path]::GetFullPath((Join-Path $repo $Output))
if (!(Test-Path -LiteralPath $playerPath)) { throw "Build the scenario Player first: $playerPath" }
if (Test-Path -LiteralPath $outputPath) { throw "Use a new output directory: $outputPath" }
New-Item -ItemType Directory -Path $outputPath | Out-Null
$arguments = '-force-d3d11 -screen-fullscreen 0 -screen-width 1600 -screen-height 900 -buildingAnchorOut "{0}" -logFile "{0}/player.log"' -f $outputPath
$process = Start-Process -FilePath $playerPath -ArgumentList $arguments -WorkingDirectory $repo -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(120000)) {
    Stop-Process -Id $process.Id
    throw "Scenario exceeded 120 seconds; output retained: $outputPath"
}
$process.Refresh()
$result = Join-Path $outputPath "scenario.txt"
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or
    !(Select-String -LiteralPath $result -Pattern 'FINISHED code=0 completed=3 released=True' -Quiet)) {
    throw "Scenario failed (process $($process.ExitCode)); inspect $outputPath"
}
Write-Output "PASS: $outputPath"
