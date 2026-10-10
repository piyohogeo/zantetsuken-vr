using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The compaction of the selection's argument entries on the GPU (VpCommandCompact.compute), for a draw of the
    /// Vulkan plugin by vkCmdDrawIndexedIndirectCount: the Quest 3S Vulkan route's way where its conditions hold
    /// (DESIGN 4.5.10, D-217; <see cref="TrySelect"/>), the fixed multi-draw otherwise and where a launch says so.
    /// The selection's own entries stay as they are. A route that compacts issues, each frame: a zero event of the
    /// plugin for the count slots of the sets the frame uses (its own pass, before everything else); in each pass the
    /// plugin's compute barrier after the selection, then one compaction a draw unit (a run of one material's
    /// commands, one column); and draws the compacted run with a transfer copy of its count, made by the plugin in
    /// the draw's preparation. The order on the GPU is the plugin's own at each of these steps: none of them held
    /// when left to Unity's ordering of compute dispatches on this device (the record of 2026-10-10). Nothing here is
    /// read back by the CPU for a draw.
    /// <para>
    /// The compute shader is compiled for Vulkan alone (its wave operations), so the Editor and the PC Players have no
    /// kernel: <see cref="TryLoad"/> says so. <see cref="SelfTest"/> and <see cref="Verify"/> are diagnosis -- they
    /// read back -- and run only where a launch asks for them.
    /// </para>
    /// </summary>
    public static class VpCommandCompaction
    {
        public const string ShaderResource = "VpCommandCompact";

        /// <summary>Count slots a count buffer holds: one a draw unit of a pass. The entry stride is the plugin's.</summary>
        public const int Slots = 256;

        private static int s_firstSlot;

        /// <summary>
        /// Diagnosis only (2026-10-10), 0 in any product use: the count slot a pass's FIRST run takes (the runs after it
        /// take the next ones). The static scene has one run a column, so slot 0 (countBufferOffset 0) was the only
        /// count the device ever drew from; the city walk's second run (slot 1, offset 4) was where its GPU hung (icW1)
        /// or ran away (icW3). With 1 here the static scene draws its one run from offset 4 while slot 0 stays zero: a
        /// driver reading offset 0 draws nothing (safe), one reading the offset draws the scene as before.
        /// </summary>
        public static int FirstSlot
        {
            get => s_firstSlot;
            set
            {
                if (value < 0 || value >= Slots) throw new ArgumentOutOfRangeException(nameof(value), value, "a count slot of the set");
                s_firstSlot = value;
            }
        }

        private static readonly int SourceId = Shader.PropertyToID("_VpCompactSource");
        private static readonly int TargetId = Shader.PropertyToID("_VpCompactTarget");
        private static readonly int CountsId = Shader.PropertyToID("_VpCompactCounts");
        private static readonly int FirstId = Shader.PropertyToID("_VpCompactFirst");
        private static readonly int CountId = Shader.PropertyToID("_VpCompactCount");
        private static readonly int SlotId = Shader.PropertyToID("_VpCompactSlot");

        private static ComputeShader s_shader;
        private static int s_compact = -1;
        private static string s_failure;
        private static bool s_tried;

        /// <summary>The shader and its two kernels, once; false with the reason when the build or the device has none.</summary>
        public static bool TryLoad(out string failure)
        {
            if (!s_tried)
            {
                s_tried = true;
                s_shader = Resources.Load<ComputeShader>(ShaderResource);
                if (s_shader == null) s_failure = "the compute shader '" + ShaderResource + "' is not in the build";
                else if (!s_shader.HasKernel("CompactCommands")) s_failure = "the compute shader '" + ShaderResource + "' lacks its kernel";
                else
                {
                    s_compact = s_shader.FindKernel("CompactCommands");
                    if (!s_shader.IsSupported(s_compact)) s_failure = "the compute shader '" + ShaderResource + "' is not supported on " + SystemInfo.graphicsDeviceType + " (Vulkan wave operations only)";
                }
            }

            failure = s_failure;
            return s_failure == null;
        }

        public static long Compactions { get; private set; }
        public static long Verifications { get; private set; }

        // ---- the choice, and the run's tallies (process-wide: a launch's log reads them without holding a route) ------

        /// <summary>
        /// Whether a route of this launch compacts (DESIGN 4.5.10), and why or why not, in words for the log:
        /// only on a Vulkan device, where it is asked for (the default; <see cref="VpNativeDrawPlugin.VulkanIndirectCount"/>),
        /// the plugin's count draw is ready (the extension enabled on Unity's device and its entry point found), the
        /// plugin has the zero event and the compute barrier event, and the wave kernel is in the build and supported.
        /// Nothing is read back.
        /// </summary>
        public static bool TrySelect(out string reason)
        {
            if (!VpNativeDrawPlugin.Vulkan)
            {
                reason = "fixed: not a Vulkan device (the compaction is the Quest 3S Vulkan route's)";
                return false;
            }

            if (!VpNativeDrawPlugin.VulkanIndirectCount)
            {
                reason = "fixed: asked for (" + VpNativeDrawPlugin.FixedCommandsArgument + " or the launch's configuration)";
                return false;
            }

            if (!VpNativeDrawPlugin.TryLoad(out string pluginFailure))
            {
                reason = "fixed: the plugin is not loaded (" + pluginFailure + ")";
                return false;
            }

            if (!VpNativeDrawPlugin.IndirectCountReady)
            {
                reason = "fixed: the plugin's count draw is not ready (VK_KHR_draw_indirect_count not enabled on the device, or its entry point not found; the plugin's state says which)";
                return false;
            }

            if (VpNativeDrawPlugin.ZeroEventId == 0)
            {
                reason = "fixed: the plugin has no zero event";
                return false;
            }

            if (VpNativeDrawPlugin.BarrierEventId == 0)
            {
                reason = "fixed: the plugin has no compute barrier event";
                return false;
            }

            if (!TryLoad(out string kernelFailure))
            {
                reason = "fixed: " + kernelFailure;
                return false;
            }

            reason = "compaction: the default of the Quest 3S Vulkan route (count draw ready, zero and barrier events present, wave kernel supported)";
            return true;
        }

        /// <summary>How many frames at the start of a run are told apart as "first" in the recording time (the four sets are made in them).</summary>
        public const int FirstFrames = 8;

        public static long Issues, ZeroEvents, ZeroMissed, ZeroRefused, SlotsShort, Barriers, BarrierMissed;
        public static string LastChoice, Halted;
        public static int SetsMade;
        public static long SetBytesMade;
        public static double SetMakeSeconds, SetMakeMaxSeconds;
        private static int s_recordFrame = int.MinValue;
        private static long s_frameTicks, s_firstTicks, s_firstMaxTicks, s_steadyTicks, s_steadyMaxTicks;
        private static long s_framesClosed;

        /// <summary>A compaction set was made: its GPU bytes and what making it took on the calling thread.</summary>
        public static void NoteSetMade(long bytes, double seconds)
        {
            SetsMade++;
            SetBytesMade += bytes;
            SetMakeSeconds += seconds;
            SetMakeMaxSeconds = Math.Max(SetMakeMaxSeconds, seconds);
        }

        /// <summary>Time the route spent recording the compaction's own commands (zero events, dispatches) for <paramref name="frame"/>.</summary>
        public static void NoteRecordTime(int frame, long ticks)
        {
            if (frame != s_recordFrame)
            {
                CloseRecordFrame();
                s_recordFrame = frame;
                s_frameTicks = 0;
            }

            s_frameTicks += ticks;
        }

        private static void CloseRecordFrame()
        {
            if (s_recordFrame == int.MinValue) return;
            s_framesClosed++;
            if (s_framesClosed <= FirstFrames)
            {
                s_firstTicks += s_frameTicks;
                s_firstMaxTicks = Math.Max(s_firstMaxTicks, s_frameTicks);
            }
            else
            {
                s_steadyTicks += s_frameTicks;
                s_steadyMaxTicks = Math.Max(s_steadyMaxTicks, s_frameTicks);
            }
        }

        /// <summary>The run's tallies in one short line (a device log cuts long ones): the choice, the counts, the sets made and the recording time.</summary>
        public static string DescribeRun()
        {
            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            long steadyFrames = Math.Max(0, s_framesClosed - FirstFrames);
            long firstFrames = Math.Min(s_framesClosed, FirstFrames);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return "choice [" + (LastChoice ?? "none made") + "]" + (Halted != null ? " HALTED [" + Halted + "]" : "") + (FirstSlot != 0 ? " FIRST SLOT " + FirstSlot : "")
                + (DrawFromCopy ? " counts drawn from their copies" : " COUNTS DRAWN DIRECTLY") + (CastersFixed ? " CASTERS FIXED" : "")
                + (PoisonEntries ? " ENTRIES FILLED WITH ZERO EACH FRAME (" + EntryFills + " fills)" : "")
                + "; issues " + Issues + ", compactions " + Compactions + ", zero events " + ZeroEvents + ", fixed for want of zero " + ZeroMissed
                + ", fixed for want of slot " + SlotsShort + ", zero refused " + ZeroRefused + ", barriers " + Barriers + ", fixed for want of barrier " + BarrierMissed
                + "; sets made " + SetsMade + " (" + (SetBytesMade / (1024.0 * 1024.0)).ToString("F2", inv) + " MiB, " + (SetMakeSeconds * 1000.0).ToString("F3", inv)
                + " ms in all, longest " + (SetMakeMaxSeconds * 1000.0).ToString("F3", inv) + " ms)"
                + "; record ms/frame: first " + firstFrames + " frames mean " + (firstFrames > 0 ? (s_firstTicks * toMs / firstFrames).ToString("F4", inv) : "-")
                + " max " + (s_firstMaxTicks * toMs).ToString("F4", inv) + ", then " + steadyFrames + " frames mean "
                + (steadyFrames > 0 ? (s_steadyTicks * toMs / steadyFrames).ToString("F4", inv) : "-") + " max " + (s_steadyMaxTicks * toMs).ToString("F4", inv);
        }

        /// <summary>The zeroes a count buffer is made with: its slots are zeroed afterwards by the plugin, after each draw that read them.</summary>
        public static readonly uint[] ZeroSlots = new uint[Slots];

        /// <summary>
        /// Zeroes every slot of <paramref name="counts"/> by a transfer write in the command buffer. Only the self-test
        /// uses it now: in a frame the plugin zeroes the slots itself after the draw that read them (a managed
        /// SetBufferData before the compaction was not ordered before its atomics on the device, 2026-10-10).
        /// </summary>
        public static void Reset(CommandBuffer commands, GraphicsBuffer counts)
        {
            commands.SetBufferData(counts, ZeroSlots);
        }

        private static readonly int VerifyCompactedId = Shader.PropertyToID("_VpVerifyCompacted");
        private static readonly int VerifyCountsId = Shader.PropertyToID("_VpVerifyCounts");
        private static readonly int VerifyStatsId = Shader.PropertyToID("_VpVerifyStats");
        private static int s_verify = -1;
        private static GraphicsBuffer s_stats;

        /// <summary>The experiment's check (VerifyCompaction) runs after every compaction and its tallies are kept; off by default.</summary>
        public static bool VerificationEnabled { get; set; }

        // ---- diagnosis (2026-10-10): the count as each draw read it, and the casters drawn fixed ---------------------
        // The TL saw the cut objects' shadows vanish for one frame every tens of seconds with the compaction and not
        // with the fixed draw, while no CPU tally moved. With SnapshotEnabled every count draw names a slot of a ring
        // buffer; the plugin copies the count there right where the draw reads it (after the same barrier), and the
        // ring is read back asynchronously and tallied a column: samples, zeros (a draw of nothing), the most. Off by
        // default; the readback is for the record and decides no draw. CastersFixed draws the caster column fixed
        // while bodies and motion compact: whether the flashes are the caster column's.

        public static bool SnapshotEnabled { get; set; }
        public static bool CastersFixed { get; set; }

        /// <summary>
        /// The count draws read a transfer COPY of their count (the set's draw counts), which the plugin makes in each
        /// draw's preparation, and not the buffer the compaction's compute wrote: the way of a route that compacts.
        /// On Quest 3S a count draw recorded right after the compute that wrote its count read, in some frames, the
        /// count's reset value -- the cut objects' shadows were gone for a frame every tens of seconds (2026-10-10:
        /// seen whenever the caster column was compacted, never with the casters fixed, never in the run whose every
        /// count draw had a transfer read of its count before it). False is the earlier way, kept for the comparison;
        /// the snapshot diagnosis applies to it alone.
        /// </summary>
        public static bool DrawFromCopy { get; set; } = true;

        /// <summary>
        /// Diagnosis (2026-10-10), off by default: the frame's zero pass also fills the used range of every compacted
        /// entries buffer with zeroes -- records that draw nothing -- before the frame's compactions. The entries have
        /// no reset of their own, so a draw that read them before the compaction's writes would otherwise show the
        /// nearly identical list of four frames before; with this it draws nothing, and the cut objects or their
        /// shadows are gone for that frame: a stale read an eye can see. Not for a measurement (a fill a frame).
        /// </summary>
        public static bool PoisonEntries { get; set; }

        /// <summary>Entry fills the zero passes recorded (<see cref="PoisonEntries"/>).</summary>
        public static long EntryFills;
        public const int SnapshotSlots = 8192;
        private static GraphicsBuffer s_snapshot;
        private static readonly byte[] s_snapColumn = new byte[SnapshotSlots];
        private static readonly int[] s_snapFrame = new int[SnapshotSlots];
        private static readonly ushort[] s_snapRun = new ushort[SnapshotSlots];
        private static readonly int[] s_snapStart = new int[SnapshotSlots];
        private static System.IO.StreamWriter s_snapWriter;

        /// <summary>Where every sample is written as "frame,column,first,run,count" (null: tallies only). Set before the samples wanted.</summary>
        public static string SnapshotFile { get; set; }
        private static readonly bool[] s_snapPending = new bool[SnapshotSlots];
        private static int s_snapNext;
        private static AsyncGPUReadbackRequest s_snapRequest;
        private static bool s_snapRequested;
        private static int s_snapRequestFrame;
        private static readonly long[] s_snapSamples = new long[Columns], s_snapZeros = new long[Columns];
        private static readonly uint[] s_snapMax = new uint[Columns];
        private static readonly System.Collections.Generic.List<string> s_snapZeroNotes = new System.Collections.Generic.List<string>();
        public static long SnapshotRequests, SnapshotReads, SnapshotReadErrors;

        /// <summary>The ring the plugin copies each count into (made on first asking).</summary>
        public static GraphicsBuffer SnapshotBuffer
        {
            get
            {
                if (s_snapshot == null)
                {
                    s_snapshot = new GraphicsBuffer(GraphicsBuffer.Target.Raw, SnapshotSlots, sizeof(uint)) { name = "VP compaction count snapshots" };
                    s_snapshot.SetData(new uint[SnapshotSlots]);
                }

                return s_snapshot;
            }
        }

        /// <summary>The next slot of the ring for a count draw of <paramref name="column"/> in <paramref name="frame"/> (a run of <paramref name="run"/> commands).</summary>
        public static int TakeSnapshotSlot(int column, int frame, int first, int run)
        {
            int slot = s_snapNext;
            s_snapNext = (s_snapNext + 1) % SnapshotSlots;
            s_snapColumn[slot] = (byte)Math.Max(0, Math.Min(Columns - 1, column));
            s_snapFrame[slot] = frame;
            s_snapRun[slot] = (ushort)Math.Min(run, ushort.MaxValue);
            s_snapStart[slot] = first;
            s_snapPending[slot] = true;
            return slot;
        }

        /// <summary>Once a frame: tallies a finished readback (slots issued 8 or more frames before it) and asks for the next every 32 frames.</summary>
        public static void PumpSnapshots(int frame)
        {
            if (s_snapshot == null) return;
            if (s_snapRequested && s_snapRequest.done)
            {
                s_snapRequested = false;
                if (s_snapRequest.hasError) SnapshotReadErrors++;
                else
                {
                    SnapshotReads++;
                    var data = s_snapRequest.GetData<uint>();
                    if (SnapshotFile != null && s_snapWriter == null)
                    {
                        s_snapWriter = new System.IO.StreamWriter(SnapshotFile, false, new System.Text.UTF8Encoding(false), 1 << 16);
                        s_snapWriter.Write("frame,column,first,run,count\n");
                    }
                    for (int i = 0; i < SnapshotSlots; i++)
                    {
                        if (!s_snapPending[i] || s_snapFrame[i] > s_snapRequestFrame - 8) continue;
                        s_snapPending[i] = false;
                        int c = s_snapColumn[i];
                        uint v = data[i];
                        s_snapSamples[c]++;
                        if (s_snapWriter != null)
                        {
                            s_snapWriter.Write(s_snapFrame[i]); s_snapWriter.Write(','); s_snapWriter.Write(c); s_snapWriter.Write(',');
                            s_snapWriter.Write(s_snapStart[i]); s_snapWriter.Write(','); s_snapWriter.Write(s_snapRun[i]); s_snapWriter.Write(',');
                            s_snapWriter.Write(v); s_snapWriter.Write('\n');
                        }
                        if (v > s_snapMax[c]) s_snapMax[c] = v;
                        if (v == 0)
                        {
                            s_snapZeros[c]++;
                            if (s_snapZeroNotes.Count < 12) s_snapZeroNotes.Add(s_columnNames[c] + "@" + s_snapFrame[i] + "/run" + s_snapRun[i]);
                        }
                    }
                }
            }

            if (s_snapWriter != null && !s_snapRequested) s_snapWriter.Flush();
            if (!s_snapRequested && frame % 32 == 0)
            {
                s_snapRequest = AsyncGPUReadback.Request(s_snapshot);
                s_snapRequested = true;
                s_snapRequestFrame = frame;
                SnapshotRequests++;
            }
        }

        /// <summary>The snapshot tallies so far, one line (empty when the diagnosis is off).</summary>
        public static string DescribeSnapshots()
        {
            if (!SnapshotEnabled) return "count snapshots: off";
            var text = new System.Text.StringBuilder("count snapshots (readbacks " + SnapshotReads + " of " + SnapshotRequests + ", errors " + SnapshotReadErrors + ", frame " + Time.frameCount + "):");
            for (int c = 0; c < Columns; c++)
            {
                text.Append(' ').Append(s_columnNames[c]).Append(' ').Append(s_snapSamples[c]).Append(" zero ").Append(s_snapZeros[c]).Append(" max ").Append(s_snapMax[c]).Append(';');
            }

            if (s_snapZeroNotes.Count > 0) text.Append(" first zeros ").Append(string.Join(",", s_snapZeroNotes));
            return text.ToString();
        }

        /// <summary>
        /// Checks the compaction just recorded for the same run: every command with instances is among the compacted
        /// entries once with its fields, none without instances is. The tallies accumulate in one buffer for the run.
        /// </summary>
        private static readonly int VerifyColumnId = Shader.PropertyToID("_VpVerifyColumn");
        private const int StatsPerColumn = 12, Columns = 3;
        private static readonly string[] s_columnNames = { "body", "casters", "motion" };

        public static void Verify(CommandBuffer commands, GraphicsBuffer source, GraphicsBuffer target, GraphicsBuffer counts, int firstCommand, int commandCount, int slot, int column)
        {
            if (!TryLoad(out string failure)) throw new InvalidOperationException(failure);
            if (s_verify < 0) s_verify = s_shader.FindKernel("VerifyCompaction");
            if (s_stats == null)
            {
                s_stats = new GraphicsBuffer(GraphicsBuffer.Target.Raw, StatsPerColumn * Columns, sizeof(uint)) { name = "VP compaction verification" };
                s_stats.SetData(new uint[StatsPerColumn * Columns]);
            }

            commands.SetComputeIntParam(s_shader, FirstId, firstCommand);
            commands.SetComputeIntParam(s_shader, CountId, commandCount);
            commands.SetComputeIntParam(s_shader, SlotId, slot);
            commands.SetComputeIntParam(s_shader, VerifyColumnId, Math.Max(0, Math.Min(Columns - 1, column)));
            commands.SetComputeBufferParam(s_shader, s_verify, SourceId, source);
            commands.SetComputeBufferParam(s_shader, s_verify, VerifyCompactedId, target);
            commands.SetComputeBufferParam(s_shader, s_verify, VerifyCountsId, counts);
            commands.SetComputeBufferParam(s_shader, s_verify, VerifyStatsId, s_stats);
            commands.DispatchCompute(s_shader, s_verify, (commandCount + 63) / 64, 1, 1);
            Verifications++;
        }

        /// <summary>The check's tallies so far, read back now (a synchronous readback: for the record of a check run, never in a measurement).</summary>
        public static string ReadVerification()
        {
            if (s_stats == null) return "verification: no dispatch yet (" + Verifications + ")";
            var v = new uint[StatsPerColumn * Columns];
            s_stats.GetData(v);
            var text = new System.Text.StringBuilder("verification (compactions " + Compactions + ", checks " + Verifications + ", frame " + Time.frameCount + "):");
            for (int c = 0; c < Columns; c++)
            {
                int o = c * StatsPerColumn;
                if (v[o + 3] == 0) continue;
                text.Append(' ').Append(s_columnNames[c]).Append(": dispatches ").Append(v[o + 3]).Append(", with instances ").Append(v[o + 1])
                    .Append(" (").Append(((double)v[o + 1] / v[o + 3]).ToString("F1")).Append(" a dispatch), counts summed ").Append(v[o + 2])
                    .Append(" (").Append(((double)v[o + 2] / v[o + 3]).ToString("F1")).Append("), mismatched ").Append(v[o])
                    .Append(" = missing ").Append(v[o + 5]).Append(" + duplicated ").Append(v[o + 6]).Append(" + other fields ").Append(v[o + 8])
                    .Append(" + present without instances ").Append(v[o + 7]).Append(", counts above the run ").Append(v[o + 4]).Append(';');
            }

            return text.ToString();
        }

        /// <summary>
        /// Compacts the run [<paramref name="firstCommand"/>, +<paramref name="commandCount"/>) of <paramref name="source"/>
        /// into the same run of <paramref name="target"/>, counting into slot <paramref name="slot"/> of <paramref name="counts"/>
        /// (zeroed by the plugin after the last draw that read it).
        /// </summary>
        public static void Compact(CommandBuffer commands, GraphicsBuffer source, GraphicsBuffer target, GraphicsBuffer counts, int firstCommand, int commandCount, int slot)
        {
            if (!TryLoad(out string failure)) throw new InvalidOperationException(failure);
            if (slot < 0 || slot >= Slots) throw new ArgumentOutOfRangeException(nameof(slot));
            if (firstCommand < 0 || commandCount <= 0) throw new ArgumentOutOfRangeException(nameof(commandCount));
            commands.SetComputeIntParam(s_shader, FirstId, firstCommand);
            commands.SetComputeIntParam(s_shader, CountId, commandCount);
            commands.SetComputeIntParam(s_shader, SlotId, slot);
            commands.SetComputeBufferParam(s_shader, s_compact, SourceId, source);
            commands.SetComputeBufferParam(s_shader, s_compact, TargetId, target);
            commands.SetComputeBufferParam(s_shader, s_compact, CountsId, counts);
            commands.DispatchCompute(s_shader, s_compact, (commandCount + 63) / 64, 1, 1);
            Compactions++;
        }

        /// <summary>
        /// Runs the kernels once on a known input and reads the result back (the one readback of this experiment,
        /// deciding no draw): whether the device compacts as written. The text says what was expected and what came.
        /// </summary>
        public static bool SelfTest(out string report)
        {
            if (!TryLoad(out string failure)) { report = "self-test not run: " + failure; return false; }
            // A run longer than the group (the static scene's 1,602 commands), its visible commands scattered as a
            // pseudo-random tenth; the entries carry their own number and distinct arguments.
            const int commands = 1700, first = 7, count = 1602, slot = 3;
            var entries = new uint[commands * 6];
            var expected = new System.Collections.Generic.List<uint>();
            for (int c = 0; c < commands; c++)
            {
                bool kept = c >= first && c < first + count && (((uint)c * 2654435761u) >> 7) % 10u == 3u;
                entries[c * 6 + 0] = (uint)c;
                entries[c * 6 + 1] = 36u + (uint)c;
                entries[c * 6 + 2] = kept ? 2u : 0u;
                entries[c * 6 + 3] = 1000u + (uint)c;
                entries[c * 6 + 4] = 0u;
                entries[c * 6 + 5] = 500u + (uint)c * 4u;
                if (kept) expected.Add((uint)c);
            }

            GraphicsBuffer source = null, target = null, counts = null;
            CommandBuffer buffer = null;
            try
            {
                source = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commands, VpGpuCullSetup.NativeArgumentStride) { name = "VP compaction self-test source" };
                target = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commands, VpGpuCullSetup.NativeArgumentStride) { name = "VP compaction self-test target" };
                counts = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, Slots, sizeof(uint)) { name = "VP compaction self-test counts" };
                source.SetData(entries);
                var poison = new uint[commands * 6];
                for (int i = 0; i < poison.Length; i++) poison[i] = 0xDEADBEEFu;
                target.SetData(poison);
                var stale = new uint[Slots];
                for (int i = 0; i < stale.Length; i++) stale[i] = 77u;
                counts.SetData(stale);
                buffer = new CommandBuffer { name = "VP compaction self-test" };
                Reset(buffer, counts);
                Compact(buffer, source, target, counts, first, count, slot);
                Graphics.ExecuteCommandBuffer(buffer);
                var countsBack = new uint[Slots];
                counts.GetData(countsBack);
                var back = new uint[commands * 6];
                target.GetData(back);

                int n = (int)countsBack[slot];
                var got = new System.Collections.Generic.List<uint>();
                bool argumentsKept = true;
                for (int k = 0; k < Math.Min(n, commands - first); k++)
                {
                    int at = (first + k) * 6;
                    uint id = back[at];
                    got.Add(id);
                    if (id < commands && (back[at + 1] != 36u + id || back[at + 2] != 2u || back[at + 3] != 1000u + id || back[at + 5] != 500u + id * 4u)) argumentsKept = false;
                }

                got.Sort();
                bool otherSlotsZero = true;   // the reset zeroed every slot; the kernel added to its own alone
                for (int s = 0; s < Slots; s++) if (s != slot && countsBack[s] != 0u) otherSlotsZero = false;
                bool untouchedOutside = back[(first - 1) * 6] == 0xDEADBEEFu && back[(first + count) * 6] == 0xDEADBEEFu;
                bool same = n == expected.Count && got.Count == expected.Count;
                for (int k = 0; same && k < expected.Count; k++) same &= got[k] == expected[k];
                report = "wave compaction self-test: expected " + expected.Count + " of " + count + " commands, count slot " + slot + " = " + n
                    + ", the set of numbers " + (same ? "matches" : "DIFFERS") + ", arguments " + (argumentsKept ? "kept" : "CHANGED")
                    + ", other slots " + (otherSlotsZero ? "zero" : "NOT zero (reset failed)") + ", outside the run " + (untouchedOutside ? "untouched" : "WRITTEN")
                    + "; device " + SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceType;
                return same && argumentsKept && otherSlotsZero && untouchedOutside;
            }
            catch (Exception e)
            {
                report = "wave compaction self-test threw: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            finally
            {
                buffer?.Dispose();
                source?.Dispose();
                target?.Dispose();
                counts?.Dispose();
            }
        }
    }
}
