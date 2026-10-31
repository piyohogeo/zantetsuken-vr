using System;
using System.Globalization;
using System.Text;

namespace Zantetsu.Sandbox
{
    /// <summary>What the benchmark's individuals do with their pose while it measures.</summary>
    public enum NpcSkinBenchPose
    {
        /// <summary>The pose applied once at the activation; nothing applied after, no pose version change.</summary>
        Stop,
        /// <summary>Every individual's pose applied every frame, at the step's time.</summary>
        Update,
    }

    /// <summary>The main light's shadows during the benchmark.</summary>
    public enum NpcSkinBenchShadows
    {
        Off,
        /// <summary>One cascade: the Quest product's setting (SKIN-BENCH-2 §2).</summary>
        Cascade1,
        Cascade4,
    }

    /// <summary>
    /// The NPC skin benchmark's settings (DESIGN 3.4, npc-skin-bench): read from a PC Player's arguments, or given by the
    /// Quest's entry from its configuration. The skin route itself is the launch's existing choice
    /// (<see cref="Zantetsu.Rendering.VpSkinDrawSetup"/>: <c>-zantetsuSkinUnity</c> or <c>-zantetsuSkinDraw</c>), not a
    /// setting of this: each route is a launch of its own.
    /// </summary>
    public sealed class NpcSkinBenchSettings
    {
        /// <summary>The argument that starts the benchmark on a PC, with the directory its results are written to.</summary>
        public const string Argument = "-zantetsuNpcSkinBench";
        public const string CountArgument = "-zantetsuNpcSkinBenchCount";
        public const string PoseArgument = "-zantetsuNpcSkinBenchPose";
        public const string ShadowsArgument = "-zantetsuNpcSkinBenchShadows";
        public const string FamiliesArgument = "-zantetsuNpcSkinBenchFamilies";
        public const string WarmupArgument = "-zantetsuNpcSkinBenchWarmupSeconds";
        public const string MeasureArgument = "-zantetsuNpcSkinBenchMeasureSeconds";
        /// <summary>The measurement as a frame count (SKIN-BENCH-2 §2): the same frames and the same step range in every launch; 0 measures by seconds.</summary>
        public const string MeasureFramesArgument = "-zantetsuNpcSkinBenchMeasureFrames";
        /// <summary>The frames between the pose clock's resynchronisation and the measurement (the history settles), not measured.</summary>
        public const string SettleFramesArgument = "-zantetsuNpcSkinBenchSettleFrames";
        public const string StepArgument = "-zantetsuNpcSkinBenchStepSeconds";
        /// <summary>Diagnosis: the Profiler's binary log of the measurement, written to this file. Such a run is not a performance sample.</summary>
        public const string ProfilerArgument = "-zantetsuNpcSkinBenchProfiler";
        /// <summary>Diagnosis: with an external GPU profiler attached (RenderDoc), this many frames captured after the measurement. Not a performance sample.</summary>
        public const string CaptureArgument = "-zantetsuNpcSkinBenchCaptureFrames";
        /// <summary>The eye's allowed movement during the measurement, metres, and turn, degrees, for the comparison to hold (SKIN-BENCH-2 §2).</summary>
        public const string ToleranceMetresArgument = "-zantetsuNpcSkinBenchPoseToleranceMetres";
        public const string ToleranceDegreesArgument = "-zantetsuNpcSkinBenchPoseToleranceDegrees";
        /// <summary>The fixed view: the rig counter-moves every frame so that the eye's world pose stays at the pose it had at the resynchronisation (every pass sees that one view).</summary>
        public const string FixedViewArgument = "-zantetsuNpcSkinBenchFixedView";
        /// <summary>Diagnosis (SKIN-BENCH-2 §6): the skin display's palette join gap, matrices (VpSkinDisplay.PaletteJoinGapMatrices).</summary>
        public const string PaletteJoinGapArgument = "-zantetsuNpcSkinBenchPaletteJoinGap";
        /// <summary>SKIN-BENCH-3: under the update pose, how many individuals (the first so many) have their pose applied each frame; -1 (the default) every one, 0 none.</summary>
        public const string UpdateCountArgument = "-zantetsuNpcSkinBenchUpdateCount";
        public const string KeepOpenArgument = "-zantetsuNpcSkinBenchKeepOpen";
        public const string NoPictureArgument = "-zantetsuNpcSkinBenchNoPicture";

        public const int CountLimit = 512;
        public const int FamiliesLimit = 64;
        public const int DefaultCount = 64;
        public const int DefaultFamilies = 8;
        public const float DefaultWarmupSeconds = 10f;
        public const float DefaultMeasureSeconds = 20f;
        public const int DefaultSettleFrames = 30;
        public const float DefaultToleranceMetres = 0.01f;
        public const float DefaultToleranceDegrees = 1f;
        /// <summary>The fixed step the pose's time advances by each frame: one 72 Hz frame.</summary>
        public const double DefaultStepSeconds = 1.0 / 72.0;

        public string directory;
        public int count = DefaultCount;
        public NpcSkinBenchPose pose = NpcSkinBenchPose.Update;
        public NpcSkinBenchShadows shadows = NpcSkinBenchShadows.Cascade4;
        public int families = DefaultFamilies;
        public float warmupSeconds = DefaultWarmupSeconds;
        public float measureSeconds = DefaultMeasureSeconds;
        public int measureFrames;
        public int settleFrames = DefaultSettleFrames;
        public double stepSeconds = DefaultStepSeconds;
        public float toleranceMetres = DefaultToleranceMetres;
        public float toleranceDegrees = DefaultToleranceDegrees;
        public bool fixedView;
        public int paletteJoinGap;
        public int updateCount = -1;
        public string profilerFile;
        public int captureFrames;
        public bool keepOpen;
        public bool picture = true;

        /// <summary>
        /// The settings a PC launch asks for. False with no failure when the benchmark is not asked for (no
        /// <see cref="Argument"/>); false with the failure when it is and cannot run as asked.
        /// </summary>
        public static bool TryRead(string[] arguments, out NpcSkinBenchSettings settings, out string failure)
        {
            settings = null;
            failure = null;
            arguments ??= Array.Empty<string>();
            int at = IndexOf(arguments, Argument);
            if (at < 0) return false;
            var read = new NpcSkinBenchSettings();
            if (!TryValue(arguments, at, out read.directory)) { failure = Argument + " needs a directory"; return false; }
            if (!TryInt(arguments, CountArgument, 1, CountLimit, ref read.count, out failure)) return false;
            if (!TryInt(arguments, FamiliesArgument, 1, FamiliesLimit, ref read.families, out failure)) return false;
            if (!TryFloat(arguments, WarmupArgument, 0f, 3600f, ref read.warmupSeconds, out failure)) return false;
            if (!TryFloat(arguments, MeasureArgument, 0.001f, 3600f, ref read.measureSeconds, out failure)) return false;
            if (!TryInt(arguments, MeasureFramesArgument, 0, 1000000, ref read.measureFrames, out failure)) return false;
            if (!TryInt(arguments, SettleFramesArgument, 0, 100000, ref read.settleFrames, out failure)) return false;
            if (!TryDouble(arguments, StepArgument, 1e-4, 1.0, ref read.stepSeconds, out failure)) return false;
            if (!TryFloat(arguments, ToleranceMetresArgument, 0f, 100f, ref read.toleranceMetres, out failure)) return false;
            if (!TryFloat(arguments, ToleranceDegreesArgument, 0f, 180f, ref read.toleranceDegrees, out failure)) return false;
            if (!TryInt(arguments, PaletteJoinGapArgument, 0, 1000000, ref read.paletteJoinGap, out failure)) return false;
            if (!TryInt(arguments, UpdateCountArgument, -1, 512, ref read.updateCount, out failure)) return false;
            if (!TryInt(arguments, CaptureArgument, 0, 1000, ref read.captureFrames, out failure)) return false;
            int poseAt = IndexOf(arguments, PoseArgument);
            if (poseAt >= 0)
            {
                if (!TryValue(arguments, poseAt, out string value) || !TryParsePose(value, out read.pose)) { failure = PoseArgument + " takes stop or update"; return false; }
            }

            int shadowsAt = IndexOf(arguments, ShadowsArgument);
            if (shadowsAt >= 0)
            {
                if (!TryValue(arguments, shadowsAt, out string value) || !TryParseShadows(value, out read.shadows)) { failure = ShadowsArgument + " takes off, cascade1 or cascade4"; return false; }
            }

            int profilerAt = IndexOf(arguments, ProfilerArgument);
            if (profilerAt >= 0 && !TryValue(arguments, profilerAt, out read.profilerFile)) { failure = ProfilerArgument + " needs a file"; return false; }
            read.fixedView = IndexOf(arguments, FixedViewArgument) >= 0;
            read.keepOpen = IndexOf(arguments, KeepOpenArgument) >= 0;
            read.picture = IndexOf(arguments, NoPictureArgument) < 0;
            settings = read;
            return true;
        }

        public static bool TryParsePose(string value, out NpcSkinBenchPose pose)
        {
            pose = NpcSkinBenchPose.Update;
            if (string.Equals(value, "stop", StringComparison.Ordinal)) { pose = NpcSkinBenchPose.Stop; return true; }
            return string.Equals(value, "update", StringComparison.Ordinal);
        }

        public static bool TryParseShadows(string value, out NpcSkinBenchShadows shadows)
        {
            shadows = NpcSkinBenchShadows.Cascade4;
            if (string.Equals(value, "off", StringComparison.Ordinal)) { shadows = NpcSkinBenchShadows.Off; return true; }
            if (string.Equals(value, "cascade1", StringComparison.Ordinal)) { shadows = NpcSkinBenchShadows.Cascade1; return true; }
            return string.Equals(value, "cascade4", StringComparison.Ordinal);
        }

        public static string Name(NpcSkinBenchPose pose) => pose == NpcSkinBenchPose.Stop ? "stop" : "update";

        public static string Name(NpcSkinBenchShadows shadows) => shadows == NpcSkinBenchShadows.Off ? "off" : shadows == NpcSkinBenchShadows.Cascade1 ? "cascade1" : "cascade4";

        /// <summary>The cascades the condition asks for (0: shadows off).</summary>
        public static int CascadesOf(NpcSkinBenchShadows shadows) => shadows == NpcSkinBenchShadows.Off ? 0 : shadows == NpcSkinBenchShadows.Cascade1 ? 1 : 4;

        /// <summary>What is wrong with these settings, or null (the values a configuration gives are checked the same as a launch's).</summary>
        public string Validate()
        {
            if (count < 1 || count > CountLimit) return "count must be 1.." + CountLimit;
            if (families < 1 || families > FamiliesLimit) return "families must be 1.." + FamiliesLimit;
            if (!(warmupSeconds >= 0f) || warmupSeconds > 3600f) return "warmup seconds must be 0..3600";
            if (!(measureSeconds > 0f) || measureSeconds > 3600f) return "measure seconds must be above 0 and at most 3600";
            if (measureFrames < 0 || measureFrames > 1000000) return "measure frames must be 0..1000000";
            if (settleFrames < 0 || settleFrames > 100000) return "settle frames must be 0..100000";
            if (!(stepSeconds >= 1e-4) || stepSeconds > 1.0) return "step seconds must be 0.0001..1";
            if (!(toleranceMetres >= 0f) || toleranceMetres > 100f) return "pose tolerance metres must be 0..100";
            if (!(toleranceDegrees >= 0f) || toleranceDegrees > 180f) return "pose tolerance degrees must be 0..180";
            if (paletteJoinGap < 0) return "palette join gap must be 0 or more";
            if (updateCount < -1 || updateCount > 512) return "update count must be -1 (every individual) or 0..512";
            if (captureFrames < 0 || captureFrames > 1000) return "capture frames must be 0..1000";
            return null;
        }

        public string Describe()
        {
            var text = new StringBuilder();
            text.Append("count ").Append(count).Append(", pose ").Append(Name(pose)).Append(", shadows ").Append(Name(shadows))
                .Append(", families ").Append(families).Append(", warmup ").Append(warmupSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(" s, measure ").Append(measureFrames > 0 ? measureFrames + " frames" : measureSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s")
                .Append(", settle ").Append(settleFrames).Append(" frames, step ")
                .Append(stepSeconds.ToString("0.######", CultureInfo.InvariantCulture)).Append(" s, pose tolerance ")
                .Append(toleranceMetres.ToString("0.###", CultureInfo.InvariantCulture)).Append(" m / ").Append(toleranceDegrees.ToString("0.##", CultureInfo.InvariantCulture)).Append(" deg")
                .Append(fixedView ? ", FIXED VIEW" : ", tracked view").Append(", palette join gap ").Append(paletteJoinGap)
                .Append(", update count ").Append(updateCount < 0 ? "all" : updateCount.ToString(CultureInfo.InvariantCulture))
                .Append(", directory ").Append(directory ?? "(none)")
                .Append(profilerFile != null ? ", PROFILER DIAGNOSIS to " + profilerFile + " (not a performance sample)" : "")
                .Append(captureFrames > 0 ? ", GPU CAPTURE " + captureFrames + " frames after the measurement (not a performance sample)" : "")
                .Append(keepOpen ? ", kept open" : "").Append(picture ? ", picture" : ", no picture");
            return text.ToString();
        }

        private static int IndexOf(string[] arguments, string name) => Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        private static bool TryValue(string[] arguments, int at, out string value)
        {
            value = null;
            if (at + 1 >= arguments.Length || arguments[at + 1].StartsWith("-", StringComparison.Ordinal)) return false;
            value = arguments[at + 1];
            return true;
        }

        private static bool TryInt(string[] arguments, string name, int least, int most, ref int value, out string failure)
        {
            failure = null;
            int at = IndexOf(arguments, name);
            if (at < 0) return true;
            // A negative literal ("-1": every individual, SKIN-BENCH-3) is a value, not the next argument: read the text as it stands.
            string text = at + 1 < arguments.Length ? arguments[at + 1] : null;
            if (text == null || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int read) || read < least || read > most)
            {
                failure = name + " takes a whole number " + least + ".." + most;
                return false;
            }

            value = read;
            return true;
        }

        private static bool TryFloat(string[] arguments, string name, float least, float most, ref float value, out string failure)
        {
            failure = null;
            int at = IndexOf(arguments, name);
            if (at < 0) return true;
            if (!TryValue(arguments, at, out string text) || !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float read) || !(read >= least) || read > most)
            {
                failure = name + " takes a number " + least.ToString(CultureInfo.InvariantCulture) + ".." + most.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            value = read;
            return true;
        }

        private static bool TryDouble(string[] arguments, string name, double least, double most, ref double value, out string failure)
        {
            failure = null;
            int at = IndexOf(arguments, name);
            if (at < 0) return true;
            if (!TryValue(arguments, at, out string text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double read) || !(read >= least) || read > most)
            {
                failure = name + " takes a number " + least.ToString(CultureInfo.InvariantCulture) + ".." + most.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            value = read;
            return true;
        }
    }
}
