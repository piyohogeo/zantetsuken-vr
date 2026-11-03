using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The bake probe's known small rig (DESIGN 4.5.14): a tube of 7 rings of 9 vertices (63: not a multiple of the
    /// kernel's group of 64) along +Y over 4 bones at 0, 0.5, 1.0 and 1.5 m, with 1, 2 and 4 influences a vertex and
    /// weights a byte cannot hold exactly (0.3 / 0.7, 0.4 / 0.3 / 0.2 / 0.1), radial normals of irrational components
    /// and UVs off the byte centres -- the inputs a 24-byte quantisation would change -- drawn by a SkinnedMeshRenderer.
    /// Three deterministic poses: the bind pose, a bend and a strong bend with the root moved and turned.
    /// </summary>
    public sealed class SkinBakeProbeRig : IDisposable
    {
        public const int Rings = 7, PerRing = 9, VertexCount = Rings * PerRing, BoneCount = 4;
        public const float Radius = 0.15f, Height = 1.5f;

        public GameObject Root { get; }
        public SkinnedMeshRenderer Renderer { get; }
        public Mesh Mesh { get; }
        public Transform[] Bones { get; }
        private bool _disposed;

        public SkinBakeProbeRig(string name, Material material)
        {
            Root = new GameObject(name);
            Bones = new Transform[BoneCount];
            Transform parent = Root.transform;
            for (int b = 0; b < BoneCount; b++)
            {
                var bone = new GameObject("bone " + b);
                bone.transform.SetParent(parent, false);
                bone.transform.localPosition = new Vector3(0f, b == 0 ? 0f : Height / (BoneCount - 1), 0f);
                Bones[b] = bone.transform;
                parent = bone.transform;
            }

            var holder = new GameObject("skin");
            holder.transform.SetParent(Root.transform, false);
            Mesh = BuildMesh();
            var bind = new Matrix4x4[BoneCount];
            for (int b = 0; b < BoneCount; b++) bind[b] = Bones[b].worldToLocalMatrix * holder.transform.localToWorldMatrix;
            Mesh.bindposes = bind;
            Renderer = holder.AddComponent<SkinnedMeshRenderer>();
            Renderer.sharedMesh = Mesh;
            Renderer.bones = Bones;
            Renderer.rootBone = Bones[0];
            Renderer.sharedMaterial = material;
            Renderer.updateWhenOffscreen = true;
        }

        private static Mesh BuildMesh()
        {
            var mesh = new Mesh { name = "skin bake probe rig" };
            var positions = new Vector3[VertexCount];
            var normals = new Vector3[VertexCount];
            var uv = new Vector2[VertexCount];
            var counts = new NativeArray<byte>(VertexCount, Allocator.Temp);
            var weights = new NativeList<BoneWeight1>(VertexCount * 4, Allocator.Temp);
            try
            {
                for (int r = 0; r < Rings; r++)
                {
                    float h = Height * r / (Rings - 1);
                    for (int j = 0; j < PerRing; j++)
                    {
                        int v = r * PerRing + j;
                        float theta = 2f * Mathf.PI * j / PerRing + 0.123f;
                        var radial = new Vector3(Mathf.Cos(theta), 0.17f * Mathf.Sin(3f * theta + 0.4f), Mathf.Sin(theta));
                        positions[v] = new Vector3(Radius * Mathf.Cos(theta), h, Radius * Mathf.Sin(theta));
                        normals[v] = radial.normalized;
                        uv[v] = new Vector2((j + 0.37f) / PerRing, 0.02f + 0.96f * h / Height);
                        // Influences by ring: the ends on one bone; the middle ring on all four; the others on the two bones around them.
                        switch (r)
                        {
                            case 0: counts[v] = 1; weights.Add(new BoneWeight1 { boneIndex = 0, weight = 1f }); break;
                            case Rings - 1: counts[v] = 1; weights.Add(new BoneWeight1 { boneIndex = BoneCount - 1, weight = 1f }); break;
                            case 3:
                                counts[v] = 4;
                                weights.Add(new BoneWeight1 { boneIndex = 1, weight = 0.4f }); weights.Add(new BoneWeight1 { boneIndex = 2, weight = 0.3f });
                                weights.Add(new BoneWeight1 { boneIndex = 0, weight = 0.2f }); weights.Add(new BoneWeight1 { boneIndex = 3, weight = 0.1f });
                                break;
                            default:
                            {
                                // The two bones around the height, the nearer one heavier: 0.7 / 0.3.
                                float t = h / (Height / (BoneCount - 1));
                                int lower = Mathf.Clamp(Mathf.FloorToInt(t), 0, BoneCount - 2);
                                bool nearLower = t - lower < 0.5f;
                                counts[v] = 2;
                                weights.Add(new BoneWeight1 { boneIndex = nearLower ? lower : lower + 1, weight = 0.7f });
                                weights.Add(new BoneWeight1 { boneIndex = nearLower ? lower + 1 : lower, weight = 0.3f });
                                break;
                            }
                        }
                    }
                }

                var triangles = new int[(Rings - 1) * PerRing * 6];
                int t2 = 0;
                for (int r = 0; r < Rings - 1; r++)
                {
                    for (int j = 0; j < PerRing; j++)
                    {
                        int a = r * PerRing + j, b = r * PerRing + (j + 1) % PerRing, c = a + PerRing, d = b + PerRing;
                        triangles[t2++] = a; triangles[t2++] = c; triangles[t2++] = b;
                        triangles[t2++] = b; triangles[t2++] = c; triangles[t2++] = d;
                    }
                }

                mesh.vertices = positions;
                mesh.normals = normals;
                mesh.uv = uv;
                mesh.triangles = triangles;
                mesh.SetBoneWeights(counts, weights.AsArray());
                mesh.RecalculateBounds();
                mesh.UploadMeshData(false);
                return mesh;
            }
            finally
            {
                counts.Dispose();
                weights.Dispose();
            }
        }

        /// <summary>The pose <paramref name="id"/>: 0 the bind pose; 1 a bend; 2 a strong bend with the root moved and turned; others fold the id into the three.</summary>
        public void SetPose(int id)
        {
            ThrowIfDisposed();
            int k = ((id % 3) + 3) % 3;
            Root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            for (int b = 0; b < BoneCount; b++) Bones[b].localRotation = Quaternion.identity;
            if (k == 1)
            {
                Bones[1].localRotation = Quaternion.Euler(0f, 0f, 35f);
                Bones[2].localRotation = Quaternion.Euler(0f, 0f, -50f);
                Bones[3].localRotation = Quaternion.Euler(25f, 0f, 0f);
                Root.transform.SetPositionAndRotation(new Vector3(0.2f, 0f, 0.1f), Quaternion.Euler(0f, 20f, 0f));
            }
            else if (k == 2)
            {
                Bones[1].localRotation = Quaternion.Euler(0f, 0f, 85f);
                Bones[2].localRotation = Quaternion.Euler(0f, 0f, -110f);
                Bones[3].localRotation = Quaternion.Euler(70f, 0f, 0f);
                Root.transform.SetPositionAndRotation(new Vector3(-0.3f, 0.1f, 0.2f), Quaternion.Euler(10f, 140f, 0f));
            }
        }

        /// <summary>Moves the bones and the root away from any pose (what a palette taken before must not follow).</summary>
        public void Disturb()
        {
            ThrowIfDisposed();
            for (int b = 0; b < BoneCount; b++) Bones[b].localRotation = Quaternion.Euler(13f * (b + 1), 7f * b, -29f * b);
            Root.transform.SetPositionAndRotation(new Vector3(1.7f, -0.4f, 3.1f), Quaternion.Euler(33f, 210f, 15f));
        }

        private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SkinBakeProbeRig)); }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Destroy in play, DestroyImmediate in the Editor's edit mode (an EditMode test): Destroy is refused there.
            if (Application.isPlaying) { if (Root != null) UnityEngine.Object.Destroy(Root); if (Mesh != null) UnityEngine.Object.Destroy(Mesh); }
            else { if (Root != null) UnityEngine.Object.DestroyImmediate(Root); if (Mesh != null) UnityEngine.Object.DestroyImmediate(Mesh); }
        }
    }
}
