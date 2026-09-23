# Compact16uv legacy shader usage audit

Source repository: `C:\Users\junic\src\zantetsuken-vr-compact16uv`
Pinned commit: `985fb3a4ddcfae2e9f020c16773c49f37d3ede3b`

| path | runtime draw/construct | serialized refs | enabled-scene refs | decision |
|---|---:|---:|---:|---|
| `VpUnlit.shader` | 3 callers | 7 | 7 | **MIGRATE-OR-REMOVE-ENABLED-SCENE** |
| `VpIndirectUnlit.shader` | 0 constructions | 0 | 0 | **DELETE-CANDIDATE** |
| `VpIndirectShadowCaster.shader` | 0 constructions | 0 | 0 | **DELETE-CANDIDATE** |

## Evidence

Enabled build scenes: `Assets/Scenes/Sandbox.unity`, `Assets/Scenes/CutWorldSandbox.unity`.

`VpDirectDraw.Render` runtime callers: `Assets/Zantetsu/Runtime/Rendering/VpMeshDisplay.cs`, `Assets/Zantetsu/Runtime/Rendering/VpMultiMeshDisplay.cs`, `Assets/Zantetsu/Runtime/Rendering/VpSharedMeshDisplay.cs`.

`VpUnlit.shader` GUID appears 7 times, all in enabled scenes; the referenced paths are in `audit.json`.

No tracked runtime code constructs `VpIndirectDrawBatch`, and neither old indirect shader GUID is serialized outside its `.meta`. Tests still exercise the Stage 2 API, so deletion must include the class/tests and pass source compile/test verification.

## Recommendation

Migrate to Compact16uv while Sandbox remains an enabled build scene, or remove the seven serialized direct-display components from that scene.

Prefer deletion with VpIndirectDrawBatch after source compile/test verification; the pinned tracked runtime has no construction site and no serialized shader GUID reference.

Scope limitation: tracked files at the pinned commit only; external packages, reflection and user-created scenes are not disproved.
