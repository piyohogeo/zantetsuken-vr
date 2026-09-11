# ZantetsuNvenc — NVENC SDK build contract

This target compiles one translation unit against the NVIDIA Video Codec SDK
header and asserts that the SDK is version 13.0. That is all it does: it checks
the header, its version, and the local toolchain. It implements no Unity plugin,
opens no encoder session, calls no NVENC entry point, and links no library.

## Prerequisite

The NVIDIA Video Codec SDK 13.0 is an external prerequisite. Obtain it yourself
from NVIDIA's official distribution and agree to the license that applies to it.
This repository redistributes no SDK header, source, library, or DLL, and none
of it is stored here.

At run time, `nvEncodeAPI64.dll` is provided by the NVIDIA driver installed on
the machine. It is never bundled with, copied by, or produced from this build.

## Build

Pass your own SDK root and a build directory outside this repository:

```powershell
.\Tools\Build-NvencNative.ps1 `
  -SdkRoot '<path to your extracted Video Codec SDK 13.0>' `
  -BuildDirectory '<path to a build directory outside this repository>'
```

The SDK root is the directory that contains `Interface\nvEncodeAPI.h`. Both
arguments are required: nothing is downloaded, searched for, read from an
environment variable, or guessed, and no build directory is created inside the
repository.
