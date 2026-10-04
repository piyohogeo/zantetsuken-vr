# Reference asset intake

These tools implement the intake boundary in DESIGN 10.2.3. Licensed assets,
derivatives and asset-specific reports belong in the private asset repository,
not this code repository. Registration is not product adoption or Gate approval.

## Identity and explicit inputs

`Register-ReferenceAsset.ps1` stores entities under `<private>/Working/Phase0.21`.
The FBX and required textures determine the original Asset SHA. Optional processing
inputs also enter the identity, with their role, target, filename and content hash.
An absent or empty input list preserves the original FBX-plus-textures identity.
Source blends and export records are references, excluded from the Asset SHA.
Dataset revisions identify sorted sets of Asset SHAs; earlier revisions are retained.

An input descriptor is a JSON array, for example:

```json
[{"role":"cut-physics","target":"scene","path":"example.physics.json"}]
```

Paths are relative to the descriptor, or absolute. The cut-draft sidecar explicitly
maps scene objects, rigs, bones, convex hulls and anchors; consumers must not infer
these roles from unrelated neighbouring files.

## Convex/anchor drafts from Blender

Use Blender 4.5 with scripts disabled. The exporter opens but never saves originals.
Use a fresh, private output directory for each run.

```powershell
& $blender --background --factory-startup --disable-autoexec --python-exit-code 2 `
    --python .\Tools\ReferenceIntake\export_cut_draft.py -- `
    --sources $sourceWorkingDirectory --out $privateExportDirectory

& .\Tools\ReferenceIntake\Register-ExportedCutDrafts.ps1 `
    -Root $privateRepository -SourceRoot $sourceWorkingDirectory `
    -ExportRoot $privateExportDirectory -DatasetName $datasetName

& .\Tools\ReferenceIntake\Register-ReferenceAsset.ps1 -Root $privateRepository `
    resolve -Dataset $datasetName -Revision $revision
```

The exporter selects SOURCE render meshes, rigs and necessary parents. Convex
hulls and rig control meshes are not extra render geometry. Required packed or
external textures are copied; missing textures are reported, not substituted.

Character hull attachments retain the bone name and the intermediate Empty's
transform through `localToAttachment`. Replacing this with a bone name alone loses
the bone-tail/parent correction. Static hulls and anchors retain their object owner.
Character drafts with no anchors remain anchor-free; no ground anchors are invented.

`*.physics.json` uses right-handed Blender Z-up coordinates in metres. Matrices
are row-major and act on column vectors. For a bone hull its world transform is
`rigWorld * bonePose * localToAttachment`; for a static hull it is
`ownerWorld * localToAttachment`. The stored world transform is a reference copy.
FBX has its own axis/unit metadata. Convert the sidecar explicitly into the chosen
consumer frame, consistently with the imported rig/geometry.

The FBX retains skeletal and skin inputs, not the Rigify constraints, drivers or
animation-editing environment. This is not an animation bake. Rest/pose matrices
and the original blend remain available. The existing lightweight
`FbxGeometryImport` does not evaluate model transforms or skinning; feeding these
rigged FBXs into that raw-geometry reader alone is not a posed-character import.

## Limited registered-data consumer

```powershell
& $blender --background --factory-startup --disable-autoexec --python-exit-code 2 `
    --python .\Tools\ReferenceIntake\check_registered_cut_draft.py -- `
    --root $privateRepository --asset-sha $assetSha --out $newPrivateReport `
    --plane 0 0 1 0.01
```

The checker reads only registered FBX, textures and explicit sidecars, not source
blends. It reconstructs hull attachments and anchor positions, classifies anchors
against the specified plane, runs a limited offline convex check, and imports the
FBX to check bounds, bone/binding correspondence, attachment placement and textures.
It does not prove per-vertex skin-weight equivalence or general animation behaviour.
The convex tolerance is recorded; failures are retained without repair or automatic
tolerance widening. A failed draft check returns a nonzero exit code but does not
delete its registration or change standard test inputs/results.

This is an intake consumer, not a Runtime/PhysX/cooking/Cut DAG/Geometry Commit or
rendering test. Later consumers should pin the dataset revision or Asset SHA and
use the registered inputs. Re-exported FBX bytes can change (for example timestamps);
the identity names the actual exported content, not an assumed deterministic bake.

Synthetic registry regression: `Test-ReferenceAssetRegistration.ps1`.
Stored-blob integrity: `Register-ReferenceAsset.ps1 -Root $privateRepository verify`.
