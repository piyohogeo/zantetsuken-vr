#Requires -Version 5.1
[CmdletBinding()]
param([string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor',
      [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../Assets/Zantetsu/Runtime/Rendering/Plugins/Android/arm64-v8a'))
$ErrorActionPreference = 'Stop'
$ndk = Join-Path $UnityEditor 'Data/PlaybackEngines/AndroidPlayer/NDK'
$clang = Join-Path $ndk 'toolchains/llvm/prebuilt/windows-x86_64/bin/clang++.exe'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
& $clang --target=aarch64-linux-android32 -std=c++17 -shared -fPIC -O2 -Wall -Wextra -Werror -Wno-missing-field-initializers -fvisibility=hidden -static-libstdc++ `
    '-Wl,-z,max-page-size=16384' '-Wl,--no-undefined' "-I$(Join-Path $UnityEditor 'Data/PluginAPI')" `
    (Join-Path $PSScriptRoot 'ZantetsuVpVulkan.cpp') -lvulkan -llog -o (Join-Path $OutputDirectory 'libZantetsuVpVulkan.so')
if ($LASTEXITCODE -ne 0) { throw 'Vulkan native plugin build failed' }
Write-Output (Join-Path $OutputDirectory 'libZantetsuVpVulkan.so')
