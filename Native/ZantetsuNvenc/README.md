# ZantetsuNvenc — NVENC SDK build contract and Unity plugin lifecycle

Two targets are built here.

`ZantetsuNvencSdkVersionContract` compiles one translation unit against the
NVIDIA Video Codec SDK header and asserts that the SDK is version 13.0. It
checks the header, its version, and the local toolchain, and does nothing else.

`ZantetsuNvenc.dll` binds to Unity's native plugin lifecycle and, while Unity's
current renderer is D3D11, holds a COM reference to the exact device Unity is
using. That is a check of the binding to Unity's lifecycle, not an NVENC
capability probe: it says the renderer was D3D11, that a device was returned,
and that this plugin holds a reference to it. It says nothing about the
adapter's vendor, NVENC support, or whether an encoder session would succeed.

Neither target opens an encoder session, calls an NVENC entry point, or links
an NVENC library.

## Prerequisites

Both are external and are obtained by you:

* **NVIDIA Video Codec SDK 13.0** — obtain it from NVIDIA's official
  distribution and agree to the license that applies to it. This repository
  redistributes no SDK header, source, library, or DLL.
* **Unity 6000.3.22f1** — its `Editor/Data/PluginAPI` headers are used under the
  license Unity ships with them. This repository redistributes none of them
  either.

At run time, `nvEncodeAPI64.dll` is provided by the NVIDIA driver installed on
the machine. It is never bundled with, copied by, or produced from this build.

## Build

Pass your own SDK root, your own Unity installation root, and a build directory
outside this repository:

```powershell
.\Tools\Build-NvencNative.ps1 `
  -SdkRoot '<path to your extracted Video Codec SDK 13.0>' `
  -UnityEditorRoot '<path to your Unity 6000.3.22f1 installation>' `
  -BuildDirectory '<path to a build directory outside this repository>'
```

The SDK root is the directory that contains `Interface\nvEncodeAPI.h`; the Unity
root is the one that contains `Editor\Data\PluginAPI`. All three arguments are
required: nothing is downloaded, searched for, read from Unity Hub or an
environment variable, or guessed, and no build directory is created inside the
repository.

The built DLL stays in that build directory. It is not placed in a Unity
project, and no managed API calls into it yet.
