using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Zantetsu.Core.MobPlan;
using Debug = UnityEngine.Debug;

namespace Zantetsu.Sandbox
{
    // PROP-OBSTACLE-1: the crowd's side of the prop fragments' map (PropObstacleSource makes it; the planner's Core reads it)
    // and of its capture (PropCaptureWriter: the numeric inputs of the map and of the planner, from ordinary play).
    // On by default in every build (D-264, 2026-11-17); -zantetsuPropObstaclesOff turns it off: nothing here runs then and
    // the planner is given no map and keeps its settings of before.
    public sealed partial class MobPlanCrowd
    {
        private PropObstacleSource props;
        private PropCaptureWriter propCapture;
        private int refillStartsDroppedInProps;
        private int cyclesWithPropMap, cyclesWithoutPropMap;
        private bool propsEndFile;
        private double propMapAgeSecondsInAll, propMapAgeSecondsMost;
        private byte[] playerBlock;
        private int playerBlockUsed;
        private MemoryStream captureState;   // the planning worker's only

        /// <summary>The prop fragments' map source of this crowd (null: off).</summary>
        public PropObstacleSource Props => props;

        /// <summary>Starts refused at the last moment because the newest map has a felled piece there.</summary>
        public int StartsDroppedInProps => refillStartsDroppedInProps;

        /// <summary>For a test or a tool: the settings a crowd not yet begun takes instead of the launch's (null: the launch's).</summary>
        internal static PropObstacleSettings propSettingsForTest;

        private void BeginProps()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            bool on = PropObstacleSource.Read(arguments, out PropObstacleSettings settings);
            if (propSettingsForTest != null) { on = true; settings = propSettingsForTest.Clone(); }
            string captureInto = PropCaptureWriter.Asked(arguments);
            if (!on)
            {
                Debug.Log("MOBPLAN PROPS: off, asked for by " + PropObstacleSource.OffArgument + " (the prop fragments' map is on by default since 2026-11-17, D-264): no map, the planner as before it");
                if (captureInto != null) Debug.LogError("MOBPLAN PROPS: " + PropCaptureWriter.Argument + " needs the map, which " + PropObstacleSource.OffArgument + " turned off: nothing is captured", this);
                return;
            }

            props = new PropObstacleSource(world, mapOffset, simulation.Map, settings) { Clock = () => PlanTime };
            propsEndFile = Array.IndexOf(arguments, PropObstacleSource.EndFileArgument) >= 0;
            Debug.Log("MOBPLAN PROPS: on -- the default since 2026-11-17 (D-264; " + PropObstacleSource.OffArgument + " turns it off): " + settings.Describe()
                + FormattableString.Invariant($"; NPC radius {simulation.Scenario.npcRadius:F2} m + keep {simulation.Config.propKeepMeters:F2} m for the walking cost, + {simulation.Config.propSpawnKeepMeters:F2} m for a spawn spot; one build at a time on the Background pool"));
            PlannerConfig c = simulation.Config;
            Debug.Log(FormattableString.Invariant($"MOBPLAN PROPS PLANNER: goal field {(c.propGoalField ? 1 : 0)} (cell {c.propGoalFieldCellMeters:F1} m), recovery {(c.goalFieldRecovery ? 1 : 0)}, idle fix {(c.idleCostLowTranslation ? 1 : 0)}, goal reach {(c.propGoalReach ? 1 : 0)}, no-reach mode {c.propGoalNoReachMode}, frozen-part re-check {(c.propPrefixRecheck ? 1 : 0)}"));
            if (captureInto == null) return;
            try
            {
                settings.captureSquareMeters = PropObstacleSource.CaptureSquareMeters;   // the source reads this same object
                propCapture = new PropCaptureWriter(captureInto, SessionJson(settings, arguments));
                props.Capture = propCapture;
                playerBlock = new byte[PropCapture.PlayerRecordBytes * 128];
                Debug.Log("MOBPLAN PROPS CAPTURE: on -- " + propCapture.Folder);
            }
            catch (Exception e)
            {
                propCapture = null;
                Debug.LogError("MOBPLAN PROPS CAPTURE: NOT STARTED: " + e.Message, this);
            }
        }

        private string SessionJson(PropObstacleSettings settings, string[] arguments)
        {
            string Sha(byte[] bytes)
            {
                using (SHA256 sha = SHA256.Create())
                {
                    var text = new StringBuilder(64);
                    foreach (byte b in sha.ComputeHash(bytes)) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                    return text.ToString();
                }
            }

            int code = Array.IndexOf(arguments, PropCaptureWriter.CodeArgument);
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            var json = new StringBuilder();
            json.Append("{\n  \"formatVersion\": ").Append(PropCapture.Format)
                .Append(",\n  \"startedUtc\": \"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('"')
                .Append(",\n  \"codeVersion\": \"").Append(PropCaptureWriter.Escape(code >= 0 && code + 1 < arguments.Length ? arguments[code + 1] : "")).Append('"')
                .Append(",\n  \"build\": \"").Append(Debug.isDebugBuild ? "Development" : "Release").Append(Application.isEditor ? " (Editor)" : "").Append('"')
                .Append(",\n  \"unity\": \"").Append(Application.unityVersion).Append('"')
                .Append(",\n  \"settings\": ").Append(JsonUtility.ToJson(settings))
                .Append(",\n  \"mapOffset\": [").Append(F(mapOffset.x)).Append(", ").Append(F(mapOffset.y)).Append(", ").Append(F(mapOffset.z)).Append(']')
                .Append(",\n  \"mapCentre\": [").Append(F(mapCentre.x)).Append(", ").Append(F(mapCentre.y)).Append(']')
                .Append(",\n  \"mapWindowMeters\": 300")
                .Append(",\n  \"staticMap\": { \"asset\": \"").Append(PropCaptureWriter.Escape(assets.polygons.name)).Append("\", \"bytes\": ").Append(assets.polygons.bytes.Length)
                .Append(", \"sha256\": \"").Append(Sha(assets.polygons.bytes)).Append("\", \"description\": \"").Append(PropCaptureWriter.Escape(simulation.Map.Description ?? "")).Append("\" }")
                .Append(",\n  \"rootMotion\": { \"asset\": \"").Append(PropCaptureWriter.Escape(assets.dataset.name)).Append("\", \"bytes\": ").Append(assets.dataset.bytes.Length)
                .Append(", \"sha256\": \"").Append(Sha(assets.dataset.bytes)).Append("\", \"clips\": ").Append(simulation.Dataset.Count).Append(" }")
                .Append(",\n  \"plannerConfig\": ").Append(JsonUtility.ToJson(simulation.Config))
                .Append(",\n  \"scenario\": ").Append(JsonUtility.ToJson(simulation.Scenario))
                .Append(",\n  \"simulationStateFormat\": ").Append(LocomotionSimulation.StateFormat)
                .Append(",\n  \"arguments\": \"").Append(PropCaptureWriter.Escape(string.Join(" ", arguments, 1, Math.Max(0, arguments.Length - 1)))).Append("\"\n}\n");
            return json.ToString();
        }

        private void StepProps()
        {
            if (props == null) return;
            Vector3 p = player.transform.position - mapOffset;
            double now = PlanTime;
            props.Step(now, p.x, p.z);
            if (propCapture == null) return;
            // the player each frame (the velocity is the last frame's): fixed records, handed a block at a time
            if (playerBlockUsed + PropCapture.PlayerRecordBytes > playerBlock.Length) FlushPlayerBlock();
            Vector3 eye = (view != null ? view.position : player.transform.position) - mapOffset;
            float yaw = view != null ? view.eulerAngles.y : player.transform.eulerAngles.y;
            int at = playerBlockUsed;
            Put(playerBlock, ref at, now);
            Put(playerBlock, ref at, p.x); Put(playerBlock, ref at, p.z); Put(playerBlock, ref at, yaw);
            Put(playerBlock, ref at, playerVelocity.x); Put(playerBlock, ref at, playerVelocity.z);
            Put(playerBlock, ref at, eye.x); Put(playerBlock, ref at, eye.z);
            playerBlockUsed = at;
        }

        private void FlushPlayerBlock()
        {
            if (playerBlockUsed == 0) return;
            propCapture.Hand(PropCaptureWriter.Player, playerBlock, playerBlockUsed);
            playerBlock = new byte[playerBlock.Length];   // the writer has the old one
            playerBlockUsed = 0;
        }

        private static unsafe void Put(byte[] into, ref int at, double value)
        {
            fixed (byte* p = &into[at]) *(double*)p = value;
            at += 8;
        }

        private static unsafe void Put(byte[] into, ref int at, float value)
        {
            fixed (byte* p = &into[at]) *(float*)p = value;
            at += 4;
        }

        private bool InProps(float x, float z)
        {
            PropObstacleMap map = props?.Pool.Current;
            return map != null && simulation.Config.propObstacles && map.Overlaps(x, z, simulation.Scenario.npcRadius + simulation.Config.propSpawnKeepMeters);
        }

        // what a queued cycle took: with a version or without, and how old the version's input was
        private void NoteCycleProps(PropObstacleMap map, double now)
        {
            if (props == null) return;
            if (map == null) { cyclesWithoutPropMap++; return; }
            cyclesWithPropMap++;
            double age = now - map.InputTime;
            propMapAgeSecondsInAll += age;
            if (age > propMapAgeSecondsMost) propMapAgeSecondsMost = age;
        }

        // ---- the capture's cycle stream ----

        /// <summary>The planning worker, after the last accepted cycle was acknowledged and before this cycle's inputs are taken: the state to take the play up from here.</summary>
        private void CaptureCycleStart(float now, int frame, int capturedGeneration, Vector3 p, Vector3 velocity, float yaw, PropObstacleMap map,
            IReadOnlyDictionary<int, PublishedMobPlan> baseline, int[] waitingIds)
        {
            PropCaptureWriter capture = propCapture;
            if (capture == null) return;
            if (captureState == null) captureState = new MemoryStream(1 << 16);
            captureState.SetLength(0);
            using (var w = new BinaryWriter(captureState, Encoding.UTF8, true)) simulation.SaveState(w);
            capture.Hand(PropCaptureWriter.Cycles, PropCapture.EncodeCycleStart(now, frame, capturedGeneration, p.x, p.z, velocity.x, velocity.z, yaw,
                map?.Version ?? 0, map?.InputTime ?? double.NaN, captureState.GetBuffer(), (int)captureState.Length, baseline, waitingIds ?? Array.Empty<int>()));
        }

        private void CaptureCycleEnd(float queuedNow, string outcome, CycleResult cycle, int replacements)
        {
            propCapture?.Hand(PropCaptureWriter.Cycles, PropCapture.EncodeCycleCollected(PlanTime, queuedNow, outcome, QueueMilliseconds, ComputeMilliseconds, replacements, cycle?.Metrics));
        }

        private void CaptureRetired(int id, Actor actor)
        {
            if (propCapture == null) return;
            float x = 0f, z = 0f;
            if (actor.character != null && actor.character.CharacterRoot != null)
            {
                Vector3 at = actor.character.CharacterRoot.transform.position - mapOffset;
                x = at.x; z = at.z;
            }
            else if (actor.plan != null && actor.plan.TryResolve(PlanTime, simulation.Dataset, out _, out _, out Pose2 root)) { x = root.X; z = root.Z; }
            propCapture.Hand(PropCaptureWriter.Cycles, PropCapture.EncodeEvent(PropCapture.RetiredRecord, PlanTime, id, x, z, 0));
        }

        private void CaptureStart(int id, float x, float z, byte result)
        {
            propCapture?.Hand(PropCaptureWriter.Cycles, PropCapture.EncodeEvent(PropCapture.StartAttemptRecord, PlanTime, id, x, z, result));
        }

        /// <summary>The file under Application.persistentDataPath the map's end line is also written to, whole, when the launch asks (-zantetsuPropObstacleEndFile).</summary>
        public const string PropsEndFileName = "prop-obstacles-end.txt";
        /// <summary>The most characters of one part of the end line in the log (the Quest's log keeps about 1,000 of a line).</summary>
        public const int PropsEndPartCharacters = 800;

        private string PropsEndWords()
        {
            return "MOBPLAN PROPS (end): " + props.Describe()
                + FormattableString.Invariant($"; planning cycles with a version {cyclesWithPropMap} (the version's input older by mean {(cyclesWithPropMap > 0 ? propMapAgeSecondsInAll / cyclesWithPropMap * 1000.0 : 0.0):F0} ms, most {propMapAgeSecondsMost * 1000.0:F0} ms), without {cyclesWithoutPropMap}; ")
                + FormattableString.Invariant($"starts refused in a felled piece {refillStartsDroppedInProps}; spawn spots refused by the planner {simulation?.PropSpawnRejects ?? 0}, goals {simulation?.PropGoalRejects ?? 0}; prop lineages marked {world?.Lifetime?.PropLineageCount ?? 0}");
        }

        /// <summary>
        /// The words into the end file: the kind ("end", or "pause" for the counters so far), the moment, the words' length, then
        /// the words in one line. A device log cuts a long line, and an app ended while paused writes no end line at all.
        /// </summary>
        private void WritePropsEndFile(string kind, string words)
        {
            try
            {
                string path = Path.Combine(Application.persistentDataPath, PropsEndFileName);
                File.WriteAllText(path, FormattableString.Invariant($"{kind} {DateTime.UtcNow:o} plan time {PlanTime:F2} s, {words.Length} characters\n") + words + "\n", new UTF8Encoding(false));
                Debug.Log(FormattableString.Invariant($"MOBPLAN PROPS ({kind}): the line's {words.Length} characters written to {path}"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("MOBPLAN PROPS (" + kind + "): the end file NOT written: " + e.Message, this);
            }
        }

        // The Quest pauses the app when the headset is taken off and may end it there without the world's end: in a launch
        // that asks for the end file the counters so far go to it at every pause (the world's end writes it again).
        private void OnApplicationPause(bool paused)
        {
            if (paused && props != null && propsEndFile) WritePropsEndFile("pause", PropsEndWords());
        }

        private void EndProps()
        {
            if (props == null) return;
            string words = PropsEndWords();
            Debug.Log(words);
            if (words.Length > PropsEndPartCharacters)
            {
                List<string> parts = PropCapture.LogParts(words, PropsEndPartCharacters);
                for (int i = 0; i < parts.Count; i++)
                    Debug.Log(FormattableString.Invariant($"MOBPLAN PROPS (end, part {i + 1} of {parts.Count}): ") + parts[i]);
            }
            if (propsEndFile) WritePropsEndFile("end", words);
            props.Release();
            if (propCapture == null) return;
            FlushPlayerBlock();
            PropCaptureWriter capture = propCapture;
            propCapture = null;
            props.Capture = null;
            Debug.Log("MOBPLAN PROPS CAPTURE (end): " + capture.Close(5.0, words));
        }
    }
}
