# Compact16uv product shader audit

Source repository: `C:\Users\junic\src\zantetsuken-vr-compact16uv`
Pinned commit: `985fb3a4ddcfae2e9f020c16773c49f37d3ede3b`
Previous audit commit: `7a23a4977f27f08b8eebdc826a19cc5d74dfcc84`

Result: **PASS**

Only Git blobs from the pinned commit are accepted as shader evidence. Dirty or untracked working-tree contents are never hashed as accepted input.

## Pinned shader surface

| path | blob | bytes | passes | references | migration |
|---|---|---:|---|---:|---|
| `VpIndexedIndirectUnlit.shader` | `7b5e701b6dc63f1aecb2b27da8633dfae8ea7581` | 11955 | VpIndexedIndirectForward | 24 | 16B layout + oct/UV decode + real-cap slot; product textured forward path |
| `VpIndexedIndirectShadowCaster.shader` | `a0c2c955ace1c82a1497a762291867153c197c5b` | 7360 | ShadowCaster | 11 | 16B layout + oct decode for shadow bias |
| `VpIndirectUnlit.shader` | `b016adc1f6410100e046dfbb1e6762eb73f16077` | 6658 | VpIndirectForward | 5 | 16B layout + oct decode; legacy non-index-buffer forward path |
| `VpIndirectShadowCaster.shader` | `fedfd30f4d0fa24701bef3a3941a808479ace0fe` | 4429 | ShadowCaster | 5 | 16B layout + oct decode; legacy non-index-buffer shadow path |
| `VpStencilInit.shader` | `2029a229ca2105789cf60842929bf6532df198fa` | 3883 | VpStencilInit | 2 | layout independent; no Compact16uv change |
| `VpStencilVolume.shader` | `49fdf160055f72b390b2cae2c577c840d588749f` | 7697 | VpStencilVolume | 2 | 16B layout; position-only consumer still changes structured stride |
| `VpStencilCap.shader` | `2ed17c8d4fe9e00ba849f534fecd6b55cc1261e4` | 6403 | VpStencilCap | 2 | separate cap buffers; add provisional atlas slot sampling/material contract |
| `VpUnlit.shader` | `2b8aa0e8b5ce37e9e5c41d56bf400730a5499562` | 5578 | VpForward, ShadowCaster | 6 | 16B layout + oct decode in direct forward and ShadowCaster passes |
| `VpCutSurfaceShading.hlsl` | `b5780d335750191d21be9ce8418ee4450796943d` | 1492 | include/no named pass | 0 | layout independent shared lighting include |

All nine pinned blobs are unchanged from the previous audit commit.

## Culled paths

| path | commit status | consequence |
|---|---|---|
| `VpCulledIndexedIndirectUnlit.shader` | **PINNED** | Cannot be accepted or migrated from a reproducible source revision. |
| `VpCulledIndexedShadowCaster.shader` | **PINNED** | Cannot be accepted or migrated from a reproducible source revision. |

## Decision

Pinned shaders: 9/9. Layout readers: 6. Explicit 32-byte declarations: 5.

The tracked product surface can proceed to a Compact16uv integration fixture. Full-pass completion remains stopped until both culled shaders exist in a source commit; their current untracked copies are informational only.
