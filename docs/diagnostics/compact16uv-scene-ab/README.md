# Product scene Legacy32 / Compact16uv diagnostic builds

## Scope and controls

Two diagnostic Players from the same source tree: `VP_DIAGNOSTIC_SCENE_AB` enables the fixture and counters in both; `VP_DIAGNOSTIC_LEGACY32` additionally selects the 32B CPU/GPU vertex in one build. The default product CPU layout remains 16B. The shared vertex HLSL and all eight readers have an explicit matching shader keyword variant; the diagnostic initializes that keyword before scene load and verifies CPU stride/keyword agreement. These comparison variants are not a new supported product format; release stripping/removal is a remaining merge concern.

The existing product sandbox box is subdivided into 64x64 quads per face: 25,350 render vertices, 147,456 indices, 24,578 shared topology vertices. Its authoritative convex/collider remains the same exact box, not a Megacity stand-in. Topology sharing uses exact power-of-two grid coordinates; normals are the six axis directions and surface UV is the constant BottomHalfUV centre `(32.5/256,32.5/256)`. This eliminates attribute quantization differences in the source fixture without introducing a Main repack. No static licensed source is substituted for animated/current-pose input.

Both builds clone the same private scene/profile, changing only vertex/index element capacities to 524,288 / 2,097,152. Other worker, dispatcher, draw, physics, stencil and shutdown settings remain unchanged. Zero separation impulse removes uncontrolled flight. The same parent cut at local y=0.137 and positive-child recut at local x=0.137 use the ordinary root/driver/Worker/Cook/DAG/commit/upload/camera path; no manual driver pumping. At each commit the diagnostic holds the child Rigidbodies kinematic and restores their known source pose; the first positive child is then lifted 1.1 m as in the existing walkthrough. This controls the settled pose; it is not a free-falling fragments benchmark. Before/first-cut/recut checkpoints hash committed float positions and the live fragments' published index sequences; counts and hashes must match across builds/runs.

This is a dense synthetic **whole-product-path** comparison, not a full licensed-asset scene, character workload, or production framerate prediction. Legacy32 uses float normal/UV interpolation; CPU16 uses packed persistent attributes. Geometric hashes are compared separately from appearance.

## Measurement

Main-thread bounded wall-time scopes cover driver Update (ask/admission/dispatch/collection), driver LateUpdate (advance/display settle), and camera prepare/render submission. Their sum is product-scope Main work, not total engine CPU, GPU execution, nor pure CPU cycles free of OS preemption/synchronous API stalls. No scope is nested in another measured scope. Instrumentation exists only in diagnostic builds. Scoped managed allocation is unavailable (`-1`): the installed IL2CPP per-thread GC API is unimplemented, so its historical zeros must not be interpreted as zero allocation. Unity's separate `GC Allocated In Frame` recorder, when valid, reports the previous whole frame, not allocation attributable to these scopes.

An end-of-frame recorder stores raw frame/phase/scopes, committed vertex count, drawn-frame count, Unity allocated/reserved bytes, current process working set and OS lifetime peak working set. Main Thread recorder data explicitly names the **previous frame**; it is inclusive of waits and must not be assigned to the current cut frame. Unity memory is sampled at frame boundaries, so its maximum can miss intra-frame peaks. OS working-set high-water includes startup and the engine, not just vertex resources. GraphicsBuffer capacities are logical byte counts, not GPU residency. The product pools use fixed capacities in this scenario; this does not test automatic growth.

Phases: 0 before-cut (60 warm + 240 settled frames), 1 cut requested, 2 first commit/interlude, 3 recut requested, 4 second commit/after-recut (60 warm + 240 settled). A commit may be observed in the coroutine after driver Update, so the commit frame belongs to phase 2/4. Include these transition frames in cut-window totals/peaks. Settled statistics use the last 240 frames of phase 0 and 4. Source/checkpoint hashing and recorder sampling are outside product scopes but can affect inclusive Main timing; they are not subtracted from memory.

All screenshots/readback/PNG creation are suppressed until after measurement stops and JSON is written. A final recut image and normal shutdown image are retained. Require nonblank recut image, consistent cross-layout geometry and appearance, drawn-frame progression, both commits, no geometry faults, ordinary drained/released/disposed shutdown, and exit 0. A mere process exit is insufficient.

## Reproduce

For each fresh external output path set `VP_SCENE_AB_BUILD=1`, `VP_SCENE_AB_LEGACY32=0` or `1`, and `VP_COMPACT16UV_PLAYER_OUT=<path>`. Invoke Unity `-batchmode -projectPath <worktree> -executeMethod Zantetsu.EditorTools.Sandbox.Compact16uvSandboxSceneBuild.BuildPlayer -logFile <fresh.log>`. For the 32B build only, temporarily create `Assets/csc.rsp` containing `-define:VP_DIAGNOSTIC_SCENE_AB,VP_DIAGNOSTIC_LEGACY32` before Editor startup so Editor and Player agree on the native struct fields. Remove this task-owned file and its generated meta after the 32B Editor exits, before building/testing the default 16B layout. Never overwrite an existing response file. The build also supplies explicit Player-only defines; the project scripting-define setting is not mutated. XR initialization is disabled only during the mono diagnostic build and restored.

Launch Players visibly, one at a time, `-force-d3d11 -screen-fullscreen 0 -screen-width 960 -screen-height 540 -zantetsuPlayerCheck <fresh-directory> -logFile <fresh.log>`. Leave `VP_VERTEX_UPLOAD_COMPARE` unset. Each compiled AB Player enables the measurement and offscreen mono target automatically. Use alternating layout order across independent runs; no Editor build/test should run during timed Players.

## Results (2026-09-24)

Six independent visible processes, order **16-1, 32-1, 32-2, 16-2, 16-3, 32-3**, using the final `Pinned` binary for each layout. All reached both commits, recorded 614 frames, drew every settled frame, reported zero geometry faults and completed drained/released/disposed shutdown with exit 0. All three corresponding final recut image pairs are **pixel-exact (960x540, 0 changed pixels)**. All runs have identical checkpoint position/index hashes: 25,350 -> 26,890 -> 28,235 committed vertices, and 147,456 -> 153,588 -> 158,940 live-fragment indices. The original authoritative box physics is still used.

Each settled window below uses the last 240 raw frames; values are medians of three process medians. Product scopes include Update + LateUpdate + camera submission, excluding engine physics/render-loop work outside those scopes.

| Metric | Legacy32 | Compact16uv |
| --- | ---: | ---: |
| Before cut, product-scope Main (us/frame) | 16.5 | 16.5 |
| After recut, product-scope Main (us/frame) | 21.2 | 21.1 |
| Before cut, inclusive Main Thread (ms/frame, waits included) | 0.3749 | 0.3874 |
| After recut, inclusive Main Thread (ms/frame, waits included) | 0.3908 | 0.3909 |
| First-cut window, product-scope sum (ms / 9 frames) | 4.699 | 3.206 |
| Recut window, product-scope sum (ms / 4 frames) | 0.577 | 0.599 |

**No stable end-to-end Main speedup is established.** Settled product scopes are essentially equal. First-cut totals have overlapping ranges: 32B 2.974–6.762 ms, 16B 2.772–3.555 ms, and only three independent runs per layout. Recut totals also overlap (32B 0.525–0.684 ms, 16B 0.471–0.689 ms). Do not turn the first-cut median difference into a general speedup ratio. Timings are on a non-exclusive host; the observed variation is not isolated to vertex format. Both first-cut and recut frame-window lengths were equal across all runs, but these small sample counts do not establish deadline guarantees.

| Memory measure | Legacy32 | Compact16uv |
| --- | ---: | ---: |
| CPU vertex allocation, capacity x ABI | 16 MiB | 8 MiB |
| Logical GPU vertex buffer capacity | 16 MiB | 8 MiB |
| Observed Unity allocated peak, median of process maxima | 142.280 MiB | 135.127 MiB |
| Same observed peak, range across processes | 142.112–145.261 MiB | 133.962–135.383 MiB |
| OS lifetime working-set peak, median of process maxima | 364.465 MiB | 357.609 MiB |
| Same OS peak, range across processes | 358.992–372.055 MiB | 349.895–358.434 MiB |

The 16B runs show a lower observed memory peak in this controlled scenario. The Unity allocated-peak median difference is **7.153 MiB**; OS working-set peak difference is **6.855 MiB**. Neither equals a general total-memory halving: allocator/engine/driver activity contributes, and OS high-water includes startup. No forced GC is used. Unity peak is a frame-boundary maximum, not a guarantee about intra-frame peaks, and no pool growth was exercised. GPU residency and reservation/growth/retirement worst cases remain unmeasured.

The valid Unity whole-frame GC counter recorded **93,188–93,676 bytes** over the 614-frame intervals. This includes diagnostic/engine work and is not a product-scope allocation count. The unsupported per-thread API is represented as `-1`, never allocation-free. Previous upload/appearance reports have explicit corrections; their raw historical zeros remain preserved.

Conclusion: the dense synthetic scene supports the 16B memory-saving direction without a measurable settled Main penalty here. Actual licensed asset physics, character current-pose/self-skin input, multi-target concurrency/deadlines, XR and release diagnostic-variant stripping/removal remain gates. Main remains unmerged. Raw per-frame samples, per-run p95/max/window sums, image comparisons, excluded attempts and source/binary hashes are in `summary.json` and the named run directories.

Regression after restoring normal 16B definitions: **EditMode 3,613/3,613; StandaloneRendering 20/20 passed**, zero failures/skips/inconclusive cases. The latter rechecks static intake/storage, four forward paths, cap atlas/stencil/shadow and real Megacity source/recut appearance. No new broad Physics PlayMode run was performed; the six ordinary scene runs cover this representative physics path. Temporary response-file definitions, generated pipeline-prefilter/preloaded-input changes and generated scene-template metadata were removed/restored after Editor exit. Original licensed input assets and original main remain untouched; derived private scene/profile assets stay ignored by Git.

## Failure / diagnostic history

`smoke16` passed geometry, rendering and lifecycle gates, but used the initially unvalidated per-thread GC counter. It is kept as setup evidence and excluded from the final performance comparison. `build32.log` failed Unity's Editor/Player script-layout validation when the layout symbol existed only in Player extra defines. The compiler response file above addresses the mismatch by compiling both sides with the same definition, without disabling the validation. The initial `build16.log` and failed `build32.log` are retained. No source assets use a serialized legacy VP struct; Static16 files retain their explicit 16B on-disk format and are decoded only for the diagnostic 32B intake.

`run32-1` matched all three geometry hashes against `smoke16`, but the images differed by 3,590 pixels: the recut grandchildren were dynamic and their gravity motion depended on elapsed wall time during the unlimited-fps settled window. It is excluded from final performance acceptance. The `Pinned` builds hold/restore the commit poses in both layouts; final evidence uses only `accepted16-*` / `accepted32-*`. The temporary compiler response file is not a permanent repository file.
