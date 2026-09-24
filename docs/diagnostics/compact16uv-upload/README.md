# Same-binary Legacy32 / Compact16uv upload control

**Correction (2026-09-24, scene-AB follow-up): the reported managed-allocation zeros are NOT valid evidence.** This Unity installation's `Editor/Data/il2cpp/libil2cpp/icalls/mscorlib/System/GC.cpp`, `GC::GetAllocatedBytesForCurrentThread`, invokes `IL2CPP_NOT_IMPLEMENTED_ICALL` and returns 0. Raw results and original source/binary hashes are retained as historical evidence; interpret their `managed16/32` fields as unavailable, not allocation-free. SetData timing and GPU content/guard checks are independent and remain valid. The later scene diagnostic uses Unity's frame-level profiler counter separately, without attributing whole-frame GC allocation to a product scope.

This diagnostic compares the CPU duration of `GraphicsBuffer.SetData` for identical decoded vertex attributes in 32-byte and 16-byte layouts. It is **not** a two-layout product scene, a Legacy32 cutting kernel comparison, GPU time, or a process-memory comparison.

Input: the verified private Static16 Megacity resource. Run the current product storage/cutting implementation through the same X/Y/Z positive-child recuts as the preceding appearance gate (plane through bounds centre + 0.137 axis extent). Require Burst execution, closed contours and nonempty append. At each stage measure all committed vertices, including inherited/unreferenced history; for cuts also measure only newly appended vertices. Duplicate each payload 1/16/64 times for transfer-size scaling, not as 64 independently placed physical objects. Legacy32 is an expanded copy of the same quantized attributes, not the original unquantized source.

For each of 21 cells both CPU arrays and GPU buffers have the same element capacity (next power of two of payload count + 16), destination offset 8, Structured target and identical SetData calls. No pack, decode, indices, drawing, readback, logging, allocations of fixtures, or screenshot work occurs inside the timed calls. Ten warmup pairs followed by 61 measured pairs, alternating 16/32 order, with one ordinary frame yielded between pairs. Record raw microseconds and per-thread managed allocation, median and nearest-rank p95. These are submission durations; buffers have no draw consumer, so a production in-flight hazard is not represented. Both layouts coexist in one process: working set cannot be attributed to one layout.

After each cell, synchronous GPU readback checks every field and all untouched prefix/suffix guards. CPU vertex payload and logical GPU buffer capacity are calculated from the actual allocations; driver residency, allocator metadata, lifetime/growth peaks and full-world memory are unmeasured. Failure returns Player exit 9; missing private assets is failure, never a skipped success.

## Reproduce

Build via `Compact16uvSandboxSceneBuild.BuildPlayer` with `VP_COMPACT16UV_PLAYER_OUT` set to a fresh external directory. Launch the resulting Player visibly with `-force-d3d11 -screen-fullscreen 0 -screen-width 960 -screen-height 540 -zantetsuPlayerCheck <fresh-directory> -logFile <fresh-log>` and `VP_VERTEX_UPLOAD_COMPARE=1`. Leave `VP_COMPACT16UV_SCENE_MEASURE` unset. Alternate `VP_UPLOAD_ORDER_SEED=0/1` between independent processes. The comparison runs instead of the screenshot/cut-scene walkthrough, then shuts down the existing sandbox world. Normal play without the explicit argument/environment variable is unchanged.

## Result (2026-09-24)

Windows x64 Development IL2CPP / Unity 6000.3.22f1 / D3D11. Three fresh visible Player processes using one binary, starting-order seeds 0/0/1 (order also alternates within every cell). All **63 cells** passed full readback/guard checks and each Player exited 0. Each layout has **3,843 measured calls**; per-thread managed allocation is unavailable (see correction above). Product kernels produced the same 5,326 -> 6,255 -> 6,478 -> 6,558 committed-vertex sequence in all runs; new tails were 929, 223 and 80 vertices. Cuts are executed synchronously in fixture preparation, not measured or claimed as Worker offload here.

Values below are the median of three process medians, in microseconds. All 21 cells, raw samples, per-cell p95 and run-median min/max are in `summary.json` and `run*/upload-comparison.json`.

| Payload | Copies | Vertices transferred | Legacy32 (us) | Compact16uv (us) |
| --- | ---: | ---: | ---: | ---: |
| Uncut, all committed | 1 | 5,326 | 11.8 | 5.6 |
| Uncut, all committed | 16 | 85,216 | 131.7 | 60.5 |
| Uncut, all committed | 64 | 340,864 | 940.9 | 517.0 |
| Third recut, all committed | 1 | 6,558 | 15.2 | 8.1 |
| Third recut, all committed | 16 | 104,928 | 184.2 | 90.0 |
| Third recut, all committed | 64 | 419,712 | 1,232.6 | 660.3 |
| First cut, new tail only | 1 | 929 | 2.1 | 1.1 |
| Third recut, new tail only | 1 | 80 | 0.5 | 0.5 |

Larger payloads support a lower CPU SetData cost for the 16B layout on this host (NVIDIA GeForce RTX 3090). This does **not** make the scene twice as fast: a single initial cut saves about 1 us in this isolated new-tail call, and the 80-vertex tail has no resolved median difference. Recorded values have 0.1 us granularity. The first uncut 1-copy cell varies substantially across processes (32B 11.8–51.2 us, 16B 5.4–25.2 us); short warmup/startup/host effects are not isolated, so do not use cross-stage differences as a cut-related speed change. This was not an exclusive-host experiment.

At the largest cell, equal actual element capacities are 524,288: **CPU vertex allocation 16 -> 8 MiB** and **logical GPU buffer capacity 16 -> 8 MiB** per layout. These are actual capacity × ABI calculations, not measured GPU residency or process RSS. Both layouts coexist during this diagnostic; no total-process halving claim is made. Index/topology, scratch, reservations/growth/retirement and other scene resources are outside this comparison.

Initial `build.log` preserves the failed diagnostic compilation (C# readonly using-variable NativeArray setter). Writable views of the same owned allocations fixed it; `build2.log` records a successful build. No product storage, kernel, shader, or vertex-layout implementation was changed for this control. Existing ordinary scene walkthrough remains available with the comparison environment variable unset.

Full EditMode regression after these diagnostic changes: **3,613/3,613 passed**, zero failed/skipped/inconclusive (`editmode.xml`). The earlier PlayMode and rendering screenshot suites were not rerun here; this change does not alter the product shaders, storage or cutting kernels. Unity-generated pipeline-prefilter changes were restored and the new generated scene-template settings file removed after Editor exit. Original main, source scene, shared materials and XR configuration remain unchanged.

Full matched-layout product Main/cut-frame measurement, actual asset physics/character integration and memory peaks remain separate gates. This completes the **isolated upload comparison**, not the full Legacy32 product baseline.
