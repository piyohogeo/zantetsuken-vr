#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Phase 0.11 native targets against an externally provided NVIDIA
    Video Codec SDK 13.0 and Unity installation.

.DESCRIPTION
    Configures and builds Native/ZantetsuNvenc as x64 Release: the SDK version
    contract, which compiles one translation unit against the SDK header and
    asserts its version, ZantetsuNvenc.dll, which binds to Unity's plugin
    lifecycle and holds the D3D11 device Unity is currently using, and the
    device ownership contract test, which is then run - its exit code is this
    script's. The NVENC driver API contract test is built too, but it loads the
    driver from System32 and so is run only with -RunDriverApiContractTest: an
    ordinary build needs no NVIDIA hardware or driver. Neither
    links an NVENC library or names an NVENC entry point, and the DLL is left
    in the build directory: nothing is copied into this repository or into a
    Unity project.

    The fixed RGBA to NV12 shaders are compiled at build time by the fxc.exe
    named with -Fxc, into byte code headers under the build directory. Nothing
    is compiled at run time and no d3dcompiler dependency is added.

    The SDK, the Unity installation, and the Windows SDK that provides fxc.exe
    are external prerequisites and are never redistributed by this repository.
    The SDK root, the Unity root, the build directory, and the shader compiler
    are required arguments: this script downloads nothing, searches for no SDK,
    Unity installation, or shader compiler, reads no Unity Hub setting,
    registry key, or environment variable, guesses no default path, and refuses
    a build directory inside the repository.

    CMake comes from PATH unless -CMake names another one. Targeting a Visual
    Studio generator with -A x64 needs a CMake that knows the installed Visual
    Studio; the one bundled with Visual Studio is a usual choice when the CMake
    on PATH is older.

    Exit codes:
      0 - configure and both builds succeeded
      1 - a required input was missing or rejected
      CMake's own exit code when configure or build fails.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SdkRoot,

    [Parameter(Mandatory = $true)]
    [string]$UnityEditorRoot,

    [Parameter(Mandatory = $true)]
    [string]$BuildDirectory,

    # Full path to the fxc.exe of your own Windows SDK, which compiles the
    # fixed RGBA to NV12 shaders at build time. Required: no shader compiler is
    # searched for on PATH, read from the registry, or guessed, and none is
    # used at run time.
    [Parameter(Mandatory = $true)]
    [string]$Fxc,

    [string]$CMake = 'cmake',

    # Runs the contract tests that need the real driver after the build: the
    # driver API loading boundary, which loads nvEncodeAPI64.dll from System32,
    # and the retained session's capability observation, which opens a session
    # on the default hardware adapter. Both need an NVIDIA driver on this
    # machine and are never run unless they are asked for.
    [switch]$RunDriverApiContractTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

function Fail {
    param([string]$Message)
    [Console]::Error.WriteLine("ERROR: $Message")
    exit 1
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeProject = Join-Path $repoRoot 'Native\ZantetsuNvenc'

if (-not (Test-Path -LiteralPath (Join-Path $nativeProject 'CMakeLists.txt'))) {
    Fail "The native project was not found at '$nativeProject'."
}

# --- 1. The SDK root and the exact header it must contain ---

if ([string]::IsNullOrWhiteSpace($SdkRoot)) {
    Fail 'SdkRoot must not be empty.'
}

if (-not (Test-Path -LiteralPath $SdkRoot -PathType Container)) {
    Fail "The NVIDIA Video Codec SDK root '$SdkRoot' does not exist. Provide the root of your own SDK 13.0 installation; this repository ships no SDK."
}

$sdkRootFull = (Resolve-Path -LiteralPath $SdkRoot).Path
$sdkHeader = Join-Path $sdkRootFull 'Interface\nvEncodeAPI.h'

if (-not (Test-Path -LiteralPath $sdkHeader -PathType Leaf)) {
    Fail "nvEncodeAPI.h was not found at '$sdkHeader'. SdkRoot must point at the root of an extracted NVIDIA Video Codec SDK 13.0."
}

# --- 2. The Unity installation and the plugin headers it must contain ---

if ([string]::IsNullOrWhiteSpace($UnityEditorRoot)) {
    Fail 'UnityEditorRoot must not be empty.'
}

if (-not (Test-Path -LiteralPath $UnityEditorRoot -PathType Container)) {
    Fail "The Unity installation root '$UnityEditorRoot' does not exist. Provide the root of your own Unity installation; this repository ships no Unity headers."
}

$unityRootFull = (Resolve-Path -LiteralPath $UnityEditorRoot).Path
$unityPluginApi = Join-Path $unityRootFull 'Editor\Data\PluginAPI'

foreach ($header in @('IUnityInterface.h', 'IUnityGraphics.h', 'IUnityGraphicsD3D11.h')) {
    $headerPath = Join-Path $unityPluginApi $header
    if (-not (Test-Path -LiteralPath $headerPath -PathType Leaf)) {
        Fail "$header was not found at '$headerPath'. UnityEditorRoot must point at a Unity installation root, which contains Editor\Data\PluginAPI."
    }
}

# --- 3. The build directory, resolved to an absolute path ---

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    Fail 'BuildDirectory must not be empty.'
}

try {
    $buildFull = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine((Get-Location).Path, $BuildDirectory))
}
catch {
    Fail "BuildDirectory '$BuildDirectory' is not a usable path: $($_.Exception.Message)"
}

# --- 4. Never build inside the repository ---

$repoFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
$buildCompare = $buildFull.TrimEnd('\')

if ($buildCompare -eq $repoFull -or
    $buildCompare.StartsWith($repoFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail "BuildDirectory '$buildFull' is inside the repository '$repoFull'. Build output and the CMake cache must stay outside it."
}

# --- 5. The shader compiler, named and never searched for ---
#
# Checked here, before the build directory is created, so a missing or wrong
# path costs nothing on disk.

if ([string]::IsNullOrWhiteSpace($Fxc)) {
    Fail 'Fxc must not be empty.'
}

if (-not (Test-Path -LiteralPath $Fxc -PathType Leaf)) {
    Fail "The shader compiler '$Fxc' does not exist. Pass the full path to the fxc.exe of your own Windows SDK; this repository ships no shader compiler and searches for none."
}

$fxcFull = (Resolve-Path -LiteralPath $Fxc).Path

if ([System.IO.Path]::GetExtension($fxcFull).ToLowerInvariant() -ne '.exe') {
    Fail "The shader compiler '$fxcFull' is not an .exe. Pass the full path to fxc.exe."
}

$cmakeCommand = Get-Command $CMake -ErrorAction SilentlyContinue
if (-not $cmakeCommand) {
    Fail "cmake was not found: '$CMake' is neither on PATH nor an existing executable."
}

if (-not (Test-Path -LiteralPath $buildFull)) {
    New-Item -ItemType Directory -Path $buildFull -Force | Out-Null
}

Write-Host "NVENC SDK build contract"
Write-Host "  Native project  : $nativeProject"
Write-Host "  SDK header      : $sdkHeader"
Write-Host "  Unity PluginAPI : $unityPluginApi"
Write-Host "  Build directory : $buildFull"
Write-Host ("  CMake           : " + $cmakeCommand.Source)
Write-Host "  fxc             : $fxcFull"

# --- 6. Configure: x64, Release ---

# The Visual Studio generator is multi-config, so Release is chosen by the
# build step below rather than at configure time.
& $cmakeCommand.Source -S $nativeProject -B $buildFull -A x64 `
    "-DZANTETSU_NVENC_SDK_ROOT=$sdkRootFull" `
    "-DZANTETSU_UNITY_EDITOR_ROOT=$unityRootFull" `
    "-DZANTETSU_FXC=$fxcFull"

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('ERROR: CMake configure failed.')
    exit $LASTEXITCODE
}

# --- 7. Build every target ---

& $cmakeCommand.Source --build $buildFull --config Release --target ZantetsuNvencSdkVersionContract

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('ERROR: The NVENC SDK version contract failed to build.')
    exit $LASTEXITCODE
}

& $cmakeCommand.Source --build $buildFull --config Release --target ZantetsuNvenc

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('ERROR: The Unity plugin failed to build.')
    exit $LASTEXITCODE
}

& $cmakeCommand.Source --build $buildFull --config Release `
    --target ZantetsuNvencD3D11DeviceBindingContractTest

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('ERROR: The device ownership contract test failed to build.')
    exit $LASTEXITCODE
}

# --- 8. Run the native contract test ---

$contractTest = Join-Path $buildFull 'Release\ZantetsuNvencD3D11DeviceBindingContractTest.exe'
if (-not (Test-Path -LiteralPath $contractTest -PathType Leaf)) {
    Fail "The device ownership contract test was not found at '$contractTest'."
}

& $contractTest

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('ERROR: The device ownership contract test failed.')
    exit $LASTEXITCODE
}

& $cmakeCommand.Source --build $buildFull --config Release `
    --target ZantetsuNvencDriverApiContractTest

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('ERROR: The driver API contract test failed to build.')
    exit $LASTEXITCODE
}

& $cmakeCommand.Source --build $buildFull --config Release `
    --target ZantetsuNvencSessionCapabilityContractTest

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine(
        'ERROR: The session capability contract test failed to build.')
    exit $LASTEXITCODE
}

# --- 9. Run the driver API contract test, only when asked ---

if ($RunDriverApiContractTest) {
    $driverApiTest = Join-Path $buildFull 'Release\ZantetsuNvencDriverApiContractTest.exe'
    if (-not (Test-Path -LiteralPath $driverApiTest -PathType Leaf)) {
        Fail "The driver API contract test was not found at '$driverApiTest'."
    }

    & $driverApiTest

    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine('ERROR: The driver API contract test failed.')
        exit $LASTEXITCODE
    }

    $sessionCapabilityTest =
        Join-Path $buildFull 'Release\ZantetsuNvencSessionCapabilityContractTest.exe'
    if (-not (Test-Path -LiteralPath $sessionCapabilityTest -PathType Leaf)) {
        Fail "The session capability contract test was not found at '$sessionCapabilityTest'."
    }

    & $sessionCapabilityTest

    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine('ERROR: The session capability contract test failed.')
        exit $LASTEXITCODE
    }
}

Write-Host 'NVENC native build: PASSED'
exit 0
