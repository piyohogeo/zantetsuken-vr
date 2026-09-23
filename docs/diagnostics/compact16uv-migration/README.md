# Compact16uv product migration worktree

## Baseline

- Branch: `compact16uv-product-migration`
- Worktree: `C:/Users/junic/src/zantetsuken-vr-compact16uv`
- Product base: `7a23a4977f27f08b8eebdc826a19cc5d74dfcc84`
- Culled source snapshot: `985fb3a`
- Source provenance: `source-snapshot.json` (47 copied files; 1,773 source files verified unchanged).
- Benchmark evidence: `C:/Users/junic/src/zantetsuken-mesh-vp-transform-benchmark`, commit `cadd235`.

This branch owns product changes. The benchmark repository remains the home of the experiment and its results.
The snapshot commit fixes source provenance; it does not assert test success or completed Compact16uv migration.

## Validated starting point

Unity 6000.3.22f1, D3D11, EditMode, filter `Zantetsu.Rendering`: **379/379 passed**, failed/skipped/inconclusive all 0. This includes the copied RenderingUrp tests. The baseline uses the original 32-byte GPU layout; no 16-byte rendering result is claimed. See `baseline-editmode.xml`, `baseline-editmode.log` and `baseline-summary.json`.

The source audit at `985fb3a` reports PASS: both culled shaders are now pinned, and all nine previously tracked shader/include blobs are unchanged. Direct rendering still has three runtime callers and seven enabled-scene references; the old Stage 2 batch still has no runtime construction. See `source-audit/` and `legacy-usage-audit/`.

Unity exited normally after the baseline run. Its shutdown log includes a `MemoryLeaks` telemetry record; the successful test count is not a zero-leak claim. No product runtime or shader migration has been applied in this starting-point commit.

## Implementation boundaries

1. Establish the Rendering / RenderingUrp EditMode baseline after importing the copied sources. Retain failures as baseline evidence before interpreting migration failures.
2. Introduce a separate GPU vertex type: float3 position, signed oct8x2 normal, uint8x2 UV, stride 16. Keep CPU `VpRenderVertex`, geometry storage and the cut kernel at 32 bytes initially.
3. Migrate every upload entry, including `VpGpuGeometryBuffers`, `VpGpuIndexedGeometryBuffers`, `VpStoredGeometryTransfer` and `VpStoredGeometryDisplay`. The last two upload directly and must not be missed when changing buffer constructors. Preserve element offsets and range ownership; revise byte offsets, retirement readbacks and readback tests for the GPU ABI.
4. Migrate direct, indexed, culled, shadow and stencil-volume layout readers together. Keep stereo visible-instance addressing and both culled-shadow cull modes. Separate shaders that only need a stride change from shaders that need normal/UV decoding.
5. Resolve the CPU negative-UV cap marker at the GPU upload boundary and explicitly revise product DESIGN sections 4.5 and 5.3 before making the new atlas path the default. UInt8 UV cannot preserve the current unrestricted signed-UV contract. Do not silently clamp or reinterpret unsupported source UVs. Establish the updated BottomHalfUV inputs and generated normal/debug atlas before using their restricted UV domain as a product input contract.
6. Treat self-skinning, offline Static16 ingestion, atlas sampling, and removal of Stage 2 as reviewable changes with their own checks. Existing buffer/direct oracle coverage in Stage 2 test files must be retained if those files are removed.

## Verification and handoff

- Compare Legacy32 reference images against Compact16uv for regular surfaces, real caps, stencil caps and shadows. Exercise nonzero ranges, multiple commands and buffer growth/retirement.
- Record compile and EditMode/PlayMode results, D3D11 Player results and any baseline failures. Source provenance PASS is not rendering PASS.
- Product-scene memory includes the current descriptor-to-slot table and the current PhysicsCut mesh-slot layout. Do not attribute their costs or savings to the vertex-format change.
- The source main worktree remains at its existing branch and retains all original tracked/untracked edits. Only shared Git metadata changes when this worktree commits.
- No merge to main is implied by worktree validation. XR validation remains distinct from mono results.
