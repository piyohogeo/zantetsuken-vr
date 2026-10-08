#ifndef ZANTETSU_VP_NATIVE_MULTI_DRAW_INCLUDED
#define ZANTETSU_VP_NATIVE_MULTI_DRAW_INCLUDED

// The variant of the two indirect VP shaders that the Direct3D 12 plugin draws (DESIGN 4.5.8; keyword
// VP_NATIVE_MULTIDRAW). One ExecuteIndirect of the plugin issues a run of commands, so the command's number cannot be a
// constant Unity sets per draw (unity_BaseCommandID, which GetCommandID reads). Instead the plugin's command signature
// sets a root constant per command from the command's own argument entry, and the shader reads the entry by that
// number. An entry is 24 bytes: the command's number and then the five indexed arguments in Unity's order (index count,
// instance count, start index, base vertex, start instance). The selection compute shader writes the number beside
// the arguments when it is told to write entries of this stride.
//
// Unity binds nothing in this variant: the plugin binds every buffer, texture and constant from the compiled shader's
// own reflection. No material enables the keyword; the Editor compiles the variant for the plugin alone, so it adds no
// variant to a Player's shaders. Everything after the arguments -- transform, clip record, lighting, shadow -- is the
// code of the ordinary variant.
//
// Requires UnityIndirect.cginc, with UNITY_INDIRECT_DRAW_ARGS defined as IndirectDrawIndexedArgs, included before it.

cbuffer VpNativeCommand
{
    uint _VpNativeCommand;
};

ByteAddressBuffer _VpNativeArguments;

#define VP_NATIVE_ARGUMENT_STRIDE 24u

// The number of the command this instance belongs to: what GetCommandID(0) is in a draw Unity issues.
uint VpNativeCommandId()
{
    return _VpNativeCommand;
}

// Fills the draw's arguments as InitIndirectDrawArgs does, from the plugin's entry; call first in the vertex shader.
void VpNativeInitIndirectDrawArgs()
{
    uint offset = _VpNativeCommand * VP_NATIVE_ARGUMENT_STRIDE + 4u;
    globalIndirectDrawArgs.indexCountPerInstance = _VpNativeArguments.Load(offset);
    globalIndirectDrawArgs.instanceCount = _VpNativeArguments.Load(offset + 4u);
    globalIndirectDrawArgs.startIndex = _VpNativeArguments.Load(offset + 8u);
    globalIndirectDrawArgs.baseVertexIndex = _VpNativeArguments.Load(offset + 12u);
    globalIndirectDrawArgs.startInstance = _VpNativeArguments.Load(offset + 16u);
}

#endif
