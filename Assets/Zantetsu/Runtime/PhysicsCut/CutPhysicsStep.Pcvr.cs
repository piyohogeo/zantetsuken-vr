using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace Zantetsu.PhysicsCut
{
    /// <summary>How the shared Main budget's common margin is taken (DESIGN 4.4).</summary>
    public enum MainMarginKind
    {
        /// <summary>9 % of the target frame period (2026-10-20): 90 Hz 1.00 ms, 72 Hz 1.25 ms, 45 Hz 2.00 ms.</summary>
        PeriodFraction = 0,

        /// <summary>The fixed 2 ms of 2026-10-19 (D-251), for comparison: <c>-zantetsuMainMargin fixed2ms</c>.</summary>
        Fixed2Ms = 1,
    }

    /// <summary>Where a frame's target period came from.</summary>
    public enum ApplicationFrameSourceKind
    {
        /// <summary>None usable: one 90 Hz frame.</summary>
        Default = 0,

        /// <summary>The host's source (a Quest under frame synthesis).</summary>
        Host = 1,

        /// <summary>The PCVR display's rate, settled once at the session's start.</summary>
        PcvrDisplay = 2,

        /// <summary>Non-VR Android's fixed 60 Hz render target, independent of XR and measured frame times.</summary>
        AndroidFlat = 3,
    }

    /// <summary>The physics step's mode in effect for the session.</summary>
    public enum PhysicsStepMode
    {
        /// <summary>The fixed step of before: 45 or 90 Hz, one normal step at a time.</summary>
        Fixed = 0,

        /// <summary>PCVR: the display's step (72 or 90 Hz), and one double step when two are owed.</summary>
        Display = 1,

        /// <summary>Non-VR Android: 60 Hz with a single 1/30 s simulation when two normal steps are owed.</summary>
        AndroidFlat = 2,
    }

    // The PCVR display step and its target period (DESIGN 4.4, 2026-10-20). Quest (Android) is not concerned: its step,
    // frequency and source are as they were.
    //
    // The source: the running XRDisplaySubsystem's TryGetDisplayRefreshRate -- the OpenXR runtime's display rate (with
    // XR_FB_display_refresh_rate, the display's; otherwise Unity's reading of the runtime's predicted display period).
    // When it is read: at each frame's start (BeginFrame, after the frame's start is taken, so the query's time is the
    // frame's), from the session's first frame until it is settled, and settled once: a rate within PcvrToleranceHz of 72
    // or 90 is that rate; any other rate is out of scope, said so, and the provisional 90 Hz is taken; no rate within
    // PcvrSettleSeconds of the first frame is "not obtained", said so, and the provisional 90 Hz is taken. The session is
    // PCVR when a running XR display is seen (or -zantetsuPhysicsStep display); an XR display created and not yet started
    // is waited for (XR not ready is not a desktop); no XR display at all at the first frame is a desktop session at once,
    // and one that never starts within the wait is taken as desktop, said so (TL review, 2026-10-20). After it is settled
    // the rate is read again once a second and a change is only said (at most PcvrChangeLogs times): the step and the
    // period stay as settled until the next session -- a reprojected application (45 or 36 fps) changes neither; an XR
    // display that starts after a desktop settling is said once and changes nothing either.
    public static partial class CutPhysicsStep
    {
        /// <summary>The start-up argument choosing the physics step's mode: <c>fixed</c> (the 45 Hz step of before) or <c>display</c> (PCVR's display step).</summary>
        public const string StepModeArgument = "-zantetsuPhysicsStep";

        /// <summary>The start-up argument choosing the margin: <c>9percent</c> (the default) or <c>fixed2ms</c>.</summary>
        public const string MarginArgument = "-zantetsuMainMargin";

        /// <summary>How far a reported rate may be from 72 or 90 Hz and still be that rate, in Hz.</summary>
        public const float PcvrToleranceHz = 0.5f;

        /// <summary>How long after the first frame a PCVR session waits for its display's rate before taking the provisional rate, in seconds. No step is taken meanwhile.</summary>
        public const double PcvrSettleSeconds = 3.0;

        /// <summary>The rate taken when the display's is not obtained or out of scope: 90 Hz, recorded as provisional.</summary>
        public const int PcvrProvisionalHz = 90;

        /// <summary>How many times a change of the display's rate after it was settled is said in the log, at most.</summary>
        public const int PcvrChangeLogs = 8;

        internal enum PhysicsStepRequest
        {
            Auto = 0,
            Fixed = 1,
            Display = 2,
        }

        internal enum PcvrRateState
        {
            /// <summary>Not a PC session (Quest), or before a session.</summary>
            None = 0,

            /// <summary>The first frame not reached yet, or waiting for the rate.</summary>
            Pending = 1,

            /// <summary>72 or 90 Hz read from the display.</summary>
            Obtained = 2,

            /// <summary>No rate within the wait: the provisional 90 Hz.</summary>
            NotObtained = 3,

            /// <summary>A rate other than 72 or 90 Hz: the provisional 90 Hz, said so.</summary>
            OutOfScope = 4,

            /// <summary>No XR display at all at the first frame, not even one not yet started (a desktop session): no PCVR rate.</summary>
            NoXr = 5,

            /// <summary>An XR display existed but did not start within the wait: taken as a desktop session, said so.</summary>
            XrNotStarted = 6,
        }

        private static PhysicsStepRequest s_stepRequest;
        private static PhysicsStepMode s_stepMode;
        private static string s_stepChoice = "not chosen";
        private static PcvrRateState s_pcvrState;
        private static int s_pcvrHz;
        private static float s_pcvrReportedHz = float.NaN;
        private static long s_pcvrFirstAt;
        private static int s_pcvrReads;
        private static int s_pcvrSettledFrame = -1;
        private static double s_pcvrWaitedSeconds;
        private static long s_pcvrNextWatch;
        private static float s_pcvrLastSeenHz = float.NaN;
        private static int s_pcvrChangesSeen;
        private static XRDisplaySubsystem s_pcvrDisplay;
        private static readonly List<XRDisplaySubsystem> s_pcvrDisplays = new List<XRDisplaySubsystem>(1);

        /// <summary>The physics step's mode in effect (Display only on PCVR, once settled).</summary>
        public static PhysicsStepMode StepMode => s_stepMode;

        /// <summary>Whether the step is settled: false only while a PCVR display step waits for its rate at the session's start (no step meanwhile).</summary>
        public static bool StepSettled => s_stepSettled;

        /// <summary>The PCVR target rate taken, in Hz: 72 or 90 when obtained, 90 provisional otherwise; 0 when no PCVR rate (Quest, desktop, not yet settled).</summary>
        public static int PcvrTargetHz => s_pcvrHz;

        /// <summary>
        /// The 72 or 90 Hz a reported rate stands for (within <see cref="PcvrToleranceHz"/>), or 0 for any other rate --
        /// never rounded to the nearer one (pure).
        /// </summary>
        internal static int SupportedPcvrRate(float reportedHz)
        {
            if (float.IsNaN(reportedHz) || float.IsInfinity(reportedHz) || reportedHz <= 0f) return 0;
            if (Math.Abs(reportedHz - 72f) <= PcvrToleranceHz) return 72;
            if (Math.Abs(reportedHz - 90f) <= PcvrToleranceHz) return 90;
            return 0;
        }

        /// <summary>
        /// One reading of the PCVR display at the session's start (pure). <paramref name="xr"/>: whether the session is
        /// known to be PCVR (the display mode asked for, or a running XR display seen); <paramref name="displayRunning"/>
        /// and <paramref name="displayPresent"/>: a running XR display now, and any XR display at all (one created and not
        /// started yet is XR not ready, not a desktop); what the running display answered; how long since the first frame.
        /// <list type="bullet">
        /// <item>a running display's answer: 72 or 90 within the tolerance, else out of scope (the provisional 90);</item>
        /// <item>no XR display at all and not known to be PCVR: a desktop session, at once (no wait, no hold);</item>
        /// <item>past the wait: PCVR known -- not obtained (the provisional 90); else an XR display that never started --
        /// taken as a desktop session, said so;</item>
        /// <item>otherwise pending (no step yet).</item>
        /// </list>
        /// The rate taken is in <paramref name="hz"/> (0 for a desktop session).
        /// </summary>
        internal static PcvrRateState ClassifyPcvr(bool xr, bool displayRunning, bool displayPresent, bool rateKnown, float reportedHz, double waitedSeconds, out int hz)
        {
            hz = 0;
            if (displayRunning && rateKnown && reportedHz > 0f && !float.IsInfinity(reportedHz))
            {
                hz = SupportedPcvrRate(reportedHz);
                if (hz != 0) return PcvrRateState.Obtained;
                hz = PcvrProvisionalHz;
                return PcvrRateState.OutOfScope;
            }

            bool knownXr = xr || displayRunning;
            if (!knownXr && !displayPresent) return PcvrRateState.NoXr;
            if (waitedSeconds >= PcvrSettleSeconds)
            {
                if (!knownXr) return PcvrRateState.XrNotStarted;
                hz = PcvrProvisionalHz;
                return PcvrRateState.NotObtained;
            }

            return PcvrRateState.Pending;
        }

        /// <summary>
        /// The step's mode once the PCVR reading is settled (pure). The explicit frequency first
        /// (<see cref="FrequencyArgument"/>: the fixed step at it), then <see cref="StepModeArgument"/> (fixed, or display
        /// on PC), then automatically: the display step on PCVR, the fixed 45 Hz step on a desktop and on Quest. The
        /// display step's frequency is the PCVR rate taken (72 or 90). Non-VR Android uses its own 60/30 Hz mode.
        /// </summary>
        internal static PhysicsStepMode ModeFor(bool pc, int explicitHz, PhysicsStepRequest request, PcvrRateState pcvr)
        {
            if (!pc || explicitHz != 0 || request == PhysicsStepRequest.Fixed) return PhysicsStepMode.Fixed;
            if (request == PhysicsStepRequest.Display) return PhysicsStepMode.Display;
            return pcvr == PcvrRateState.NoXr || pcvr == PcvrRateState.XrNotStarted || pcvr == PcvrRateState.None ? PhysicsStepMode.Fixed : PhysicsStepMode.Display;
        }

        /// <summary>The margin chosen at start: <see cref="MarginArgument"/> followed by <c>9percent</c> or <c>fixed2ms</c>; anything else is said and the default kept.</summary>
        internal static MainMarginKind MarginFromCommandLine(string[] arguments)
        {
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (!string.Equals(arguments[i], MarginArgument, StringComparison.OrdinalIgnoreCase)) continue;
                string value = arguments[i + 1];
                if (string.Equals(value, "fixed2ms", StringComparison.OrdinalIgnoreCase)) return MainMarginKind.Fixed2Ms;
                if (!string.Equals(value, "9percent", StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogWarning(MarginArgument + " takes 9percent or fixed2ms; '" + value + "' is not one, so 9percent is used.");
                }

                break;
            }

            return MainMarginKind.PeriodFraction;
        }

        /// <summary>The step's mode asked for at start: <see cref="StepModeArgument"/> followed by <c>fixed</c> or <c>display</c>; none (or anything else, said) is automatic.</summary>
        internal static PhysicsStepRequest StepRequestFromCommandLine(string[] arguments)
        {
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (!string.Equals(arguments[i], StepModeArgument, StringComparison.OrdinalIgnoreCase)) continue;
                string value = arguments[i + 1];
                if (string.Equals(value, "fixed", StringComparison.OrdinalIgnoreCase)) return PhysicsStepRequest.Fixed;
                if (string.Equals(value, "display", StringComparison.OrdinalIgnoreCase)) return PhysicsStepRequest.Display;
                UnityEngine.Debug.LogWarning(StepModeArgument + " takes fixed or display; '" + value + "' is not one, so the mode is chosen automatically.");
                break;
            }

            return PhysicsStepRequest.Auto;
        }

        // At the session's reset: the fixed step is settled now when it is the one (Quest, an explicit frequency, the fixed
        // mode asked for), or non-VR Android's 60/30 Hz step; otherwise the PCVR reading at the first frame decides.
        internal static void ChooseStepAtSessionStart(bool pc, int explicitHz, PhysicsStepRequest request, bool androidFlat = false)
        {
            if (!pc && !androidFlat && request == PhysicsStepRequest.Display)
            {
                UnityEngine.Debug.LogWarning(StepModeArgument + " display is for PCVR; Quest keeps its fixed step.");
                request = PhysicsStepRequest.Fixed;
            }

            if (!androidFlat && explicitHz != 0 && request == PhysicsStepRequest.Display)
            {
                UnityEngine.Debug.LogWarning(FrequencyArgument + " " + explicitHz + " is given: it comes first, and " + StepModeArgument + " display is not taken.");
            }

            s_stepRequest = request;
            s_pcvrState = pc && !androidFlat ? PcvrRateState.Pending : PcvrRateState.None;
            s_pcvrHz = 0;
            s_pcvrReportedHz = float.NaN;
            s_pcvrFirstAt = 0;
            s_pcvrReads = 0;
            s_pcvrSettledFrame = -1;
            s_pcvrWaitedSeconds = 0.0;
            s_pcvrNextWatch = 0;
            s_pcvrLastSeenHz = float.NaN;
            s_pcvrChangesSeen = 0;
            s_pcvrDisplay = null;
            s_pcvrSawRunning = false;
            s_pcvrWaitedForStart = false;
            XrStartedAfterSettling = false;
            s_pcvrSettledUnsaid = false;
            s_explicitHz = explicitHz;
            s_stepMode = androidFlat ? PhysicsStepMode.AndroidFlat : PhysicsStepMode.Fixed;
            s_clock = new ManualPhysicsClock(androidFlat ? AndroidFlatFrequencyHz : explicitHz != 0 ? explicitHz : DefaultFrequencyHz, Stopwatch.Frequency, doubleStep: androidFlat);
            bool fixedNow = androidFlat || !pc || explicitHz != 0 || request == PhysicsStepRequest.Fixed;
            s_stepSettled = fixedNow;
            s_stepChoice = androidFlat ? "Android non-VR target: 60 Hz normal, 30 Hz catch-up"
                : !pc ? "fixed (Quest: unchanged)"
                : explicitHz != 0 ? "fixed (" + FrequencyArgument + " " + explicitHz + ")"
                : request == PhysicsStepRequest.Fixed ? "fixed (" + StepModeArgument + " fixed)"
                : request == PhysicsStepRequest.Display ? "display (" + StepModeArgument + " display)"
                : "automatic";
        }

        private static int s_explicitHz;

        /// <summary>
        /// Tests only: <paramref name="clock"/> as this session's clock, settled, with <paramref name="mode"/> -- a PCVR
        /// display step's clock in an Editor without XR -- and the pause as it stands; the step counts and durations
        /// summed start again with it (as they would with a session's clock). Returns the clock it replaced, to be put
        /// back the same way.
        /// </summary>
        internal static ManualPhysicsClock UseClockForTest(ManualPhysicsClock clock, PhysicsStepMode mode)
        {
            ManualPhysicsClock before = s_clock;
            s_clock = clock;
            s_stepMode = mode;
            s_stepSettled = true;
            NormalSimulateSeconds = DoubleSimulateSeconds = MaxNormalSimulateSeconds = MaxDoubleSimulateSeconds = 0.0;
            BudgetShortDecisions = DoubleFellBackToNormal = 0;
            DoubleEstimateRefreshes = 0;
            s_doubleReevaluateAsked = false;
            s_doubleFellBackInARow = 0;
            ApplyPause();
            return before;
        }

        // At a frame's start (and at a decision whose frame's start was missed): the PCVR reading while pending, then the
        // once-a-second watch. Nothing outside Play, nothing on Quest.
        private static void ReadPcvrDisplay(long now)
        {
            if (!Application.isPlaying || s_pcvrState == PcvrRateState.None)
            {
                return;
            }

            if (s_pcvrState != PcvrRateState.Pending)
            {
                WatchPcvrDisplay(now);
                return;
            }

            if (s_pcvrReads == 0) s_pcvrFirstAt = now;
            s_pcvrReads++;
            XRDisplaySubsystem display = RunningDisplay();
            if (display != null) s_pcvrSawRunning = true;
            // An XR display created and not started yet is XR not ready (waited for), not a desktop session.
            bool present = display != null || AnyDisplayPresent();
            if (present && display == null) s_pcvrWaitedForStart = true;
            bool xr = s_stepRequest == PhysicsStepRequest.Display || s_pcvrSawRunning;
            bool rateKnown = false;
            float reported = float.NaN;
            if (display != null) rateKnown = display.TryGetDisplayRefreshRate(out reported);
            double waited = (double)(now - s_pcvrFirstAt) / Stopwatch.Frequency;
            PcvrRateState state = ClassifyPcvr(xr, display != null, present, rateKnown, reported, waited, out int hz);
            if (state == PcvrRateState.Pending)
            {
                return;
            }

            s_pcvrState = state;
            s_pcvrHz = hz;
            s_pcvrReportedHz = rateKnown ? reported : float.NaN;
            s_pcvrSettledFrame = Time.frameCount;
            s_pcvrWaitedSeconds = waited;
            s_pcvrLastSeenHz = s_pcvrReportedHz;
            s_pcvrNextWatch = now + Stopwatch.Frequency;
            if (!s_stepSettled)
            {
                s_stepMode = ModeFor(true, s_explicitHz, s_stepRequest, state);
                if (s_stepMode == PhysicsStepMode.Display)
                {
                    // Nothing was owed or stepped before: the clock of the settled step starts here, with the pause as it stands.
                    s_clock = new ManualPhysicsClock(hz, Stopwatch.Frequency, doubleStep: true);
                    ApplyPause();
                }

                s_stepSettled = true;
            }

            s_pcvrSettledUnsaid = true;   // said once the frame's period is settled with it (SayPcvrSettled)
        }

        private static bool s_pcvrSawRunning;
        private static bool s_pcvrWaitedForStart;
        private static bool s_pcvrSettledUnsaid;

        /// <summary>Whether an XR display started after the session was settled as a desktop one (said once; the step stays fixed until the next session).</summary>
        public static bool XrStartedAfterSettling { get; private set; }

        // The settled step and budget, said once, with the period of the frame it was settled in.
        private static void SayPcvrSettled()
        {
            if (!s_pcvrSettledUnsaid) return;
            s_pcvrSettledUnsaid = false;
            string line = "CUT PHYSICS STEP: " + DescribeStepSettings();
            if (s_pcvrState == PcvrRateState.NotObtained || s_pcvrState == PcvrRateState.OutOfScope || s_pcvrState == PcvrRateState.XrNotStarted) UnityEngine.Debug.LogWarning(line);
            else UnityEngine.Debug.Log(line);
        }

        private static void WatchPcvrDisplay(long now)
        {
            if (now < s_pcvrNextWatch)
            {
                return;
            }

            s_pcvrNextWatch = now + Stopwatch.Frequency;
            XRDisplaySubsystem display = RunningDisplay();
            if (s_pcvrState == PcvrRateState.NoXr || s_pcvrState == PcvrRateState.XrNotStarted)
            {
                // Settled as a desktop session: an XR display starting later is said, once, and changes nothing.
                if (display != null && !XrStartedAfterSettling)
                {
                    XrStartedAfterSettling = true;
                    UnityEngine.Debug.LogWarning("CUT PHYSICS STEP: an XR display is running now, after the session was settled as a desktop one ("
                        + DescribeRate() + "); the fixed step and the default period stay until the next session");
                }

                return;
            }

            if (display == null || !display.TryGetDisplayRefreshRate(out float reported) || !(reported > 0f))
            {
                return;
            }

            bool same = !float.IsNaN(s_pcvrLastSeenHz) && Math.Abs(reported - s_pcvrLastSeenHz) <= PcvrToleranceHz;
            s_pcvrLastSeenHz = reported;
            if (same || SupportedPcvrRate(reported) == s_pcvrHz && s_pcvrState == PcvrRateState.Obtained)
            {
                return;
            }

            s_pcvrChangesSeen++;
            if (s_pcvrChangesSeen <= PcvrChangeLogs)
            {
                UnityEngine.Debug.LogWarning("CUT PHYSICS STEP: the display now reports " + reported.ToString("R", CultureInfo.InvariantCulture)
                    + " Hz (settled at the start: " + DescribeRate() + "); the step and the period stay as settled until the next session"
                    + (s_pcvrChangesSeen == PcvrChangeLogs ? " (said no more this session)" : ""));
            }
        }

        private static XRDisplaySubsystem RunningDisplay()
        {
            if (s_pcvrDisplay != null && s_pcvrDisplay.running) return s_pcvrDisplay;
            s_pcvrDisplay = null;
            SubsystemManager.GetSubsystems(s_pcvrDisplays);
            for (int i = 0; i < s_pcvrDisplays.Count; i++)
            {
                if (s_pcvrDisplays[i].running)
                {
                    s_pcvrDisplay = s_pcvrDisplays[i];
                    break;
                }
            }

            return s_pcvrDisplay;
        }

        // Any XR display at all, started or not (read only while the session's reading is pending).
        private static bool AnyDisplayPresent()
        {
            SubsystemManager.GetSubsystems(s_pcvrDisplays);
            return s_pcvrDisplays.Count > 0;
        }

        private static string DescribeRate()
        {
            if (s_stepMode == PhysicsStepMode.AndroidFlat) return "Android non-VR target 60 Hz";
            CultureInfo c = CultureInfo.InvariantCulture;
            string reported = float.IsNaN(s_pcvrReportedHz) ? "none" : s_pcvrReportedHz.ToString("R", c) + " Hz";
            switch (s_pcvrState)
            {
                case PcvrRateState.None: return "not PC (Quest: its own source)";
                case PcvrRateState.Pending: return "PCVR rate pending (no step yet)";
                case PcvrRateState.NoXr: return "no XR display at the first frame, started or not (desktop): no PCVR rate";
                case PcvrRateState.XrNotStarted:
                    return "XR NOT STARTED: an XR display existed but did not run within " + PcvrSettleSeconds.ToString("R", c) + " s; taken as desktop (settled at frame " + s_pcvrSettledFrame + ")";
                case PcvrRateState.Obtained:
                    return s_pcvrHz + " Hz from the XR display (reported " + reported + ", settled at frame " + s_pcvrSettledFrame + " after " + s_pcvrWaitedSeconds.ToString("F2", c) + " s"
                        + (s_pcvrWaitedForStart ? ", after waiting for the display to start" : "") + ")";
                case PcvrRateState.OutOfScope:
                    return "OUT OF SCOPE: the XR display reported " + reported + ", not 72 or 90 Hz (within " + PcvrToleranceHz.ToString("R", c) + "); provisional " + s_pcvrHz + " Hz (settled at frame " + s_pcvrSettledFrame + ")";
                default:
                    return "NOT OBTAINED: no rate from the XR display in " + PcvrSettleSeconds.ToString("R", c) + " s; provisional " + s_pcvrHz + " Hz (settled at frame " + s_pcvrSettledFrame + ")";
            }
        }

        /// <summary>
        /// The session's step and budget in one line, for records: the mode and why, the normal step, whether the
        /// double step is allowed, the PCVR target rate and its source, the margin and the budget of the period in effect.
        /// </summary>
        public static string DescribeStepSettings()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            var b = new StringBuilder(320);
            b.Append("mode ").Append(s_stepSettled ? s_stepMode.ToString() : "not settled").Append(" (").Append(s_stepChoice).Append(')')
                .Append(", normal step 1/").Append(s_clock.FrequencyHz).Append(" s (").Append((s_clock.StepSeconds * 1000.0).ToString("F3", c)).Append(" ms)")
                .Append(s_clock.DoubleStep ? ", double step 1/" + (s_clock.FrequencyHz / 2.0).ToString("R", c) + " s allowed" : ", no double step")
                .Append("; target rate ").Append(DescribeRate())
                .Append("; period ").Append((s_frameSeconds * 1000.0).ToString("F2", c)).Append(" ms (").Append(ApplicationFrameSourceName).Append(')')
                .Append(", margin ").Append(s_marginKind == MainMarginKind.Fixed2Ms ? "fixed 2 ms" : "9 % of the period").Append(" = ").Append((CommonMarginSeconds * 1000.0).ToString("F2", c)).Append(" ms")
                .Append(", budget ").Append((MainBudgetSeconds * 1000.0).ToString("F2", c)).Append(" ms");
            return b.ToString();
        }

        /// <summary>
        /// The session's step counts in one line, for records: normal and double steps and their measured durations,
        /// the double steps that fell back to the normal one, the decisions the budget refused, the verification steps,
        /// what is owed now and what was dropped at the cap, the physics time and the step id.
        /// </summary>
        public static string DescribeStepCounts()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            ManualPhysicsClock k = s_clock;
            string Ms(double s) => (s * 1000.0).ToString("F3", c);
            var b = new StringBuilder(320);
            b.Append("normal steps ").Append(k.NormalSteps).Append(" (simulate ").Append(Ms(NormalSimulateSeconds)).Append(" ms, mean ")
                .Append(k.NormalSteps > 0 ? Ms(NormalSimulateSeconds / k.NormalSteps) : "-").Append(", longest ").Append(Ms(MaxNormalSimulateSeconds)).Append(')')
                .Append(", double steps ").Append(k.DoubleSteps).Append(" (simulate ").Append(Ms(DoubleSimulateSeconds)).Append(" ms, mean ")
                .Append(k.DoubleSteps > 0 ? Ms(DoubleSimulateSeconds / k.DoubleSteps) : "-").Append(", longest ").Append(Ms(MaxDoubleSimulateSeconds)).Append(')')
                .Append(", double fell back to normal ").Append(DoubleFellBackToNormal)
                .Append(", double estimate taken again from the normal ").Append(DoubleEstimateRefreshes)
                .Append(", budget-short decisions ").Append(BudgetShortDecisions)
                .Append(", verification steps ").Append(VerificationSteps)
                .Append("; owed ").Append(Ms(k.UnsimulatedSeconds)).Append(" ms, dropped ").Append(Ms(k.DroppedSeconds)).Append(" ms")
                .Append("; physics ").Append(k.PhysicsSeconds.ToString("F3", c)).Append(" s, step id ").Append(k.StepId)
                .Append("; expected normal ").Append(Ms(s_costs.ExpectedSeconds)).Append(" ms, double ").Append(Ms(ExpectedDoubleSimulateSeconds))
                .Append(s_doubleCosts.Count > 0 ? " ms (measured)" : " ms (twice the normal)")
                .Append("; display rate changes seen after the start ").Append(s_pcvrChangesSeen);
            return b.ToString();
        }
    }
}
