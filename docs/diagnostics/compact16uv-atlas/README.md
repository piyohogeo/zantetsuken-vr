# Shared palette atlas integration

Baseline: `336f031`, branch `compact16uv-product-migration`. This is an explicit opt-in rendering feature; no Sandbox material/scene is silently replaced and the source main/Blender project are untouched.

## Runtime boundary

`VpCutSurfaceAtlas.Bind(normalAtlas, debugAtlas)` binds one caller-owned 256x256 pair, without cloning textures or materials. Set `_VpUsePaletteAtlas=1` on approved **shared** forward materials. For the owned provisional-cap materials of a `VpLogicalCutDisplay`, call `SetCapPaletteAtlasEnabled(true)` once at setup. `VpCutSurfaceColour.SetDebugEnabled` remains the single switch. Bind/opt-in and debug toggles issue no geometry writes or collection; ordinary display preparation retains its existing costs.

The four forward readers (direct, Stage 2, indexed, culled indexed) and the stencil-cap shader use the shared sampling include. The three previously flat-colour paths gain palette sampling only when opted in. Materials not opted in, or with no bound pair, keep their prior behaviour, including the indexed global-colour cap path. Shared material opt-in applies to every user of that material, so do not opt in a mixed/unverified palette material. One pair is global across the application, not per camera or per renderer.

Switch before registering draws for the camera group. Changing global bindings in the middle of queued per-camera/per-draw work is not a captured per-draw state and is outside this contract.

- Real cap: vertex UV byte `(247,247)`; provisional cap: pass-fixed `(239,247)`. Both are 5x5 patches centred at those indices, lower-left texture coordinates.
- Normal atlas has equal grey patches; debug atlas has green real and red provisional patches. Neither cap is tinted or UV-transformed. Constant cap coordinates explicitly sample mip 0.
- Surface always samples the **normal atlas** with its usual UV transform/tint. Only the cap texture binding switches to debug. This prevents debug-patch contamination of ordinary surfaces even at coarse mip levels. The debug atlas still preserves source lower-half pixels but surface invariance does not depend on matching its generated mips.
- Shading, shadow/clip/winding/stencil rules, vertex layout and visible-instance addressing are unchanged. Atlas colour selection is not a geometry/topology marker used by cutting.
- `SetColours` affects the non-atlas path only. Atlas colours are prepared offline. Clear the binding before releasing caller-owned textures. Play-session/domain startup clears the binding; scene setup must bind again.

## Offline assets and provenance

`Compact16uvAtlasBuild.Run` reads the private source palette already verified by the preceding intake. SHA256: `88fdb7640dcadbef46db9bb52a533180ae0ef2d5bec5bea599d024c3614ebcc5`. It copies source pixels, modifies only the two upper-half patches, and checks every source lower-half pixel unchanged before PNG encoding. Generated normal/debug PNGs and texture references stay under ignored `Assets/Licensed/Compact16uvIntake/Resources/PaletteAtlas/`; no licensed source or generated atlas is committed publicly. Public probe materials reference only project shaders, not licensed textures.

Both atlases import as 256x256 DXT5, sRGB, 9 mips, Bilinear, Repeat, anisotropy 1. `build.json` records settings and Unity's Editor `Profiler.GetRuntimeMemorySizeLong` result (175,712 bytes per texture in this run). That number is not D3D dedicated residency, an allocation delta or a claim about total scene memory. Two caller-owned textures exist; runtime binding does not make copies. This generation is a product-side offline preparation step, not an upstream Blender pipeline change.

## Verification and failure history

The first build found a test-only access to the pool's internal `AppendedVertices` API. The Player test now snapshots the GPU vertex fields immediately after upload and compares those exact fields after the texture/ST/tint changes; product API visibility was not expanded for the test. `build-first.log` is retained. `build-second` generated the compressed pair and checked all probe shaders supported.

The Player matrix is four forward paths × near/far/oblique views, using varying surface UVs and a separate cap range. It checks covered surface/cap pixels, zero normal/debug surface difference, cap invariance under ST/tint changes, a visible surface response to those changes, and unchanged GPU vertex fields. The culled shader fixture supplies two visible-instance entries to its addressing path; it is not a new culling or XR proof. A separate real stencil-batch case verifies grey/red provisional slots, no upload/buffer-write delta for the switch, and no cap pixels without positive stencil. Resources missing from a private-less checkout are **Ignored**, not passed. Generic regression tests and the logical-display opt-in test are separate from this Player matrix.

## Reproduction and remaining work

### Recorded results (2026-09-24)

Unity 6000.3.22f1: full EditMode **3613/3613** and Development IL2CPP Windows Player **17/17** passed, with zero failures, skipped or inconclusive tests. Both test processes exited 0. Player rendering used D3D11 on an RTX 3090. The 17 cases include the 13 new atlas cases and four existing rendering/intake regression cases; the earlier Physics PlayMode suite was not rerun in this checkpoint.

All four forward paths produced the following counts per view:

| View | Surface pixels | Green cap pixels | Normal/debug surface differences | Cap differences after ST/tint | Surface pixels changed by ST/tint |
| --- | ---: | ---: | ---: | ---: | ---: |
| Near | 2368 | 2368 | 0 | 0 | 2368 |
| Far | 608 | 608 | 0 | 0 | 608 |
| Oblique | 2240 | 2240 | 0 | 0 | 2240 |

GPU vertex fields remained exactly equal in all 12 cases. The stencil case covered 4096 grey pixels in normal mode and 4096 red pixels in debug mode; switching caused **0 uploads and 0 buffer writes**. Removing the stencil volume yielded **0 red pixels**, confirming the cap still requires positive stencil.

The `images/` directory retains 39 atlas captures. Visual inspection of `path2-view0-normal.png`, `path2-view0-debug.png`, `path3-view2-debug-st-tint.png`, and `stencil-debug.png` confirmed the expected surface palette, grey/green real cap and red provisional cap. These are synthetic geometry fixtures sampling the actual prepared palette, not an appearance comparison of licensed asset geometry. Existing shadow-control captures are retained separately in `shadow-images/`.

`summary.json` records test totals, evidence/source hashes and private atlas hashes. Logs/XML have user-profile paths anonymized; original and anonymized hashes are both retained. Unity-generated render-pipeline prefilter changes were restored and the newly generated, untracked scene-template settings file was removed; no user-authored settings were deleted.

### Rerun

1. Prepare the preceding private intake as described in `../compact16uv-intake/README.md`.
2. Unity 6000.3.22f1: `-batchmode -projectPath <worktree> -executeMethod Zantetsu.MeshCut.ReferenceIntake.Compact16uvAtlasBuild.Run -quit -logFile <fresh.log>`.
3. Full EditMode: `-runTests -testPlatform EditMode -testResults <fresh.xml> -logFile <fresh.log>` (no `-quit`).
4. Standalone IL2CPP: `-runTests -testPlatform StandaloneWindows64 -assemblyNames Zantetsu.Rendering.StandaloneTests -buildPlayerPath <fresh external directory> -force-d3d11 -testResults <fresh.xml> -logFile <fresh.log>`. Set `VP_ATLAS_DIAGNOSTICS` to a fresh atlas-image directory and `VP3_SHADOW_DIAGNOSTICS` to a separate shadow-control directory.

Still separate: actual licensed-asset appearance oracle, all product materials, finished scene bootstrap/selection of shared materials, full asset topology export, product-scene Main/residency/growth peaks, XR, and upstream distribution artifact generation. The optional runtime atlas API is not a claim that every product scene already uses it. Stage 2 remains until its separately planned deletion.

Follow-up: the [actual Megacity appearance checkpoint](../compact16uv-appearance/README.md) verifies one representative asset before/after three cuts in three views. It does not close the all-material, scene-performance or XR gates above.
