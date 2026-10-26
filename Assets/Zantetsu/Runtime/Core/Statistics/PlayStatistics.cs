using System;

namespace Zantetsu.Core.Statistics
{
    /// <summary>The conditions a frame time distribution is only meaningful under: when one changes, the frames after it go to another epoch.</summary>
    public struct PlayConditions : IEquatable<PlayConditions>
    {
        public string Scene;          // the scene played
        public string Mode;           // the scenario (play, city-walk, ...)
        public string DrawRoute;      // the display's route (VP3C-NATIVE, ...)
        public string XrRuntime;      // the OpenXR runtime's name, or "none"
        public float RefreshRateHz;   // the XR display's refresh rate; 0 where unknown
        public float RenderScale;     // XR resolution scale, or Android flat effective render scale; 0 where unspecified
        public int EyeTextureWidth, EyeTextureHeight;   // per eye; 0 where no XR
        public int ScreenWidth, ScreenHeight;           // Android flat output pixels; 0 where unspecified
        public bool FsrEnabled;          // Android flat effective upscaling and draw settings; false where unspecified
        public bool MultiDrawIndirect;
        public bool DrawIndirectCount;
        public int MsaaSamples;
        public bool CutSurfaceDebug;  // the menu's cut surface debug colour (a rendering setting)

        public bool Equals(PlayConditions o)
        {
            return Scene == o.Scene && Mode == o.Mode && DrawRoute == o.DrawRoute && XrRuntime == o.XrRuntime
                && RefreshRateHz.Equals(o.RefreshRateHz) && RenderScale.Equals(o.RenderScale)
                && EyeTextureWidth == o.EyeTextureWidth && EyeTextureHeight == o.EyeTextureHeight
                && ScreenWidth == o.ScreenWidth && ScreenHeight == o.ScreenHeight && FsrEnabled == o.FsrEnabled
                && MultiDrawIndirect == o.MultiDrawIndirect && DrawIndirectCount == o.DrawIndirectCount
                && MsaaSamples == o.MsaaSamples && CutSurfaceDebug == o.CutSurfaceDebug;
        }

        public override bool Equals(object obj) => obj is PlayConditions o && Equals(o);
        public override int GetHashCode() => (Scene ?? "").GetHashCode() ^ (Mode ?? "").GetHashCode() ^ EyeTextureWidth ^ (MsaaSamples << 16);
    }

    /// <summary>One frame's times as the sources gave them: each present or absent, never 0 for absent.</summary>
    public struct FrameTimeSample
    {
        public bool HasMain, HasRender, HasGpu, HasGpuXr;
        public float MainMs, RenderMs, GpuMs, GpuXrMs;
    }

    /// <summary>The buffers whose use and room the statistics keep (names and units in <see cref="PlayStatistics.CapacityName"/>).</summary>
    /// <summary>
    /// The fixed buffers whose headroom the statistics keep (DESIGN 21.18): for each, the fixed capacity and the end in
    /// use -- holes included, as the buffer's own end figure gives it -- never a use net of holes (that would need a scan).
    /// </summary>
    public enum PlayCapacityKind
    {
        CpuVertices = 0,       // the CPU vertex storage: the reservation's limit, and the committed end (pages the reservations have reached)
        CpuIndices = 1,        // the CPU index storage, likewise
        DrawCommandSlots = 2,  // the display's draw command slots: the end taken at the adoption
        GpuVertices = 3,       // the display's GPU vertex buffer: its capacity now, and the highest end written
        GpuIndices = 4,        // its GPU index buffer
        DrawInstanceSlots = 5, // the display's instance records: the end taken at the adoption
        RenderFragments = 6,   // the adopted snapshot's render fragments, of the display's fragment capacity
        Count = 7,
    }

    /// <summary>The totals the game keeps cumulatively, which the statistics count from the agreement on.</summary>
    public enum PlayTotalKind
    {
        AcceptedCuts = 0,
        CollectedFragments = 1,
        Count = 2,
    }

    /// <summary>The in-game UI settings as they stand (the latest values, not an epoch key: a slider moved is not another distribution).</summary>
    public struct PlayUiSettings : IEquatable<PlayUiSettings>
    {
        public bool Known;              // false where no menu gives them
        public float Volume;
        public float MoveSpeed;         // m/s
        public float TurnDegrees;       // degrees per second, or the snap angle
        public bool TurnIsContinuous;
        public bool TurnOnRightStick;   // the sticks' layout: the turn on the right stick (the left moves and strafes), else on the left with the move
        public float BladeAngleDegrees;

        public bool Equals(PlayUiSettings o) => Known == o.Known && Volume.Equals(o.Volume) && MoveSpeed.Equals(o.MoveSpeed)
            && TurnDegrees.Equals(o.TurnDegrees) && TurnIsContinuous == o.TurnIsContinuous && TurnOnRightStick == o.TurnOnRightStick
            && BladeAngleDegrees.Equals(o.BladeAngleDegrees);
        public override bool Equals(object obj) => obj is PlayUiSettings o && Equals(o);
        public override int GetHashCode() => Known ? 1 : 0;
    }

    /// <summary>The failures counted where they already are counted.</summary>
    public enum PlayFailureKind
    {
        DrawRefused = 0,
        RingFull = 1,
        ShadowMapFormatRefused = 2,
        IndirectCountZeroRefused = 3,
        CompactionSlotsShort = 4,
        StorageRoomRefused = 5,
        CutAborted = 6,
        CutFailed = 7,
        CutKernelFailed = 8,
        TerminationRequested = 9,
        Count = 10,
    }

    /// <summary>
    /// A fixed buffer's figures: its capacity (the latest), the highest end in use (holes included), the least headroom
    /// (capacity less end, at the reading it was least), how often it grew, how often it fell short. The unit named.
    /// </summary>
    public struct PlayCapacity
    {
        public long Capacity;              // the capacity allocated now (a growing buffer's current size)
        public long Limit;                 // the fixed limit the capacity may grow to; 0 where there is none
        public long MaxEnd;                // the highest end in use, holes included
        public long MinHeadroomToCapacity; // capacity - end, at the reading it was least: the room before the next growth
        public long MinHeadroomToLimit;    // limit - end, at the reading it was least: the room before a refusal (meaningless without a limit)
        public long Growths, Shortfalls;
        public bool Read;                  // false before any reading: the headrooms mean nothing then
    }

    /// <summary>
    /// The frames under one set of <see cref="PlayConditions"/>: the four time histograms and their sample counts, and
    /// how many frames changed the cut structure (a frame with any change counts once).
    /// </summary>
    public sealed class PlayEpoch
    {
        public PlayConditions Conditions;
        public readonly PlayTimeHistogram Main = new PlayTimeHistogram();      // cpuMainThreadFrameTime: the main thread's frame, the wait for the present INCLUDED
        public readonly PlayTimeHistogram Render = new PlayTimeHistogram();    // cpuRenderThreadFrameTime
        public readonly PlayTimeHistogram Gpu = new PlayTimeHistogram();       // gpuFrameTime (FrameTimingManager)
        public readonly PlayTimeHistogram GpuXr = new PlayTimeHistogram();     // the XR display's app GPU time, a different source and definition, kept apart
        public readonly PlayTimeHistogram Interval = new PlayTimeHistogram();  // the real time between two counted frames
        public ulong Frames;                      // frames counted in this epoch
        public ulong StructureChangedFrames;      // of them, frames in which the cut structure changed (once a frame, however many changes)
        public ulong MainMissing, RenderMissing, GpuMissing, GpuXrMissing;   // frames whose sample lacked the value (the unattributed ones among them)
        public ulong Unattributed;                // of the missing: frames whose late-arriving times could not be told from the frames before a boundary, so were set aside
        public double PlaySeconds;                // the intervals of the counted frames, summed

        internal void Clear(in PlayConditions conditions)
        {
            Conditions = conditions;
            Main.Clear(); Render.Clear(); Gpu.Clear(); GpuXr.Clear(); Interval.Clear();
            Frames = StructureChangedFrames = 0;
            MainMissing = RenderMissing = GpuMissing = GpuXrMissing = 0;
            Unattributed = 0;
            PlaySeconds = 0.0;
        }
    }

    /// <summary>
    /// The lightweight play statistics of a distribution build (DESIGN 21.18): no history of frames, only fixed-length
    /// histograms, cumulative counts and maxima, kept by epoch of play conditions. Everything here is fixed-size and
    /// allocated once; the steady-state calls allocate nothing. It counts and keeps; it decides nothing of the game,
    /// and the game reads nothing from it.
    /// <para>
    /// **Epochs.** The time histograms are kept per set of conditions (scene, route, refresh rate, render scale, ...),
    /// so that a distribution is never explained by conditions it was not taken under. Up to <see cref="MaxEpochs"/>
    /// are kept; a change that would need one more is counted (<see cref="EpochChangesDropped"/>) and its frames
    /// are counted outside the histograms (<see cref="FramesOutsideEpochs"/>). Coming back to conditions already seen
    /// goes on in their epoch. There is no history of changes.
    /// </para>
    /// <para>
    /// **What a frame is.** The caller gives only the frames of ordinary play: it leaves out start-up, loading, the
    /// pause and the time out of focus, and the first frame after any of them (its interval is not a frame's).
    /// </para>
    /// </summary>
    public sealed class PlayStatistics
    {
        /// <summary>
        /// The version of this record's shape and meaning (fields, units, kinds, what a counted frame is). A change of
        /// meaning is a new version. 1 (2026-10-13/14: the first records stored): frames counted from the world's being
        /// ready and by the desktop window's focus; the graphics API's level under "gpuDriver"; the engine's generic XR
        /// names. 2 (2026-10-14): frames counted from where play begins (the start-up preparation and the head's
        /// tracking over) and, in XR, by the XR session's focus; "graphicsApi" apart from "gpuDriver" (the driver's
        /// version, or null); the OpenXR runtime's own name and version; the XR device null where only a generic name
        /// is to be had. 3 (2026-10-20): the settings' "turnOnRightStick" (the sticks' layout); "turn" and
        /// "turnContinuous" keep their meaning (the snap angle or the turn speed, and which). 4: the device model and
        /// Android flat screen size, FSR and effective indirect draw settings; renderScale also describes its effective scale.
        /// </summary>
        public const int FormatVersion = 4;

        public const int MaxEpochs = 4;

        private readonly PlayEpoch[] _epochs = new PlayEpoch[MaxEpochs];
        private int _epochCount;
        private int _current = -1;

        private readonly PlayCapacity[] _capacities = new PlayCapacity[(int)PlayCapacityKind.Count];
        private readonly long[] _failures = new long[(int)PlayFailureKind.Count];

        public PlayStatistics()
        {
            for (int i = 0; i < _epochs.Length; i++)
            {
                _epochs[i] = new PlayEpoch();
            }
        }

        /// <summary>Rises on every change: a snapshot is taken only where it moved since the last one.</summary>
        public long ChangeCount { get; private set; }

        public int EpochCount => _epochCount;
        public PlayEpoch EpochAt(int index) => index >= 0 && index < _epochCount ? _epochs[index] : null;
        public PlayEpoch Current => _current >= 0 ? _epochs[_current] : null;

        /// <summary>Condition changes that found every epoch taken: their frames went outside the histograms.</summary>
        public int EpochChangesDropped { get; private set; }

        public ulong FramesOutsideEpochs { get; private set; }

        // ---- load ----
        public int MaxLiveFragments { get; private set; }
        public long AcceptedCuts => _totals[(int)PlayTotalKind.AcceptedCuts];
        public long CollectedFragments => _totals[(int)PlayTotalKind.CollectedFragments];
        private readonly long[] _totals = new long[(int)PlayTotalKind.Count];

        /// <summary>The UI settings as last noted.</summary>
        public PlayUiSettings Settings { get; private set; }

        // ---- identity, conditions, environment: set once, strings kept as given ----
        public string BuildId = "";
        public string SessionId = "";
        public string Platform = "";
        public string OperatingSystem = "";
        public string DeviceModel;      // model only, never the user-given device name or an identifier; null where not obtained
        public string Cpu = "";
        public string Gpu = "";
        public string GraphicsApi = ""; // SystemInfo.graphicsDeviceVersion: the graphics API and its level (not a driver's version)
        public string GpuDriver;        // the version of the driver of the GPU that draws; null where not obtained (never another GPU's, never the API's level)
        public string XrDevice;         // the headset's own name; null where only a generic name is to be had (nothing is guessed from the runtime)
        public string XrRuntime;        // the XR runtime's own name (OpenXRRuntime.name); "none" without XR; null where XR runs and the runtime gave none
        public string XrRuntimeVersion; // its version; null where not obtained
        public string TimeSource = "";  // what gave the frame times, e.g. "FrameTimingManager+XRDisplaySubsystem"

        /// <summary>The epoch that frames go to from now: the one of these conditions, new if there is room, none otherwise.</summary>
        public void BeginEpoch(in PlayConditions conditions)
        {
            if (_current >= 0 && _epochs[_current].Conditions.Equals(conditions))
            {
                return;
            }

            for (int i = 0; i < _epochCount; i++)
            {
                if (_epochs[i].Conditions.Equals(conditions))
                {
                    _current = i;
                    ChangeCount++;
                    return;
                }
            }

            if (_epochCount < MaxEpochs)
            {
                _epochs[_epochCount].Clear(in conditions);
                _current = _epochCount;
                _epochCount++;
            }
            else
            {
                EpochChangesDropped++;
                _current = -1;
            }

            ChangeCount++;
        }

        /// <summary>
        /// One frame of ordinary play: its real interval, the times the sources gave, and whether the cut structure
        /// changed in it. <paramref name="timesUnattributed"/>: the caller set the frame's times aside (they could belong
        /// to frames before a boundary), so they are missing here and counted as unattributed too.
        /// </summary>
        public void NoteFrame(float intervalMs, in FrameTimeSample sample, bool structureChanged, bool timesUnattributed = false)
        {
            ChangeCount++;
            if (_current < 0)
            {
                FramesOutsideEpochs++;
                return;
            }

            PlayEpoch e = _epochs[_current];
            e.Frames++;
            if (timesUnattributed) e.Unattributed++;
            if (structureChanged) e.StructureChangedFrames++;
            if (e.Interval.Add(intervalMs)) e.PlaySeconds += intervalMs * 0.001;
            if (!sample.HasMain || !e.Main.Add(sample.MainMs)) e.MainMissing++;
            if (!sample.HasRender || !e.Render.Add(sample.RenderMs)) e.RenderMissing++;
            if (!sample.HasGpu || !e.Gpu.Add(sample.GpuMs)) e.GpuMissing++;
            if (!sample.HasGpuXr || !e.GpuXr.Add(sample.GpuXrMs)) e.GpuXrMissing++;
        }

        /// <summary>The live Logical Fragments now: the most is kept.</summary>
        public void NoteLiveFragments(int live)
        {
            if (live > MaxLiveFragments)
            {
                MaxLiveFragments = live;
                ChangeCount++;
            }
        }

        /// <summary>A cumulative total, counted from the agreement by the collector: kept where it rose.</summary>
        public void NoteTotal(PlayTotalKind kind, long total)
        {
            if (total > _totals[(int)kind]) { _totals[(int)kind] = total; ChangeCount++; }
        }

        public void NoteTotals(long acceptedCuts, long collectedFragments)
        {
            NoteTotal(PlayTotalKind.AcceptedCuts, acceptedCuts);
            NoteTotal(PlayTotalKind.CollectedFragments, collectedFragments);
        }

        public long Total(PlayTotalKind kind) => _totals[(int)kind];

        /// <summary>The UI settings now: kept as the latest; a change counts.</summary>
        public void NoteSettings(in PlayUiSettings settings)
        {
            if (!settings.Equals(Settings)) { Settings = settings; ChangeCount++; }
        }

        /// <summary>
        /// A buffer's end in use, its capacity now and its fixed limit (0 for none): the highest end, the latest capacity
        /// and limit, and the least of each headroom are kept; growth and shortfall totals as given.
        /// </summary>
        public void NoteCapacity(PlayCapacityKind kind, long end, long capacity, long limit, long growths, long shortfalls)
        {
            ref PlayCapacity c = ref _capacities[(int)kind];
            bool changed = false;
            long toCapacity = capacity - end, toLimit = limit - end;
            if (!c.Read) { c.Read = true; c.MinHeadroomToCapacity = toCapacity; c.MinHeadroomToLimit = toLimit; changed = true; }
            else
            {
                if (toCapacity < c.MinHeadroomToCapacity) { c.MinHeadroomToCapacity = toCapacity; changed = true; }
                if (toLimit < c.MinHeadroomToLimit) { c.MinHeadroomToLimit = toLimit; changed = true; }
            }

            if (end > c.MaxEnd) { c.MaxEnd = end; changed = true; }
            if (capacity != c.Capacity) { c.Capacity = capacity; changed = true; }
            if (limit != c.Limit) { c.Limit = limit; changed = true; }
            if (growths > c.Growths) { c.Growths = growths; changed = true; }
            if (shortfalls > c.Shortfalls) { c.Shortfalls = shortfalls; changed = true; }
            if (changed) ChangeCount++;
        }

        public void NoteCapacity(PlayCapacityKind kind, long end, long capacity, long growths, long shortfalls) => NoteCapacity(kind, end, capacity, 0, growths, shortfalls);

        public PlayCapacity Capacity(PlayCapacityKind kind) => _capacities[(int)kind];

        /// <summary>A failure's total as the game counts it: kept where it rose.</summary>
        public void NoteFailures(PlayFailureKind kind, long total)
        {
            if (total > _failures[(int)kind])
            {
                _failures[(int)kind] = total;
                ChangeCount++;
            }
        }

        public long Failures(PlayFailureKind kind) => _failures[(int)kind];

        public static string CapacityName(PlayCapacityKind kind)
        {
            switch (kind)
            {
                case PlayCapacityKind.CpuVertices: return "cpuVertices";
                case PlayCapacityKind.CpuIndices: return "cpuIndices";
                case PlayCapacityKind.DrawCommandSlots: return "drawCommandSlots";
                case PlayCapacityKind.GpuVertices: return "gpuVertices";
                case PlayCapacityKind.GpuIndices: return "gpuIndices";
                case PlayCapacityKind.DrawInstanceSlots: return "drawInstanceSlots";
                case PlayCapacityKind.RenderFragments: return "renderFragments";
                default: return "?";
            }
        }

        /// <summary>The unit of a capacity's figures.</summary>
        public static string CapacityUnit(PlayCapacityKind kind)
        {
            switch (kind)
            {
                case PlayCapacityKind.CpuVertices: return "vertices";
                case PlayCapacityKind.CpuIndices: return "indices";
                case PlayCapacityKind.DrawCommandSlots: return "slots";
                case PlayCapacityKind.GpuVertices: return "vertices";
                case PlayCapacityKind.GpuIndices: return "indices";
                case PlayCapacityKind.DrawInstanceSlots: return "records";
                case PlayCapacityKind.RenderFragments: return "fragments";
                default: return "?";
            }
        }

        public static string FailureName(PlayFailureKind kind)
        {
            switch (kind)
            {
                case PlayFailureKind.DrawRefused: return "drawRefused";
                case PlayFailureKind.RingFull: return "ringFull";
                case PlayFailureKind.ShadowMapFormatRefused: return "shadowMapFormatRefused";
                case PlayFailureKind.IndirectCountZeroRefused: return "indirectCountZeroRefused";
                case PlayFailureKind.CompactionSlotsShort: return "compactionSlotsShort";
                case PlayFailureKind.StorageRoomRefused: return "storageRoomRefused";
                case PlayFailureKind.CutAborted: return "cutAborted";
                case PlayFailureKind.CutFailed: return "cutFailed";
                case PlayFailureKind.CutKernelFailed: return "cutKernelFailed";
                case PlayFailureKind.TerminationRequested: return "terminationRequested";
                default: return "?";
            }
        }

        /// <summary>Whether anything of play was counted: a snapshot of nothing is not sent.</summary>
        public bool HasPlay
        {
            get
            {
                for (int i = 0; i < _epochCount; i++)
                {
                    if (_epochs[i].Frames > 0) return true;
                }

                return FramesOutsideEpochs > 0 || AcceptedCuts > 0;
            }
        }

        /// <summary>Everything back to nothing counted (a withdrawal of consent): the identity strings stay.</summary>
        public void Clear()
        {
            _epochCount = 0;
            _current = -1;
            EpochChangesDropped = 0;
            FramesOutsideEpochs = 0;
            MaxLiveFragments = 0;
            Array.Clear(_totals, 0, _totals.Length);
            Settings = default;
            Array.Clear(_capacities, 0, _capacities.Length);
            Array.Clear(_failures, 0, _failures.Length);
            ChangeCount++;
        }
    }
}
