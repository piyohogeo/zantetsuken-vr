using System;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Shows several meshes through the Stage 1 VP path from one pool (DESIGN 4.5.1 / 4.5.5): while enabled, each
    /// group's mesh is appended in group order to this component's one CPU pool, the pool is uploaded once to one set
    /// of GPU buffers, and each group's own geometry range is drawn once per frame at every active child of the group's
    /// instances transform with <see cref="VpDirectDraw"/>. A group whose mesh reference resolves to nothing, such as a
    /// licensed mesh not generated in this checkout, is skipped. There is no registry across components, culling or
    /// growth.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VpMultiMeshDisplay : MonoBehaviour
    {
        /// <summary>One mesh and the transform at whose children it is drawn.</summary>
        [Serializable]
        private sealed class Group
        {
            public Mesh mesh;
            public Transform instances;
        }

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField] private Group[] groups = Array.Empty<Group>();
        [SerializeField] private Shader shader;
        [SerializeField] private Color color = Color.white;

        private VpCpuGeometryPool _pool;
        private VpGpuGeometryBuffers _buffers;
        private Material _material;
        private MaterialPropertyBlock _properties;
        private VpGeometryRange[] _ranges;

        private void OnEnable()
        {
            int vertexCount = 0;
            int indexCount = 0;
            bool anyMesh = false;
            foreach (Group group in groups)
            {
                if (group.mesh == null)
                {
                    continue;
                }

                anyMesh = true;
                vertexCount += group.mesh.vertexCount;
                for (int s = 0; s < group.mesh.subMeshCount; s++)
                {
                    indexCount += (int)group.mesh.GetIndexCount(s);
                }
            }

            // Mesh references that all resolve to nothing show nothing and create nothing, as MeshFilters would.
            if (!anyMesh)
            {
                return;
            }

            if (shader == null)
            {
                Debug.LogError(name + ": VpMultiMeshDisplay needs a shader.", this);
                return;
            }

            _pool = new VpCpuGeometryPool(vertexCount, indexCount, Allocator.Persistent);
            _ranges = new VpGeometryRange[groups.Length];
            for (int g = 0; g < groups.Length; g++)
            {
                Mesh mesh = groups[g].mesh;
                if (mesh != null && !_pool.TryAppend(mesh, out _ranges[g]))
                {
                    Debug.LogError(name + ": " + mesh.name + " cannot be converted to VP geometry.", this);
                    Release();
                    return;
                }
            }

            _buffers = new VpGpuGeometryBuffers(Mathf.Max(1, _pool.VertexCount), Mathf.Max(1, _pool.IndexCount));
            _buffers.TryUpload(_pool);
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetColor(BaseColorId, color);
            _properties = new MaterialPropertyBlock();
        }

        private void LateUpdate()
        {
            if (_buffers == null)
            {
                return;
            }

            // One property block, reused only within this component while issuing the draws sequentially.
            int groupCount = Mathf.Min(groups.Length, _ranges.Length);
            for (int g = 0; g < groupCount; g++)
            {
                Group group = groups[g];
                if (group.mesh == null || group.instances == null || _ranges[g].indexCount == 0)
                {
                    continue;
                }

                foreach (Transform instance in group.instances)
                {
                    if (!instance.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    Matrix4x4 objectToWorld = instance.localToWorldMatrix;
                    VpDirectDraw.Render(
                        _material,
                        _properties,
                        _buffers,
                        _ranges[g],
                        objectToWorld,
                        VpDirectDraw.WorldBounds(group.mesh.bounds, objectToWorld),
                        instance.gameObject.layer);
                }
            }
        }

        private void OnDisable()
        {
            Release();
        }

        private void Release()
        {
            _buffers?.Dispose();
            _buffers = null;
            _pool?.Dispose();
            _pool = null;
            _ranges = null;
            if (_material != null)
            {
                Destroy(_material);
                _material = null;
            }
        }
    }
}
