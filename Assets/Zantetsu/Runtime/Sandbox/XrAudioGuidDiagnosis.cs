using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A diagnosis, not a product behaviour (TL, 2026-10-04): what the OpenXR runtime is asked through
    /// <c>xrGetAudioOutputDeviceGuidOculus</c> and <c>xrGetAudioInputDeviceGuidOculus</c>, how often, how long each call
    /// takes and what it answers. The interception itself lives in the OpenXR feature that feeds this class; this
    /// class only counts, and holds the rules of the second mode.
    /// <para>
    /// <b>Observe</b>: every call is passed on and recorded. Nothing is changed.
    /// </para>
    /// <para>
    /// <b>Reuse</b>: a successful answer is kept, for the instance it was given for and for its direction (output and
    /// input apart), and given again to later calls of that instance and direction without asking the runtime. A failed
    /// answer is never kept. Everything kept is dropped when the instance is destroyed and when an instance is created:
    /// nothing is carried from one instance to the next. Reuse is honoured only when <see cref="ReuseAllowed"/> was set
    /// (the feature sets it for the Simulator's runtime alone); otherwise the mode behaves as Observe.
    /// </para>
    /// On a device the audio route can change while the application runs, so a kept answer may be stale there: this is
    /// why Reuse is Simulator-only and off unless asked for.
    /// <para>
    /// <b>Standing (TL, 2026-10-04):</b> Reuse is the adopted configuration of performance measurements in the Simulator:
    /// still off by default and Simulator-only, switched on explicitly by <see cref="ReuseArgument"/> at each
    /// measurement. Measurements up to o5 were made without it and those from o6 on with it; whole-frame values of the
    /// two are not one baseline. What it gains in the Simulator is not a device's performance.
    /// </para>
    /// </summary>
    public sealed class XrAudioGuidDiagnosis
    {
        public enum DiagnosisMode { Off = 0, Observe = 1, Reuse = 2 }

        public const int Output = 0, Input = 1;
        public const int GuidLength = 128;   // XR_MAX_AUDIO_DEVICE_STR_SIZE_OCULUS, in wide characters
        public const int Success = 0;        // XR_SUCCESS

        public const string ObserveArgument = "-zantetsuXrAudioGuidObserve";
        public const string ReuseArgument = "-zantetsuXrAudioGuidReuse";

        /// <summary>The mode asked for on the command line: none, observe, or reuse (reuse wins when both are given).</summary>
        public static DiagnosisMode ModeFromArguments(string[] arguments)
        {
            var mode = DiagnosisMode.Off;
            if (arguments == null) return mode;
            foreach (string a in arguments)
            {
                if (a == ObserveArgument && mode == DiagnosisMode.Off) mode = DiagnosisMode.Observe;
                if (a == ReuseArgument) mode = DiagnosisMode.Reuse;
            }

            return mode;
        }

        /// <summary>Whether a runtime of this name may have its answers given again: the Simulator's only.</summary>
        public static bool IsSimulatorRuntime(string runtimeName) =>
            !string.IsNullOrEmpty(runtimeName) && runtimeName.IndexOf("Simulator", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>The one the feature feeds and the check reads. Off until the feature turns it on.</summary>
        public static readonly XrAudioGuidDiagnosis Current = new XrAudioGuidDiagnosis();

        private sealed class Side
        {
            public long calls, forwarded, reused, failed, ticks, maxTicks, valueChanges;
            public int lastResult;
            public readonly Dictionary<int, long> results = new Dictionary<int, long>();
            public readonly char[] last = new char[GuidLength];
            public bool hasLast;
            public readonly char[] kept = new char[GuidLength];
            public bool hasKept;
            public ulong keptInstance;
        }

        private readonly Side[] _sides = { new Side(), new Side() };
        private int _firstThread = -1;

        public DiagnosisMode Mode { get; private set; }

        /// <summary>Whether a kept answer may be given again: set by the feature for the Simulator's runtime only.</summary>
        public bool ReuseAllowed { get; private set; }

        public string RuntimeName { get; private set; } = "";

        public ulong Instance { get; private set; }

        public long InstancesCreated { get; private set; }

        public long InstancesDestroyed { get; private set; }

        /// <summary>How many times each function's address was asked of the runtime through the hook.</summary>
        public long OutputLookups { get; private set; }

        public long InputLookups { get; private set; }

        public long CallsFromAnotherThread { get; private set; }

        public bool HookInstalled { get; private set; }

        public void Begin(DiagnosisMode mode)
        {
            Mode = mode;
            HookInstalled = mode != DiagnosisMode.Off;
        }

        public void NoteLookup(int side)
        {
            if (side == Output) OutputLookups++; else InputLookups++;
        }

        /// <summary>An instance exists from now on. Whatever was kept is dropped: nothing is carried into it.</summary>
        public void OnInstanceCreate(ulong instance, string runtimeName, bool reuseAllowed)
        {
            DropKept();
            Instance = instance;
            RuntimeName = runtimeName ?? "";
            ReuseAllowed = reuseAllowed;
            InstancesCreated++;
        }

        /// <summary>The instance is going. Whatever was kept for it is dropped.</summary>
        public void OnInstanceDestroy(ulong instance)
        {
            DropKept();
            if (Instance == instance) Instance = 0;
            InstancesDestroyed++;
        }

        private void DropKept()
        {
            foreach (Side s in _sides)
            {
                s.hasKept = false;
                s.keptInstance = 0;
            }
        }

        /// <summary>
        /// Before a call is passed on: in Reuse (and allowed), a kept successful answer of this instance and direction is
        /// copied into <paramref name="buffer"/> and true is returned -- the runtime is then not asked.
        /// </summary>
        public bool TryReuse(int side, ulong instance, Span<char> buffer)
        {
            Side s = _sides[side];
            NoteThread();
            if (Mode != DiagnosisMode.Reuse || !ReuseAllowed || !s.hasKept || s.keptInstance != instance || instance == 0 || buffer.Length < GuidLength)
            {
                return false;
            }

            s.kept.AsSpan().CopyTo(buffer);
            s.calls++;
            s.reused++;
            return true;
        }

        /// <summary>After a call was passed on: its result, its duration (Stopwatch ticks) and the answer in the buffer.</summary>
        public void Record(int side, ulong instance, int result, long ticks, ReadOnlySpan<char> buffer)
        {
            Side s = _sides[side];
            s.calls++;
            s.forwarded++;
            s.ticks += ticks;
            if (ticks > s.maxTicks) s.maxTicks = ticks;
            s.lastResult = result;
            s.results.TryGetValue(result, out long n);
            s.results[result] = n + 1;
            if (result != Success)
            {
                s.failed++;
                return;   // a failed answer is neither compared nor kept
            }

            if (buffer.Length < GuidLength) return;
            ReadOnlySpan<char> value = buffer.Slice(0, GuidLength);
            if (s.hasLast && !value.SequenceEqual(s.last)) s.valueChanges++;
            value.CopyTo(s.last);
            s.hasLast = true;
            if (Mode == DiagnosisMode.Reuse && ReuseAllowed && instance != 0 && instance == Instance)
            {
                value.CopyTo(s.kept);
                s.hasKept = true;
                s.keptInstance = instance;
            }
        }

        private void NoteThread()
        {
            int id = Environment.CurrentManagedThreadId;
            if (_firstThread < 0) _firstThread = id;
            else if (_firstThread != id) CallsFromAnotherThread++;
        }

        /// <summary>For the feature: the thread is noted on the forwarding path too.</summary>
        public void NoteForwardingThread() => NoteThread();

        public long Calls(int side) => _sides[side].calls;

        public long Forwarded(int side) => _sides[side].forwarded;

        public long Reused(int side) => _sides[side].reused;

        public long Failed(int side) => _sides[side].failed;

        public long ValueChanges(int side) => _sides[side].valueChanges;

        public bool HasKept(int side) => _sides[side].hasKept;

        public string LastValue(int side)
        {
            Side s = _sides[side];
            if (!s.hasLast) return "";
            int end = Array.IndexOf(s.last, '\0');
            return new string(s.last, 0, end < 0 ? GuidLength : end);
        }

        /// <summary>The reuse mode's counts alone: for each direction, the calls passed on to the runtime and the calls answered from the kept value.</summary>
        public string DescribeReuseCounts() =>
            "output: real calls " + _sides[Output].forwarded + " / cache hits " + _sides[Output].reused
            + "; input: real calls " + _sides[Input].forwarded + " / cache hits " + _sides[Input].reused;

        public string Describe()
        {
            if (Mode == DiagnosisMode.Off) return "off (not asked for)";
            var b = new StringBuilder();
            b.Append("mode ").Append(Mode).Append(Mode == DiagnosisMode.Reuse && !ReuseAllowed ? " (not allowed for this runtime: observing only)" : "")
                .Append("; runtime '").Append(RuntimeName).Append("'; instances created ").Append(InstancesCreated).Append(", destroyed ").Append(InstancesDestroyed)
                .Append("; address lookups output ").Append(OutputLookups).Append(", input ").Append(InputLookups)
                .Append("; first calling thread (managed id) ").Append(_firstThread).Append(", calls from another thread than the first ").Append(CallsFromAnotherThread);
            for (int side = 0; side < 2; side++)
            {
                Side s = _sides[side];
                double ms = s.ticks * 1000.0 / Stopwatch.Frequency;
                b.Append("; ").Append(side == Output ? "output" : "input").Append(": calls ").Append(s.calls).Append(", passed on ").Append(s.forwarded)
                    .Append(", answered from the kept value ").Append(s.reused).Append(", failed ").Append(s.failed)
                    .Append(", time passed on ").Append(ms.ToString("F1", CultureInfo.InvariantCulture)).Append(" ms (mean ")
                    .Append((s.forwarded > 0 ? ms / s.forwarded : 0.0).ToString("F4", CultureInfo.InvariantCulture)).Append(", max ")
                    .Append((s.maxTicks * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture)).Append(" ms)")
                    .Append(", results [");
                bool first = true;
                foreach (KeyValuePair<int, long> r in s.results)
                {
                    b.Append(first ? "" : ", ").Append(r.Key).Append(": ").Append(r.Value);
                    first = false;
                }

                b.Append("], value changes ").Append(s.valueChanges).Append(", last value '").Append(LastValue(side)).Append('\'');
            }

            return b.ToString();
        }
    }
}
