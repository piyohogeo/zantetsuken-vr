# Actual Megacity appearance gate

Baseline `da7e35ee`, dedicated `compact16uv-product-migration` worktree. This checkpoint checks the representative licensed `bar_001` through the real Static16 intake, CPU16 cut/recut storage, direct packed upload and indexed VP draw. It does not replace any product scene or touch product main.

## Oracle and scope

The offline builder copies the original imported float position/normal/UV Mesh into **ignored private test resources**, with the original DXT1 palette reference. It is not a runtime product cache and no licensed Mesh or palette is included in Git. Prepare the already hash-verified intake first. Only this known asset/topology pair is authorized.

The test registers two copies so the rendered geometry starts at a nonzero global vertex/index offset. It draws the second copy, then its positive-side X/Y/Z cuts in sequence. All cuts must execute Burst with no open contour. Each of three views covers the uncut model and three successive cut outputs; one view is farther away.

- Uncut reference: original imported **float** Mesh, ordinary MeshRenderer vertex fetch, same shading and normal atlas. This detects position/index/UV errors and quantifies oct8 shading differences.
- Cut reference: published cut vertices expanded to a test-only ordinary Mesh, using CPU oct decode and the published indices. This independently checks GPU fetch, global indexing and decode, **not** independent cutting mathematics or a Legacy32 cut oracle.
- Normal/debug are compared against the Mesh oracle. Acceptance requires zero alpha silhouette mismatches and at most 3/255 RGB channel error. The uncut surface must remain exactly unchanged by debug switching. At least one real cap must visibly change over each view's recut sequence.
- Separately, the original DXT1 palette is compared with the prepared DXT5 normal atlas on the original float Mesh. This records compression/mipmap changes independently of 16B geometry. Silhouette equality is required; colour differences are reported rather than hidden in the oct8 tolerance.
- No shadow caster, production lighting, outline/toon completion, XR or all-material acceptance is inferred from this isolated image test.

An isolated upload sample measures 101 repeated vertex/index transfer calls after 10 warmups, with no image readback/rendering/file IO in the timing window. It includes the existing index read-lease boundary. It measures CPU API time: neither total Main frame time, driver/GPU completion, WDDM residency nor a 32B performance comparison. Re-uploading all committed vertices is a probe workload, not the production incremental-upload policy. Capacity/payload byte counts are logical quantities only. **Correction from the scene-AB follow-up: its managed-allocation counter is unavailable on this IL2CPP runtime; historical zero values below/raw are not proof of zero allocation.** The installed IL2CPP `icalls/mscorlib/System/GC.cpp` marks `GetAllocatedBytesForCurrentThread` unimplemented and returns 0; timing/readback results do not depend on this counter.

## Reproduction

1. Prepare the preceding private Static16 intake and normal/debug atlas checkpoints.
2. Unity 6000.3.22f1 batch mode, `-executeMethod Zantetsu.MeshCut.ReferenceIntake.Compact16uvAppearanceBuild.Run -quit`.
3. Set `VP_APPEARANCE_DIAGNOSTICS` to a fresh image directory. Run `-runTests -testPlatform StandaloneWindows64 -assemblyNames Zantetsu.Rendering.StandaloneTests -buildPlayerPath <fresh external directory> -force-d3d11 -testResults <fresh.xml> -logFile <fresh.log>` (without `-quit`). Missing private resources are **Ignored**, not passed.
4. Set `VP3_SHADOW_DIAGNOSTICS` to a separate fresh directory when running the whole assembly; the existing shadow-control test requires it.

## Recorded results (2026-09-24)

`player-second`: **20/20 passed**, failed/skipped/inconclusive **0**, Editor test-run exit code **0**. Unity 6000.3.22f1, Windows Development IL2CPP, D3D11, RTX 3090. This includes three new appearance cases and all 17 preceding rendering/storage/atlas cases. Product runtime code is unchanged from `da7e35ee`; full EditMode/Physics PlayMode were not rerun in this test-only checkpoint.

| View | Uncut covered pixels | Uncut maximum RGB error (0..255) | Uncut mean RGB error | Cut/recut maximum RGB error | All-stage silhouette differences |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0, near oblique | 16607 | 1 | 0.011200 | 0 | 0 |
| 1, far opposite | 3561 | 1 | 0.003183 | 0 | 0 |
| 2, underside oblique | 16118 | 1 | 0.008024 | 0 | 0 |

The same maximum RGB errors held in debug mode. Original DXT1 palette versus prepared DXT5 normal atlas had **zero RGB or silhouette differences** in all three uncut views. This is representative observed agreement, not a guarantee for every texture footprint/mip/material. Debug switching changed zero uncut surface pixels. Visible real caps changed as expected; after the third cut, 12260 / 657 / 12858 pixels changed for views 0 / 1 / 2.

`images-second/` retains 51 final captures; `images/` retains six failed-reference captures. Visual inspection of final `stage0-view0-packed.png`, `stage1-view1-debug-packed.png`, `stage3-view2-packed.png` and `stage3-view2-debug-packed.png` confirmed the textured model and grey/green recut faces. Geometry is drawn in its imported mesh-local frame, without claiming the product scene's world placement. Shadow regression captures are under `shadow-images/`.

Isolated upload of **11884 committed vertices (190144 bytes)** plus **3240 index bytes**: median **0.092300 ms**, p95 **0.116300 ms**, **0 managed bytes** over 101 measured calls. The vertex count includes the non-rendered prefix copy and inherited/retained vertices. Vertex-buffer capacity is 65536 × 16 = **1048576 bytes**, not measured residency. A 32B vertex representation would double these vertex byte counts arithmetically; no 32B timing or resident-memory comparison was run here. Do not use this small isolated sample as a product-frame performance claim.

`summary.json` retains test outcomes, per-case measurements and evidence/source/private-reference hashes. Profile paths in logs/XML are anonymized with before/after hashes. After Unity exited, build-generated render-pipeline prefilter settings were restored and the newly generated scene-template settings file was removed (Unity can regenerate it); no user-authored data was deleted.

## Remaining migration gates

Product scene selection/bootstrap and material opt-in; full licensed asset coverage; character current-pose/self-skin integration; actual product-scene Main/residency/growth peaks with a comparable baseline; XR. Do not treat this representative image gate or isolated SetData timing as completion of those tasks.

Follow-up: [product Sandbox scene integration](../compact16uv-scene/README.md) adds explicit composition-root atlas ownership. Hidden-window attempts remain excluded; three visible-host offscreen mono runs verify the synthetic product scene and record settled Main/working-set samples. This is not a Legacy32 comparison or licensed-asset physics integration.

## Failure history

`player-first` passed 16/20, with four failures and no skipped cases. The reference MeshRenderer had only one material slot for the source's **three submeshes** (10173 / 1653 / 1338 indices). This omitted source geometry while VP rendered all 13164 indices, producing 23 / 7 / 56 silhouette mismatches across the three views. The reference now assigns the same oracle material to all source submeshes, asserts material-slot and total index counts, and retains the original per-submesh index ordering. Neither the VP implementation nor the colour/silhouette tolerance changed. The fourth failure was the pre-existing shadow diagnostic's required output-directory environment variable missing from the launch; the second run sets it. Original failed images and XML/logs are retained, not overwritten.

All three private source `.blend` files and their importer metadata were rechecked against the preceding intake manifest and matched. The reference asset is 5326 vertices / 13164 indices and stays private, together with its source-texture reference.
