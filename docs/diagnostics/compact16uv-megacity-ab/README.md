# Actual Megacity scene: Legacy32 / Compact16uv — 2026-09-24

Checkpoint after `47e8442c`, dedicated migration worktree only. This compares the real static Megacity product scene, not the earlier dense synthetic box. It retains the ordinary profile: **65,536 vertices / 262,144 indices**, three material slots, the same authored convex, atlas, camera, actor placement, two cuts and post-commit poses from `../compact16uv-megacity-scene/`. Anchors are disabled. No large capacity was introduced to amplify the saving.

## What is compared

Both Players start from the **same hash-pinned Static16 file** and physics fixture. The diagnostic Legacy32 loader decodes its oct/UV bytes once at registration into float normal/UV fields; it does **not** load the original unquantized float asset. The initial decoded attribute hash must match. Thus this isolates retaining/updating/uploading 16B versus 32B working vertices from source-asset differences. On later cuts 16B quantizes new normals/UVs and 32B retains float interpolation. Cross-layout shading is allowed to differ and is measured, not silently required to be bit-identical.

The 32B branch and matching shader keyword are comparison-only. `VP_AUTHORED_MEGACITY_SCENE=1` plus `VP_SCENE_AB_BUILD=1` selects the private `Compact16uvMegacityAb.unity`, keeping the original integration scene separate. `VP_SCENE_AB_LEGACY32=1` adds the 32B ABI. A temporary `Assets/csc.rsp` supplies matching Editor definitions during the 32B build because Unity validates Editor/Player type layouts. It is removed before normal regression and is not committed. No CPU32B pool or Main repack is introduced into the normal 16B path.

The first cut is Mesh-local Z=2.5, the positive child's recut is X=-0.01360642. Normal root/driver/Worker/Cook/DAG/commit/upload/camera frames are used. Committed children are held kinematic; the first positive child is moved +3 m world Y, the positive grandchild additionally +3 m world X. This is a controlled pose, not a free-fall, support-anchor or physical-dynamics benchmark.

## Measurement and acceptance

Windows x64 IL2CPP Development, D3D11, visible 960×540 mono Player, ordinary camera rendering to an owned target, XR initialization disabled for the build and restored afterwards. Six independent sequential processes: **16-1, 32-1, 32-2, 16-2, 16-3, 32-3**. The first 16B run precedes the 32B build; no Player timing overlaps an Editor/build/test process. This small interleaved sample is not a randomized statistical trial.

The existing AB counters time driver Update + LateUpdate and camera preparation/submission on Main with `Stopwatch`. The inclusive Main recorder is separate and contains engine work/waits. Before/after settled windows use 60 warmup + 240 frames; summaries use the last 240 samples in each phase. Cut windows retain their actual frame counts and sum/peak scoped time, so a run that took more frames is not treated as an equal-duration window.

Position/index checkpoint hashes must match across all six runs; decoded initial attributes must also match. Per-layout subsequent attribute hashes and final images must repeat exactly. Cross-layout normal/debug images are compared quantitatively; debug green cap coverage must match. Screenshots/PNG encoding occur only **after recording stops**. The extra decoded-attribute checkpoint hashing is diagnostic work outside the product scopes, not a Worker or upload time.

`summary.json` and each `scene-ab.json` retain per-frame times/memory, checkpoint hashes and comparison metrics. Sanitized diagnostic traces, build summaries, images, restored normal EditMode XML and source/binary/evidence hashes accompany them. Full Editor/Player logs remain local and ignored. Run `python Tools/Summarize-Megacity-Ab.py --player16 <directory> --player32 <directory>` to validate and summarize all six runs; private licensed inputs are required.

Memory accounting distinguishes fixed vertex capacity from observed process counters. CPU vertex capacity is **2 MiB → 1 MiB**, and logical GPU vertex-buffer capacity is separately **2 MiB → 1 MiB**; these are not additive measurements of OS working set. Source TextAssets remain referenced in both fixtures. Frame-end Unity allocated/reserved maxima are sampled observations, while OS lifetime working-set high-water also includes startup. Neither is GPU residency or a growth/retirement worst-case peak.

`GC.GetAllocatedBytesForCurrentThread` remains unsupported in this IL2CPP version. Scoped managed allocation is **-1 / unavailable**, never zero-allocation proof. The separate valid Unity whole-frame GC counter includes engine and diagnostics; checkpoint hashing and logging are not attributed to product scopes.

## Results

All **6/6 processes exited 0**, recorded 614 frames, reached both commits and released/drained the world with disposed display and unbound atlas. Each settled window drew all 240 frames. Checkpoint positions and live indices match across all six runs: vertices **5,326 → 6,078 → 6,974**, live indices **13,164 → 15,456 → 18,648**. Initial decoded attributes match (`f5937f3c3c69d543`); post-cut attributes differ between layouts as expected from quantizing new attributes, and repeat exactly within each layout.

Each cell below is the median of three process measurements; brackets give the range across processes. Settled Main is the bounded product scopes, not the whole engine Main thread.

| Measurement | Legacy32 | Compact16uv |
| --- | ---: | ---: |
| Before-cut scoped Main, µs/frame | 16.20 [15.55–17.70] | 17.25 [14.20–17.65] |
| After-recut scoped Main, µs/frame | 20.90 [18.70–23.30] | 22.65 [18.15–24.30] |
| First-cut scoped sum, ms / 9 frames | 2.823 [2.439–2.884] | 3.069 [2.762–3.183] |
| Recut scoped sum, ms / 4 frames | 0.558 [0.510–1.483] | 0.441 [0.359–0.486] |
| Observed Unity allocated peak, MiB | 118.343 [118.116–118.653] | 119.662 [116.655–120.510] |
| OS lifetime working-set peak, MiB | 322.098 [316.684–322.383] | 321.996 [320.531–322.332] |

There is **no demonstrated general Main speedup** here. The 16B after-recut median is 1.75 µs higher (about 8.4%), while settled ranges overlap. First-cut median is also higher; recut sums are lower in these three 16B runs. These small, mixed results do not establish a universal regression or improvement, nor a zero-cost claim. No causal attribution to Worker encode, upload or driver behavior is inferred from these aggregate scopes.

The exact vertex-capacity saving is **1 MiB CPU and separately 1 MiB logical GPU buffer**. The observed whole-process/Unity counters do **not** establish a peak reduction at this asset/profile size; the Unity allocated median is actually higher in the 16B sample, with wide run variation. Do not transfer the earlier enlarged dense-box peak reduction to this scene. The reason to retain 16B supported here is the fixed vertex-capacity saving and functioning real-asset path, not an observed process-peak or Main-time win.

Final normal and debug images repeat pixel-exact within each layout. Across layouts, each pair differs at **1,168 / 518,400 pixels (0.225%)**, maximum RGB-channel difference **5/255**, whole-image mean absolute channel difference **0.001786/255**. The debug green cap mask is **6,984 pixels in both layouts, zero changed mask pixels**; shutdown images are identical. The small shading differences are consistent with post-cut attribute quantization, not geometry drift. Visual inspection finds the same building fragments and horizontal/vertical caps; this is not an independent geometric correctness oracle. The 16B normal image also matches the previous non-AB integration image pixel-exact.

Unity's whole-frame GC counter totals 108,232–108,720 bytes over each 614-frame recording. This includes diagnostics and is not scoped product allocation. After removing the temporary compiler response file and its generated metadata, normal **EditMode 3,614/3,614 passed**, no failures/skips/inconclusive cases. Generated build settings were restored. No additional StandaloneRendering suite was run this turn; both AB IL2CPP builds and the restored EditMode run are the regression evidence.

## Limits / next gate

This is one actual **static** asset at a fixed capacity and two cuts. It does not establish original-float source equivalence, character current-pose skinning, multiple-target concurrency/deadlines, Anchor support, XR, pool growth/retirement peaks, GPU residency or shipping diagnostic-variant stripping. Those remain gates; main is unmerged.
