using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using Plugin = Zantetsu.Rendering.VpNativeDrawPlugin;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The keeper of what the plugin's issued events still need, and of the facts that say when they need it no more
    /// (TL, 2026-10-08; the sentinel ordering of the same day).
    /// <para>
    /// **What is judged, and by what.** An event's CPU data is a block of a route's ring (<see cref="VpNativeDrawData"/>),
    /// whose states move in the order of the code (see that class): Prepared in a pass's Execute, Recorded into the
    /// pass's command buffer at the end of that Execute, Submitted once the camera's rendering has ended -- URP's
    /// <c>context.Submit()</c> is inside RenderSingleCameraInternal, and RenderPipelineManager.endCameraRendering is
    /// raised after it returns -- and then Consumed when the plugin ran it on the rendering thread (its own mark), or
    /// Discard confirmed. A block is released by its own consumed mark, in any state; a Submitted block without the
    /// mark is released once the sentinel it is under is consumed; a Prepared or Recorded block is released by nothing
    /// else (a Prepared one the pass never recorded is freed by the ring itself: it was in no command buffer).
    /// </para>
    /// <para>
    /// **The sentinel, and why it cannot overtake.** The sentinel is a plugin event that only marks its block consumed.
    /// It is issued at one place alone: <see cref="OnEndCameraRendering"/>, through the camera's own
    /// ScriptableRenderContext (<c>context.ExecuteCommandBuffer</c> then <c>context.Submit()</c>), after that camera's
    /// rendering was submitted by URP on the same context. The rendering thread executes a context's submissions in
    /// the order they were submitted, so when the sentinel's block is consumed every command submitted before it has
    /// run. A Submitted block without its own mark at that point was therefore never submitted -- its command buffer
    /// was cleared (RenderGraph.ResetGraphAndLogException) or orphaned -- and nothing will run it: discard confirmed.
    /// Blocks recorded after the sentinel was issued are Recorded, not Submitted under it, and wait for the next one.
    /// Nothing here is judged by a frame count or a wait: a block is held until one of these facts is read.
    /// </para>
    /// <para>
    /// **What is held.** The event's CPU data (the ring blocks) and the GPU resources the events name: a disposed
    /// route's pipelines and constant buffers, a camera's constant buffers, and the display's buffers given up through
    /// <see cref="IVpNativeDrawSink.Retire"/> or, for a display that drew through more than one route in its life,
    /// through <see cref="Retire"/>, which waits on every route whose events may still name the buffer -- the last sink
    /// is never taken for the buffer's only user. Each resource is in exactly one entry, released exactly once.
    /// </para>
    /// <para>
    /// **When no event will ever run.** The device gone (<see cref="Plugin.IsAvailableNow"/> false after a device
    /// shutdown): the plugin then writes to no block, and the rendering thread had run every earlier event before the
    /// shutdown event, so everything is released. Application quitting: what is complete is released, the rest is
    /// counted and left to the process.
    /// </para>
    /// <para>
    /// **The journal.** With <see cref="JournalOn"/> (a test, or the Player argument <c>-zantetsuNativeJournal</c>) every
    /// state move, sentinel, deferral and release is written in order with the block's ring, slot and serial, so that a
    /// run's evidence is the order of events, not a final count. Off, nothing is allocated.
    /// </para>
    /// </summary>
    public static unsafe class VpNativeDrawRelease
    {
        /// <summary>What an entry waits on: a ring the entry owns and frees, a view of a ring that lives on, or several of these.</summary>
        public interface IRingWait
        {
            bool AllReleased(ulong consumedSentinel);

            void Free();

            string Describe();
        }

        /// <summary>A view of a ring the entry does not own: it waits on the blocks taken before the view was made, frees nothing.</summary>
        public sealed class RingView : IRingWait
        {
            private readonly VpNativeDrawData _ring;
            private readonly uint _serial;

            public RingView(VpNativeDrawData ring)
            {
                _ring = ring;
                _serial = ring.LastSerial;
            }

            public bool AllReleased(ulong consumedSentinel)
            {
                return _ring.IsDisposed || _ring.AllReleasedUpTo(_serial, consumedSentinel);
            }

            public void Free()
            {
            }

            public string Describe() => _ring.Name + " up to serial " + _serial;
        }

        /// <summary>Every wait of several: over when each is over (a buffer named by the events of more than one route).</summary>
        public sealed class AllOf : IRingWait
        {
            private readonly List<IRingWait> _waits;

            public AllOf(List<IRingWait> waits)
            {
                _waits = waits;
            }

            public int Count => _waits.Count;

            public bool AllReleased(ulong consumedSentinel)
            {
                for (int i = 0; i < _waits.Count; i++)
                {
                    if (!_waits[i].AllReleased(consumedSentinel)) return false;
                }

                return true;
            }

            public void Free()
            {
                foreach (IRingWait wait in _waits) wait.Free();
            }

            public string Describe()
            {
                var text = new StringBuilder("all of [");
                for (int i = 0; i < _waits.Count; i++) text.Append(i > 0 ? "; " : "").Append(_waits[i].Describe());
                return text.Append(']').ToString();
            }
        }

        private sealed class OwnedRing : IRingWait
        {
            private readonly VpNativeDrawData _ring;

            public OwnedRing(VpNativeDrawData ring)
            {
                _ring = ring;
            }

            public bool AllReleased(ulong consumedSentinel) => _ring.AllReleased(consumedSentinel);

            public void Free()
            {
                Unregister(_ring);
                _ring.Dispose();
            }

            public string Describe() => _ring.Name + " (owned, " + _ring.InFlight + " in flight)";
        }

        private sealed class Entry
        {
            public long id;
            public string name;
            public IRingWait wait;
            public readonly List<IDisposable> gpuResources = new List<IDisposable>();
            public int sinceFrame;
        }

        private const int SentinelBlocks = 8;
        private const int JournalLimit = 50000;

        private static readonly List<Entry> s_entries = new List<Entry>();
        private static readonly List<VpNativeDrawData> s_rings = new List<VpNativeDrawData>();
        private static readonly List<string> s_journal = new List<string>();
        private static IntPtr[] s_sentinels;
        private static ulong[] s_sentinelSerials;
        private static bool[] s_sentinelNoted;
        private static ulong s_sentinelSerial;
        private static ulong s_consumedSentinel;
        private static CommandBuffer s_commands;
        private static bool s_subscribed;
        private static long s_entryId;

        /// <summary>How many entries (disposed routes, cameras, retired buffers) wait for their events to finish.</summary>
        public static int Pending => s_entries.Count;

        public static long Released { get; private set; }

        /// <summary>The longest an entry waited, in frames. A record, never a condition.</summary>
        public static int LongestWaitFrames { get; private set; }

        /// <summary>How many entries were left at quit with events still in flight.</summary>
        public static int LeftAtQuit { get; private set; }

        /// <summary>How many sentinels were issued; how many ends of a camera's rendering wanted one but every sentinel block was still unconsumed.</summary>
        public static long SentinelsIssued { get; private set; }

        public static long SentinelsNotIssued { get; private set; }

        public static long SentinelsConsumed => (long)s_consumedSentinel;

        /// <summary>The serial the next sentinel will carry.</summary>
        public static ulong NextSentinelSerial => s_sentinelSerial + 1;

        /// <summary>The serial of the latest sentinel consumed.</summary>
        public static ulong ConsumedSentinelSerial => s_consumedSentinel;

        /// <summary>How many buffers were retired through <see cref="Retire"/> with more than one route's events to wait on.</summary>
        public static long RetiredAcrossRoutes { get; private set; }

        /// <summary>A test's stand-in for the sentinel's issue: null issues it through the camera's context; else the delegate takes the sentinel block, and marks it consumed when it sees fit.</summary>
        public static Action<IntPtr> IssueSentinelForTest;

        /// <summary>A test's stand-in for the plugin's availability; null asks the plugin.</summary>
        public static Func<bool> DeviceAvailableForTest;

        /// <summary>Whether the journal is written (see the class remarks). Set by a test, or by the Player argument.</summary>
        public static bool JournalOn;

        /// <summary>The journal's lines, oldest first (the newest <see cref="JournalLimit"/> kept).</summary>
        public static IReadOnlyList<string> Journal => s_journal;

        public static void ClearJournal() => s_journal.Clear();

        /// <summary>The journal as text, for a file or a test's output.</summary>
        public static string JournalText()
        {
            var text = new StringBuilder();
            foreach (string line in s_journal) text.Append(line).Append('\n');
            return text.ToString();
        }

        /// <summary>A journal line, with the frame. Nothing is done when the journal is off.</summary>
        public static void Note(string line)
        {
            if (!JournalOn) return;
            s_journal.Add("f" + Time.frameCount + " " + line);
            if (s_journal.Count > JournalLimit) s_journal.RemoveRange(0, s_journal.Count - JournalLimit);
        }

        /// <summary>A journal line about a block.</summary>
        public static void Note(VpNativeDrawData ring, string what, int index, uint serial)
        {
            if (!JournalOn) return;
            Note(ring.Name + " slot " + index + " serial " + serial + ": " + what);
        }

        /// <summary>A test's rendering thread: marks a sentinel block consumed, as the plugin's sentinel event does.</summary>
        public static void MarkSentinelConsumedForTest(IntPtr sentinel)
        {
            ((Plugin.Draw*)sentinel)->consumed = 1;
        }

        /// <summary>A live ring: sealed at the end of each camera's rendering (see <see cref="OnEndCameraRendering"/>) and judged by the sentinels.</summary>
        public static void Register(VpNativeDrawData ring)
        {
            if (ring != null && !s_rings.Contains(ring)) s_rings.Add(ring);
            Subscribe();
        }

        public static void Unregister(VpNativeDrawData ring)
        {
            s_rings.Remove(ring);
        }

        /// <summary>
        /// Takes a disposed route's ring and GPU resources (pipelines, buffers), to be released once the ring's blocks
        /// are all consumed or confirmed discarded. The ring stays registered (sealed and judged) until then. The ring
        /// may be null (a route that never issued): the resources go at once.
        /// </summary>
        public static void Defer(string name, VpNativeDrawData ring, IEnumerable<IDisposable> gpuResources)
        {
            if (ring != null)
            {
                ring.DropPrepared();   // a dispose happens in no Execute: a Prepared block was never recorded
                Register(ring);
            }

            Defer(name, ring != null ? new OwnedRing(ring) : null, gpuResources);
        }

        /// <summary>The same, waiting on a wait the entry does not own (<see cref="RingView"/>, <see cref="AllOf"/>).</summary>
        public static void Defer(string name, IRingWait wait, IEnumerable<IDisposable> gpuResources)
        {
            var entry = new Entry { id = ++s_entryId, name = name, wait = wait, sinceFrame = Time.frameCount };
            if (gpuResources != null)
            {
                foreach (IDisposable resource in gpuResources)
                {
                    if (resource != null) entry.gpuResources.Add(resource);
                }
            }

            s_entries.Add(entry);
            if (JournalOn) Note("entry " + entry.id + " (" + name + ") deferred: " + entry.gpuResources.Count + " resources, waits on " + (wait != null ? wait.Describe() : "nothing"));
            Subscribe();
            Tick();
        }

        /// <summary>
        /// A buffer an owner is done with that the events of any of <paramref name="sinks"/> may still name: disposed
        /// once every one of those sinks' issued events is consumed or confirmed discarded -- not when the last sink's
        /// are. An owner that drew through one sink alone may call that sink's <see cref="IVpNativeDrawSink.Retire"/>.
        /// </summary>
        public static void Retire(string name, IReadOnlyList<IVpNativeDrawSink> sinks, GraphicsBuffer buffer)
        {
            if (buffer == null) return;
            IRingWait wait = null;
            List<IRingWait> waits = null;
            if (sinks != null)
            {
                for (int i = 0; i < sinks.Count; i++)
                {
                    IRingWait one = sinks[i]?.IssuedEventsWait();
                    if (one == null) continue;
                    if (wait == null)
                    {
                        wait = one;
                    }
                    else
                    {
                        waits ??= new List<IRingWait> { wait };
                        waits.Add(one);
                    }
                }
            }

            if (waits != null)
            {
                wait = new AllOf(waits);
                RetiredAcrossRoutes++;
            }

            Defer(name, wait, new IDisposable[] { buffer });
        }

        /// <summary>
        /// Reads the sentinels consumed, tells the rings, and releases every entry whose wait is over. Called after
        /// each camera's and each frame's rendering, and at a deferral; a test may call it. Issues nothing (the
        /// sentinel's one place is <see cref="OnEndCameraRendering"/>). Returns how many entries were released.
        /// </summary>
        public static int Tick()
        {
            bool deviceGone = !DeviceAvailable();
            ReadSentinels();
            foreach (VpNativeDrawData ring in s_rings)
            {
                ring.SetConsumedSentinel(s_consumedSentinel);
                if (deviceGone) ring.DeviceGone();
            }

            int released = 0;
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                Entry entry = s_entries[i];
                if (entry.wait != null && !deviceGone && !entry.wait.AllReleased(s_consumedSentinel))
                {
                    continue;
                }

                Release(entry, deviceGone ? "device gone" : entry.wait == null ? "nothing to wait on" : "its events are over");
                s_entries.RemoveAt(i);
                released++;
            }

            return released;
        }

        /// <summary>
        /// The end of a camera's rendering: URP has submitted the camera's commands on <paramref name="context"/> (or
        /// cleared them, never to be submitted), and no pass's Execute is in progress. Every Prepared block was then
        /// never recorded and is freed; and when a Recorded block is unconsumed and something waits (an entry, or more
        /// than half a ring in flight), the sentinel is issued here, through the same context, and the Recorded blocks
        /// become Submitted under it -- the one place a sentinel is issued, and the one place blocks are promoted.
        /// </summary>
        public static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            // The camera's name is read only for a journal line or a warning: a string a frame is an allocation
            // (natP11, 2026-10-08: +48 B a frame against natP10).
            Seal(context, camera);
            Tick();
        }

        /// <summary>A test's end of a camera's rendering: the same with the sentinel given to <see cref="IssueSentinelForTest"/>.</summary>
        public static void SealForTest()
        {
            Seal(null, null);
            Tick();
        }

        private static string Where(Camera camera) => camera != null ? camera.name : "test";

        private static void Seal(ScriptableRenderContext? context, Camera camera)
        {
            foreach (VpNativeDrawData ring in s_rings)
            {
                ring.DropPrepared();
            }

            if (!WantsSentinel())
            {
                return;
            }

            if (!DeviceAvailable())
            {
                return;   // Tick releases everything
            }

            int slot = FreeSentinelSlot();
            if (slot < 0)
            {
                // Every sentinel block is still unconsumed: the rendering thread has not reached the oldest of them,
                // so a new one would tell nothing yet. The Recorded blocks stay Recorded, and a later sentinel covers
                // them (their camera's submission is behind any sentinel issued later on any context).
                SentinelsNotIssued++;
                return;
            }

            var block = (Plugin.Draw*)s_sentinels[slot];
            *block = default;
            block->version = Plugin.ContractVersion;
            ulong serial = s_sentinelSerial + 1;
            block->serial = (uint)serial;
            try
            {
                if (IssueSentinelForTest != null)
                {
                    IssueSentinelForTest(s_sentinels[slot]);
                }
                else
                {
                    if (context == null) return;
                    s_commands ??= new CommandBuffer { name = "VP Native Draw Sentinel" };
                    s_commands.Clear();
                    s_commands.IssuePluginEventAndData(Plugin.RenderEventFunction, Plugin.SentinelEventId, s_sentinels[slot]);
                    context.Value.ExecuteCommandBuffer(s_commands);
                    context.Value.Submit();
                }
            }
            catch (Exception e)
            {
                // Not issued: nothing is promoted under it, so nothing is released by it.
                Debug.LogWarning("VP native draw release: the sentinel could not be issued at " + Where(camera) + ": " + e.Message);
                return;
            }

            // Issued, behind the camera's submission on its context: the Recorded blocks are Submitted under it.
            s_sentinelSerial = serial;
            s_sentinelSerials[slot] = serial;
            s_sentinelNoted[slot] = false;
            SentinelsIssued++;
            int promoted = 0;
            foreach (VpNativeDrawData ring in s_rings)
            {
                promoted += ring.SubmitRecorded(serial);
            }

            if (JournalOn) Note("sentinel " + serial + " issued at the end of " + Where(camera) + "'s rendering; " + promoted + " recorded blocks submitted under it");
        }

        private static bool WantsSentinel()
        {
            bool recordedUnconsumed = false;
            int recorded = 0, capacity = 0;
            foreach (VpNativeDrawData ring in s_rings)
            {
                if (ring.IsDisposed) continue;
                if (!ring.HasRecordedUnconsumed) continue;
                recordedUnconsumed = true;
                recorded += ring.InFlightIn(VpNativeDrawData.BlockState.Recorded);
                capacity += ring.Blocks;
            }

            if (!recordedUnconsumed)
            {
                return false;
            }

            // Something waits on the blocks (a deferred release), or the Recorded blocks crowd the rings (events whose
            // commands were cleared would otherwise hold their slots until something waits). A trigger, not a ground:
            // what releases a block is the sentinel's consumption, read in Tick.
            return s_entries.Count > 0 || recorded * 2 > capacity;
        }

        private static int FreeSentinelSlot()
        {
            if (s_sentinels == null)
            {
                s_sentinels = new IntPtr[SentinelBlocks];
                s_sentinelSerials = new ulong[SentinelBlocks];
                s_sentinelNoted = new bool[SentinelBlocks];
                for (int i = 0; i < SentinelBlocks; i++)
                {
                    s_sentinels[i] = Marshal.AllocHGlobal(sizeof(Plugin.Draw));
                    *(Plugin.Draw*)s_sentinels[i] = default;
                }
            }

            for (int i = 0; i < SentinelBlocks; i++)
            {
                if (s_sentinelSerials[i] == 0 || ((Plugin.Draw*)s_sentinels[i])->consumed != 0) return i;
            }

            return -1;
        }

        private static void ReadSentinels()
        {
            if (s_sentinels == null)
            {
                return;
            }

            for (int i = 0; i < SentinelBlocks; i++)
            {
                var draw = (Plugin.Draw*)s_sentinels[i];
                if (s_sentinelSerials[i] == 0 || draw->consumed == 0) continue;
                if (!s_sentinelNoted[i])
                {
                    s_sentinelNoted[i] = true;
                    if (JournalOn) Note("sentinel " + s_sentinelSerials[i] + " consumed: every command submitted before it has run");
                }

                if (s_sentinelSerials[i] > s_consumedSentinel) s_consumedSentinel = s_sentinelSerials[i];
            }
        }

        private static bool DeviceAvailable()
        {
            if (DeviceAvailableForTest != null)
            {
                return DeviceAvailableForTest();
            }

            return Plugin.IsAvailableNow();
        }

        private static void Release(Entry entry, string why)
        {
            foreach (IDisposable resource in entry.gpuResources)
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception e)
                {
                    Debug.LogWarning("VP native draw release: " + entry.name + ": " + e.Message);
                }
            }

            int resources = entry.gpuResources.Count;
            entry.gpuResources.Clear();
            entry.wait?.Free();
            entry.wait = null;
            Released++;
            LongestWaitFrames = Mathf.Max(LongestWaitFrames, Time.frameCount - entry.sinceFrame);
            if (JournalOn) Note("entry " + entry.id + " (" + entry.name + ") released (" + why + "): " + resources + " resources disposed");
        }

        private static void Subscribe()
        {
            if (s_subscribed)
            {
                return;
            }

            s_subscribed = true;
            if (!JournalOn && Array.IndexOf(Environment.GetCommandLineArgs(), "-zantetsuNativeJournal") >= 0) JournalOn = true;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            Application.quitting += OnQuitting;
        }

        private static void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            Tick();
        }

        private static void OnQuitting()
        {
            Tick();
            LeftAtQuit += s_entries.Count;
            if (s_entries.Count > 0)
            {
                Debug.Log("VP native draw release at quit: " + Describe());
            }
        }

        /// <summary>For a record: what waits, with the oldest wait, and the sentinels.</summary>
        public static string Describe()
        {
            int oldest = 0;
            foreach (Entry entry in s_entries) oldest = Mathf.Max(oldest, Time.frameCount - entry.sinceFrame);
            long neverRecorded = 0, discards = 0;
            foreach (VpNativeDrawData ring in s_rings)
            {
                neverRecorded += ring.NeverRecorded;
                discards += ring.DiscardsConfirmed;
            }

            return "deferred releases: pending " + s_entries.Count + " (oldest " + oldest + " frames), released " + Released + " (longest wait " + LongestWaitFrames
                   + " frames), sentinels issued " + SentinelsIssued + " / consumed " + s_consumedSentinel + " / not issued for want of a block " + SentinelsNotIssued
                   + ", blocks never recorded " + neverRecorded + ", discards confirmed " + discards + ", retired across routes " + RetiredAcrossRoutes + ", left at quit " + LeftAtQuit;
        }

        /// <summary>For a record: the entries waiting now, each with what it waits on.</summary>
        public static string DescribePending()
        {
            var text = new StringBuilder();
            foreach (Entry entry in s_entries)
            {
                text.Append("entry ").Append(entry.id).Append(" (").Append(entry.name).Append("): ").Append(entry.gpuResources.Count).Append(" resources, waits on ")
                    .Append(entry.wait != null ? entry.wait.Describe() : "nothing").Append('\n');
            }

            return text.ToString();
        }
    }
}
