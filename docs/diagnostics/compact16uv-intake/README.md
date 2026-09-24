# Static16 offline intake checkpoint

This continues the CPU16 foundation at `c083815b` on `compact16uv-product-migration`. Original product main, benchmark working files and the Blender asset source are not modified.

Verified: **3/3 representative input contracts**, **3611/3611 EditMode**, **4/4 Development IL2CPP Player**, no failed/skipped/inconclusive test cases in the final runs. Runtime rendering code from the CPU16 checkpoint is unchanged.

## Implemented boundary

- `VpStatic16Writer` prepares a static BottomHalfUV Mesh once in the Editor. It refuses skin/blend-shape data, unsupported UV and invalid cut topology before emitting bytes. The caller supplies authoritative topology IDs and material indices; this writer does not weld positions or invent topology.
- `VpStatic16File.TryAppendCuttable` reads existing 16B fields directly into a temporary 16B managed array, then uses the product storage's existing registration gate and allocator. There is no runtime Mesh conversion, oct/UV encode or full float32 vertex copy. It does not retain the source stream or a second geometry cache. Registration still has file IO, temporary arrays, topology validation, index rebase and a copy into storage; this is **not** a zero-allocation/zero-copy registration claim.
- Mesh-local UInt32 indices in the file are rebased exactly once on placement into the existing global storage. A pool-dependent global index file is deliberately not introduced: allocations/reuse change the vertex base. Published CPU/GPU offsets and ownership remain unchanged.
- This is an intake API and representative integration gate, not an automatic Sandbox/scene replacement. Scene-specific lifetime/loading and transforms remain the caller's responsibility.

## Representative input evidence

`Prepare-Compact16uv-Intake.ps1` copies three current source `.blend` files and the palette into ignored `Assets/Licensed/Compact16uvIntake/`. It checks source hashes against the established U0 snapshot and copies its importer settings. It refuses to overwrite an existing private intake. The original files stay unchanged; no licensed model, texture, packed geometry or topology payload is committed publicly.

The Editor audit compares ordered position, normal, UV, index/submesh and skin/bind-pose hashes to U0. Only an exact import match permits reuse of the existing U3 topology map (benchmark provenance commit `bc22d00c4ecf80c48891d3dd68a502a82d2a80fc`). This is reuse of a pinned representative correspondence, not a general production exporter. New/changed assets require new authoring topology correspondence and cannot be authorized by matching vertex counts alone.

| Family | Vertices | Indices | Outcome |
|---|---:|---:|---|
| Casual `f_1` | 4,728 | 20,304 | Ordered input hashes/UV/texture pass; remains skinned, no frozen Static16 output |
| Professional `c_1` | 3,834 | 17,742 | Ordered input hashes/UV/texture pass; remains skinned, no frozen Static16 output |
| Megacity `bar_001` | 5,326 | 13,164 | Static16 file generated, registered through the actual product cut-input gate |

All three have exact BottomHalfUV centres `(i+0.5)/256`, `u=0..255`, `v=0..127`. Referenced palette SHA256 is `88fdb7640dcadbef46db9bb52a533180ae0ef2d5bec5bea599d024c3614ebcc5`; each imported texture is DXT1, sRGB, 9 mip levels, Bilinear, Repeat, anisotropy 1. These are source-palette observations, not finished cap-atlas validation.

The Megacity file is 159,240 bytes, including 85,216 bytes of 16B vertices, 52,656 bytes of indices, 21,304 bytes of topology IDs, a 28-byte header and a 12-byte submesh descriptor. SHA256: `4542dbe96f3938fff0f0fa58fc753cac70dc8cfc6b93883fbf0d0591f77ccb78`. This is a logical payload inventory, not resident-memory or frame-time measurement.

## File contract (version 1, little endian)

Header: `uint32 magic=0x36315056` (`VP16`), then `int32 version=1, stride=16, vertexCount, indexCount, topologyVertexCount, submeshCount`. Followed by:

1. `vertexCount` × `{float3 position, sbyte normalX, sbyte normalY, byte u, byte v}`.
2. `indexCount` × local `uint32` indices.
3. `vertexCount` × `int32` topology IDs.
4. `submeshCount` × `{int32 indexOffset, indexCount, materialIndex}`.

The stream is one geometry record, must be seekable, and stays caller-owned. Unsupported version/stride, wrong exact length, invalid references/attributes or insufficient storage are rejected without publishing geometry. Parser count checks occur before payload allocation. The existing storage remains the authority for cuttable geometry acceptance. This is a local trusted-asset format, not a network protocol or comprehensive hostile-file parser.

## Verification and failure history

- `intake-first`: Casual and Professional passed; Megacity was refused because the diagnostic initially used a different helper-exclusion rule from U0. No topology map was applied to the mismatched import.
- `intake-second`: the selection now uses the exact U0 prefixes (`WGT-`, `WGTS_rig`, `UCX_`, `PHYS_NULL`); all three ordered input contracts pass. Megacity passes the unmodified product topology gate and file registration. `intake-first.json` is retained alongside final `intake.json`.
- New EditMode cases cover the format length, raw packed round trip, nonzero base/local-index rebase, material/topology preservation, caller-owned streams, and refusals before storage use/output writes.
- The licensed Player test reads only the packed resource, unloads the TextAsset, then registers at nonzero base and runs three real Burst cuts/recuts plus GPU readback. If the licensed fixture has not been prepared it reports **Ignored**, not a pass. A passing run must report no ignored case.

`editmode-first` passed 3611/3611 (13 new cases), and `player-first` passed 4/4 on D3D11 / RTX 3090; both Editors exited with code 0. The Player includes the existing three rendering/storage controls plus the new licensed Megacity test. For the latter, cuts on X/Y/Z appended respectively 929/223/80 vertices, including 570/152/57 cap-slot vertices. All three executed Burst (`executedManaged=0`), had zero open contours, retained all previous input fields and matched every uploaded vertex field at nonzero GPU offset. This is byte/storage/cut evidence, **not a new rendered-image oracle of the Megacity asset**.

`player-images/` retains the existing synthetic forward/shadow control images; `08-lit-vp-both.png` was visually inspected. As at the CPU16 checkpoint, the explicit Lit receiver is the shadow control; the unsupported default receiver and 4x4 empty atlas readback do not establish a successful default material or atlas render. No actual cap-atlas or licensed Megacity appearance claim is inferred from these images.

After the Editors exited, build-generated shader-prefilter changes were restored and the newly auto-generated scene-template settings file was removed (Unity can regenerate it). Source `.blend` importer metadata still hashes identically to the copied settings for all three families. `summary.json` records source/evidence hashes and anonymous log paths; licensed input/packed bytes remain private. The successful test counts do not assert zero process leaks or product scene performance.

## Reproduction

1. In a fresh product worktree run `Tools/Prepare-Compact16uv-Intake.ps1` with the benchmark/pipeline roots. The licensed snapshot must be locally available.
2. Unity 6000.3.22f1: `-batchmode -projectPath <worktree> -executeMethod Zantetsu.MeshCut.ReferenceIntake.Compact16uvProductIntake.Run -quit -logFile <fresh.log>`.
3. EditMode: `-runTests -testPlatform EditMode -testResults <fresh.xml> -logFile <fresh.log>` (omit `-quit`).
4. Player: `-runTests -testPlatform StandaloneWindows64 -assemblyNames Zantetsu.Rendering.StandaloneTests -buildPlayerPath <fresh external directory> -force-d3d11 -testResults <fresh.xml> -logFile <fresh.log>`. Supply `VP3_SHADOW_DIAGNOSTICS=<fresh image directory>` for the existing shadow-control test.

## Remaining gates

Full Megacity intake needs authoritative topology for every selected mesh, not the three representative maps. Character self-skin/current-pose integration, finished normal/debug cap atlas, product scene wiring, residency/peak/Main measurements, and XR are still separate. No all-assets migration, atlas integration, scene performance gain or main merge is claimed here. Source palette and Static16 local offline preparation are now ready for the next atlas/material integration step; generation of distribution artifacts by the upstream Blender project has not been changed.

The following checkpoint implements the opt-in [shared palette atlas](../compact16uv-atlas/README.md); its verification and remaining scene/material gates are recorded separately.
