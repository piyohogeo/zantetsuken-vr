# Compact16uv product scene integration

Baseline `737adcfa`, branch `compact16uv-product-migration`. Original product main and the original `CutWorldSandbox.unity`/shared materials are not replaced.

## Implemented boundary

`CutWorldRoot` now accepts an optional serialized normal/debug 256x256 atlas pair. Both absent preserves the old world setup. A partial/wrong-size pair or an already-bound global pair is an authoring error refused **before world storage is allocated**. An opting-in world binds after display creation and enables its owned provisional-cap materials before becoming ready. Approved forward materials opt in **offline**; no shared material is mutated/cloned during runtime binding and no vertex repack is added.

The pair is a single application-global resource, not per-world/per-camera. An opting-in world requires exclusive binding ownership; another opting-in world is refused even if it supplies the same pair. Ordinary shutdown clears the binding after disposing the display, without destroying the caller-owned textures. An opt-out world never clears another caller's binding. If another caller replaced the pair, shutdown does not clear that replacement. Player termination retains the existing no-cleanup-guarantee contract. This is not an additive-scene atlas arbitration system.

`Compact16uvSandboxSceneBuild.BuildScene` explicitly opens the existing product sandbox, copies its forward materials into ignored private assets, opts those copies in, assigns the pair, and saves to `Assets/Licensed/Compact16uvIntake/SceneIntegration/Compact16uvSandbox.unity`. Original scene/material files and build-settings scene selection remain unchanged. `CutWorldSandboxPlayerBuild.Build` accepts an optional scene path while retaining its old default.

The scene uses the **existing synthetic body**, physical shape, profile, product root/driver, shared dispatch, geometry commit and camera callback. It does not substitute a Megacity display mesh for an unaudited physical shape. Actual Megacity image correctness is the preceding checkpoint, not a claim that Megacity physics/character integration is finished here.

## Measurements

The existing explicit `-zantetsuPlayerCheck` walk is extended only when `VP_COMPACT16UV_SCENE_MEASURE=1`. In that diagnostic process it enables background execution, observes an ordinary cut and child recut, captures grey/debug cap images, and takes before-cut/after-recut samples. The dedicated diagnostic builder disables Standalone XR initialize-on-start for this **mono build only**, restoring the authored value in `finally` after building. The measurement refuses an active XR device rather than stopping a loader after startup. Normal product builds/play keep their original XR setup. It never drives updates, dispatch, preparation or rendering manually.

Each settled window warms for 60 frames then records 240 `Main Thread` profiler samples. This is **inclusive Main Thread duration**, including waits, not active CPU work or the upload marker from the preceding checkpoint. Missing/zero recorder data makes the walk fail rather than inferring a timing. Memory readings are Unity allocated/reserved bytes and Windows process working-set snapshots after the sample window; they are not dedicated GPU residency, total commit or lifetime peaks. Sampling arrays/Player-check state and normal engine activity are present. No forced GC, GPU fence or synchronous worker completion is introduced for sampling. Screenshots/file writes are outside sample windows.

The measured window must contain product camera drawing callbacks, and every captured image must contain non-black pixels. Exit 0 alone is not image correctness; representative captures are visually inspected as well.

In measurement mode only, the existing main camera targets a 960×540 RGBA8 RenderTexture with an 8-bit stencil attachment and one sample. This avoids dependence on a hidden/minimized desktop swapchain. It is still rendered by Unity's ordinary camera loop and `CutWorldCameraDrawing`, not a manual render request. Captures read that target after end of frame. Consequently these are **offscreen mono scene** measurements, not onscreen presentation, shipping MSAA, XR or headset frame-rate results.

This checkpoint has no like-for-like Legacy32 build, so it must not claim a measured 32B-to-16B speedup or residency reduction. Fixed vertex capacity ×16 describes the logical backing size only.

## Reproduction

1. Prepare the private intake and normal/debug atlas from the preceding checkpoints.
2. Set `VP_COMPACT16UV_PLAYER_OUT` to a fresh directory outside the project; run Unity 6000.3.22f1 batch mode with `-executeMethod Zantetsu.EditorTools.Sandbox.Compact16uvSandboxSceneBuild.BuildPlayer -logFile <fresh.log>`.
3. Set `VP_COMPACT16UV_SCENE_MEASURE=1`; launch the built `CutWorldSandbox.exe` with `-force-d3d11 -screen-fullscreen 0 -screen-width 960 -screen-height 540 -zantetsuPlayerCheck <fresh output directory> -logFile <fresh.log>`. Do not use `-batchmode`/`-nographics`: the existing walk captures rendered end-of-frame images.
4. Lifecycle tests: PlayMode filter `Zantetsu.PhysicsCut.PlayModeTests.CutWorldAtlasLifecycleTests` (pair/reload, malformed pair, competing world, opt-out ownership).

## Remaining gates

Authoritative licensed-asset physics/scene setup and all-material coverage; character current-pose/self-skin integration; matching Legacy32/Compact16uv product workload measurements, active Main work vs waits, GPU residency and growth peaks; XR. Product main remains unmerged.

## Checkpoint outcome (2026-09-24)

- Full EditMode: **3613/3613 passed**; new atlas lifecycle PlayMode filter: **5/5 passed**. Zero failed/skipped/inconclusive cases, both Editor test runs exited 0. The five cases cover rebind after shutdown, partial pair, wrong dimensions, competing atlas world and opt-out ownership. The earlier full Physics PlayMode suite was not rerun here.
- All five diagnostic scene builds succeeded. The last build is Windows x64 Development IL2CPP, Unity 6000.3.22f1, D3D11. Generated Player binaries remain outside Git.
- Runs 2–6 reached both parent/child geometry commits with no geometry fault, ordinary `IsReleased=True` / `IsDrained=True` / `displayDisposed=True`, and atlas binding false after shutdown. These are state/lifecycle observations, not proof of pixels.
- **Scene rendering and performance acceptance remain NOT ESTABLISHED.** Runs 2–6 produced black captures; runs 3–6 explicitly failed with exit 8. No speedup, Main-thread budget, memory saving or visible cap result is claimed. Run 1 stopped at an unsupported diagnostic API before cuts.
- Original scene, shared materials, XR settings and build-scene selection have no Git changes. Build-generated pipeline-prefilter changes were restored; the new generated scene-template settings file was removed (Unity may regenerate it). Logs and screenshots, including failures, are retained. `summary.json` excludes every failed Player run from rendering/performance acceptance and records source/evidence/private-scene hashes with anonymized profile paths.

## Failure history

The first scene build succeeded, and `run1` reached a ready world/body with atlas bound and stride 16. The diagnostic then called `.NET Process.WorkingSet64`, which threw `NotSupportedException` in IL2CPP (`NativeMethods::GetProcessData`). No cut or successful memory measurement is inferred from that run. Its test-owned process was stopped after checking the executable path; its log is retained. The diagnostic now queries the current process through Windows `K32GetProcessMemoryInfo` with `PROCESS_MEMORY_COUNTERS`; failure is reported, not replaced by a guessed value. This changes only the explicitly requested Player-check measurement, not the product cut path.

`run2` reached both geometry commits, ordinary shutdown and exit 0, but **all screenshots were black**. OpenXR started in `XR_SESSION_STATE_IDLE`. Its very small Main Thread values (0.054 / 0.066 ms medians) are rejected as scene-render performance evidence; exit 0 did not establish actual rendering. The mono diagnostic now stops/deinitializes XR before sampling, records the product camera's drawn-frame delta and fails on blank captures. The failed run and its measurements remain as failure evidence, not accepted results.

`run3` stopped/deinitialized XR but still had zero drawn frames and black captures with the hidden window; the new checks correctly returned exit 8. Thus idle XR was not established as the sole cause. The next diagnostic assigns the existing camera an offscreen target instead of relying on the hidden swapchain; no rendering-performance claim is made from `run3`.

`run4` (offscreen target) and `run5` (the same binary with graphics-enabled batchmode) also had zero drawn frames and blank captures, returning exit 8. These are excluded too. The subsequent build disables XR **before startup**, rather than deinitializing an already-started loader. Failed runs are not evidence of normal scene Main-thread performance.

`run6` used the build with XR initialization disabled from startup. It still had zero drawn frames and black captures, returning exit 8. Therefore XR initialization is not established as the root cause. All six runs are excluded from rendering/performance acceptance. No hidden-window timing/memory sample is promoted to a product-scene performance result. The next distinguishing check is a **visible Player window**, which has not been launched without user approval. This checkpoint establishes binding lifecycle and cut/recut shutdown progression, not successful product-scene drawing or a measured speed/memory improvement.
