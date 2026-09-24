# Actual Megacity physics intake — 2026-09-24

Checkpoint after `deafb0aa`, on the dedicated `compact16uv-product-migration` worktree. This advances actual-asset integration through **offline binding and Unity component tests**, not a product-scene acceptance or performance result. No runtime product code or original main changed.

## Input identity and frame

The upstream BottomHalfUV tree moved. The current representative is `Generated/BottomHalfUV/MegacityMeshRepair/1.21.1-convex/Working/ITHappy_Megacity_Buildings--bar_001.blend` in the pipeline repository. Its SHA-256 is identical to the product's pinned private copy: `d014869e992fc4d80b7c898e4acc173d1038e5c6fa10f26e4bccb392716b4f87`. The earlier Phase0.21 physics export has a different source SHA (`3b9de422…16ea2a`) and was not reused by filename.

`Tools/Export-Compact16uv-Physics.py` opens the pinned blend with Blender 4.5.13 LTS, factory startup and auto-execution disabled. It never saves the blend. It reads the authored UCX and its object transform, applies owner inverse and the explicit `(-x,y,z)` imported-Mesh-local basis. This is **not Unity prefab/world space**; the scene integration must supply that separate frame mapping. No fitted transform, hull regeneration, repair, vertex welding or new topology reconstruction is performed.

Before emitting the private fixture it verifies:

- All 5,326 packed display positions match their 2,408 authoring topology vertices, exactly (maximum error 0).
- All 4,388 triangles match the existing topology map with explicit reversed source winding; the three submeshes are retained in Static16.
- One authored convex, 24 vertices / 44 faces / 66 edges: closed opposite edge incidence, connected, Euler characteristic 2, outward nondegenerate faces, positive volume and convexity within scale-aware tolerance.
- All 21 Anchor empties belong to this owner. Their transformed positions are exported, but no Anchor attachment/grounding behavior is tested here.

Convex volume is 1,434.1973982 local cubic metres. Maximum convexity error is `6.41e-16`, versus tolerance `1.38e-5`. Raw geometry remains in ignored `Assets/Licensed/Compact16uvIntake/Resources/AuthoredPhysicsMigration/Megacity.json`. Public `offline.json` contains only aggregates and hashes. Existing source/palette/Static16 files were not rewritten.

One initial exporter assumption (same source/display winding) was rejected: **0 same / 4,388 reversed** triangles. The final gate requires reversed winding explicitly, rather than accepting either orientation automatically. No fixture was emitted by that failed attempt.

## Unity verification

`Compact16uvAuthoredPhysicsTests` pins the fixture hash and checks its source/Static16 hashes. The initial convex and all six child convexes are submitted to `Physics.BakeMesh` with the product cooking flags. This checks completion without Unity error logs; it does not inspect or prove the identity of PhysX's internal cooked hull.

The real convex runs through `ConvexCutOwnerKernel` via the existing Unity Job harness. The actual Static16 display runs through `VpStorageCut` (16B storage, no managed fallback). Both receive the same planes in the same Mesh-local frame. Each next cut uses the preceding positive child. Planes are chosen from display bounds at centre + 0.137 × extent.

| Axis | Plane offset w | Positive / negative physics volume | Committed display vertices |
| --- | ---: | ---: | ---: |
| Z | -7.608755 | 309.780852 / 1124.416539 | 5,518 |
| X | 0.01360642 | 116.316747 / 193.464098 | 5,887 |
| Y | 1.72333145 | 58.695569 / 57.621177 | 6,130 |

Every cut returns `Ok`, both convex sides have positive volume, volume sum agrees with the parent within relative `1e-5`, input hashes remain unchanged and output/scratch guards are intact. Display open contours and managed fallbacks are zero. Seven Python checks cover a valid tetrahedron and rejected open, reversed, duplicated-face, nonfinite, unused-vertex and degenerate inputs. Unity focused/full-suite results are pinned in `summary.json` and sanitized XML. This turn does not repeat Standalone rendering: no shader or runtime code changed.

The test uses a manually driven kernel harness, not the ordinary scene dispatcher/commit loop. Its passing result must not be described as full integrated scene acceptance.

Accepted results: Python **7/7**, focused Unity **1/1**, full EditMode **3,614/3,614**; zero failures, skips or inconclusive cases. Raw Editor logs remain local and ignored; sanitized result XML and source/evidence hashes are committed.

## Reproduction / next gate

Run Blender on the pinned private blend with `--background --factory-startup --disable-autoexec --python-exit-code 2 --python Tools/Export-Compact16uv-Physics.py -- --product-root <worktree> --upstream-blend <current blend> --out <fresh ignored directory> --report <fresh report>`. Existing fixture/report paths are refused, not overwritten. Run `python -B Tools/Test-Compact16uv-Physics.py` and Unity EditMode filter `Compact16uvAuthoredPhysicsTests`. The Unity test is ignored (not passed) if the private fixture is absent; this acceptance requires zero skips.

Next: connect this authoritative shape and Static16 to a private product scene with all three material slots, correct prefab/owner frame and collider lifetime; run ordinary registration → provisional → commit → recut → drained shutdown with images. Check actual Anchor registration separately if enabled. Only after that compare Legacy32/16B Main and memory with matched scene conditions. Character current-pose input, concurrency/deadline load, XR and release diagnostic-variant removal remain separate gates. Main stays unmerged.
