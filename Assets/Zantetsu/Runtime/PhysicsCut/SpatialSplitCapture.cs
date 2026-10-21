#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The bounds of one capture session of the additional split's inputs (DESIGN 7.9.8): the queue between Main and
    /// the writer, by count and by bytes -- the item being written still counts --, one sample's copy, and what the
    /// session may put on disk. Reaching any of them stops the capture; nothing is thinned out, and the game goes on.
    /// </summary>
    public readonly struct SpatialSplitCaptureLimits
    {
        public readonly int maxQueuedItems;
        public readonly long maxQueuedBytes, maxSampleBytes, maxSessionBytes;

        /// <summary>
        /// 16 items and 256 MiB in the queue, 64 MiB one sample, 2 GiB one session (2026-10-21, from no measurement;
        /// the record perf\split-capture\SPLIT-CAPTURE-1.md says how they follow from the search's limits).
        /// </summary>
        public const int DefaultQueuedItems = 16;
        public const long DefaultQueuedBytes = 256L << 20, DefaultSampleBytes = 64L << 20, DefaultSessionBytes = 2L << 30;

        public SpatialSplitCaptureLimits(int maxQueuedItems, long maxQueuedBytes, long maxSampleBytes, long maxSessionBytes)
        {
            this.maxQueuedItems = maxQueuedItems;
            this.maxQueuedBytes = maxQueuedBytes;
            this.maxSampleBytes = maxSampleBytes;
            this.maxSessionBytes = maxSessionBytes;
        }

        public static SpatialSplitCaptureLimits Default =>
            new SpatialSplitCaptureLimits(DefaultQueuedItems, DefaultQueuedBytes, DefaultSampleBytes, DefaultSessionBytes);

        public bool IsValid => maxQueuedItems >= 1 && maxQueuedBytes >= 1 && maxSampleBytes >= 1 && maxSampleBytes <= maxQueuedBytes && maxSessionBytes >= 1;
    }

    /// <summary>Why an issued attempt's input was not captured, and why the capture stopped.</summary>
    public enum SpatialSplitCaptureRefusal
    {
        None = 0,

        /// <summary>The capture had stopped before, for its own reason (<see cref="SpatialSplitCaptureSink.StopReason"/>).</summary>
        Stopped,

        /// <summary>The session was closed (the application quitting).</summary>
        Closed,

        /// <summary>One sample's copy would be more than <see cref="SpatialSplitCaptureLimits.maxSampleBytes"/>.</summary>
        SampleOverCap,

        /// <summary>The queue holds <see cref="SpatialSplitCaptureLimits.maxQueuedItems"/> already.</summary>
        QueueItems,

        /// <summary>The queue would hold more than <see cref="SpatialSplitCaptureLimits.maxQueuedBytes"/>.</summary>
        QueueBytes,

        /// <summary>The session's files would be more than <see cref="SpatialSplitCaptureLimits.maxSessionBytes"/>.</summary>
        SessionDisk,

        /// <summary>The copy on Main threw.</summary>
        CopyFailed,

        /// <summary>A file could not be made or written.</summary>
        WriteFailed,

        Count,
    }

    /// <summary>Who an issued attempt is: the session's world, the attempt, and the object it is of.</summary>
    internal struct SpatialSplitCaptureIdentity
    {
        internal long world, attempt;
        internal int issuedFrame, fragment, rootFragment, splitSource, generation, epoch;
        internal string objectName;
    }

    /// <summary>
    /// One issued attempt's input, copied on Main into one native block of the capture's own (nothing of the attempt,
    /// the shape, the storage or the lease is referred to): what the numbers read beside it, then the arrays in the order
    /// <see cref="SpatialSplitCaptureCopy"/> writes them. The writer frees the block once the file is written or given up.
    /// </summary>
    internal sealed unsafe class SpatialSplitCaptureSample
    {
        internal SpatialSplitCaptureIdentity identity;
        internal float margin, supportEpsilon, anchorEpsilon;
        internal int vertexLimit;
        internal double parentMass;
        internal SpatialSplitSearchLimits limits;
        internal bool cutConvexes, bentSearch;
        internal int minCutSideTriangles, maxConvexVertices;
        internal SpatialSplitBentLimits bentLimits;
        internal float4x4 geometryToOwner, shapeToOwner;
        internal int submeshCount, anchorCount, indexCount, vertexViewLength, convexCount, vertexStructBytes;
        internal uint spanLo;
        internal int spanCount;
        internal byte* data;
        internal long bytes;
        internal double copySeconds;

        internal void Free()
        {
            if (data != null)
            {
                UnsafeUtility.Free(data, Allocator.Persistent);
                data = null;
            }
        }
    }

    /// <summary>
    /// What an attempt with a captured input came to: how it ended (an end of <see cref="SpatialSplitEnd"/>, or
    /// WorldEnded / Abandoned / AppQuit), the stages it passed -- as they stood at its end --, the search's record, and
    /// -- where the numbers' outputs were read -- their canonical bytes (<see cref="SpatialSplitCanonical"/>), copied on
    /// Main where they are first read and hashed by the writer. The big input is not in it: the result names its sample
    /// by the world and the attempt. Its room in the queue (<see cref="bytes"/>) is reserved before anything is taken
    /// for it and counted until <see cref="Discard"/>, while the attempt holds it as well.
    /// </summary>
    internal sealed unsafe class SpatialSplitCaptureResult
    {
        /// <summary>What a result takes in the queue beside its canonical bytes (the line, its own fields).</summary>
        internal const long Overhead = 4096;

        internal long world, attempt;
        internal int issuedFrame, endFrame;
        internal string end, detail, failure, outputsNotKept;
        internal bool observed, ran, cancelled, distributed, anchorsDistributed, classified, cut, applied, meshesBad, hasClassification;
        internal int meshCount, hullsChecked, indexCount, convexCount, positiveSides, negativeSides, splitSides, nearSides;
        internal SpatialSplitSearchResult search;
        internal CutSpatialSplitSearch.Distribution distribution;
        internal AnchorDistributionStatus anchorStatus;
        internal int anchorPositive, anchorNegative;
        internal ConvexCutOwnerResult kernel;
        internal double workerSeconds, bentSeconds, bakeSeconds, copySeconds, resultSeconds;
        internal long copyBytes;
        internal byte* data;
        internal long dataBytes;
        internal readonly long[] sectionEnds = new long[SpatialSplitCanonical.SectionCount];

        /// <summary>The room reserved for it in the sink's queue (0: none yet), and the sink it is reserved in.</summary>
        internal long bytes;
        internal SpatialSplitCaptureSink sink;

        /// <summary>Gives back its memory and its room in the queue, once.</summary>
        internal void Discard()
        {
            if (data != null)
            {
                UnsafeUtility.Free(data, Allocator.Persistent);
                data = null;
            }

            if (bytes > 0 && sink != null)
            {
                sink.Unreserve(bytes);
            }

            bytes = 0;
        }
    }

    /// <summary>Little-endian writes into a native block of a known size (both machines this runs on are little-endian).</summary>
    internal unsafe struct SpatialSplitRawWriter
    {
        private readonly byte* _at;
        private readonly long _capacity;

        internal SpatialSplitRawWriter(byte* at, long capacity)
        {
            _at = at;
            _capacity = capacity;
            Length = 0;
        }

        internal long Length { get; private set; }

        internal void Bytes(void* from, long count)
        {
            if (count < 0 || Length + count > _capacity)
            {
                throw new InvalidOperationException("the capture's block has room for " + _capacity + " bytes, not " + (Length + count));
            }

            if (count > 0) UnsafeUtility.MemCpy(_at + Length, from, count);
            Length += count;
        }

        internal void Byte(bool v) { byte b = v ? (byte)1 : (byte)0; Bytes(&b, 1); }
        internal void Byte(byte v) => Bytes(&v, 1);
        internal void Int(int v) => Bytes(&v, sizeof(int));
        internal void UInt(uint v) => Bytes(&v, sizeof(uint));
        internal void Long(long v) => Bytes(&v, sizeof(long));
        internal void Float(float v) => Bytes(&v, sizeof(float));
        internal void Double(double v) => Bytes(&v, sizeof(double));
        internal void Float3(float3 v) => Bytes(&v, sizeof(float3));
        internal void Float4(float4 v) => Bytes(&v, sizeof(float4));
        internal void Double3(double3 v) => Bytes(&v, sizeof(double3));
    }

    /// <summary>Reads back what <see cref="SpatialSplitRawWriter"/> wrote, in the same order.</summary>
    internal unsafe struct SpatialSplitRawReader
    {
        private readonly byte* _at;
        private readonly long _length;
        private long _read;

        internal SpatialSplitRawReader(byte* at, long length)
        {
            _at = at;
            _length = length;
            _read = 0;
        }

        internal byte* Take(long bytes)
        {
            if (bytes < 0 || _read + bytes > _length)
            {
                throw new InvalidOperationException("the capture's block holds " + _length + " bytes, not " + (_read + bytes));
            }

            byte* at = _at + _read;
            _read += bytes;
            return at;
        }

        internal bool AtEnd => _read == _length;
    }

    /// <summary>
    /// Everything a file of samples or results is written through: a buffer of the writer's own, the SHA-256 of what was
    /// written (before compression) and its length. Values are written as their bytes: a float keeps every bit.
    /// </summary>
    internal sealed unsafe class SpatialSplitHashWriter : IDisposable
    {
        private readonly Stream _to;
        private readonly byte[] _buffer;
        private readonly SHA256 _sha = SHA256.Create();
        private int _used;

        internal SpatialSplitHashWriter(Stream to, byte[] buffer)
        {
            _to = to;
            _buffer = buffer;
        }

        internal long Length { get; private set; }

        internal void Bytes(void* from, long count)
        {
            byte* p = (byte*)from;
            while (count > 0)
            {
                if (_used == _buffer.Length) Flush();
                int n = (int)Math.Min(count, _buffer.Length - _used);
                fixed (byte* b = &_buffer[_used])
                {
                    UnsafeUtility.MemCpy(b, p, n);
                }

                _used += n;
                p += n;
                count -= n;
            }
        }

        internal void Byte(bool v) { byte b = v ? (byte)1 : (byte)0; Bytes(&b, 1); }
        internal void Int(int v) => Bytes(&v, sizeof(int));
        internal void UInt(uint v) => Bytes(&v, sizeof(uint));
        internal void Long(long v) => Bytes(&v, sizeof(long));
        internal void Float(float v) => Bytes(&v, sizeof(float));
        internal void Double(double v) => Bytes(&v, sizeof(double));
        internal void Matrix(float4x4 m) => Bytes(&m, sizeof(float4x4));

        internal void Ascii(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                byte b = (byte)text[i];
                Bytes(&b, 1);
            }
        }

        internal void String(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
            Int(bytes.Length);
            fixed (byte* p = bytes)
            {
                Bytes(p, bytes.Length);
            }
        }

        private void Flush()
        {
            if (_used == 0) return;
            _sha.TransformBlock(_buffer, 0, _used, null, 0);
            _to.Write(_buffer, 0, _used);
            Length += _used;
            _used = 0;
        }

        /// <summary>Writes what is buffered and gives the hash of everything written.</summary>
        internal string Finish()
        {
            Flush();
            _sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return SpatialSplitHash.Hex(_sha.Hash);
        }

        public void Dispose() => _sha.Dispose();
    }

    /// <summary>The reading side of <see cref="SpatialSplitHashWriter"/>: the same values in the same order, hashed as they are read.</summary>
    internal sealed unsafe class SpatialSplitHashReader : IDisposable
    {
        private readonly Stream _from;
        private readonly byte[] _buffer = new byte[1 << 16];
        private readonly SHA256 _sha = SHA256.Create();
        private int _have, _at;

        internal SpatialSplitHashReader(Stream from) => _from = from;

        internal long Length { get; private set; }

        private bool Fill()
        {
            _at = 0;
            _have = 0;
            while (_have == 0)
            {
                int read = _from.Read(_buffer, 0, _buffer.Length);
                if (read <= 0) return false;
                _have = read;
            }

            _sha.TransformBlock(_buffer, 0, _have, null, 0);
            Length += _have;
            return true;
        }

        internal void Into(void* to, long count)
        {
            byte* p = (byte*)to;
            while (count > 0)
            {
                if (_at == _have && !Fill())
                {
                    throw new EndOfStreamException("the sample ends early: " + count + " bytes short");
                }

                int n = (int)Math.Min(count, _have - _at);
                fixed (byte* b = &_buffer[_at])
                {
                    UnsafeUtility.MemCpy(p, b, n);
                }

                _at += n;
                p += n;
                count -= n;
            }
        }

        internal bool Bool() { byte b; Into(&b, 1); return b != 0; }
        internal int Int() { int v; Into(&v, sizeof(int)); return v; }
        internal uint UInt() { uint v; Into(&v, sizeof(uint)); return v; }
        internal long Long() { long v; Into(&v, sizeof(long)); return v; }
        internal float Float() { float v; Into(&v, sizeof(float)); return v; }
        internal double Double() { double v; Into(&v, sizeof(double)); return v; }
        internal float4x4 Matrix() { float4x4 v; Into(&v, sizeof(float4x4)); return v; }

        internal int Count(string what, long most)
        {
            int n = Int();
            if (n < 0 || n > most) throw new InvalidDataException("the sample's " + what + " count " + n + " is not one");
            return n;
        }

        internal T[] Array<T>(int count) where T : unmanaged
        {
            var values = new T[count];
            if (count > 0)
            {
                fixed (T* p = values)
                {
                    Into(p, (long)count * sizeof(T));
                }
            }

            return values;
        }

        internal string String()
        {
            int n = Count("text", 1 << 20);
            var bytes = Array<byte>(n);
            return Encoding.UTF8.GetString(bytes);
        }

        internal void Expect(string ascii)
        {
            for (int i = 0; i < ascii.Length; i++)
            {
                byte b;
                Into(&b, 1);
                if (b != (byte)ascii[i]) throw new InvalidDataException("the sample does not read '" + ascii + "' where it should");
            }
        }

        /// <summary>Nothing may follow; the hash of everything read.</summary>
        internal string Finish()
        {
            if (_at < _have || Fill())
            {
                throw new InvalidDataException("the sample goes on past its end");
            }

            _sha.TransformFinalBlock(System.Array.Empty<byte>(), 0, 0);
            return SpatialSplitHash.Hex(_sha.Hash);
        }

        public void Dispose() => _sha.Dispose();
    }

    internal static unsafe class SpatialSplitHash
    {
        internal static string Hex(byte[] hash)
        {
            var text = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) text.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }

        /// <summary>The SHA-256 of a native range, read through the buffer given.</summary>
        internal static string Of(byte* at, long count, byte[] buffer)
        {
            using (SHA256 sha = SHA256.Create())
            {
                while (count > 0)
                {
                    int n = (int)Math.Min(count, buffer.Length);
                    fixed (byte* b = buffer)
                    {
                        UnsafeUtility.MemCpy(b, at, n);
                    }

                    sha.TransformBlock(buffer, 0, n, null, 0);
                    at += n;
                    count -= n;
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Hex(sha.Hash);
            }
        }
    }

    /// <summary>
    /// What the numbers of one attempt came to, as bytes that are the same for the same answer: the search's record,
    /// the sharing out of the triangles (every index as the vertex's id in the issuing storage), the anchors, the
    /// convexes' sides and the kernel's counts -- and apart from those, the kernel's masses and integrals, which a test
    /// may compare with a tolerance. Written by the live split (Main, where the outputs are first read) and by the
    /// replay, from the same fields of <see cref="CutSpatialSplit.Attempt"/>; no time and nothing of the machine is in it.
    /// </summary>
    internal static unsafe class SpatialSplitCanonical
    {
        internal const int SectionCount = 6;
        internal static readonly string[] SectionNames = { "search", "distribution", "anchors", "classification", "kernel", "kernelNumbers" };

        internal static long MaxBytes(CutSpatialSplit.Attempt a)
        {
            long s = a.submeshCount;
            long anchors = a.anchors != null ? a.anchors.Length : 0;
            return 1024 + 64 + ((long)a.indexCount * sizeof(uint)) + (s * 2 * ((2 * sizeof(int)) + (2 * sizeof(float3))))
                + 64 + (anchors * 2 * sizeof(float3)) + 64 + a.convexCount + 256 + 512;
        }

        // An index of the attempt's numbering as the issuing storage's: the live split's own (no map), the replay's through
        // its table of the vertices the sample kept (local id -> the storage's).
        private static uint Map(uint v, uint* toOriginal, int count)
        {
            return toOriginal == null ? v : v < (uint)count ? toOriginal[v] : uint.MaxValue;
        }

        internal static void Write(CutSpatialSplit.Attempt a, uint* toOriginal, int originalCount, ref SpatialSplitRawWriter w, long[] ends)
        {
            // 0. The search: what it found and what it spent, which is a function of the input and the settings.
            SpatialSplitSearchResult r = a.search;
            w.Byte(a.ran);
            w.Byte(a.failure != null);
            w.Int((int)r.outcome);
            w.Float4(r.planeOwner);
            w.Float4(r.planeGeometry);
            w.Float4(r.planeShape);
            w.Float(r.margin);
            w.Float(r.gap);
            w.Int(r.directionsTried);
            w.Int(r.planesTested);
            w.Long(r.triangleVisits);
            w.Int(r.positiveTriangles);
            w.Int(r.negativeTriangles);
            w.Int(r.positiveConvexes);
            w.Int(r.negativeConvexes);
            w.Byte(r.meshLed);
            w.Int(r.crossingConvexes);
            w.Int(r.meshDirectionsTried);
            w.Int(r.meshPlanesTested);
            w.Int(r.refusedForTriangleMinimum);
            w.Int(r.refusedForNoConvexOnASide);
            w.Byte(r.bent);
            w.Float3(r.bentSurface.height);
            w.Float3(r.bentSurface.along);
            int knots = math.clamp(r.bentSurface.knotCount, 0, SpatialSplitBentSurface.MaxKnots);
            w.Int(r.bentSurface.knotCount);
            for (int k = 0; k < knots; k++)
            {
                w.Float(r.bentSurface.KnotU(k));
                w.Float(r.bentSurface.KnotH(k));
            }

            w.Int(r.bentPieces);
            w.Int(r.bentFramesTried);
            w.Int(r.bentSeedsTried);
            w.Int(r.bentCandidates);
            w.Int(r.bentIterations);
            w.Long(r.bentElementsVerified);
            ends[0] = w.Length;

            // 1. The sharing out: written only where it succeeded (a refused one leaves the output half written).
            w.Byte(a.distributed);
            if (a.distributed)
            {
                CutSpatialSplitSearch.Distribution d = a.distribution;
                w.Int(d.positiveIndexCount);
                w.Int(d.negativeIndexCount);
                w.UInt(Map(d.positiveReferencedLo, toOriginal, originalCount));
                w.UInt(Map(d.positiveReferencedHi, toOriginal, originalCount));
                w.UInt(Map(d.negativeReferencedLo, toOriginal, originalCount));
                w.UInt(Map(d.negativeReferencedHi, toOriginal, originalCount));
                for (int i = 0; i < a.indexCount; i++)
                {
                    w.UInt(Map(a.output[i], toOriginal, originalCount));
                }

                for (int i = 0; i < 2 * a.submeshCount; i++)
                {
                    w.Int(a.outSubmeshOffsets[i]);
                    w.Int(a.outSubmeshCounts[i]);
                    w.Float3(a.outBoundsMin[i]);
                    w.Float3(a.outBoundsMax[i]);
                }
            }

            ends[1] = w.Length;

            // 2. The anchors: whether they were shared out, and each side's in order.
            bool tried = a.positiveAnchors != null;
            w.Byte(tried);
            w.Byte(a.anchorsDistributed);
            w.Int((int)a.anchorResult.status);
            w.Int(a.anchorResult.positiveCount);
            w.Int(a.anchorResult.negativeCount);
            if (tried)
            {
                w.Int(a.positiveAnchors.Count);
                for (int i = 0; i < a.positiveAnchors.Count; i++) w.Float3(a.positiveAnchors[i]);
                w.Int(a.negativeAnchors.Count);
                for (int i = 0; i < a.negativeAnchors.Count; i++) w.Float3(a.negativeAnchors[i]);
            }

            ends[2] = w.Length;

            // 3. The convexes' sides, as the cut's scan (or the bent search) gave them.
            w.Byte(a.classified);
            PhysicsCutClassification classification = a.classification;
            bool has = classification != null && classification.Sides != null;
            w.Byte(has);
            if (has)
            {
                w.Int(classification.Sides.Count);
                for (int c = 0; c < classification.Sides.Count; c++) w.Byte((byte)classification.Sides[c]);
                w.Byte(classification.SplitsBothSides);
            }

            w.Byte(a.cut);
            ends[3] = w.Length;

            // 4. The kernel's counts (what it ran on is the classification above). Its "ran as managed" flag is the
            //    machine's, not the answer's, and is left out.
            ConvexCutOwnerResult kr = a.kernel;
            w.Int((int)kr.status);
            w.Int(kr.failedConvex);
            w.Int(kr.cutStatus);
            w.Int(kr.reductionStatus);
            w.Int(kr.positiveConvexCount);
            w.Int(kr.negativeConvexCount);
            w.Int(kr.splitConvexCount);
            w.Int(kr.reducedConvexCount);
            w.Int(kr.removedVertices);
            w.Int(kr.r1Invocations);
            w.Int(kr.usedVertices);
            w.Int(kr.usedFaceOffsets);
            w.Int(kr.usedFaceIndices);
            w.Int(kr.usedEdges);
            w.Int(kr.usedScratchBytes);
            w.Int(kr.reservedVertices);
            w.Int(kr.reservedFaceOffsets);
            w.Int(kr.reservedFaceIndices);
            w.Int(kr.reservedEdges);
            ends[4] = w.Length;

            // 5. The kernel's numbers.
            foreach (double v in KernelNumbers(in kr)) w.Double(v);
            ends[5] = w.Length;
        }

        /// <summary>The kernel's masses and integrals in one order: volumes, masses, centres, inertias (positive then negative each).</summary>
        internal static double[] KernelNumbers(in ConvexCutOwnerResult k)
        {
            return new[]
            {
                k.positiveVolume, k.negativeVolume, k.positiveMass, k.negativeMass,
                k.positiveCenterOfMass.x, k.positiveCenterOfMass.y, k.positiveCenterOfMass.z,
                k.negativeCenterOfMass.x, k.negativeCenterOfMass.y, k.negativeCenterOfMass.z,
                k.positiveInertia.xx, k.positiveInertia.yy, k.positiveInertia.zz, k.positiveInertia.xy, k.positiveInertia.xz, k.positiveInertia.yz,
                k.negativeInertia.xx, k.negativeInertia.yy, k.negativeInertia.zz, k.negativeInertia.xy, k.negativeInertia.xz, k.negativeInertia.yz,
            };
        }

        /// <summary>The sections' hashes of a block <see cref="Write"/> filled.</summary>
        internal static string[] Hashes(byte* data, long[] ends, byte[] buffer)
        {
            var hashes = new string[SectionCount];
            long from = 0;
            for (int i = 0; i < SectionCount; i++)
            {
                hashes[i] = SpatialSplitHash.Of(data + from, ends[i] - from, buffer);
                from = ends[i];
            }

            return hashes;
        }
    }

    /// <summary>
    /// Main's part of the capture: one pass over the indices for the span of vertices they name, then one copy of
    /// everything the numbers read into a block of the capture's own -- the submeshes, the anchors, the indices as
    /// issued, the vertices of that span as the storage holds them, and each convex's range and arrays as its bank
    /// holds them. Nothing is renumbered, merged or reordered here; the writer keeps of the span only the vertices
    /// the indices name, with their ids.
    /// </summary>
    internal static unsafe class SpatialSplitCaptureCopy
    {
        internal static SpatialSplitCaptureSample TryTake(SpatialSplitCaptureSink sink, CutSpatialSplit.Attempt a, in SpatialSplitCaptureIdentity identity)
        {
            if (!sink.IsCapturing)
            {
                sink.NoteRefused(sink.IsClosed ? SpatialSplitCaptureRefusal.Closed : SpatialSplitCaptureRefusal.Stopped);
                return null;
            }

            int vertexBytes = UnsafeUtility.SizeOf<VpRenderVertex>();
            uint view = (uint)Math.Max(0, a.vertexViewLength);
            uint lo = uint.MaxValue;
            uint hi = 0;
            for (int i = 0; i < a.indexCount; i++)
            {
                uint v = a.indices[i];
                if (v >= view) continue;   // the search refuses such an input; the index is kept as it is
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }

            int spanCount = lo <= hi ? checked((int)(hi - lo + 1)) : 0;
            if (spanCount == 0) lo = 0;
            int s = a.submeshCount;
            int anchors = a.anchors != null ? a.anchors.Length : 0;
            long bytes = (3L * s * sizeof(int)) + ((long)anchors * sizeof(float3)) + ((long)a.indexCount * sizeof(uint)) + ((long)spanCount * vertexBytes);
            for (int c = 0; c < a.convexCount; c++)
            {
                ConvexBrepRange r = a.ranges[c];
                bytes += sizeof(ConvexBrepRange) + ((long)r.vertexCount * sizeof(float3)) + ((long)(r.faceCount + 1) * sizeof(int))
                    + (2L * r.faceIndexCount * sizeof(int)) + ((long)r.edgeCount * sizeof(BrepEdge));
            }

            if (!sink.TryReserve(bytes, false))
            {
                return null;
            }

            byte* data = null;
            try
            {
                data = (byte*)UnsafeUtility.Malloc(Math.Max(1, bytes), 16, Allocator.Persistent);
                var w = new SpatialSplitRawWriter(data, bytes);
                w.Bytes(a.submeshOffsets, (long)s * sizeof(int));
                w.Bytes(a.submeshCounts, (long)s * sizeof(int));
                fixed (int* materials = a.materials)
                {
                    w.Bytes(materials, (long)s * sizeof(int));
                }

                if (anchors > 0)
                {
                    fixed (float3* p = a.anchors)
                    {
                        w.Bytes(p, (long)anchors * sizeof(float3));
                    }
                }

                w.Bytes(a.indices, (long)a.indexCount * sizeof(uint));
                if (spanCount > 0)
                {
                    w.Bytes(a.vertices + lo, (long)spanCount * vertexBytes);
                }

                for (int c = 0; c < a.convexCount; c++)
                {
                    ConvexBrepRange r = a.ranges[c];
                    ConvexBrepBank b = a.banks[c];
                    w.Bytes(&r, sizeof(ConvexBrepRange));
                    w.Bytes(b.vertices + r.vertexBase, (long)r.vertexCount * sizeof(float3));
                    w.Bytes(b.faceOffsets + r.faceBase, (long)(r.faceCount + 1) * sizeof(int));
                    w.Bytes(b.faceIndices + r.faceIndexBase, (long)r.faceIndexCount * sizeof(int));
                    w.Bytes(b.faceEdges + r.faceIndexBase, (long)r.faceIndexCount * sizeof(int));
                    w.Bytes(b.edges + r.edgeBase, (long)r.edgeCount * sizeof(BrepEdge));
                }

                if (w.Length != bytes)
                {
                    throw new InvalidOperationException("the copy wrote " + w.Length + " of " + bytes + " bytes");
                }
            }
            catch (Exception e)
            {
                if (data != null) UnsafeUtility.Free(data, Allocator.Persistent);
                sink.Unreserve(bytes);
                sink.Stop(SpatialSplitCaptureRefusal.CopyFailed, e.GetType().Name + ": " + e.Message);
                sink.NoteRefused(SpatialSplitCaptureRefusal.CopyFailed);
                return null;
            }

            return new SpatialSplitCaptureSample
            {
                identity = identity,
                margin = a.margin,
                supportEpsilon = a.supportEpsilon,
                anchorEpsilon = a.anchorEpsilon,
                vertexLimit = a.vertexLimit,
                parentMass = a.parentMass,
                limits = a.limits,
                cutConvexes = a.cutConvexes,
                minCutSideTriangles = a.minCutSideTriangles,
                bentSearch = a.bentSearch,
                bentLimits = a.bentLimits,
                maxConvexVertices = a.maxConvexVertices,
                geometryToOwner = a.geometryToOwner,
                shapeToOwner = a.shapeToOwner,
                submeshCount = s,
                anchorCount = a.anchors != null ? anchors : -1,
                indexCount = a.indexCount,
                vertexViewLength = a.vertexViewLength,
                convexCount = a.convexCount,
                vertexStructBytes = vertexBytes,
                spanLo = lo,
                spanCount = spanCount,
                data = data,
                bytes = bytes,
            };
        }

        /// <summary>
        /// What the numbers came to, copied (Main, after the work was collected; the worker is done with it), into a
        /// block whose whole room -- not only what is written of it -- was reserved in the sink's queue first. Null, with
        /// the reason, where the room was refused; the result is then made without the outputs where the attempt ends.
        /// </summary>
        internal static SpatialSplitCaptureResult TakeResult(SpatialSplitCaptureSink sink, CutSpatialSplit.Attempt a, out string refused)
        {
            long capacity = SpatialSplitCanonical.MaxBytes(a);
            if (!sink.TryReserve(capacity + SpatialSplitCaptureResult.Overhead, true))
            {
                refused = "no room in the queue for the outputs' copy (" + sink.LastRefusal + ")";
                return null;
            }

            var result = new SpatialSplitCaptureResult { observed = true, sink = sink, bytes = capacity + SpatialSplitCaptureResult.Overhead };
            try
            {
                result.data = (byte*)UnsafeUtility.Malloc(capacity, 16, Allocator.Persistent);
                var w = new SpatialSplitRawWriter(result.data, capacity);
                SpatialSplitCanonical.Write(a, null, 0, ref w, result.sectionEnds);
                result.dataBytes = w.Length;
                NoteNumbers(result, a);
            }
            catch
            {
                result.Discard();
                throw;
            }

            refused = null;
            return result;
        }

        /// <summary>
        /// What the numbers came to, beside the canonical bytes: fixed once the work came back, read where the outputs
        /// are first read (Main).
        /// </summary>
        internal static void NoteNumbers(SpatialSplitCaptureResult result, CutSpatialSplit.Attempt a)
        {
            result.ran = a.ran;
            result.cancelled = a.cancelled;
            result.failure = a.failure != null ? a.failure.GetType().Name + ": " + a.failure.Message : null;
            result.distributed = a.distributed;
            result.anchorsDistributed = a.anchorsDistributed;
            result.classified = a.classified;
            result.cut = a.cut;
            result.indexCount = a.indexCount;
            result.convexCount = a.convexCount;
            result.search = a.search;
            result.distribution = a.distribution;
            result.anchorStatus = a.anchorResult.status;
            result.anchorPositive = a.anchorResult.positiveCount;
            result.anchorNegative = a.anchorResult.negativeCount;
            result.kernel = a.kernel;
            result.workerSeconds = a.workerSeconds;
            result.bentSeconds = a.bentSeconds;
            PhysicsCutClassification classification = a.classification;
            result.hasClassification = classification != null && classification.Sides != null;
            if (result.hasClassification)
            {
                result.positiveSides = result.negativeSides = result.splitSides = result.nearSides = 0;
                foreach (ConvexSide side in classification.Sides)
                {
                    if (side == ConvexSide.Positive) result.positiveSides++;
                    else if (side == ConvexSide.Negative) result.negativeSides++;
                    else if (side == ConvexSide.Split) result.splitSides++;
                    else result.nearSides++;
                }
            }
        }

        /// <summary>
        /// The stages after the numbers -- the meshes made, the bake, the hulls read -- as they stand at the attempt's
        /// end (Main; only when no work of the attempt is out, as the bake writes its own time).
        /// </summary>
        internal static void NoteStages(SpatialSplitCaptureResult result, CutSpatialSplit.Attempt a)
        {
            result.applied = a.applied;
            result.meshesBad = a.meshesBad;
            result.meshCount = a.meshCount;
            result.hullsChecked = a.hullsChecked;
            result.bakeSeconds = a.bakeSeconds;
        }
    }

    /// <summary>
    /// The saving side of the capture (DESIGN 7.9.8; development and the Editor only, off without
    /// <see cref="SpatialSplitCaptureSession.Argument"/>). Main reserves room (<see cref="TryReserve"/>), copies, and hands
    /// the copy over once the work was taken (<see cref="NoteIssued"/>) or gives it back when it was not
    /// (<see cref="Return"/>); one worker thread of its own compresses, hashes and writes each sample to a file of its own
    /// -- made beside its final name with CreateNew, flushed, then moved there without overwriting, as
    /// TracePagedHistoryFileStore saves a trace -- and appends one line a sample to samples.jsonl and one a result to
    /// results.jsonl, each flushed, so that what was committed outlives the process. Main never waits for it.
    /// <para>
    /// **Bounds** (<see cref="SpatialSplitCaptureLimits"/>): an item is counted from its reservation until the writer is
    /// done with it. A sample over its bound, a queue at its count or bytes, the session's files at their bound, a copy or
    /// a write that fails: each stops the capture once, with its reason; later inputs are not captured and are counted as
    /// such. Results of samples already taken are still written while the writer can.
    /// </para>
    /// <para>
    /// **What is not captured**: candidates the walk passed over before an issue, and inputs a NotFound (or other) mark
    /// keeps from being issued again. Only inputs actually issued are.
    /// </para>
    /// </summary>
    public sealed unsafe class SpatialSplitCaptureSink
    {
        public const int FormatVersion = 1;
        public const string SampleMagic = "ZTKSSPL1", SampleEnd = "END!";

        public readonly string directory;
        public readonly SpatialSplitCaptureLimits limits;
        public readonly string sessionId, codeVersion;
        private readonly string[] _notes;

        private readonly ConcurrentQueue<object> _queue = new ConcurrentQueue<object>();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
        private readonly Thread _worker;
        private readonly long[] _notCaptured = new long[(int)SpatialSplitCaptureRefusal.Count];
        private int _stopped, _closed, _workerEnded, _writeFailed, _stopReason;
        private string _stopDetail = "";
        private long _queuedItems, _queuedBytes, _mostItems, _mostBytes;
        private long _saved, _unindexed, _lost, _resultsWritten, _resultsLost, _diskBytes, _rawBytes, _writeTicks, _mostWriteTicks;
        private long _outputsNotKept;
        private SpatialSplitCaptureRefusal _lastRefusal;

        // The splits that hand their attempts to this one (Main): what the application's quitting asks for their results.
        private readonly System.Collections.Generic.List<CutSpatialSplit> _splits = new System.Collections.Generic.List<CutSpatialSplit>();

        // The writer's own.
        private readonly byte[] _buffer = new byte[1 << 16];
        private bool[] _marks = new bool[0];
        private FileStream _samplesIndex, _results;

        /// <summary>For a test: the writer fails at this many samples written (-1: never). The product never sets it.</summary>
        internal int FailWriteAtSampleForTest = -1;

        /// <summary>For a test: the index line of the sample after this many committed fails, after the file was committed (-1: never).</summary>
        internal int FailIndexAtSampleForTest = -1;

        public SpatialSplitCaptureSink(string directory, in SpatialSplitCaptureLimits limits, string codeVersion, string[] notes)
        {
            if (string.IsNullOrEmpty(directory) || !Path.IsPathRooted(directory))
            {
                throw new ArgumentException("the capture's directory must be an absolute path", nameof(directory));
            }

            if (!limits.IsValid)
            {
                throw new ArgumentException("the capture's limits are not valid", nameof(limits));
            }

            this.directory = directory;
            this.limits = limits;
            this.codeVersion = codeVersion ?? "";
            _notes = notes ?? System.Array.Empty<string>();
            sessionId = Guid.NewGuid().ToString("N");
            _worker = new Thread(WorkerLoop) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "Spatial Split Capture" };
            _worker.Start();
        }

        /// <summary>Whether an input issued now would be copied (not stopped, not closed).</summary>
        public bool IsCapturing => Volatile.Read(ref _stopped) == 0 && Volatile.Read(ref _closed) == 0;
        public bool IsClosed => Volatile.Read(ref _closed) == 1;

        /// <summary>Why the capture stopped (None while it runs), and what was said of it.</summary>
        public SpatialSplitCaptureRefusal StopReason => (SpatialSplitCaptureRefusal)Volatile.Read(ref _stopReason);
        public string StopDetail => Volatile.Read(ref _stopDetail);

        /// <summary>Main's counts: attempts issued while the session lived, inputs handed to the writer, copies given back because the work was not taken.</summary>
        public long Issued { get; private set; }
        public long Accepted { get; private set; }
        public long Returned { get; private set; }

        /// <summary>Issued attempts whose input was not captured, by reason.</summary>
        public long NotCaptured(SpatialSplitCaptureRefusal why) => _notCaptured[(int)why];

        public long NotCapturedInAll
        {
            get
            {
                long all = 0;
                foreach (long n in _notCaptured) all += n;
                return all;
            }
        }

        /// <summary>
        /// The writer's counts: samples committed and in the index; committed whose index line could not be written
        /// (the file is whole and the replay finds it by the folder: never also counted lost); taken and not committed;
        /// results written and not recorded; bytes on disk and before compression.
        /// </summary>
        public long Saved => Interlocked.Read(ref _saved);
        public long Unindexed => Interlocked.Read(ref _unindexed);
        public long Lost => Interlocked.Read(ref _lost);
        public long ResultsWritten => Interlocked.Read(ref _resultsWritten);
        public long ResultsLost => Interlocked.Read(ref _resultsLost);
        public long DiskBytes => Interlocked.Read(ref _diskBytes);
        public long RawBytes => Interlocked.Read(ref _rawBytes);

        /// <summary>Results of attempts whose numbers ran but whose outputs could not be kept (no room, or the copy failed): written without them.</summary>
        public long OutputsNotKept => Interlocked.Read(ref _outputsNotKept);

        /// <summary>Samples taken and not yet committed or lost (in the queue or being written).</summary>
        public long InFlight => Accepted - Saved - Unindexed - Lost;

        /// <summary>The reason the last reservation was refused (Main).</summary>
        internal SpatialSplitCaptureRefusal LastRefusal => _lastRefusal;

        /// <summary>What the queue holds now and held at most, counting the item being written.</summary>
        public long QueuedItems => Interlocked.Read(ref _queuedItems);
        public long QueuedBytes => Interlocked.Read(ref _queuedBytes);
        public long MostQueuedItems => Interlocked.Read(ref _mostItems);
        public long MostQueuedBytes => Interlocked.Read(ref _mostBytes);

        /// <summary>The writer's time on samples, in all and the longest (ms).</summary>
        public double WriteMs => Interlocked.Read(ref _writeTicks) * 1000.0 / Stopwatch.Frequency;
        public double MostWriteMs => Interlocked.Read(ref _mostWriteTicks) * 1000.0 / Stopwatch.Frequency;

        /// <summary>Whether the writer has ended (after <see cref="Close"/>, or a failure it could not go on from).</summary>
        public bool Settled => Volatile.Read(ref _workerEnded) == 1;

        // ---- Main ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Room for one item of these bytes, counted until the writer is done with it -- or not, and the capture is
        /// stopped at the bound reached. A result (of a sample already taken) is let through a stopped capture.
        /// </summary>
        internal bool TryReserve(long bytes, bool result)
        {
            if (Volatile.Read(ref _closed) == 1 || Volatile.Read(ref _writeFailed) == 1 || Volatile.Read(ref _workerEnded) == 1)
            {
                _lastRefusal = Volatile.Read(ref _closed) == 1 ? SpatialSplitCaptureRefusal.Closed : SpatialSplitCaptureRefusal.Stopped;
                return false;
            }

            if (!result && Volatile.Read(ref _stopped) == 1)
            {
                _lastRefusal = SpatialSplitCaptureRefusal.Stopped;
                return false;
            }

            if (!result && bytes > limits.maxSampleBytes)
            {
                Stop(SpatialSplitCaptureRefusal.SampleOverCap, "a sample of " + bytes + " bytes (bound " + limits.maxSampleBytes + ")");
                _lastRefusal = SpatialSplitCaptureRefusal.SampleOverCap;
                return false;
            }

            long items = Interlocked.Increment(ref _queuedItems);
            if (items > limits.maxQueuedItems)
            {
                Interlocked.Decrement(ref _queuedItems);
                Stop(SpatialSplitCaptureRefusal.QueueItems, "the queue held " + (items - 1) + " items (bound " + limits.maxQueuedItems + ")");
                _lastRefusal = SpatialSplitCaptureRefusal.QueueItems;
                return false;
            }

            long queued = Interlocked.Add(ref _queuedBytes, bytes);
            if (queued > limits.maxQueuedBytes)
            {
                Interlocked.Add(ref _queuedBytes, -bytes);
                Interlocked.Decrement(ref _queuedItems);
                Stop(SpatialSplitCaptureRefusal.QueueBytes, "the queue would hold " + queued + " bytes (bound " + limits.maxQueuedBytes + ")");
                _lastRefusal = SpatialSplitCaptureRefusal.QueueBytes;
                return false;
            }

            Most(ref _mostItems, items);
            Most(ref _mostBytes, queued);
            return true;
        }

        internal void Unreserve(long bytes)
        {
            Interlocked.Add(ref _queuedBytes, -bytes);
            Interlocked.Decrement(ref _queuedItems);
        }

        internal void NoteRefused(SpatialSplitCaptureRefusal why) => _lastRefusal = why;

        /// <summary>An attempt was issued: its copy (or none, for the last refusal's reason) is the writer's from here.</summary>
        internal void NoteIssued(SpatialSplitCaptureSample sample)
        {
            Issued++;
            if (sample == null)
            {
                _notCaptured[(int)(_lastRefusal == SpatialSplitCaptureRefusal.None ? SpatialSplitCaptureRefusal.Stopped : _lastRefusal)]++;
                return;
            }

            Accepted++;
            Hand(sample);
        }

        /// <summary>The work was not taken: the copy goes back here, and the attempt is not counted as issued.</summary>
        internal void Return(SpatialSplitCaptureSample sample)
        {
            if (sample == null) return;
            Returned++;
            sample.Free();
            Unreserve(sample.bytes);
        }

        /// <summary>
        /// An attempt with a captured input ended: its result to the writer, or counted as not recorded. A result with
        /// the outputs had its room reserved when they were copied; one without them asks for its own here.
        /// </summary>
        internal void HandResult(SpatialSplitCaptureResult result)
        {
            if (result.outputsNotKept != null)
            {
                Interlocked.Increment(ref _outputsNotKept);
            }

            if (result.bytes == 0)
            {
                result.sink = this;
                if (!TryReserve(SpatialSplitCaptureResult.Overhead, true))
                {
                    result.Discard();
                    Interlocked.Increment(ref _resultsLost);
                    return;
                }

                result.bytes = SpatialSplitCaptureResult.Overhead;
            }

            Hand(result);
        }

        /// <summary>A result that was taken and is given up without being handed on (Main): its room back, counted as not recorded.</summary>
        internal void LoseResult(SpatialSplitCaptureResult result)
        {
            result.Discard();
            Interlocked.Increment(ref _resultsLost);
        }

        /// <summary>A split hands its attempts here from now (Main).</summary>
        internal void Attach(CutSpatialSplit split)
        {
            if (!_splits.Contains(split)) _splits.Add(split);
        }

        /// <summary>A split no longer hands anything here (its world let it go, or it was given another capture).</summary>
        internal void Detach(CutSpatialSplit split) => _splits.Remove(split);

        /// <summary>
        /// The application's quitting (Main): every attached split's attempt still out hands its result now -- an end of
        /// AppQuit, with its outputs where they were read -- so that nothing is lost to the order in which the worlds go
        /// afterwards; then <see cref="Close"/>, waiting at most this long for the writer to write it all and end.
        /// </summary>
        internal bool Quit(double waitSeconds)
        {
            foreach (CutSpatialSplit split in _splits.ToArray())
            {
                split.EndCaptureForQuit();
            }

            return Close(waitSeconds);
        }

        private void Hand(object item)
        {
            _queue.Enqueue(item);
            _signal.Release();
            if (Volatile.Read(ref _workerEnded) == 1)
            {
                // The writer is gone (a failure it could not go on from): nothing will take this, so it goes back here.
                Drain();
            }
        }

        /// <summary>
        /// No more input is taken; the writer writes what it holds, then the summary, and ends. Waits at most this long for
        /// it (the application's quitting: a background thread does not outlive the process); a test may wait.
        /// </summary>
        public bool Close(double waitSeconds)
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                _signal.Release();
            }

            return waitSeconds > 0.0 ? _worker.Join(TimeSpan.FromSeconds(waitSeconds)) : Settled;
        }

        /// <summary>Stops the capture, once, for its first reason; the game goes on.</summary>
        internal void Stop(SpatialSplitCaptureRefusal why, string detail)
        {
            if (Interlocked.CompareExchange(ref _stopReason, (int)why, 0) == 0)
            {
                Volatile.Write(ref _stopDetail, detail ?? "");
                UnityEngine.Debug.LogWarning("SPLIT CAPTURE STOPPED: " + why + " (" + detail + "); the game goes on, later inputs are counted as not captured");
            }

            Volatile.Write(ref _stopped, 1);
        }

        private static void Most(ref long most, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref most)) && Interlocked.CompareExchange(ref most, value, seen) != seen)
            {
            }
        }

        public string Describe()
        {
            var text = new StringBuilder();
            // The folder by its own name: a log need not say where on the machine it is.
            text.Append("split capture ").Append(Path.GetFileName(directory)).Append(": issued ").Append(Issued).Append(", accepted ").Append(Accepted)
                .Append(", saved ").Append(Saved).Append(", committed without an index line ").Append(Unindexed)
                .Append(", in flight ").Append(InFlight).Append(", lost ").Append(Lost)
                .Append(", not captured ").Append(NotCapturedInAll);
            for (int i = 1; i < _notCaptured.Length; i++)
            {
                if (_notCaptured[i] > 0) text.Append(" (").Append((SpatialSplitCaptureRefusal)i).Append(' ').Append(_notCaptured[i]).Append(')');
            }

            text.Append(", returned ").Append(Returned).Append("; results written ").Append(ResultsWritten).Append(", not recorded ").Append(ResultsLost)
                .Append(", written without the outputs (no room or the copy failed) ").Append(OutputsNotKept)
                .Append("; disk ").Append(DiskBytes).Append(" bytes (").Append(RawBytes).Append(" raw); queue most ").Append(MostQueuedItems)
                .Append(" items, ").Append(MostQueuedBytes).Append(" bytes; write ").Append(WriteMs.ToString("F1", CultureInfo.InvariantCulture))
                .Append(" ms in all, most ").Append(MostWriteMs.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms; ")
                .Append(StopReason == SpatialSplitCaptureRefusal.None ? "not stopped" : "STOPPED: " + StopReason + " (" + StopDetail + ")")
                .Append(Settled ? "; writer ended" : "; writer running");
            return text.ToString();
        }

        // ---- the writer --------------------------------------------------------------------------------------------------

        private void WorkerLoop()
        {
            try
            {
                Open();
                while (true)
                {
                    _signal.Wait();
                    while (_queue.TryDequeue(out object item))
                    {
                        Process(item);
                    }

                    if (Volatile.Read(ref _closed) == 1 && _queue.IsEmpty)
                    {
                        break;
                    }
                }

                WriteSummary();
            }
            catch (Exception e)
            {
                Fail("the writer: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                _samplesIndex?.Dispose();
                _results?.Dispose();
                Volatile.Write(ref _workerEnded, 1);
                Drain();
            }
        }

        // What is left in the queue goes back (the writer is gone or failed): counted, freed, unreserved.
        private void Drain()
        {
            while (_queue.TryDequeue(out object item))
            {
                Drop(item);
            }
        }

        private void Drop(object item)
        {
            if (item is SpatialSplitCaptureSample sample)
            {
                Interlocked.Increment(ref _lost);
                sample.Free();
                Unreserve(sample.bytes);
            }
            else if (item is SpatialSplitCaptureResult result)
            {
                Interlocked.Increment(ref _resultsLost);
                result.Discard();
            }
        }

        private void Fail(string why)
        {
            Volatile.Write(ref _writeFailed, 1);
            Stop(SpatialSplitCaptureRefusal.WriteFailed, why);
        }

        private void Open()
        {
            if (Directory.Exists(directory) || File.Exists(directory))
            {
                throw new IOException("the capture's directory exists already: " + directory);
            }

            Directory.CreateDirectory(directory);
            WriteAtomic(Path.Combine(directory, "session.json"), SessionJson());
            _samplesIndex = new FileStream(Path.Combine(directory, "samples.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _results = new FileStream(Path.Combine(directory, "results.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }

        private void Process(object item)
        {
            if (Volatile.Read(ref _writeFailed) == 1)
            {
                Drop(item);
                return;
            }

            try
            {
                if (item is SpatialSplitCaptureSample sample)
                {
                    long begin = Stopwatch.GetTimestamp();
                    try
                    {
                        WriteSample(sample);
                    }
                    finally
                    {
                        long ticks = Stopwatch.GetTimestamp() - begin;
                        Interlocked.Add(ref _writeTicks, ticks);
                        Most(ref _mostWriteTicks, ticks);
                    }
                }
                else if (item is SpatialSplitCaptureResult result)
                {
                    WriteResult(result);
                }
            }
            catch (Exception e)
            {
                if (item is SpatialSplitCaptureSample) Interlocked.Increment(ref _lost);
                else Interlocked.Increment(ref _resultsLost);
                Fail(e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                if (item is SpatialSplitCaptureSample sample)
                {
                    sample.Free();
                    Unreserve(sample.bytes);
                }
                else if (item is SpatialSplitCaptureResult result)
                {
                    result.Discard();
                }
            }
        }

        /// <summary>The file a sample is written to, by its world and attempt.</summary>
        public static string SampleFileName(long world, long attempt) =>
            "w" + world.ToString("D3", CultureInfo.InvariantCulture) + "-a" + attempt.ToString("D7", CultureInfo.InvariantCulture) + ".zss.gz";

        // What the lines and the summary may still take once a sample would reach the session's bound.
        private long LineReserve => Math.Min(8L << 20, limits.maxSessionBytes / 16);

        private void WriteSample(SpatialSplitCaptureSample sample)
        {
            if (FailWriteAtSampleForTest >= 0 && Interlocked.Read(ref _saved) >= FailWriteAtSampleForTest)
            {
                throw new IOException("refused for a test");
            }

            string name = SampleFileName(sample.identity.world, sample.identity.attempt);
            string final = Path.Combine(directory, name);
            string temporary = final + "." + Guid.NewGuid().ToString("N") + ".partial";
            var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            bool published = false;
            try
            {
                string hash;
                long raw;
                using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest, true))
                using (var w = new SpatialSplitHashWriter(gzip, _buffer))
                {
                    WriteContent(w, sample);
                    hash = w.Finish();
                    raw = w.Length;
                }

                file.Flush();
                long length = file.Length;
                file.Dispose();
                if (Interlocked.Read(ref _diskBytes) + length > limits.maxSessionBytes - LineReserve)
                {
                    Stop(SpatialSplitCaptureRefusal.SessionDisk, "a sample of " + length + " bytes over " + Interlocked.Read(ref _diskBytes) + " on disk (bound " + limits.maxSessionBytes + ")");
                    Interlocked.Increment(ref _lost);
                    return;
                }

                // Nothing is overwritten: a file of that name that appeared in the meantime makes this fail.
                File.Move(temporary, final);
                published = true;
                Interlocked.Add(ref _diskBytes, length);
                Interlocked.Add(ref _rawBytes, raw);

                // From here the sample is committed, whatever becomes of its line: it is counted once -- saved with its
                // line, or unindexed without it (never lost as well) -- and the replay finds an unindexed one by the folder.
                SpatialSplitCaptureIdentity id = sample.identity;
                try
                {
                    if (FailIndexAtSampleForTest >= 0 && Interlocked.Read(ref _saved) >= FailIndexAtSampleForTest)
                    {
                        throw new IOException("the index line refused for a test");
                    }

                    AppendLine(_samplesIndex, "{\"world\":" + id.world + ",\"attempt\":" + id.attempt + ",\"file\":" + Json(name)
                        + ",\"sha256\":" + Json(hash) + ",\"rawBytes\":" + raw + ",\"fileBytes\":" + length + ",\"copyBytes\":" + sample.bytes
                        + ",\"issuedFrame\":" + id.issuedFrame + ",\"fragment\":" + id.fragment + ",\"rootFragment\":" + id.rootFragment
                        + ",\"object\":" + Json(id.objectName) + "}");
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _unindexed);
                    Fail("the index line of " + name + " (the file is committed): " + e.GetType().Name + ": " + e.Message);
                    return;
                }

                Interlocked.Increment(ref _saved);
            }
            finally
            {
                if (!published)
                {
                    file.Dispose();
                    try
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);   // this call's own file only (made with CreateNew)
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        // The file's content (SpatialSplitSample.Read reads it): the header, what the numbers read beside the arrays, then
        // the arrays -- of the span copied, only the vertices the indices name, with their ids, ascending.
        private void WriteContent(SpatialSplitHashWriter w, SpatialSplitCaptureSample s)
        {
            SpatialSplitCaptureIdentity id = s.identity;
            w.Ascii(SampleMagic);
            w.Int(FormatVersion);
            w.Int(s.vertexStructBytes);
            w.String(codeVersion);
            w.String(sessionId);
            w.String(id.objectName);
            w.Long(id.world);
            w.Long(id.attempt);
            w.Int(id.issuedFrame);
            w.Int(id.fragment);
            w.Int(id.rootFragment);
            w.Int(id.splitSource);
            w.Int(id.generation);
            w.Int(id.epoch);
            w.Float(s.margin);
            w.Float(s.supportEpsilon);
            w.Float(s.anchorEpsilon);
            w.Int(s.vertexLimit);
            w.Double(s.parentMass);
            w.Int(s.limits.maxConvexes);
            w.Int(s.limits.maxCentreDirections);
            w.Int(s.limits.maxPlaneTests);
            w.Int(s.limits.maxTriangles);
            w.Int(s.limits.maxMeshDirections);
            w.Int(s.limits.meshBins);
            w.Byte(s.cutConvexes);
            w.Int(s.minCutSideTriangles);
            w.Byte(s.bentSearch);
            w.Int(s.bentLimits.maxFrames);
            w.Int(s.bentLimits.maxSeeds);
            w.Int(s.bentLimits.maxCandidates);
            w.Int(s.bentLimits.maxIterations);
            w.Int(s.bentLimits.maxPieces);
            w.Int(s.maxConvexVertices);
            w.Matrix(s.geometryToOwner);
            w.Matrix(s.shapeToOwner);

            var r = new SpatialSplitRawReader(s.data, s.bytes);
            int n = s.submeshCount;
            int* offsets = (int*)r.Take((long)n * sizeof(int));
            int* counts = (int*)r.Take((long)n * sizeof(int));
            int* materials = (int*)r.Take((long)n * sizeof(int));
            w.Int(n);
            for (int i = 0; i < n; i++)
            {
                w.Int(offsets[i]);
                w.Int(counts[i]);
                w.Int(materials[i]);
            }

            w.Int(s.anchorCount);
            if (s.anchorCount > 0) w.Bytes(r.Take((long)s.anchorCount * sizeof(float3)), (long)s.anchorCount * sizeof(float3));
            w.Int(s.indexCount);
            uint* indices = (uint*)r.Take((long)s.indexCount * sizeof(uint));
            w.Bytes(indices, (long)s.indexCount * sizeof(uint));
            w.Int(s.vertexViewLength);

            byte* span = r.Take((long)s.spanCount * s.vertexStructBytes);
            if (_marks.Length < s.spanCount) _marks = new bool[Math.Max(s.spanCount, _marks.Length * 2)];
            System.Array.Clear(_marks, 0, s.spanCount);
            uint view = (uint)Math.Max(0, s.vertexViewLength);
            int named = 0;
            for (int i = 0; i < s.indexCount; i++)
            {
                uint v = indices[i];
                if (v >= view) continue;
                int k = (int)(v - s.spanLo);
                if (!_marks[k])
                {
                    _marks[k] = true;
                    named++;
                }
            }

            w.Int(named);
            for (int k = 0; k < s.spanCount; k++)
            {
                if (_marks[k]) w.UInt(s.spanLo + (uint)k);
            }

            for (int k = 0; k < s.spanCount;)
            {
                if (!_marks[k])
                {
                    k++;
                    continue;
                }

                int start = k;
                while (k < s.spanCount && _marks[k]) k++;
                w.Bytes(span + ((long)start * s.vertexStructBytes), (long)(k - start) * s.vertexStructBytes);
            }

            w.Int(s.convexCount);
            for (int c = 0; c < s.convexCount; c++)
            {
                ConvexBrepRange range = *(ConvexBrepRange*)r.Take(sizeof(ConvexBrepRange));
                w.Int(range.vertexBase);
                w.Int(range.vertexCount);
                w.Int(range.faceBase);
                w.Int(range.faceCount);
                w.Int(range.faceIndexBase);
                w.Int(range.faceIndexCount);
                w.Int(range.edgeBase);
                w.Int(range.edgeCount);
                w.Int(range.maxFaceLoop);
                long arrays = ((long)range.vertexCount * sizeof(float3)) + ((long)(range.faceCount + 1) * sizeof(int))
                    + (2L * range.faceIndexCount * sizeof(int)) + ((long)range.edgeCount * sizeof(BrepEdge));
                w.Bytes(r.Take(arrays), arrays);
            }

            if (!r.AtEnd)
            {
                throw new InvalidOperationException("the copied block was not read to its end");
            }

            w.Ascii(SampleEnd);
        }

        /// <summary>
        /// What the search adopted, as its numbers (each float with every bit, as text that reads back to it): the plane
        /// in the geometry's and the shape's frames, and the bent surface -- its height and along axes and its knots.
        /// The result line's and the replay's, so that two answers that differ can be told apart by what they adopted.
        /// </summary>
        internal static void AppendAdopted(StringBuilder line, in SpatialSplitSearchResult s)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            void Floats(string name, params float[] values)
            {
                line.Append(",\"").Append(name).Append("\":[");
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0) line.Append(',');
                    line.Append(Json(values[i].ToString("R", inv)));
                }

                line.Append(']');
            }

            Floats("planeGeometry", s.planeGeometry.x, s.planeGeometry.y, s.planeGeometry.z, s.planeGeometry.w);
            Floats("planeShape", s.planeShape.x, s.planeShape.y, s.planeShape.z, s.planeShape.w);
            SpatialSplitBentSurface surface = s.bentSurface;
            int knots = math.clamp(surface.knotCount, 0, SpatialSplitBentSurface.MaxKnots);
            var u = new float[knots];
            var h = new float[knots];
            for (int k = 0; k < knots; k++)
            {
                u[k] = surface.KnotU(k);
                h[k] = surface.KnotH(k);
            }

            line.Append(",\"bentKnotCount\":").Append(surface.knotCount);
            Floats("bentHeight", surface.height.x, surface.height.y, surface.height.z);
            Floats("bentAlong", surface.along.x, surface.along.y, surface.along.z);
            Floats("bentKnotU", u);
            Floats("bentKnotH", h);
        }

        /// <summary>
        /// Every field of the canonical search section (<see cref="SpatialSplitCanonical"/>), as a nested "search"
        /// object -- floats as text that reads back to their bits -- with the kernel's own "ran as managed" flag beside
        /// it: what a difference of the section's hash can be traced to, field by field. The result line's and the replay's.
        /// </summary>
        internal static void AppendSearchRecord(StringBuilder line, in SpatialSplitSearchResult s, bool ran, bool failed, byte kernelExecutedManaged)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string F(float v) => Json(v.ToString("R", inv));
            string B(bool v) => v ? "true" : "false";
            string Floats(params float[] values)
            {
                var text = new StringBuilder("[");
                for (int i = 0; i < values.Length; i++) text.Append(i > 0 ? "," : "").Append(F(values[i]));
                return text.Append(']').ToString();
            }

            SpatialSplitBentSurface surface = s.bentSurface;
            int knots = math.clamp(surface.knotCount, 0, SpatialSplitBentSurface.MaxKnots);
            var u = new float[knots];
            var h = new float[knots];
            for (int k = 0; k < knots; k++)
            {
                u[k] = surface.KnotU(k);
                h[k] = surface.KnotH(k);
            }

            line.Append(",\"kernelExecutedManaged\":").Append(kernelExecutedManaged)
                .Append(",\"search\":{\"ran\":").Append(B(ran)).Append(",\"failed\":").Append(B(failed))
                .Append(",\"outcome\":").Append((int)s.outcome)
                .Append(",\"planeOwner\":").Append(Floats(s.planeOwner.x, s.planeOwner.y, s.planeOwner.z, s.planeOwner.w))
                .Append(",\"planeGeometry\":").Append(Floats(s.planeGeometry.x, s.planeGeometry.y, s.planeGeometry.z, s.planeGeometry.w))
                .Append(",\"planeShape\":").Append(Floats(s.planeShape.x, s.planeShape.y, s.planeShape.z, s.planeShape.w))
                .Append(",\"margin\":").Append(F(s.margin)).Append(",\"gap\":").Append(F(s.gap))
                .Append(",\"directionsTried\":").Append(s.directionsTried).Append(",\"planesTested\":").Append(s.planesTested)
                .Append(",\"triangleVisits\":").Append(s.triangleVisits)
                .Append(",\"positiveTriangles\":").Append(s.positiveTriangles).Append(",\"negativeTriangles\":").Append(s.negativeTriangles)
                .Append(",\"positiveConvexes\":").Append(s.positiveConvexes).Append(",\"negativeConvexes\":").Append(s.negativeConvexes)
                .Append(",\"meshLed\":").Append(B(s.meshLed)).Append(",\"crossingConvexes\":").Append(s.crossingConvexes)
                .Append(",\"meshDirectionsTried\":").Append(s.meshDirectionsTried).Append(",\"meshPlanesTested\":").Append(s.meshPlanesTested)
                .Append(",\"refusedForTriangleMinimum\":").Append(s.refusedForTriangleMinimum).Append(",\"refusedForNoConvexOnASide\":").Append(s.refusedForNoConvexOnASide)
                .Append(",\"bent\":").Append(B(s.bent))
                .Append(",\"bentHeight\":").Append(Floats(surface.height.x, surface.height.y, surface.height.z))
                .Append(",\"bentAlong\":").Append(Floats(surface.along.x, surface.along.y, surface.along.z))
                .Append(",\"bentKnotCount\":").Append(surface.knotCount)
                .Append(",\"bentKnotU\":").Append(Floats(u)).Append(",\"bentKnotH\":").Append(Floats(h))
                .Append(",\"bentPieces\":").Append(s.bentPieces).Append(",\"bentFramesTried\":").Append(s.bentFramesTried)
                .Append(",\"bentSeedsTried\":").Append(s.bentSeedsTried).Append(",\"bentCandidates\":").Append(s.bentCandidates)
                .Append(",\"bentIterations\":").Append(s.bentIterations).Append(",\"bentElementsVerified\":").Append(s.bentElementsVerified)
                .Append('}');
        }

        private void WriteResult(SpatialSplitCaptureResult r)
        {
            string[] hashes = r.observed ? SpatialSplitCanonical.Hashes(r.data, r.sectionEnds, _buffer) : null;
            CultureInfo inv = CultureInfo.InvariantCulture;
            var line = new StringBuilder(1024);
            SpatialSplitSearchResult s = r.search;
            line.Append("{\"world\":").Append(r.world).Append(",\"attempt\":").Append(r.attempt)
                .Append(",\"file\":").Append(Json(SampleFileName(r.world, r.attempt)))
                .Append(",\"end\":").Append(Json(r.end)).Append(",\"detail\":").Append(Json(r.detail ?? ""))
                .Append(",\"issuedFrame\":").Append(r.issuedFrame).Append(",\"endFrame\":").Append(r.endFrame)
                .Append(",\"searchObserved\":").Append(r.observed ? "true" : "false")
                .Append(",\"ran\":").Append(B(r.ran)).Append(",\"cancelled\":").Append(B(r.cancelled)).Append(",\"failure\":").Append(Json(r.failure ?? ""))
                .Append(",\"outcome\":").Append(Json(s.outcome.ToString()))
                .Append(",\"meshLed\":").Append(B(s.meshLed)).Append(",\"bent\":").Append(B(s.bent)).Append(",\"bentPieces\":").Append(s.bentPieces)
                .Append(",\"distributed\":").Append(B(r.distributed)).Append(",\"anchorsDistributed\":").Append(B(r.anchorsDistributed))
                .Append(",\"classified\":").Append(B(r.classified)).Append(",\"cut\":").Append(B(r.cut))
                .Append(",\"applied\":").Append(B(r.applied)).Append(",\"meshesBad\":").Append(B(r.meshesBad))
                .Append(",\"meshes\":").Append(r.meshCount).Append(",\"hullsChecked\":").Append(r.hullsChecked)
                .Append(",\"indices\":").Append(r.indexCount).Append(",\"convexes\":").Append(r.convexCount)
                .Append(",\"plane\":[").Append(F(s.planeOwner.x)).Append(',').Append(F(s.planeOwner.y)).Append(',').Append(F(s.planeOwner.z)).Append(',').Append(F(s.planeOwner.w)).Append(']')
                .Append(",\"outputsNotKept\":").Append(Json(r.outputsNotKept ?? ""));
            AppendAdopted(line, in s);
            AppendSearchRecord(line, in s, r.ran, r.failure != null, r.kernel.executedManaged);
            line.Append(",\"gap\":").Append(F(s.gap))
                .Append(",\"positiveTriangles\":").Append(s.positiveTriangles).Append(",\"negativeTriangles\":").Append(s.negativeTriangles)
                .Append(",\"positiveConvexes\":").Append(s.positiveConvexes).Append(",\"negativeConvexes\":").Append(s.negativeConvexes)
                .Append(",\"crossingConvexes\":").Append(s.crossingConvexes)
                .Append(",\"directionsTried\":").Append(s.directionsTried).Append(",\"planesTested\":").Append(s.planesTested).Append(",\"triangleVisits\":").Append(s.triangleVisits)
                .Append(",\"meshDirectionsTried\":").Append(s.meshDirectionsTried).Append(",\"meshPlanesTested\":").Append(s.meshPlanesTested)
                .Append(",\"refusedForTriangleMinimum\":").Append(s.refusedForTriangleMinimum).Append(",\"refusedForNoConvexOnASide\":").Append(s.refusedForNoConvexOnASide)
                .Append(",\"bentFramesTried\":").Append(s.bentFramesTried).Append(",\"bentSeedsTried\":").Append(s.bentSeedsTried)
                .Append(",\"bentCandidates\":").Append(s.bentCandidates).Append(",\"bentIterations\":").Append(s.bentIterations).Append(",\"bentElementsVerified\":").Append(s.bentElementsVerified)
                .Append(",\"positiveIndices\":").Append(r.distribution.positiveIndexCount).Append(",\"negativeIndices\":").Append(r.distribution.negativeIndexCount)
                .Append(",\"anchorStatus\":").Append(Json(r.anchorStatus.ToString())).Append(",\"anchorsPositive\":").Append(r.anchorPositive).Append(",\"anchorsNegative\":").Append(r.anchorNegative)
                .Append(",\"sides\":[").Append(r.positiveSides).Append(',').Append(r.negativeSides).Append(',').Append(r.splitSides).Append(',').Append(r.nearSides).Append(']')
                .Append(",\"kernelStatus\":").Append(Json(r.kernel.status.ToString()))
                .Append(",\"kernelNumbers\":[");
            double[] numbers = SpatialSplitCanonical.KernelNumbers(in r.kernel);
            for (int i = 0; i < numbers.Length; i++)
            {
                if (i > 0) line.Append(',');
                line.Append(Json(numbers[i].ToString("R", inv)));
            }

            line.Append("],\"workerMs\":").Append(Ms(r.workerSeconds)).Append(",\"bentMs\":").Append(Ms(r.bentSeconds)).Append(",\"bakeMs\":").Append(Ms(r.bakeSeconds))
                .Append(",\"copyMs\":").Append(Ms(r.copySeconds)).Append(",\"resultCopyMs\":").Append(Ms(r.resultSeconds)).Append(",\"copyBytes\":").Append(r.copyBytes)
                .Append(",\"sections\":[");
            for (int i = 0; i < SpatialSplitCanonical.SectionCount; i++)
            {
                if (i > 0) line.Append(',');
                line.Append(Json(hashes != null ? hashes[i] : ""));
            }

            line.Append("]}");
            string text = line.ToString();
            if (Interlocked.Read(ref _diskBytes) + text.Length + 1 > limits.maxSessionBytes)
            {
                Stop(SpatialSplitCaptureRefusal.SessionDisk, "a result line over " + Interlocked.Read(ref _diskBytes) + " bytes on disk (bound " + limits.maxSessionBytes + ")");
                Interlocked.Increment(ref _resultsLost);
                return;
            }

            AppendLine(_results, text);
            Interlocked.Increment(ref _resultsWritten);

            string B(bool v) => v ? "true" : "false";
            string F(float v) => Json(v.ToString("R", inv));
            string Ms(double seconds) => (seconds * 1000.0).ToString("R", inv);
        }

        private void AppendLine(FileStream to, string line)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            to.Write(bytes, 0, bytes.Length);
            to.Flush();
            Interlocked.Add(ref _diskBytes, bytes.Length);
        }

        private string SessionJson()
        {
            var text = new StringBuilder();
            text.Append("{\"formatVersion\":").Append(FormatVersion).Append(",\"sessionId\":").Append(Json(sessionId))
                .Append(",\"codeVersion\":").Append(Json(codeVersion))
                .Append(",\"startedUtc\":").Append(Json(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)))
                .Append(",\"vertexStructBytes\":").Append(UnsafeUtility.SizeOf<VpRenderVertex>())
                .Append(",\"limits\":{\"maxQueuedItems\":").Append(limits.maxQueuedItems).Append(",\"maxQueuedBytes\":").Append(limits.maxQueuedBytes)
                .Append(",\"maxSampleBytes\":").Append(limits.maxSampleBytes).Append(",\"maxSessionBytes\":").Append(limits.maxSessionBytes).Append('}')
                .Append(",\"notCapturedByDesign\":[\"candidates passed over before an issue\",\"inputs a NotFound or other mark keeps from being issued again\"]")
                .Append(",\"notes\":[");
            for (int i = 0; i < _notes.Length; i++)
            {
                if (i > 0) text.Append(',');
                text.Append(Json(_notes[i]));
            }

            return text.Append("]}").ToString();
        }

        private void WriteSummary()
        {
            WriteAtomic(Path.Combine(directory, "summary.json"), "{\"description\":" + Json(Describe()) + ",\"issued\":" + Issued + ",\"accepted\":" + Accepted
                + ",\"saved\":" + Saved + ",\"unindexed\":" + Unindexed + ",\"lost\":" + Lost + ",\"inFlight\":" + InFlight + ",\"notCaptured\":" + NotCapturedInAll + ",\"returned\":" + Returned
                + ",\"outputsNotKept\":" + OutputsNotKept
                + ",\"resultsWritten\":" + ResultsWritten + ",\"resultsNotRecorded\":" + ResultsLost + ",\"diskBytes\":" + DiskBytes + ",\"rawBytes\":" + RawBytes
                + ",\"mostQueuedItems\":" + MostQueuedItems + ",\"mostQueuedBytes\":" + MostQueuedBytes
                + ",\"writeMs\":" + WriteMs.ToString("R", CultureInfo.InvariantCulture) + ",\"mostWriteMs\":" + MostWriteMs.ToString("R", CultureInfo.InvariantCulture)
                + ",\"stopReason\":" + Json(StopReason.ToString()) + ",\"stopDetail\":" + Json(StopDetail) + "}");
        }

        private static void WriteAtomic(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            byte[] bytes = Encoding.UTF8.GetBytes(text + "\n");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush();
            }

            File.Move(temporary, path);
        }

        internal static string Json(string text)
        {
            var b = new StringBuilder((text?.Length ?? 0) + 2);
            b.Append('"');
            foreach (char ch in text ?? "")
            {
                switch (ch)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (ch < 0x20) b.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                        else b.Append(ch);
                        break;
                }
            }

            return b.Append('"').ToString();
        }
    }

    /// <summary>
    /// The capture of a launch: made from <see cref="Argument"/> &lt;an absolute directory&gt; the first time a split asks
    /// for it, into a new folder of its own there, and closed when the application quits. Without the argument there is
    /// none, and nothing is copied. <see cref="CodeArgument"/> names the code the build was made from (the launcher's
    /// commit), which the Player cannot know by itself.
    /// </summary>
    public static class SpatialSplitCaptureSession
    {
        public const string Argument = "-zantetsuSplitCapture";
        public const string CodeArgument = "-zantetsuSplitCaptureCode";

        private static bool s_read;
        private static SpatialSplitCaptureSink s_sink;
        private static long s_worlds;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            // The Editor without a domain reload: a session of an earlier play is closed, and this one reads the launch again.
            s_sink?.Close(0.0);
            s_sink = null;
            s_read = false;
        }

        /// <summary>The launch's capture, or null.</summary>
        public static SpatialSplitCaptureSink Sink
        {
            get
            {
                if (!s_read)
                {
                    s_read = true;
                    s_sink = FromArguments(Environment.GetCommandLineArgs());
                    if (s_sink != null)
                    {
                        Application.quitting += OnQuitting;
                    }
                }

                return s_sink;
            }
        }

        /// <summary>A number for each world a split is made in (a world's attempts are numbered from 1).</summary>
        internal static long NextWorld() => Interlocked.Increment(ref s_worlds);

        internal static SpatialSplitCaptureSink FromArguments(string[] arguments)
        {
            arguments ??= System.Array.Empty<string>();
            int at = System.Array.FindIndex(arguments, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
            if (at < 0)
            {
                return null;
            }

            if (at + 1 >= arguments.Length || arguments[at + 1].StartsWith("-", StringComparison.Ordinal) || !Path.IsPathRooted(arguments[at + 1]))
            {
                UnityEngine.Debug.LogError("SPLIT CAPTURE: " + Argument + " needs an absolute directory; nothing is captured");
                return null;
            }

            int codeAt = System.Array.FindIndex(arguments, a => string.Equals(a, CodeArgument, StringComparison.OrdinalIgnoreCase));
            string code = codeAt >= 0 && codeAt + 1 < arguments.Length ? arguments[codeAt + 1] : "(not given)";
#if ENABLE_IL2CPP
            const string backend = "IL2CPP";
#else
            const string backend = "Mono";
#endif
            string version = "app " + Application.version + "; build " + Application.buildGUID + "; Unity " + Application.unityVersion + "; " + backend
                + "; " + (Application.isEditor ? "Editor" : UnityEngine.Debug.isDebugBuild ? "Development Player" : "Player") + "; " + Application.platform + "; code " + code;

            // The launch's own switches, without the capture's directory (the private place it writes to).
            var notes = new System.Collections.Generic.List<string>();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!arguments[i].StartsWith("-zantetsu", StringComparison.OrdinalIgnoreCase) && !arguments[i].StartsWith("-sandbox", StringComparison.OrdinalIgnoreCase)) continue;
                bool valued = i + 1 < arguments.Length && !arguments[i + 1].StartsWith("-", StringComparison.Ordinal);
                notes.Add(i == at ? arguments[i] + " <directory>" : valued ? arguments[i] + " " + arguments[i + 1] : arguments[i]);
            }

            string folder = Path.Combine(arguments[at + 1], "split-capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var sink = new SpatialSplitCaptureSink(folder, SpatialSplitCaptureLimits.Default, version, notes.ToArray());
            UnityEngine.Debug.Log("SPLIT CAPTURE: on, session " + sink.sessionId + " into a new folder " + Path.GetFileName(folder) + "; " + version);
            return sink;
        }

        private static void OnQuitting()
        {
            Application.quitting -= OnQuitting;
            SpatialSplitCaptureSink sink = s_sink;
            if (sink == null) return;
            // Before the worlds go (their release comes after this, in the objects' destruction, and a world with work out is
            // not released at all): every attempt still out hands its result first, then the writer is closed.
            bool ended = sink.Quit(5.0);
            UnityEngine.Debug.Log("SPLIT CAPTURE: " + sink.Describe() + (ended ? "" : "; the writer had not ended after 5 s"));
        }
    }
}
#endif
