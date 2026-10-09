#ifndef ZANTETSU_VP_NATIVE_DIRECT_CLIP_INCLUDED
#define ZANTETSU_VP_NATIVE_DIRECT_CLIP_INCLUDED

// CPU direct indexed draws use firstInstance as the record selector. Quest multiview uses one instance per
// logical record; Single Pass Instanced uses two physical copies and the caller sets the multiplier to two.
// The ordinary indirect variant retains Unity's platform-specific start-instance handling.
uint VpPhysicalInstanceID(uint instanceID)
{
#if defined(VP_NATIVE_DIRECT)
    return instanceID;
#else
    return GetIndirectInstanceID_Base(instanceID);
#endif
}

#if defined(VP_NATIVE_DIRECT)
#if defined(VP_NO_CLIP)
    #error VP_NATIVE_DIRECT is the clipped direct variant; it cannot use VP_NO_CLIP.
#endif

// Exactly 208 bytes per record, 6656 bytes for all 32 records. Matrices use the same column-major memory layout
// as Unity's Matrix4x4. No visible list, indirect arguments or instance-transform/clip storage buffer is read.
struct VpDirectClipRecord
{
    float4x4 objectToWorld;
    float4 planes[8];
    float4 planeCount; // x is the number of valid planes; the remaining components are padding.
};

cbuffer VpNativeDirect
{
    VpDirectClipRecord _VpDirectClipRecords[32];
};

VpInstanceClip VpDirectClipState(uint logicalInstance)
{
    VpInstanceClip state = (VpInstanceClip)0;
    [unroll] for (uint i = 0; i < 8; ++i)
        state.planes[i] = _VpDirectClipRecords[logicalInstance].planes[i];
    state.planeCount = _VpDirectClipRecords[logicalInstance].planeCount.x;
    return state;
}
#endif
#endif
