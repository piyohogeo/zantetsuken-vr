# Actual Megacity product scene — 2026-09-24

Continuation of `2db5cc06` in the dedicated migration worktree. The private scene uses **the actual 5,326-vertex / 13,164-index / three-submesh Static16 display and its same-source authored 24-vertex / 44-face convex**, not the synthetic box. The source, offline topology/frame binding and physics fixture SHA are documented in `../compact16uv-physics-intake/`.

## Configuration and scope

Set `VP_AUTHORED_MEGACITY_SCENE=1` and `VP_COMPACT16UV_PLAYER_OUT=<fresh external directory>` for `Compact16uvSandboxSceneBuild.BuildPlayer`. Combining this with the dense synthetic AB build is refused. It writes ignored `Assets/Licensed/Compact16uvIntake/SceneIntegration/Compact16uvMegacity.unity` and three private atlas-enabled white-tint materials. Original scene/materials/main stay unchanged. XR startup is disabled for the mono diagnostic build and restored afterwards.

The existing sandbox probe accepts an explicit, hash-pinned private physics/Static16 pair. It creates the authored B-rep and cooks its collider; storage receives the 16B file without a float32 vertex pool or upload-boundary repack. The fixture's two TextAssets remain referenced; temporary decoded physics arrays and registration arrays are not cached. This diagnostic is not an optimized shipping asset-loader or a claim that source bytes are absent from process memory.

Both geometry-local→owner-local and lineage→geometry mappings are identity. A deliberate unit-scale actor placement of **Euler (-90,0,0), position (0,0.15,0)** maps Mesh-local Z up to world Y up for both rendering and physics. This is not a reconstruction of the original prefab placement. The source actor has gravity disabled and uses the existing sandbox's arbitrary 12 kg mass / inertia settings; no realistic building dynamics claim is made. The 21 authored anchors are **not registered** in this run.

The walk calls only the ordinary ask/shutdown entrances and waits on normal frames. Root/driver/Worker/physics cook/DAG/commit/upload/camera run normally; no manual dispatcher or camera pumping. Planes in lineage/Mesh-local coordinates are **(0,0,1,-2.5)**, then positive-child **(1,0,0,0.01360642)**. These cut the building body, unlike the earlier high-plane component test.

For repeatable visual inspection, committed children are made kinematic and restored to the known source pose. The first positive child is lifted 3 m; after recut the positive grandchild moves an additional world +3 m X. These are diagnostic scene placements, not product physical response or user interaction. Parent/child transforms and resulting fragment visibility are still processed by the normal product path.

## Evidence

Run a visible 960×540 D3D11 Player with `VP_COMPACT16UV_SCENE_MEASURE=1` and `-zantetsuPlayerCheck <fresh evidence directory>`. Its camera renders ordinarily into the owned offscreen target. Seven captures show before-cut, first commit, separated children (normal/debug), recut (normal/debug), and drained shutdown. Real cap debug colour is green. No frame-exact provisional-cap image is claimed; the trace counts observed provisional-pair frames.

`accepted1..3` are three independent sequential processes of the final building-cut binary. `summary.json` validates commit/end markers, zero geometry faults, all draw windows, matching vertex counts, repeated pixel comparisons and EditMode XML, then pins source/binary/evidence hashes. Sanitized `trace.txt` files retain the diagnostic event lines. Full Editor/Player logs remain local and ignored.

All **3/3 processes exited 0**, observed a provisional pair, reached both geometry commits, and reported released/drained world, disposed display and unbound atlas at shutdown. Storage vertices are **5,326 → 6,078 → 6,974**. Each settled sample window has 240 actual camera draws. All seven corresponding captures are **pixel-exact** across the three processes (14 comparisons, zero changed pixels). Visual inspection confirms the horizontal building-body cap, vertical recut cap, inherited surfaces, and floor-only output after shutdown. This is a visual/integration check, not an independent geometrical oracle. World release flags do not constitute a whole-process zero-leak measurement.

Representative captures: [normal recut](accepted1/04-after-the-child-cut.png), [debug recut](accepted1/04b-after-recut-debug.png), [after shutdown](accepted1/05-after-the-ending.png).

Regression: **EditMode 3,614/3,614 passed**, no failures/skips/inconclusive cases. Build-time pipeline prefilter changes and generated scene-template settings were removed/restored after Editor exit; XR settings and original scenes/materials have no diff. The fixture's cooked mesh and native input arrays retain the existing probe ownership through the walk and are disposed on probe destruction; the world's own shutdown state is observed separately.

The inclusive Main Thread and settled Unity/process memory samples are retained for context only. They include diagnostic capture/engine effects and are **not Legacy32 comparisons, product-only Main scopes, GPU residency, peaks, growth/retirement stress, or managed-allocation measurements**. Persistent source TextAssets are included in this scene. Do not compare these values to a different-capacity synthetic scene as an optimization result.

`run1` is the earlier successful smoke binary with plane Z=7.608755 and 1.1 m separation. It reached both commits and shutdown but mostly cut the sign's narrow support; it was not accepted as a visually useful building-body cut and is excluded from final repeated-image/performance evidence. Its captures are retained to distinguish the input change from a product failure.

## Remaining work

This gate covers one static real asset and two sequential cuts. Anchor support/grounding, character current-pose/self-skin input, multiple simultaneous targets and deadlines, XR, diagnostic-variant stripping/removal, and a matched real-scene Legacy32/16B performance comparison remain separate. Main is not merged. No new broad StandaloneRendering suite is claimed here; the actual IL2CPP scene runs exercise the changed sandbox/build paths, alongside full EditMode regression.
