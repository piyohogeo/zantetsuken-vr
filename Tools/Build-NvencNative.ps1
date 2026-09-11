#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the Phase 0.11 NVENC SDK build contract against an externally
    provided NVIDIA Video Codec SDK 13.0.

.DESCRIPTION
    Configures and builds Native/ZantetsuNvenc as x64 Release. The target
    compiles one translation unit against the SDK header and asserts its
    version; it links nothing and produces no DLL, executable, or Unity plugin.

    The SDK is an external prerequisite and is never redistributed by this
    repository. Both the SDK root and the build directory are required
    arguments: this script downloads nothing, searches for no SDK, reads no
    environment variable, guesses no default path, and refuses a build
    directory inside the repository.

    CMake comes from PATH unless -CMake names another one. Targeting a Visual
    Studio generator with -A x64 needs a CMake that knows the installed Visual
    Studio; the one bundled with Visual Studio is a usual choice when the CMake
    on PATH is older.

    Exit codes:
      0 - configure and build succeeded
      1 - a required input was missing or rejected
      CMake's own exit code when configure or build fails.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SdkRoot,

    [Parameter(Mandatory = $true)]
    [string]$BuildDirectory,

    [string]$CMake = 'cmake'
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

# --- 2. The build directory, resolved to an absolute path ---

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

# --- 3. Never build inside the repository ---

$repoFull = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\')
$buildCompare = $buildFull.TrimEnd('\')

if ($buildCompare -eq $repoFull -or
    $buildCompare.StartsWith($repoFull + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail "BuildDirectory '$buildFull' is inside the repository '$repoFull'. Build output and the CMake cache must stay outside it."
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
Write-Host "  Build directory : $buildFull"
Write-Host ("  CMake           : " + $cmakeCommand.Source)

# --- 4. Configure: x64, Release ---

# The Visual Studio generator is multi-config, so Release is chosen by the
# build step below rather than at configure time.
& $cmakeCommand.Source -S $nativeProject -B $buildFull -A x64 `
    "-DZANTETSU_NVENC_SDK_ROOT=$sdkRootFull"

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('ERROR: CMake configure failed.')
    exit $LASTEXITCODE
}

# --- 5. Build the contract target ---

& $cmakeCommand.Source --build $buildFull --config Release --target ZantetsuNvencSdkVersionContract

if ($LASTEXITCODE -ne 0) {
    [Console]::Error.WriteLine('ERROR: The NVENC SDK version contract failed to build.')
    exit $LASTEXITCODE
}

Write-Host 'NVENC SDK build contract: PASSED'
exit 0
