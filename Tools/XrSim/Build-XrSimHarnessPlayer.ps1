#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Tools/XrSim harness Player (x64 IL2CPP Development, D3D11) outside the repository.

.DESCRIPTION
    Copies Tools/XrSim/Harness/XrSimHarness.cs and Harness/Editor/XrSimHarnessBuild.cs into
    Assets/_XrSimHarness/ for one batchmode Player build of Assets/Scenes/Sandbox.unity, then removes every
    temporary file it created (the sources, Resources materials and their .meta files, file by file) and
    writes back the byte copies of the tracked Assets/Settings, ProjectSettings and Packages files that the
    build may rewrite. The Git working tree (HEAD and full porcelain status, including untracked files) must
    be identical before and after, otherwise the script fails.

    Requires the licensed display meshes under Assets/Licensed/ (git-ignored) and no running Unity for the
    project. Writes player-manifest.json next to the Player.

    Exit codes: 0 built, 1 build failed, 2 infrastructure error (state not restored is reported loudly).

.PARAMETER PlayerDirectory
    Output directory for the Player; must be outside the repository.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PlayerDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'XrSim.psm1') -Force

$repo = Get-XrSimRepositoryRoot
$constants = Get-XrSimConstants
$PlayerDirectory = [IO.Path]::GetFullPath($PlayerDirectory)
if ($PlayerDirectory.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { Write-Error "PlayerDirectory must be outside the repository: $PlayerDirectory"; exit 2 }
if (-not (Test-Path -LiteralPath $constants.UnityEditorPath)) { Write-Error "Unity editor not found: $($constants.UnityEditorPath)"; exit 2 }
Assert-XrSimNoConflictingProcess

$tempRoot = Join-Path $repo 'Assets\_XrSimHarness'
if (Test-Path -LiteralPath $tempRoot) { Write-Error "temporary harness folder already exists: $tempRoot"; exit 2 }
$gitBefore = Get-XrSimGitState -RepositoryRoot $repo
Write-XrSimLog "git state before: HEAD $($gitBefore.Head.Substring(0, 7)), $($gitBefore.Lines) status lines"

$work = Join-Path $PlayerDirectory '.build'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$settingsBackup = Join-Path $work ("settings-{0:yyyyMMdd-HHmmss}" -f (Get-Date))
New-Item -ItemType Directory -Path $settingsBackup | Out-Null
$settingsFiles = @(& git -C $repo ls-files -- Assets/Settings ProjectSettings Packages)
foreach ($file in $settingsFiles) {
    $copy = Join-Path $settingsBackup $file
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $copy) | Out-Null
    [IO.File]::WriteAllBytes($copy, [IO.File]::ReadAllBytes((Join-Path $repo $file)))
}
Write-XrSimLog "tracked settings saved: $($settingsFiles.Count) files"

$created = @(
    'Assets\_XrSimHarness\XrSimHarness.cs', 'Assets\_XrSimHarness\XrSimHarness.cs.meta',
    'Assets\_XrSimHarness\Editor\XrSimHarnessBuild.cs', 'Assets\_XrSimHarness\Editor\XrSimHarnessBuild.cs.meta',
    'Assets\_XrSimHarness\Editor.meta', 'Assets\_XrSimHarness\Resources.meta', 'Assets\_XrSimHarness.meta'
)
foreach ($name in 'XrSimHarnessIndexed', 'XrSimHarnessIndexedShadow', 'XrSimHarnessCulled', 'XrSimHarnessCulledShadow') {
    $created += "Assets\_XrSimHarness\Resources\$name.mat"; $created += "Assets\_XrSimHarness\Resources\$name.mat.meta"
}

$exitCode = 2
$log = Join-Path $work ("build-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
try {
    New-Item -ItemType Directory -Force -Path (Join-Path $tempRoot 'Editor') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Harness\XrSimHarness.cs') -Destination (Join-Path $tempRoot 'XrSimHarness.cs')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Harness\Editor\XrSimHarnessBuild.cs') -Destination (Join-Path $tempRoot 'Editor\XrSimHarnessBuild.cs')
    $env:XRSIM_PLAYER_DIR = $PlayerDirectory
    Write-XrSimLog "building the harness Player into $PlayerDirectory (log $log)"
    $unity = Start-Process -FilePath $constants.UnityEditorPath -ArgumentList @('-batchmode', '-quit', '-projectPath', ('"' + $repo + '"'), '-executeMethod', 'XrSimHarnessBuild.Build', '-logFile', ('"' + $log + '"')) -PassThru
    if (-not $unity.WaitForExit(3600000)) { Stop-Process -Id $unity.Id; throw 'Unity build timed out' }
    Select-String -CaseSensitive -Path $log -Pattern 'XrSimHarnessBuild: |error CS|Scripts have compiler errors|Build Finished' | Select-Object -First 30 | ForEach-Object { $_.Line }
    $exitCode = if ($unity.ExitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $PlayerDirectory 'zantetsuken-vr.exe'))) { 0 } else { 1 }
}
finally {
    Remove-Item Env:\XRSIM_PLAYER_DIR -ErrorAction SilentlyContinue
    foreach ($relative in $created) {
        $path = Join-Path $repo $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
    }
    foreach ($dir in @((Join-Path $tempRoot 'Resources'), (Join-Path $tempRoot 'Editor'), $tempRoot)) {
        if ((Test-Path -LiteralPath $dir) -and @(Get-ChildItem -LiteralPath $dir -Force).Count -eq 0) { Remove-Item -LiteralPath $dir }
    }
    if (Test-Path -LiteralPath $tempRoot) { Write-XrSimLog "WARNING: $tempRoot still holds files it did not create: $((Get-ChildItem -LiteralPath $tempRoot -Recurse -Force | ForEach-Object Name) -join ', ')" }
    foreach ($file in $settingsFiles) {
        $path = Join-Path $repo $file
        $copy = Join-Path $settingsBackup $file
        if ((Get-FileHash -LiteralPath $path).Hash -ne (Get-FileHash -LiteralPath $copy).Hash) {
            [IO.File]::WriteAllBytes($path, [IO.File]::ReadAllBytes($copy))
            Write-XrSimLog "build side effect written back from the byte copy: $file"
        }
    }
}

try {
    Write-XrSimLog (Assert-XrSimGitStateUnchanged -RepositoryRoot $repo -Before $gitBefore -When 'after the build')
}
catch {
    Write-Error $_
    exit 2
}

if ($exitCode -eq 0) {
    $graphicsLine = (Select-String -CaseSensitive -Path $log -Pattern 'XrSimHarnessBuild: graphics APIs (.*)$' | Select-Object -First 1)
    $manifest = [ordered]@{
        built_at          = (Get-Date).ToString('o')
        git_head          = $gitBefore.Head
        unity_editor      = $constants.UnityEditorPath
        build_settings    = if ($graphicsLine) { $graphicsLine.Matches[0].Groups[1].Value } else { '' }
        harness_sha256    = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Harness\XrSimHarness.cs') -Algorithm SHA256).Hash
        build_sha256      = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Harness\Editor\XrSimHarnessBuild.cs') -Algorithm SHA256).Hash
        player_exe_sha256 = (Get-FileHash -LiteralPath (Join-Path $PlayerDirectory 'zantetsuken-vr.exe') -Algorithm SHA256).Hash
        game_assembly_sha256 = (Get-FileHash -LiteralPath (Join-Path $PlayerDirectory 'GameAssembly.dll') -Algorithm SHA256).Hash
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PlayerDirectory 'player-manifest.json') -Encoding UTF8
    Write-XrSimLog "harness Player built: $PlayerDirectory"
}
exit $exitCode
