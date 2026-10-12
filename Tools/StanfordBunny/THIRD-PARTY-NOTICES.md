# Stanford Bunny preparation licenses

These licenses have separate scopes; this directory is not uniformly MIT.

| Files | License / source |
| --- | --- |
| `source/**` (official archive and PLY), `generated/*.blend`, derived Unity demo assets | Stanford custom noncommercial terms; `source/STANFORD-NOTICE.txt` |
| `addons/bow_tie_vertex_split/**` | MIT; `addons/bow_tie_vertex_split/LICENSE`. The copyright owner hogehogeo authorized relicensing this copy on 2026-10-12. Earlier releases remain available under their previous license. No ITHappy character/model data is included. |
| Preparation recipes and exporter | MIT; `LICENSE` |
| `addons/convex_decomposition.py` | Apache-2.0, Oliver Nagy; unchanged upstream source from https://github.com/olitheolix/blender-convex-decomposition ; full terms in `addons/CONVEX-DECOMPOSITION-LICENSE.txt`. No upstream NOTICE was supplied with this source. |
| `vhacd/TestVHACD.exe` | V-HACD 4.1, BSD-3-Clause, Khaled Mamou; https://github.com/kmammou/v-hacd ; `vhacd/LICENSE.txt`. Copied from the existing locally built tool. |

The execution adapter (`convex.py`) supplies `-h 32` and converts the solver's
CP932 OBJ comment to UTF-8. The Apache add-on itself is unchanged. The Blender
application is a separate required installation and is not redistributed here.
These tools and their licenses are outside Unity's Assets and are not player content.
Blender's official Extensions platform has its own GPL submission requirement;
this MIT copy is intended for direct source use, not upload to that platform.
