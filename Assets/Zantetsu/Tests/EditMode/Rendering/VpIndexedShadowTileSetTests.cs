using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Stage 3 probe shadow tiles: a lit top-down view of VP cubes and cylinders of different heights over a Unity ground,
    /// with instances on the tile boundaries, renders the same pixels (colour, occlusion, ground shadows and shadows between
    /// VP instances) with the single shadow batch and with 2x2, 4x2 and 4x4 shadow tiles; tile shadow calls alone draw no
    /// colour; a 960-instance upload splits every instance into exactly one tile whose bounds contain its whole shape, with
    /// the right geometry ranges and transforms and no empty tile or command; failed uploads keep the previous tiles and
    /// disposal releases them.
    /// </summary>
    public class VpIndexedShadowTileSetTests
    {
        private const string ShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const string ShadowShaderName = "Zantetsu/VP Indexed Indirect Shadow Caster";
        private const int ShadowSize = 128;
        private const int ShadowedPixels = 300;
        private const float ShadowedFraction = 0.7f;

        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (Object tracked in _objects)
            {
                if (tracked != null)
                {
                    Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private Material Material(string shaderName, Color color)
        {
            Material material = Track(new Material(Shader.Find(shaderName)));
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            return material;
        }

        private void InEmptyScene(Action body)
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                body();
            }
            finally
            {
                DestroyObjects();
                if (previousSetup != null && previousSetup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
            }
        }

        private (Camera camera, RenderTexture target) LitGroundView()
        {
            Light light = Track(new GameObject("VP Tile Test Sun")).AddComponent<Light>();
            light.transform.rotation = Quaternion.Euler(35f, 30f, 0f);
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;

            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.receiveShadows = true;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;

            RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Tile Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));
            camera.orthographicSize = 4.5f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            return (camera, target);
        }

        private Color32[] RenderAndRead(Camera camera, RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            Texture2D readback = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            return readback.GetPixels32();
        }

        private static int CountDiffering(Color32[] first, Color32[] second)
        {
            int differing = 0;
            for (int i = 0; i < first.Length; i++)
            {
                if (Math.Abs(first[i].r - second[i].r) > 2 || Math.Abs(first[i].g - second[i].g) > 2 || Math.Abs(first[i].b - second[i].b) > 2)
                {
                    differing++;
                }
            }

            return differing;
        }

        private static bool IsMarker(Color32 p)
        {
            return p.g > p.r + 50;
        }

        private static float Brightness(Color32 p)
        {
            return (p.r + p.g + p.b) / 3f;
        }

        /// <summary>
        /// A 5 x 5 grid over [-3, 3] of alternating cubes and cylinders standing on the ground with heights from 0.5 to 2.5,
        /// so taller ones shade shorter neighbours; the middle row and column sit on the 2x2 tile boundaries.
        /// </summary>
        private static (VpIndirectCommand[] commands, Matrix4x4[] transforms) Grid(VpGeometryRange cube, Bounds cubeBounds, VpGeometryRange cylinder, Bounds cylinderBounds)
        {
            var cubes = new List<Matrix4x4>();
            var cylinders = new List<Matrix4x4>();
            for (int row = 0; row < 5; row++)
            {
                for (int column = 0; column < 5; column++)
                {
                    float height = 0.5f + ((row * 3 + column * 7) % 5) * 0.5f;
                    var position = new Vector3(-3f + column * 1.5f, 0f, -3f + row * 1.5f);
                    if ((row + column) % 2 == 0)
                    {
                        cubes.Add(Matrix4x4.TRS(position + Vector3.up * height * 0.5f, Quaternion.Euler(0f, row * 20f, 0f), new Vector3(0.8f, height, 0.8f)));
                    }
                    else
                    {
                        cylinders.Add(Matrix4x4.TRS(position + Vector3.up * height * 0.5f, Quaternion.identity, new Vector3(0.7f, height * 0.5f, 0.7f)));
                    }
                }
            }

            return (
                new[] { new VpIndirectCommand(cube, cubeBounds, cubes.Count), new VpIndirectCommand(cylinder, cylinderBounds, cylinders.Count) },
                cubes.Concat(cylinders).ToArray());
        }

        private enum Draw
        {
            SingleBatch,
            ForwardOnly,
            Tiles,
            TileShadowsOnly,
        }

        /// <summary>Renders the lit grid with the chosen issue; tilesX / tilesZ apply to the tile issues.</summary>
        private Color32[] RenderGrid(Draw draw, int tilesX = 1, int tilesZ = 1, bool withVp = true)
        {
            (Camera camera, RenderTexture target) = LitGroundView();
            if (!withVp)
            {
                return RenderAndRead(camera, target);
            }

            Material forward = Material(ShaderName, Color.green);
            Material shadow = Material(ShadowShaderName, Color.green);
            Mesh cubeMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            Mesh cylinderMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            int vertices = cubeMesh.vertexCount + cylinderMesh.vertexCount;
            int indices = (int)(cubeMesh.GetIndexCount(0) + cylinderMesh.GetIndexCount(0));
            using (var pool = new VpCpuGeometryPool(vertices, indices, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(vertices, indices))
            using (var batch = new VpIndexedIndirectDrawBatch(2, 25))
            using (var tiles = new VpIndexedShadowTileSet(tilesX, tilesZ))
            {
                Assert.That(pool.TryAppend(cubeMesh, out VpGeometryRange cube), Is.True);
                Assert.That(pool.TryAppend(cylinderMesh, out VpGeometryRange cylinder), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                (VpIndirectCommand[] commands, Matrix4x4[] transforms) = Grid(cube, cubeMesh.bounds, cylinder, cylinderMesh.bounds);
                Assert.That(batch.TryUpload(commands, transforms, false), Is.True);
                Assert.That(tiles.TryUpload(commands, transforms), Is.True);
                var properties = new MaterialPropertyBlock();
                switch (draw)
                {
                    case Draw.SingleBatch:
                        batch.Render(forward, shadow, properties, buffers, 0, camera);
                        break;
                    case Draw.ForwardOnly:
                        batch.RenderForward(forward, properties, buffers, 0, 0, batch.CommandCount, camera);
                        break;
                    case Draw.Tiles:
                        Assert.That(tiles.TileBatchCount, Is.EqualTo(tilesX * tilesZ), "every tile holds an instance");
                        tiles.Render(batch, forward, shadow, properties, buffers, 0, camera);
                        break;
                    default:
                        tiles.RenderShadows(shadow, buffers, 0, camera);
                        break;
                }

                return RenderAndRead(camera, target);
            }
        }

        [Test]
        public void ShadowTiles_RenderTheSamePixelsAsTheSingleShadowBatch()
        {
            InEmptyScene(() =>
            {
                Color32[] single = RenderGrid(Draw.SingleBatch);
                DestroyObjects();
                Color32[] forwardOnly = RenderGrid(Draw.ForwardOnly);
                DestroyObjects();

                int marker = single.Count(IsMarker);
                float lit = forwardOnly.Where(p => !IsMarker(p)).Average(Brightness);
                int groundShadow = Enumerable.Range(0, single.Length).Count(i => !IsMarker(single[i]) && Brightness(single[i]) < lit * ShadowedFraction
                                                                                 && !(Brightness(forwardOnly[i]) < lit * ShadowedFraction));
                int vpShaded = Enumerable.Range(0, single.Length).Count(i => IsMarker(single[i]) && IsMarker(forwardOnly[i]) && single[i].g + 20 < forwardOnly[i].g);
                Assert.That(marker, Is.GreaterThan(1000), "VP instances drawn");
                Assert.That(groundShadow, Is.GreaterThan(ShadowedPixels), "ground shadows cast by the single batch");
                Assert.That(vpShaded, Is.GreaterThan(20), "VP instances shaded by other VP instances");

                foreach ((int x, int z) in new[] { (2, 2), (4, 2), (4, 4) })
                {
                    Color32[] tiled = RenderGrid(Draw.Tiles, x, z);
                    DestroyObjects();
                    Assert.That(CountDiffering(single, tiled), Is.Zero, x + "x" + z + " tiles: pixels differing from the single shadow batch");
                }
            });
        }

        [Test]
        public void TileShadowCallsAlone_CastShadowsWithoutDrawingColour()
        {
            InEmptyScene(() =>
            {
                Color32[] withoutVp = RenderGrid(Draw.SingleBatch, withVp: false);
                DestroyObjects();
                Color32[] shadowsOnly = RenderGrid(Draw.TileShadowsOnly, 4, 4);

                float lit = withoutVp.Average(Brightness);
                Assert.That(shadowsOnly.Count(IsMarker), Is.Zero, "colour drawn by tile shadow calls");
                Assert.That(Enumerable.Range(0, shadowsOnly.Length).Count(i => Brightness(shadowsOnly[i]) < lit * ShadowedFraction), Is.GreaterThan(ShadowedPixels), "shadowed ground pixels");
            });
        }

        private static Matrix4x4[] GridTransforms(int columns, int rows, Func<int, int, bool> keep)
        {
            var transforms = new List<Matrix4x4>();
            for (int row = 0; row < rows; row++)
            {
                for (int column = 0; column < columns; column++)
                {
                    if (keep(column, row))
                    {
                        transforms.Add(Matrix4x4.TRS(new Vector3((column - (columns - 1) * 0.5f) * 1.5f, 0.5f, (row - (rows - 1) * 0.5f) * 1.5f), Quaternion.Euler(0f, column * 13f, 0f), new Vector3(1.4f, 1f, 0.6f)));
                    }
                }
            }

            return transforms.ToArray();
        }

        /// <summary>Whether outer holds inner, allowing for the rounding of Bounds' centre / extents storage.</summary>
        private static bool Holds(Bounds outer, Bounds inner)
        {
            const float tolerance = 1e-4f;
            return inner.min.x >= outer.min.x - tolerance && inner.min.y >= outer.min.y - tolerance && inner.min.z >= outer.min.z - tolerance
                && inner.max.x <= outer.max.x + tolerance && inner.max.y <= outer.max.y + tolerance && inner.max.z <= outer.max.z + tolerance;
        }

        private static string Key(Matrix4x4 m)
        {
            return string.Join(",", Enumerable.Range(0, 16).Select(e => m[e].ToString("R")));
        }

        [Test]
        public void A960InstanceUpload_PutsEveryInstanceInExactlyOneTileWhoseBoundsHoldItsWholeShape()
        {
            var a = new VpGeometryRange(0, 24, 0, 36);
            var b = new VpGeometryRange(24, 30, 36, 60);
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            Matrix4x4[] all = GridTransforms(40, 24, (c, r) => true);
            Matrix4x4[] ofA = all.Where((m, i) => i % 2 == 0).ToArray();
            Matrix4x4[] ofB = all.Where((m, i) => i % 2 == 1).ToArray();
            VpIndirectCommand[] commands = { new VpIndirectCommand(a, bounds, ofA.Length), new VpIndirectCommand(b, bounds, ofB.Length) };
            Matrix4x4[] transforms = ofA.Concat(ofB).ToArray();
            var geometryOf = new Dictionary<string, int>();
            for (int i = 0; i < transforms.Length; i++)
            {
                geometryOf[Key(transforms[i])] = i < ofA.Length ? 0 : 1;
            }

            foreach ((int tilesX, int tilesZ) in new[] { (2, 2), (4, 2), (4, 4) })
            {
                using (var set = new VpIndexedShadowTileSet(tilesX, tilesZ))
                {
                    Assert.That(set.TryUpload(commands, transforms), Is.True);
                    string label = tilesX + "x" + tilesZ;
                    Assert.That(set.TileBatchCount, Is.EqualTo(tilesX * tilesZ), label + " tiles");
                    Assert.That(set.InstanceCount, Is.EqualTo(960), label + " instances");

                    var seen = new HashSet<string>();
                    int logical = 0;
                    var cells = new HashSet<int>();
                    for (int t = 0; t < set.TileBatchCount; t++)
                    {
                        VpIndexedIndirectDrawBatch tile = set.GetTile(t);
                        Assert.That(cells.Add(set.GetTileCell(t)), Is.True, label + " cells are distinct");
                        logical += tile.InstanceCount;
                        var arguments = new GraphicsBuffer.IndirectDrawIndexedArgs[tile.CommandCount];
                        tile.ShadowArgumentBuffer.GetData(arguments, 0, 0, arguments.Length);
                        var uploaded = new Matrix4x4[tile.InstanceCount];
                        tile.InstanceBuffer.GetData(uploaded, 0, 0, uploaded.Length);
                        int start = 0;
                        foreach (GraphicsBuffer.IndirectDrawIndexedArgs argument in arguments)
                        {
                            Assert.That(argument.instanceCount, Is.GreaterThan(0u), label + " no empty command");
                            Assert.That(argument.startInstance, Is.EqualTo((uint)start), label + " contiguous instances");
                            Assert.That(argument.baseVertexIndex, Is.Zero);
                            int geometry = argument.startIndex == (uint)a.indexStart ? 0 : 1;
                            Assert.That(argument.indexCountPerInstance, Is.EqualTo((uint)(geometry == 0 ? a.indexCount : b.indexCount)), label + " range");
                            for (int k = 0; k < argument.instanceCount; k++)
                            {
                                Matrix4x4 m = uploaded[start + k];
                                Assert.That(geometryOf[Key(m)], Is.EqualTo(geometry), label + " transform drawn with its own geometry");
                                Assert.That(seen.Add(Key(m)), Is.True, label + " instance in one tile only");
                                Bounds shape = VpDirectDraw.WorldBounds(bounds, m);
                                Assert.That(Holds(tile.WorldBounds, shape), Is.True, label + " tile bounds hold the whole shape");
                            }

                            start += (int)argument.instanceCount;
                        }
                    }

                    Assert.That(logical, Is.EqualTo(960), label + " logical instances over the tiles");
                    Assert.That(seen.Count, Is.EqualTo(960), label + " distinct instances over the tiles");
                }
            }
        }

        [Test]
        public void AnInstanceAcrossATileBoundary_IsKeptWholeInOneTile()
        {
            // Three long slabs along X at x = -2, 0 and 2; the middle one crosses the 2x1 boundary at x = 0.
            var range = new VpGeometryRange(0, 24, 0, 36);
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            Matrix4x4[] transforms =
            {
                Matrix4x4.TRS(new Vector3(-2f, 0f, 0f), Quaternion.identity, new Vector3(1f, 1f, 1f)),
                Matrix4x4.TRS(new Vector3(0f, 0f, 0f), Quaternion.identity, new Vector3(3f, 1f, 1f)),
                Matrix4x4.TRS(new Vector3(2f, 0f, 0f), Quaternion.identity, new Vector3(1f, 1f, 1f)),
            };
            using (var set = new VpIndexedShadowTileSet(2, 1))
            {
                Assert.That(set.TryUpload(new[] { new VpIndirectCommand(range, bounds, 3) }, transforms), Is.True);
                Assert.That(set.TileBatchCount, Is.EqualTo(2));
                Bounds middle = VpDirectDraw.WorldBounds(bounds, transforms[1]);
                int holding = 0;
                for (int t = 0; t < set.TileBatchCount; t++)
                {
                    VpIndexedIndirectDrawBatch tile = set.GetTile(t);
                    var uploaded = new Matrix4x4[tile.InstanceCount];
                    tile.InstanceBuffer.GetData(uploaded, 0, 0, uploaded.Length);
                    if (uploaded.Contains(transforms[1]))
                    {
                        holding++;
                        Assert.That(Holds(tile.WorldBounds, middle), Is.True, "the whole crossing slab is in its tile's bounds");
                        Assert.That(tile.WorldBounds.min.x, Is.LessThanOrEqualTo(-1.5f), "bounds reach across the boundary");
                    }
                }

                Assert.That(holding, Is.EqualTo(1), "tiles holding the crossing slab");
            }
        }

        [Test]
        public void EmptyTilesAndEmptyCommands_AreNotBuilt()
        {
            // Only the left half of the grid, and only geometry A: a 2x2 split has two tiles of one command each.
            var a = new VpGeometryRange(0, 24, 0, 36);
            var b = new VpGeometryRange(24, 30, 36, 60);
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            Matrix4x4[] left = GridTransforms(40, 24, (c, r) => c < 20);
            Matrix4x4[] far = { Matrix4x4.Translate(new Vector3(60f, 0.5f, -17f)) };
            using (var set = new VpIndexedShadowTileSet(2, 2))
            {
                // One far instance on the right widens the grid so the right column's upper tile is empty.
                Assert.That(set.TryUpload(new[] { new VpIndirectCommand(a, bounds, left.Length), new VpIndirectCommand(b, bounds, 0), new VpIndirectCommand(a, bounds, 1) }, left.Concat(far).ToArray()), Is.True);
                Assert.That(set.TileBatchCount, Is.EqualTo(3), "non-empty tiles");
                Assert.That(Enumerable.Range(0, 3).Select(set.GetTileCell), Is.EquivalentTo(new[] { 0, 2, 1 }), "cells: both left tiles and the lower right one");
                for (int t = 0; t < set.TileBatchCount; t++)
                {
                    VpIndexedIndirectDrawBatch tile = set.GetTile(t);
                    var arguments = new GraphicsBuffer.IndirectDrawIndexedArgs[tile.CommandCount];
                    tile.ShadowArgumentBuffer.GetData(arguments, 0, 0, arguments.Length);
                    Assert.That(arguments.All(argument => argument.instanceCount > 0 && argument.startIndex == (uint)a.indexStart), Is.True, "only geometry A commands with instances");
                }

                Assert.That(Enumerable.Range(0, 3).Sum(t => set.GetTile(t).InstanceCount), Is.EqualTo(left.Length + 1));
            }
        }

        [Test]
        public void FailedUploadsKeepThePreviousTiles_AndReuploadAndDisposeReleaseThemOnce()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndexedShadowTileSet(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VpIndexedShadowTileSet(1, 0));

            var range = new VpGeometryRange(0, 24, 0, 36);
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            Matrix4x4[] transforms = GridTransforms(4, 4, (c, r) => true);
            VpIndirectCommand[] commands = { new VpIndirectCommand(range, bounds, transforms.Length) };
            Material forward = Material(ShaderName, Color.green);
            Material shadow = Material(ShadowShaderName, Color.green);
            var set = new VpIndexedShadowTileSet(2, 2);
            using (var buffers = new VpGpuIndexedGeometryBuffers(24, 36))
            using (var forwardBatch = new VpIndexedIndirectDrawBatch(1, transforms.Length))
            {
                Assert.That(set.TryUpload(commands, transforms), Is.True);
                GraphicsBuffer[] first = Enumerable.Range(0, set.TileBatchCount).SelectMany(t => new[] { set.GetTile(t).ShadowArgumentBuffer, set.GetTile(t).InstanceBuffer }).ToArray();

                Assert.That(set.TryUpload(commands, transforms.Take(3).ToArray()), Is.False, "fewer transforms than instances");
                Assert.That(set.TryUpload(new[] { new VpIndirectCommand(range, bounds, -1) }, new Matrix4x4[0]), Is.False, "negative instance count");
                Assert.That(set.TryUpload(new[] { new VpIndirectCommand(new VpGeometryRange(0, 24, -1, 36), bounds, 1) }, new[] { Matrix4x4.identity }), Is.False, "negative index start");
                Assert.Throws<ArgumentNullException>(() => set.TryUpload(null, transforms));
                Assert.That(first.All(buffer => buffer.IsValid()), Is.True, "previous tile buffers kept after failed uploads");
                Assert.That(new[] { set.TileBatchCount, set.InstanceCount }, Is.EqualTo(new[] { 4, 16 }));

                Assert.That(set.TryUpload(commands, transforms), Is.True, "upload again");
                Assert.That(first.Any(buffer => buffer.IsValid()), Is.False, "replaced tile buffers released");
                GraphicsBuffer[] second = Enumerable.Range(0, set.TileBatchCount).SelectMany(t => new[] { set.GetTile(t).ShadowArgumentBuffer, set.GetTile(t).InstanceBuffer }).ToArray();
                Assert.That(set.TryUpload(new VpIndirectCommand[0], new Matrix4x4[0]), Is.True, "empty upload");
                Assert.That(new[] { set.TileBatchCount, set.InstanceCount }, Is.EqualTo(new[] { 0, 0 }));
                Assert.That(second.Any(buffer => buffer.IsValid()), Is.False, "tiles released by the empty upload");

                Assert.That(set.TryUpload(commands, transforms), Is.True);
                GraphicsBuffer[] third = Enumerable.Range(0, set.TileBatchCount).SelectMany(t => new[] { set.GetTile(t).ShadowArgumentBuffer, set.GetTile(t).InstanceBuffer }).ToArray();
                set.Dispose();
                Assert.That(third.Any(buffer => buffer.IsValid()), Is.False, "tiles released by Dispose");
                Assert.That(new[] { buffers.VertexBuffer.IsValid(), buffers.IndexBuffer.IsValid(), forwardBatch.InstanceBuffer.IsValid() }, Is.EqualTo(new[] { true, true, true }), "geometry buffers and forward batch are not owned");
                Assert.Throws<ObjectDisposedException>(() => set.TryUpload(commands, transforms));
                Assert.Throws<ObjectDisposedException>(() => set.Render(forwardBatch, forward, shadow, new MaterialPropertyBlock(), buffers, 0));
                Assert.Throws<ObjectDisposedException>(() => _ = set.TileBatchCount);
                Assert.DoesNotThrow(set.Dispose, "dispose again");
            }
        }
    }
}
