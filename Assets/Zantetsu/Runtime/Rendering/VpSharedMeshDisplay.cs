using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Shows one mesh through the Stage 1 VP path at each of its active child transforms (DESIGN 4.5.1 / 4.5.5): while
    /// enabled, the mesh is converted and uploaded once into this component's CPU pool and GPU buffers, and that one
    /// geometry range is drawn once per child per frame with <see cref="VpDirectDraw"/>. The children carry transforms
    /// only. The geometry is shared within this component; there is no registry across components, culling or growth.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VpSharedMeshDisplay : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField] private Mesh mesh;
        [SerializeField] private Shader shader;
        [SerializeField] private Color color = Color.white;

        private VpCpuGeometryPool _pool;
        private VpGpuGeometryBuffers _buffers;
        private Material _material;
        private MaterialPropertyBlock _properties;
        private VpGeometryRange _range;

        private void OnEnable()
        {
            // A mesh reference that resolves to nothing, such as a licensed mesh not generated in this checkout, shows
            // nothing and creates nothing, as a MeshFilter would.
            if (mesh == null)
            {
                return;
            }

            if (shader == null)
            {
                Debug.LogError(name + ": VpSharedMeshDisplay needs a shader.", this);
                return;
            }

            int indexCount = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                indexCount += (int)mesh.GetIndexCount(s);
            }

            _pool = new VpCpuGeometryPool(mesh.vertexCount, indexCount, Allocator.Persistent);
            if (!_pool.TryAppend(mesh, out _range))
            {
                Debug.LogError(name + ": " + mesh.name + " cannot be converted to VP geometry.", this);
                Release();
                return;
            }

            _buffers = new VpGpuGeometryBuffers(Mathf.Max(1, _range.vertexCount), Mathf.Max(1, _range.indexCount));
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
            foreach (Transform instance in transform)
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
                    _range,
                    objectToWorld,
                    VpDirectDraw.WorldBounds(mesh.bounds, objectToWorld),
                    instance.gameObject.layer);
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
            if (_material != null)
            {
                Destroy(_material);
                _material = null;
            }
        }
    }
}
