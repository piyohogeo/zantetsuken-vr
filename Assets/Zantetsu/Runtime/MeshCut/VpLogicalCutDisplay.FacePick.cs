#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>Which of the display's lists holds the registration a draw slot belongs to (FACE-PICK-1, diagnosis only).</summary>
    public enum VpDrawSlotOwnerList
    {
        /// <summary>No registration of the display names the slot: a draw whose owner is lost.</summary>
        None = 0,

        /// <summary>A registration that is shown.</summary>
        Shown = 1,

        /// <summary>A registration a committed cut replaced, drawn until the next adoption.</summary>
        Retiring = 2,

        /// <summary>A registration no longer shown that still holds slots.</summary>
        UnshownWithSlots = 3,
    }

    /// <summary>
    /// The registration a draw slot belongs to, as the display holds it now (FACE-PICK-1, diagnosis only): which list, which entry, which
    /// of its commands, and what the entry says of itself. Values only; nothing of the display is handed out.
    /// </summary>
    public struct VpDrawSlotOwner
    {
        public VpDrawSlotOwnerList list;
        public int index, command, commands;
        public LogicalFragmentId fragment;
        public VpStoredGeometry geometry;
        public Matrix4x4 objectToWorld, lineageToGeometryLocal;
        public Bounds localBounds;
        public bool splitShown, awaitingShown, slotsClipped, gpuRegion;
        public int renderFragmentsShown, firstRenderFragmentShown;
        public int commandStart, instanceStart, instanceStride, slotRenderFragments;
    }

    public sealed partial class VpLogicalCutDisplay
    {
        // ----- FACE-PICK-1 (development and the Editor only): what is being drawn, read for a diagnosis ----------------------
        //
        // The adopted draw data by its slots -- every command slot ever taken, with the instance records it draws -- and not by
        // the registrations: a slot no registration names is read like any other, which is what a diagnosis of a draw whose owner
        // is lost needs. Everything here reads; nothing is collected, uploaded, adopted or changed.

        /// <summary>The adoption count of the draw data read now.</summary>
        public long AdoptedGenerationForDiagnosis => _generation;

        /// <summary>
        /// One instance record of the adopted draw data: the transform and the clip record it is drawn with, and the side it was
        /// settled as. False outside [0, <see cref="DrawInstanceEnd"/>).
        /// </summary>
        public bool TryGetInstanceRecordForDiagnosis(int record, out Matrix4x4 transform, out VpInstanceClip clip, out LogicalCutDisplaySide side)
        {
            if (record < 0 || record >= _instanceEnd)
            {
                transform = default;
                clip = default;
                side = default;
                return false;
            }

            transform = _transforms[record];
            clip = _clips[record];
            side = _sides[record];
            return true;
        }

        /// <summary>The material a command slot is drawn with (null for a free slot), and whether it is cast two-sided.</summary>
        public bool TryGetCommandSlotMaterialForDiagnosis(int slot, out Material material, out bool provisional)
        {
            if (slot < 0 || slot >= _commandEnd)
            {
                material = null;
                provisional = false;
                return false;
            }

            material = _commandMaterials[slot];
            provisional = _commandProvisional[slot];
            return true;
        }

        /// <summary>
        /// Every command slot a registration of the display holds, by the slot's number: the shown registrations, the ones a commit
        /// replaced and the ones no longer shown that still hold slots. A slot not in the result is named by none.
        /// </summary>
        public void CopySlotOwnersForDiagnosis(Dictionary<int, VpDrawSlotOwner> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            AddSlotOwners(_shown, VpDrawSlotOwnerList.Shown, into);
            AddSlotOwners(_retiring, VpDrawSlotOwnerList.Retiring, into);
            AddSlotOwners(_unshownWithSlots, VpDrawSlotOwnerList.UnshownWithSlots, into);
        }

        private void AddSlotOwners(List<Shown> list, VpDrawSlotOwnerList which, Dictionary<int, VpDrawSlotOwner> into)
        {
            for (int g = 0; g < list.Count; g++)
            {
                Shown e = list[g];
                if (!e.slots.held || e.commands == null) continue;
                for (int c = 0; c < e.commands.Length; c++)
                {
                    int slot = e.slots.commandStart + c;
                    if (into.ContainsKey(slot)) continue;   // the shown registration first
                    into[slot] = new VpDrawSlotOwner
                    {
                        list = which, index = g, command = c, commands = e.commands.Length, fragment = e.fragment, geometry = e.geometry,
                        objectToWorld = e.objectToWorld, lineageToGeometryLocal = e.lineageToGeometryLocal, localBounds = e.localBounds,
                        splitShown = e.splitShown, awaitingShown = e.awaitingShown, slotsClipped = e.slots.clipped, gpuRegion = HasGpuRegion(e.geometry),
                        renderFragmentsShown = e.renderFragmentsShown, firstRenderFragmentShown = e.firstRenderFragmentShown,
                        commandStart = e.slots.commandStart, instanceStart = e.slots.instanceStart, instanceStride = e.slots.instanceStride,
                        slotRenderFragments = e.slots.renderFragments,
                    };
                }
            }
        }

        /// <summary>The root fragment of one registration of the adopted snapshot (what a render fragment's registration number names).</summary>
        public bool TryGetRegistrationRootForDiagnosis(int registration, out LogicalFragmentId root)
        {
            if (!_hasSnapshot || registration < 0 || registration >= _registrations.Count)
            {
                root = default;
                return false;
            }

            root = _registrations[registration].root;
            return true;
        }

        /// <summary>
        /// The indices of one draw range, copied: from the storage under a read lease of <paramref name="geometry"/>'s index range
        /// where the range lies in it (<paramref name="source"/> 1), else read back from the display's index buffer -- what the GPU
        /// holds, waited for (2). False with why where neither could be read. <paramref name="into"/> is grown as needed; where the
        /// GPU answered, <paramref name="failure"/> still says why the storage was not read.
        /// </summary>
        public bool TryCopyRangeIndicesForDiagnosis(VpGeometryRange range, bool hasGeometry, VpStoredGeometry geometry, ref uint[] into, out int source, out string failure)
        {
            source = 0;
            failure = null;
            if (range.indexCount <= 0 || range.indexStart < 0)
            {
                failure = "the range holds no index";
                return false;
            }

            if (into == null || into.Length < range.indexCount) into = new uint[Math.Max(range.indexCount, into != null ? into.Length * 2 : 0)];
            string leaseFailure = "no geometry names the range";
            if (hasGeometry)
            {
                if (!_storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out int start, out int count))
                {
                    leaseFailure = "the geometry's index range is not a current one of the storage";
                }
                else if (range.indexStart < start || range.indexStart + range.indexCount > start + count)
                {
                    leaseFailure = "the range [" + range.indexStart + ", +" + range.indexCount + ") is outside the geometry's indices [" + start + ", +" + count + "), state " + state;
                }
                else if (!_storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view))
                {
                    leaseFailure = "the geometry's indices could not be leased (state " + state + ")";
                }
                else
                {
                    try
                    {
                        int offset = range.indexStart - start;
                        if (offset + range.indexCount > view.Length)
                        {
                            leaseFailure = "the lease's view holds " + view.Length + " indices, fewer than the range needs";
                        }
                        else
                        {
                            for (int i = 0; i < range.indexCount; i++) into[i] = view[offset + i];
                            source = 1;
                            return true;
                        }
                    }
                    finally
                    {
                        _storage.TryReleaseIndexReadLease(lease);
                    }
                }
            }

            if (TryReadGpuIndicesForDiagnosis(range.indexStart, range.indexCount, into, out string gpuFailure))
            {
                source = 2;
                failure = leaseFailure;
                return true;
            }

            failure = leaseFailure + "; " + gpuFailure;
            return false;
        }

        /// <summary>A region of the display's index buffer read back and waited for (never in a measured frame). False with why.</summary>
        public bool TryReadGpuIndicesForDiagnosis(int start, int count, uint[] into, out string failure)
        {
            failure = null;
            GraphicsBuffer buffer = _disposed ? null : _buffers.IndexBuffer;
            if (count <= 0 || into == null || into.Length < count) { failure = "no room given for the indices"; return false; }
            if (buffer == null || !SystemInfo.supportsAsyncGPUReadback) { failure = "the index buffer cannot be read back here"; return false; }
            if (start < 0 || start + count > buffer.count) { failure = "the indices [" + start + ", +" + count + ") are outside the index buffer of " + buffer.count; return false; }
            try
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(buffer, count * VpGpuIndexedGeometryBuffers.IndexStride, start * VpGpuIndexedGeometryBuffers.IndexStride);
                request.WaitForCompletion();
                if (request.hasError) { failure = "the index buffer's readback ended in an error"; return false; }
                NativeArray<uint> data = request.GetData<uint>();
                if (data.Length < count) { failure = "the index buffer's readback gave " + data.Length + " of " + count; return false; }
                for (int i = 0; i < count; i++) into[i] = data[i];
                return true;
            }
            catch (Exception e)
            {
                failure = "the index buffer's readback threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>A region of the display's vertex buffer read back and waited for (never in a measured frame). False with why.</summary>
        public bool TryReadGpuVerticesForDiagnosis(int start, int count, VpRenderVertex[] into, out string failure)
        {
            failure = null;
            GraphicsBuffer buffer = _disposed ? null : _buffers.VertexBuffer;
            if (count <= 0 || into == null || into.Length < count) { failure = "no room given for the vertices"; return false; }
            if (buffer == null || !SystemInfo.supportsAsyncGPUReadback) { failure = "the vertex buffer cannot be read back here"; return false; }
            if (start < 0 || start + count > buffer.count) { failure = "the vertices [" + start + ", +" + count + ") are outside the vertex buffer of " + buffer.count; return false; }
            try
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(buffer, count * VpRenderVertex.Stride, start * VpRenderVertex.Stride);
                request.WaitForCompletion();
                if (request.hasError) { failure = "the vertex buffer's readback ended in an error"; return false; }
                NativeArray<VpRenderVertex> data = request.GetData<VpRenderVertex>();
                if (data.Length < count) { failure = "the vertex buffer's readback gave " + data.Length + " of " + count; return false; }
                for (int i = 0; i < count; i++) into[i] = data[i];
                return true;
            }
            catch (Exception e)
            {
                failure = "the vertex buffer's readback threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// The job the last classification of the caps -- of whichever camera prepared last -- made of the prepared cap
        /// <paramref name="capIndex"/>: false where the classification is not of the adopted snapshot or holds no job for the cap
        /// (an empty cap, one judged hidden for that camera, or one not classified at all).
        /// </summary>
        public bool TryGetCapJobForDiagnosis(int capIndex, out VpCapJob job, out int jobIndex, out bool lastColour)
        {
            job = default;
            jobIndex = -1;
            lastColour = false;
            if (!_hasSnapshot || _capJobs == null || !_capJobs.IsFor(_snapshot)) return false;
            for (int j = 0; j < _capJobs.JobCount; j++)
            {
                if (!_capJobs.TryGetJob(j, out VpCapJob at) || at.capIndex != capIndex) continue;
                job = at;
                jobIndex = j;
                lastColour = _capJobs.TryGetColour(at.colour, out VpCapJobColour colour) && colour.last;
                return true;
            }

            return false;
        }
    }
}
#endif
