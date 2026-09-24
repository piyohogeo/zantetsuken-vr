# CPU16 / GPU16 migration checkpoint

The shared `VpRenderVertex` is now 16 bytes in the product worktree: float3 position, signed oct8x2 normal, uint8x2 UV. CPU pool/storage/reservation, direct cut output and GPU upload all use it. There is no parallel float32 vertex pool and no Main upload pack. The existing geometry/topology algorithm and range ownership remain in place.

Verified checkpoint: **3598/3598 EditMode**, **41 distinct PlayMode cases** across isolated processes, and **3/3 Development IL2CPP Player** after the final runtime fix. This is the shared-storage/rendering foundation, not completion of asset/atlas integration or product performance validation.

Eight existing shader readers (direct, Stage 2, indexed, culled, shadows and stencil volume) share `VpCompactVertex.hlsl`. Unused Stage 2 has not been removed as part of this change. `VpStoredGeometryTransfer` and `VpStoredGeometryDisplay` already use the shared type/stride and continue to transfer the CPU allocations directly. The ordinary Unity Mesh export path decodes attributes when explicitly requested; it is not the steady-state VP upload path.

## Contracts

- Position is still float3. Normals are directions, oct encoded without a preliminary rsqrt; decode returns a unit normal.
- UV centres `(i+0.5)/256` round-trip exactly. Normalized endpoints use nearest-centre quantization. Only endpoint roundoff up to `2.3841858e-7` is accepted outside `[0,1]`; arbitrary signed/tiled UV is rejected, not wrapped.
- A reserved oct code (`-128`) marks invalid packed attributes. Invalid is sticky until the whole vertex is replaced, so object-initializer order cannot erase a rejection. Source Mesh validation happens before writing any destination element; prepared storage intake refuses invalid encodings before taking spans.
- Real caps carry byte slot `(247,247)` at generation, also through recuts. The indexed shader retains the existing global cap/debug colours using this slot before material UV transforms. Atlas texture switching and the temporary-cap slot are a later integration step, not claimed complete here.
- No blanket test tolerance increase: position/topology checks are unchanged. UV interpolation permits half a byte step plus the previous numerical tolerance, and new normal interpolation permits one degree of oct encoding error. Input fixtures with formerly out-of-domain UV are explicitly affine-mapped into the supported domain without altering their geometry/topology or merging seams. The old negative-UV preservation test became a compact surface/cap recut test; new rejection tests cover unsupported input.

## Failure history

1. `migration-first`: 799/928 passed. Old 32-byte/float-exact/negative-UV expectations, out-of-domain fixture UV and source import endpoint roundoff were exposed.
2. `migration-second`: 882/932 passed after the initial contract and fixture updates. The built-in Sphere diagnostic showed `u.max = 1.00000012`; arbitrary domain clamping was not adopted.
3. `migration-third`: 930/932 passed. Two UV comparison bounds had not yet included the documented endpoint roundoff allowance.
4. `migration-all-editmode`: 3596/3596 passed, failed/skipped/inconclusive 0. This includes rendering, URP, storage/retirement, worker cut, recut and physics-related EditMode coverage. A later prepared-intake rejection test and Player tests are recorded separately.
5. The first IL2CPP Player run passed the new real-storage/Burst/recut/GPU test and the existing forward/shadow/readback test. Its separate shadow-control diagnostic refused to start because this invocation omitted the required `VP3_SHADOW_DIAGNOSTICS` output directory. The original 2/3 result is retained; the retry supplies a fresh output path.
6. `migration-player-final`: 3/3 passed on D3D11 / RTX 3090 in a Development IL2CPP Player. The actual product storage executes three consecutive Burst cuts, preserves old input bytes, writes new cap bytes, refuses a 32B-stride GPU destination, and round-trips a nonzero-offset 16B upload. The existing Lit receiver control observes 1,248 darkened pixels for both native and VP shadows, and the repeat-forward control changes 0 pixels. `player-images/08-lit-vp-both.png` was also inspected visually. The diagnostic default receiver was unsupported/magenta for both paths; the explicit URP Lit receiver is the shadow evidence. The 4x4 empty atlas readback is retained as a diagnostic, not claimed to prove the shadow map contents.

Final test totals, commands and evidence hashes are recorded in `migration-summary.json`. Failed attempts are retained. A test success is not a zero-process-leak or product-scene performance claim.

### Retirement polling defect exposed during verification

The first final focused run passed 937/938, but the old buffer-retirement test logged a readback error. The same request-state polling exists in baseline `4326c48`; no claim is made that changing the vertex stride introduced it. A deterministic new test completes both readbacks, yields five frames without retirement polling, then tries to collect: the original code fails (`migration-readback-repro2`, 0/1). The first attempt at that reproduction had a test-only missing namespace import; its compile log is retained separately.

Unity documents that a successfully completed request is disposed on a later frame and then `hasError` becomes true ([AsyncGPUReadbackRequest](https://docs.unity.com/en-us/engine/6000.0/script-reference/unityengine/rendering/asyncgpureadbackrequest)). The product now latches each completion/error in its callback, in a separate object per retired generation. Collection reads that stable state; disposal only waits on requests not already completed. Real callback errors still keep the retired buffers and block growth. This fixes the reproduced delayed-poll problem, not a new proof of deferred draw ordering or GPU lifetime guarantees beyond the existing design.

### PlayMode execution isolation

The first PlayMode invocation passed 34/41 and held seven cases Inconclusive: an intentional Player-termination case leaves its world/resources held by contract and the fixture explicitly blocks later cases. These are not seven passes. The six withheld ordinary cases and the withheld termination case are run in separate Editor processes, without changing the fixture's protective guard. The initial invocation and its teardown leak warning remain in the evidence; there is no zero-leak claim for intentional termination tests.

The isolated ordinary run passed 6/6 and the isolated termination run passed 1/1 (both exit 0), covering all 41 distinct cases across the three processes. After the retirement fix, `migration-verified-editmode` passed **3598/3598**, including the deterministic delayed-poll regression and the prepared-intake rejection test; failed/skipped/inconclusive all 0 and Editor exit 0.

### Final Player rebuild

`migration-player-verified` could not copy `mscorlib.dll-resources.dat` over the previous build output because Windows reported an open user-mapped section. This was a build-output lock, not a test failure or a source compilation failure; the Editor exited with code 3 and emitted no test-result XML. The retry uses a new external build directory and retains the failed build log.

`migration-player-verified-fresh` passed **3/3**, failed/skipped/inconclusive 0, Editor exit 0, on D3D11 / RTX 3090 with the final runtime code. `player-images-verified/` contains its images and measurements: native and VP Lit receiver controls each have 1,248 darkened pixels, and repeat-forward changes 0 pixels. Its `08-lit-vp-both.png` was inspected visually. The same default-receiver/atlas-readback limitations described above apply. Unity-generated build-only preloaded assets, shader-prefilter serialization changes and a newly generated scene-template settings file were removed after verification; no product render-setting change is included.

## Still separate

- BottomHalfUV product asset intake, offline Static16 ingestion and self-skinning.
- Finished normal/debug atlas integration and full material coverage.
- Scene-level Main timing, residency and grow/retirement peak measurements.
- XR stereo validation; mono tests do not establish XR correctness.
- Stage 2 deletion and merge to the source main worktree.

The source main worktree is not modified by this migration. This branch remains isolated until these gates are addressed.

## Reproduction

Use Unity 6000.3.22f1 with `-batchmode -projectPath <this-worktree> -runTests -force-d3d11 -testResults <fresh.xml> -logFile <fresh.log>`. Do not open a second Editor on the same worktree.

- Full EditMode: `-testPlatform EditMode`.
- Focused EditMode: additionally `-testFilter "Zantetsu.Rendering;Zantetsu.MeshCut"`.
- Player: `-testPlatform StandaloneWindows64 -assemblyNames Zantetsu.Rendering.StandaloneTests -buildPlayerPath <dedicated-directory-outside-repository>`. Set `VP3_SHADOW_DIAGNOSTICS` in the launching process to a fresh image directory. This is a Development IL2CPP correctness build, not a benchmark build.
- PlayMode: `-testPlatform PlayMode -assemblyNames Zantetsu.PhysicsCut.PlayModeTests`.
- After all Editors exit, `Tools/Summarize-Compact16uv-Migration.ps1` records totals/hashes and anonymizes the profile path in new logs/XML for public documentation. Historical baseline evidence is untouched.
