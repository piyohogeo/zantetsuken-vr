using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Shows one mesh through the Stage 1 VP path (DESIGN 4.5.5): while enabled, the mesh lives in its own CPU pool
    /// and GPU buffers and is drawn once per frame with <see cref="VpDirectDraw"/> at this transform. Meant for a few
    /// meshes; it has no shared pool, shadow receiving or growth.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VpMeshDisplay : MonoBehaviour
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
            if (mesh == null || shader == null)
            {
                Debug.LogError(name + ": VpMeshDisplay needs a mesh and a shader.", this);
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

            Matrix4x4 objectToWorld = transform.localToWorldMatrix;
            VpDirectDraw.Render(
                _material,
                _properties,
                _buffers,
                _range,
                objectToWorld,
                WorldBounds(mesh.bounds, objectToWorld),
                gameObject.layer);
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

        private static Bounds WorldBounds(Bounds local, Matrix4x4 objectToWorld)
        {
            Vector3 e = local.extents;
            var extents = new Vector3(
                Mathf.Abs(objectToWorld.m00) * e.x + Mathf.Abs(objectToWorld.m01) * e.y + Mathf.Abs(objectToWorld.m02) * e.z,
                Mathf.Abs(objectToWorld.m10) * e.x + Mathf.Abs(objectToWorld.m11) * e.y + Mathf.Abs(objectToWorld.m12) * e.z,
                Mathf.Abs(objectToWorld.m20) * e.x + Mathf.Abs(objectToWorld.m21) * e.y + Mathf.Abs(objectToWorld.m22) * e.z);
            return new Bounds(objectToWorld.MultiplyPoint3x4(local.center), extents * 2f);
        }
    }
}
