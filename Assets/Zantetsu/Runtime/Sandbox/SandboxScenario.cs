using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Zantetsu.Sandbox
{
    /// <summary>What a Player does from its start to its end (DESIGN 3.4). Decided once, before anything of the scene runs.</summary>
    public enum SandboxScenarioKind
    {
        /// <summary>The default: a person moves, turns and cuts. No script, no measurement, no ending of its own.</summary>
        Play = 0,

        /// <summary>The static scene shown and held: the draw comparison's entry.</summary>
        StaticWait = 1,

        /// <summary>The city walk's script: the Prop Slash entry with the city walk.</summary>
        CityWalk = 2,

        /// <summary>The NPC slash script: the Prop Slash entry without the city walk.</summary>
        NpcSlash = 3,
    }

    /// <summary>The scenario a launch chose, where it was read from, and what is wrong with the launch if anything is.</summary>
    public readonly struct SandboxScenarioChoice
    {
        public readonly SandboxScenarioKind Kind;

        /// <summary>Where the choice came from, in words for the log.</summary>
        public readonly string Source;

        /// <summary>Null, or why nothing is started: an unknown value, or arguments that belong to another scenario.</summary>
        public readonly string Failure;

        /// <summary>
        /// Null, or the argument of the one development check this launch runs instead of a scenario: the check
        /// starts on that argument by itself, and nothing of a scenario -- not play's own setting up, not the menu --
        /// is started beside it.
        /// </summary>
        public readonly string DevelopmentCheck;

        public SandboxScenarioChoice(SandboxScenarioKind kind, string source, string failure, string developmentCheck = null)
        {
            Kind = kind;
            Source = source;
            Failure = failure;
            DevelopmentCheck = developmentCheck;
        }

        public override string ToString()
        {
            return Failure != null ? "NOT CHOSEN (" + Failure + ")"
                : DevelopmentCheck != null ? "none (" + Source + ")"
                : SandboxScenario.Name(Kind) + " (" + Source + ")";
        }
    }

    /// <summary>
    /// The one choice of what a Player runs (DESIGN 3.4): <c>play</c> unless the launch names another scenario, by
    /// <c>-zantetsuScenario &lt;value&gt;</c> on a PC and by the field <c>scenario</c> of <c>quest3s-config.json</c> on
    /// the Quest. One entry runs, the scenario's; the entries that used to start by themselves -- on an argument, on
    /// the scene's name, on the package's -- are started from here and from nowhere else.
    /// <para>
    /// **The scenario is not the run's conditions.** The draw route's mode, the measurement, the checks, the pictures
    /// and the ending stay each entry's own arguments and configuration, read as before. The scenario only says which
    /// entry may run: a launch whose arguments belong to another scenario than the one it names -- or to any, while it
    /// names none -- is reported and ended, never run as something else.
    /// </para>
    /// <para>
    /// **The development checks stand alone.** The older checks that start on an argument of their own
    /// (<see cref="DevelopmentCheckArguments"/>) are no scenario and are not started from here; a launch may carry
    /// one of them and then nothing else that starts a run: named beside a scenario, beside a scripted entry's
    /// arguments or beside another check, it is a failure, found here before anything is started. Each check asks
    /// <see cref="MayStartDevelopmentCheck"/> before it starts, so that it starts only as the launch's one choice.
    /// </para>
    /// <para>
    /// **One Player, several scenes.** A Player built with the boot scene first (<see cref="BootSceneName"/>, which
    /// holds nothing) loads the scenario's scene from here, so nothing of a world is made before the choice and the
    /// same Player serves every scenario. A Player built with one scene runs the scenario in that scene.
    /// </para>
    /// </summary>
    public static class SandboxScenario
    {
        public const string Argument = "-zantetsuScenario";
        public const string ConfigFile = "quest3s-config.json";
        public const string BootSceneName = "ZantetsuBoot";
        public const string Prefix = "ZANTETSU SCENARIO: ";

        /// <summary>The Player's exit code when the launch's scenario cannot be run as asked.</summary>
        public const int FailureExitCode = 22;

        private static readonly string[] k_names = { "play", "static-wait", "city-walk", "npc-slash" };

        /// <summary>
        /// The development checks that start by themselves on their own argument: the sandbox scene's walk, the
        /// locomotion check, the slash replay check, and the crowd's profile run of its own build. (The view diagnosis
        /// is not one: it starts no run, moves nothing and ends nothing, and may stand beside anything.)
        /// </summary>
        public static readonly string[] DevelopmentCheckArguments =
        {
            SandboxPlayerCheck.Argument, SandboxLocomotionPlayerCheck.Argument, SandboxSlashReplayPlayerCheck.Argument, MobPlanProfileArgument,
        };

        /// <summary>The crowd's profile run's argument (MobPlanPlayerProfile, compiled into its own build only).</summary>
        public const string MobPlanProfileArgument = "-mobPlanProfile";

        public static string Name(SandboxScenarioKind kind) => k_names[(int)kind];

        public static bool TryParse(string value, out SandboxScenarioKind kind)
        {
            for (int i = 0; i < k_names.Length; i++)
            {
                if (string.Equals(value, k_names[i], StringComparison.Ordinal))
                {
                    kind = (SandboxScenarioKind)i;
                    return true;
                }
            }

            kind = SandboxScenarioKind.Play;
            return false;
        }

        /// <summary>The scenes a scenario runs in, by name, in order of preference: the first one the Player was built with is loaded.</summary>
        public static IReadOnlyList<string> ScenesFor(SandboxScenarioKind kind)
        {
            switch (kind)
            {
                case SandboxScenarioKind.Play: return new[] { "StanfordBunnyDemo", "WalkCity", "QuestCityWalk" };
                case SandboxScenarioKind.StaticWait: return new[] { "WalkCity", "QuestStaticSeed14" };
                case SandboxScenarioKind.NpcSlash: return new[] { "MultiNpcCity", "QuestNpcSlash10" };
                default: return new[] { "WalkCity", "QuestCityWalk" };
            }
        }

        /// <summary>
        /// A PC launch's choice: the value of <see cref="Argument"/>, <c>play</c> without it. An unknown value, a
        /// missing value, two different values, and arguments of an entry the scenario does not run are failures.
        /// </summary>
        public static SandboxScenarioChoice Read(string[] arguments)
        {
            arguments ??= Array.Empty<string>();
            string value = null;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!string.Equals(arguments[i], Argument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i + 1 >= arguments.Length || arguments[i + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    return Failed(Argument + " has no value (one of " + string.Join(", ", k_names) + ")");
                }

                if (value != null && !string.Equals(value, arguments[i + 1], StringComparison.Ordinal))
                {
                    return Failed(Argument + " is given twice with different values (" + value + ", " + arguments[i + 1] + ")");
                }

                value = arguments[i + 1];
            }

            SandboxScenarioKind kind = SandboxScenarioKind.Play;
            if (value != null && !TryParse(value, out kind))
            {
                return Failed("unknown scenario '" + value + "' (" + Argument + " takes one of " + string.Join(", ", k_names) + ")");
            }

            // A development check stands alone: beside a scenario, a scripted entry or another check it is refused here,
            // before any of them could start.
            string check = null;
            foreach (string candidate in DevelopmentCheckArguments)
            {
                if (Array.FindIndex(arguments, a => string.Equals(a, candidate, StringComparison.OrdinalIgnoreCase)) < 0)
                {
                    continue;
                }

                if (check != null)
                {
                    return Failed("two development checks are named (" + check + ", " + candidate + "): each runs alone");
                }

                check = candidate;
            }

            if (check != null)
            {
                bool Has(string name) => Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) >= 0;
                if (value != null)
                {
                    return Failed("the development check " + check + " is named beside " + Argument + " " + value + ": a check runs alone, with no scenario named");
                }

                foreach (string entry in new[] { SandboxPropSlashPlayerCheck.Argument, SandboxDrawComparePlayerCheck.Argument })
                {
                    if (Has(entry))
                    {
                        return Failed("the development check " + check + " is named beside " + entry + ": each starts a run of its own, and one launch runs one");
                    }
                }

                return new SandboxScenarioChoice(SandboxScenarioKind.Play, "the development check " + check + " runs alone, started by its own argument", null, check);
            }

            string mismatch = CheckArguments(kind, arguments);
            return new SandboxScenarioChoice(kind, value != null ? Argument + " " + value : "the default: no " + Argument, mismatch);
        }

        /// <summary>
        /// Whether a PC launch's other arguments are the scenario's own. The Prop Slash entry's and the draw
        /// comparison's start a script, a measurement and an ending: with <c>play</c> they start nothing and are
        /// reported; a scripted scenario needs its entry's and refuses the other's.
        /// </summary>
        public static string CheckArguments(SandboxScenarioKind kind, string[] arguments)
        {
            bool Has(string name) => Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) >= 0;
            bool propSlash = Has(SandboxPropSlashPlayerCheck.Argument);
            bool cityWalk = Has(SandboxPropSlashPlayerCheck.CityWalkArgument);
            bool drawCompare = Has(SandboxDrawComparePlayerCheck.Argument);
            switch (kind)
            {
                case SandboxScenarioKind.Play:
                    if (propSlash || drawCompare)
                    {
                        return "the launch names no scenario, which is play, and carries " + (propSlash ? SandboxPropSlashPlayerCheck.Argument : SandboxDrawComparePlayerCheck.Argument)
                            + ", which starts a scripted run: name its scenario with " + Argument + " (" + Name(SandboxScenarioKind.StaticWait) + " for "
                            + SandboxDrawComparePlayerCheck.Argument + "; " + Name(SandboxScenarioKind.CityWalk) + " for " + SandboxPropSlashPlayerCheck.Argument + " with "
                            + SandboxPropSlashPlayerCheck.CityWalkArgument + ", " + Name(SandboxScenarioKind.NpcSlash) + " without)";
                    }

                    return null;
                case SandboxScenarioKind.StaticWait:
                    if (propSlash) return Name(kind) + " does not run " + SandboxPropSlashPlayerCheck.Argument;
                    if (!drawCompare) return Name(kind) + " needs " + SandboxDrawComparePlayerCheck.Argument + " <directory> and its arguments";
                    return null;
                case SandboxScenarioKind.CityWalk:
                    if (drawCompare) return Name(kind) + " does not run " + SandboxDrawComparePlayerCheck.Argument;
                    if (!propSlash) return Name(kind) + " needs " + SandboxPropSlashPlayerCheck.Argument + " <directory> and its arguments";
                    if (!cityWalk) return Name(kind) + " needs " + SandboxPropSlashPlayerCheck.CityWalkArgument + " (without it the Prop Slash entry is " + Name(SandboxScenarioKind.NpcSlash) + ")";
                    return null;
                default:
                    if (drawCompare) return Name(kind) + " does not run " + SandboxDrawComparePlayerCheck.Argument;
                    if (!propSlash) return Name(kind) + " needs " + SandboxPropSlashPlayerCheck.Argument + " <directory> and its arguments";
                    if (cityWalk) return Name(kind) + " does not run " + SandboxPropSlashPlayerCheck.CityWalkArgument + " (that is " + Name(SandboxScenarioKind.CityWalk) + ")";
                    return null;
            }
        }

        [Serializable]
        private sealed class ConfigScenario
        {
            public string scenario;
        }

        /// <summary>
        /// A Quest launch's choice: the field <c>scenario</c> of the configuration's text, <c>play</c> where the file
        /// or the field is not there. An unknown value is a failure. The rest of the file is each entry's own.
        /// </summary>
        public static SandboxScenarioChoice ReadConfig(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return new SandboxScenarioChoice(SandboxScenarioKind.Play, "the default: no " + ConfigFile, null);
            }

            ConfigScenario config;
            try
            {
                config = JsonUtility.FromJson<ConfigScenario>(json);
            }
            catch (ArgumentException e)
            {
                return Failed(ConfigFile + " is not readable (" + e.Message + ")");
            }

            if (config == null || string.IsNullOrEmpty(config.scenario))
            {
                return new SandboxScenarioChoice(SandboxScenarioKind.Play, "the default: no scenario in " + ConfigFile, null);
            }

            return TryParse(config.scenario, out SandboxScenarioKind kind)
                ? new SandboxScenarioChoice(kind, ConfigFile + " scenario " + config.scenario, null)
                : Failed("unknown scenario '" + config.scenario + "' in " + ConfigFile + " (one of " + string.Join(", ", k_names) + ")");
        }

        private static SandboxScenarioChoice Failed(string failure) => new SandboxScenarioChoice(SandboxScenarioKind.Play, "none", failure);

        // ---- the session ------------------------------------------------------------------------------------------

        private static SandboxScenarioChoice? s_choice;
        private static bool s_begun;

        /// <summary>
        /// The device's own entries, set before the first scene by the assembly that has them (the Quest's): the
        /// scripted scenarios' start, which answers a failure or null, and what play needs there. Without them the
        /// PC's entries run.
        /// </summary>
        public static Func<SandboxScenarioKind, string> PlatformEntry;
        public static Func<string> PlatformPlay;

        /// <summary>This launch's choice, read once: from the configuration file on a device, from the command line elsewhere.</summary>
        public static SandboxScenarioChoice Current
        {
            get
            {
                if (!s_choice.HasValue)
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    string file = Path.Combine(Application.persistentDataPath, ConfigFile);
                    s_choice = ReadConfig(File.Exists(file) ? File.ReadAllText(file) : null);
#else
                    s_choice = Read(Environment.GetCommandLineArgs());
#endif
                }

                return s_choice.Value;
            }
        }

        /// <summary>
        /// Whether the development check of this argument may start: only as this launch's one choice
        /// (<see cref="SandboxScenarioChoice.DevelopmentCheck"/>), with nothing wrong with the launch, and in a scene
        /// of its own -- never in the boot scene of a Player of every scenario. Each check asks before it starts.
        /// </summary>
        public static bool MayStartDevelopmentCheck(string argument)
        {
            SandboxScenarioChoice choice = Current;
            return choice.Failure == null
                && string.Equals(choice.DevelopmentCheck, argument, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(SceneManager.GetActiveScene().name, BootSceneName, StringComparison.Ordinal);
        }

        /// <summary>For tests: the choice a session reads, in place of the launch's.</summary>
        internal static void SetForTest(SandboxScenarioChoice? choice) => s_choice = choice;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            s_choice = null;
            s_begun = false;
            PlatformEntry = null;
            PlatformPlay = null;
        }

        /// <summary>
        /// After the first scene is loaded and before anything of it starts: the choice is logged, and its entry is
        /// started -- here, where the scene is the scenario's already, or after the scenario's scene is loaded, where
        /// the first scene is the boot scene. The Editor starts nothing by itself.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Begin()
        {
            if (Application.isEditor || s_begun)
            {
                return;
            }

            s_begun = true;
            SandboxScenarioChoice choice = Current;
            Debug.Log(Prefix + choice);
            if (choice.Failure != null)
            {
                Fail(choice.Failure);
                return;
            }

            Scene active = SceneManager.GetActiveScene();
            if (choice.DevelopmentCheck != null)
            {
                // The check starts itself (it has asked MayStartDevelopmentCheck); nothing of a scenario stands beside it.
                if (string.Equals(active.name, BootSceneName, StringComparison.Ordinal))
                {
                    Fail("the development check " + choice.DevelopmentCheck + " needs the Player of its own scene; this Player starts in the boot scene and loads a scenario's");
                }

                return;
            }

            if (!string.Equals(active.name, BootSceneName, StringComparison.Ordinal))
            {
                StartEntry(choice.Kind, active.name);
                return;
            }

            if (!TryFindScene(choice.Kind, out string path, out string looked))
            {
                Fail("this Player has no scene for " + Name(choice.Kind) + " (looked for " + looked + ")");
                return;
            }

            void Loaded(Scene scene, LoadSceneMode mode)
            {
                SceneManager.sceneLoaded -= Loaded;
                StartEntry(choice.Kind, scene.name);
            }

            // sceneLoaded comes after the scene's objects are awake and enabled and before any Start: where the entries
            // have always been started.
            Debug.Log(Prefix + "loading " + path + " for " + Name(choice.Kind));
            SceneManager.sceneLoaded += Loaded;
            SceneManager.LoadScene(path, LoadSceneMode.Single);
        }

        /// <summary>The scenario's scene among the ones this Player was built with.</summary>
        public static bool TryFindScene(SandboxScenarioKind kind, out string path, out string looked)
        {
            IReadOnlyList<string> names = ScenesFor(kind);
            looked = string.Join(", ", names);
            foreach (string name in names)
            {
                for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
                {
                    string candidate = SceneUtility.GetScenePathByBuildIndex(i);
                    if (string.Equals(Path.GetFileNameWithoutExtension(candidate), name, StringComparison.Ordinal))
                    {
                        path = candidate;
                        return true;
                    }
                }
            }

            path = null;
            return false;
        }

        private static void StartEntry(SandboxScenarioKind kind, string scene)
        {
            string failure;
            if (kind == SandboxScenarioKind.Play)
            {
                failure = scene == BunnyDemo.SceneName ? null : PlatformPlay != null ? PlatformPlay() : null;
                if (failure == null)
                {
                    Debug.Log(Prefix + "play in " + scene + ": no script, no measurement, no ending; " + KeepOneLocomotionInput()
                        + "; " + QuietDevelopmentTools());
                }
            }
            else if (PlatformEntry != null)
            {
                failure = PlatformEntry(kind);
            }
            else if (kind == SandboxScenarioKind.StaticWait)
            {
                failure = SandboxDrawComparePlayerCheck.StartForScenario() ? null : "the draw comparison did not start (its own line says why)";
            }
            else
            {
                failure = SandboxPropSlashPlayerCheck.StartForScenario() ? null : "the Prop Slash entry did not start";
            }

            if (failure != null)
            {
                Fail(failure);
                return;
            }

            // The XR session's focus as a reason of the game's pause (DESIGN 4.6, D-241), before the menu: it holds the
            // game whenever the system UI takes the input, whatever the scenario and whatever the statistics' consent.
            XrFocusPause.Install();
            PlayMenu menu = PlayMenu.Install(kind);

            // Play's statistics (DESIGN 21.18): the service, and the first-run question where it was never answered.
            if (kind == SandboxScenarioKind.Play)
            {
                PlayStatisticsService statistics = PlayStatisticsService.Install(kind, scene);
                menu.AskConsent(statistics);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // Play has no ending, so what the fragment collection does is said in the log as it goes (Development only).
                PlayCollectionReadout.Install();
#endif
            }
        }

        /// <summary>
        /// Play moves the player by one input (DESIGN 3.4): where a scene has several live ones for one player, the
        /// crowd's -- the city's own way of moving -- is kept and the others' live input is switched off. Nothing is
        /// changed where there is only one. Returns what it found, for the log.
        /// </summary>
        public static string KeepOneLocomotionInput()
        {
            MobPlanPlayerInput[] city = UnityEngine.Object.FindObjectsByType<MobPlanPlayerInput>(FindObjectsSortMode.None);
            SandboxLocomotionInput[] plain = UnityEngine.Object.FindObjectsByType<SandboxLocomotionInput>(FindObjectsSortMode.None);
            int kept = 0, switchedOff = 0;
            foreach (MobPlanPlayerInput input in city)
            {
                if (input.enabled && input.gameObject.activeInHierarchy && input.liveInput)
                {
                    kept++;
                }
            }

            foreach (SandboxLocomotionInput input in plain)
            {
                if (!input.enabled || !input.gameObject.activeInHierarchy || !input.LiveInputEnabled)
                {
                    continue;
                }

                if (kept > 0)
                {
                    input.LiveInputEnabled = false;
                    switchedOff++;
                }
                else
                {
                    kept++;
                }
            }

            return "live locomotion inputs kept " + kept + ", switched off " + switchedOff
                + (kept == 0 ? " (NO live locomotion input in this scene)" : "");
        }

        /// <summary>
        /// Play shows and records nothing of the development tools a scene carries (DESIGN 3.4): the slash recorder's
        /// own controls are not drawn and it arms no capture by itself, and the diagnostic overlay is hidden. The
        /// recorder and the overlay stay in the scene; a scripted scenario sets them as its own arguments say.
        /// Returns what it found, for the log.
        /// </summary>
        public static string QuietDevelopmentTools()
        {
            int recorders = 0, overlays = 0;
            foreach (SandboxSlashPoseRecorder recorder in UnityEngine.Object.FindObjectsByType<SandboxSlashPoseRecorder>(FindObjectsSortMode.None))
            {
                recorder.ShowControls = false;
                recorder.AutoCaptureWhenIdle = false;
                recorders++;
            }

            foreach (SandboxSlashDiagnosticsOverlay overlay in UnityEngine.Object.FindObjectsByType<SandboxSlashDiagnosticsOverlay>(FindObjectsSortMode.None))
            {
                overlay.Visible = false;
                overlays++;
            }

            return "development tools quieted: slash recorders " + recorders + " (controls hidden, no capture by itself), overlays " + overlays;
        }

        private static void Fail(string failure)
        {
            Debug.LogError(Prefix + "NOT STARTED: " + failure + ". Nothing is run in its place; the Player ends with code " + FailureExitCode + ".");
            Application.Quit(FailureExitCode);
        }

        /// <summary>One clause for the launch record: what the launch's arguments choose.</summary>
        public static string Describe(string[] arguments) => Read(arguments).ToString();
    }
}
