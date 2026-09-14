using System.Runtime.InteropServices;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// AoS vertex of the vertex-pulling pool (DESIGN 4.5.1). The attribute set and stride are fixed at this
    /// implementation point: position, normal, uv0, 32 bytes. Adding an attribute means updating this struct, the mesh
    /// conversion and the vertex-pulling shader together.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VpRenderVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv0;

        public const int Stride = 32;
    }
}
