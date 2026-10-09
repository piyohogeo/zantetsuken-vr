# Quest 3S AppSW: head tracking and distant edge jitter

Date: 2026-10-09 (JST). Branch: `quest3s-appsw`.

## Finding

The user observed distant edge jitter with head movement after the Clip route split.
Disabling AppSW removed it. The pre-split APK also appeared smooth with AppSW requested on,
but its compositor log was predominantly `ASW=0`. This did not compare both versions with
SpaceWarp synthesis active. The initial claim of a demonstrated Clip-split regression is withdrawn.

Keeping the current split, NoClip shaders and constant sharing enabled, limiting the scene to
200 placed objects produces 45 application fps and about 90 compositor fps with no stale frames
in the sampled interval. The user confirmed that the edge jitter disappeared.

The measurements support missed frame deadlines as a contributing cause. They do not establish
why the compositor generated almost no SpaceWarp frames in the older, slower version, or a precise
automatic disable threshold. No shader arithmetic or native synchronization repair was made.
The complete 885-object scene still needs performance work to meet 45 fps.

## Measurement

Quest 3S / Adreno 740, Unity 6000.3.22f1, Vulkan SinglePassMultiview, physical display 90 Hz,
head tracking, MSAA 4x, unchanged rendering resolution, no direct Clip probe.
Rows report medians from the final 30 one-second VrApi samples in each saved log.
Head poses differ between runs: this checks cadence and failure conditions, not matched-pose performance.

| Version / scene | Placed objects | Application fps | ASW fps | Stale frames/s | App GPU ms | User observation |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Before Clip split (`0484b5d9`) | 885 | 27 | 0 | 38 | 34.61 | No jitter |
| After Clip split (`9effd04f`) | 885 | 30 | 61 | 32 | 30.60 | Jitter |
| After split + diagnostics | 200 | 45 | 90 | 0 | 14.24 | No jitter |

ASW ranges: before 0-8; after complete scene 44-84; reduced scene 90-91.
Full scene has 1,061 renderer targets / 1,602 indirect commands; the subset has 200 targets / 382 commands.
A placed object may contain multiple renderers.

Meta defines FPS as application render fps / display Hz, ASW as compositor fps including SpaceWarp,
and Stale as reuse of a prior frame because the new frame missed its deadline.
See [Meta logcat statistic definitions](https://developers.meta.com/vr/documentation/spatial-sdk/ts-logcat-stats/).
`AppSWConfigApplied=True` only means Unity accepted the requested setting. Verify actual synthesis
and cadence in the compositor log.

## Diagnostic controls

`Run-StaticDemo.ps1 -SceneObjectLimit 200` keeps whole placed objects from the runner's existing
sorted instance list. It hides unused renderers including shadows and excludes them from VP
inventory and registration. Transforms, physics, view, resolution and MSAA stay unchanged.
`0` is unlimited and remains the default. `-KeepOpen` retains the selected subset after measurement.
Cleanup restores only renderer states changed by the limit.

Independent comparisons: `-LegacyClipColour`, `-LegacyClipMotion`, `-LegacyClipShadows`,
`-NoConstantSharing`. Production features remain enabled by default. These switches do not
combine rare Clip objects back into ordinary indirect draws. Additional shader comparisons were
not run after load reduction removed the symptom with NoClip active.

Current device: vp3c-native, head tracking, AppSW requested on, SceneObjectLimit=200,
MSAA 4x, colour preparation and all NoClip / sharing defaults enabled.

## Evidence and verification

Local ignored logs:

- `Logs/quest-appsw-reference-head.log`: pre-split, PID 20192.
- `Logs/quest-clip-restored-head.log`: post-split full scene, PID 18118.
- `Logs/quest-appsw-jitter-200-noclip.log`: reduced scene, PID 21268.
- `Logs/quest-appsw-jitter-comparison.json`: sampled windows and cadence.
- `Logs/quest-appsw-jitter-editmode.xml`: 25/25 SceneDrawCompareTests passed, including four
  cases for object selection, renderer hiding/restoration, and unchanged unlimited behavior.
- `Logs/quest-build.log`: Android development build succeeded, zero build errors.

APK: `Build/quest-appsw-jitter-diagnostics-dev.apk`.
Build GUID: `05ad18bae60840c1813e37cad7709345`.
SHA-256: `0C9842420F02455600EAFD48D2276D42A9BBA436CD3DD0E6D4C24F8C5C3CC30E`.

Read-only audits found matching CB layouts and binding registers, motion varyings and position
arithmetic, and preserved barriers for arguments, visible lists, instance data and GPU constants.
These audits do not measure live GPU matrix values.
