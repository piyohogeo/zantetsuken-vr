# The katana's model: source

`aikuchi_original.blend` is the source of the sword the game shows as the katana (DESIGN 19.1.11): an original
aikuchi-style sword made for this project -- no tsuba, a slim blade with a gentle curve. It is not licensed
material. Blender 4.5; metres; the origin at the grip's centre, +Y toward the tip, X across the blade's width, Z
through its thickness. The collection `Sword` holds the three meshes the game uses (blade 0.75 m, grip 0.26 m,
habaki: 900 triangles); `Scabbard` and `Studio` are not exported.

This folder's name ends with `~`, so Unity does not import it: the game reads the exported files beside it.

## Exporting again

The FBX keeps the absolute paths of the .blend and of the textures beside it (the exporter writes them whatever
`path_mode` is; the script's `STRIP` only shortens the relative ones). So export from a folder whose path says
nothing about the machine or its user: copy the .blend and the script into an empty folder and give it a drive
letter of its own,

    subst Z: <that folder>
    blender --background --factory-startup Z:\aikuchi_original.blend --python Z:\export_sword.py -- Z:\out
    subst Z: /D

which writes `aikuchi_sword.fbx` and the four textures (`aikuchi_blade_basecolor.png`, `aikuchi_blade_normal.png`,
`aikuchi_blade_metallic_smoothness.png`, `aikuchi_wood_basecolor.png`). Copy them over the files in
`Assets/Zantetsu/Sandbox/Katana/`, then in Unity run **Zantetsu > Make the katana model prefab**
(`KatanaModelSetup.Build`): it sets the import settings, the three materials and the prefab
`Assets/Zantetsu/Runtime/Sandbox/Resources/ZantetsuKatanaModel.prefab`, whose root has the katana root's frame
(+Z the blade's chord from its base, -Y the edge, +X the side normal).

The metallic map is made by the export script from the blade's roughness image (metallic 0.98 in R, smoothness =
1 - roughness in A).
