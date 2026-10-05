using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // The city walk (TL, 2026-10-03; Assets/Licensed/WalkCity/WalkCity.unity): the MobPlan mode over a whole city whose
    // every cuttable building and prop is a PlacedCuttableRegistration of its own, walked by the script through several
    // streets and cut only by the katana's own Slashes. With CityWalkArgument the check
    //   * runs no synthetic section: no re-cut during a drop before the script, and after it no hull limit check, no
    //     mid-drop re-cut and no reused-slot check (each gives a Slash to the detector directly);
    //   * waits, bounded, for every placed cuttable to be registered or refused before the script, and writes down each
    //     refused one by name with its reason;
    //   * bounds every stage -- the crowd, the NPCs' preparation, the registrations, the head wait, the script and the
    //     cuts' completion, the observation, the ending -- by its own deadline and by the run's budget (CityWalkBudgetArgument,
    //     seconds from the process start), records which stage reached its deadline (a failure, never a success), and
    //     writes each stage's start and end at once to city-walk-stages.txt, so a run killed from outside still shows
    //     where it stood;
    //   * reports what was cut, by kind and by name (buildings, props, NPCs), the re-cuts, and the distance walked
    //     against the script's own.
    // Nothing here cuts, moves or registers anything.
    public static partial class SandboxPropSlashPlayerCheck
    {
        public const string CityWalkArgument = "-zantetsuCityWalk";
        public const string CityWalkBudgetArgument = "-zantetsuCityWalkBudget";

        /// <summary>Pictures of the city before and after the registration (2026-10-03): "name:x,y,z,yaw,pitch;..." in the world.</summary>
        public const string CityWalkViewsArgument = "-zantetsuCityWalkViews";

        /// <summary>A stage's deadline (process seconds): its own length from now, and in a city walk never past the run's budget.</summary>
        public static float CityWalkStageDeadline(float now, float seconds, bool cityWalk, float budget) =>
            cityWalk ? Mathf.Min(now + seconds, budget) : now + seconds;

        /// <summary>
        /// The buildings with more than one fixed group or more than one free group (2026-10-03: the hull trial's end judged a
        /// building at a time -- at most one fixed and one free group each -- not the whole world's groups as one building's).
        /// </summary>
        public static int BuildingsPastOneEach(IEnumerable<(int building, bool kinematic)> groups) =>
            groups.GroupBy(g => g.building).Count(b => b.Count(g => g.kinematic) > 1 || b.Count(g => !g.kinematic) > 1);

        /// <summary>
        /// The ending of a city walk whose registration was cut short (shared with its test): every registrar held, so
        /// none still waiting registers from here; what stood where written down -- registered, refused, still waiting --
        /// then the ordinary ending (<see cref="CheckEnding.Run"/>: the world's shutdown and reclaim, bounded at 15 s), and
        /// what each stood at after it. The registered ones' actors are destroyed and their shapes given up with their owners
        /// at the world's release (the registrars give back their own arrays and meshes when destroyed); the waiting ones are
        /// left as the scene had them.
        /// </summary>
        internal static IEnumerator CityWalkRegistrationCutShort(CutWorldRoot world, CheckEnding ending, IReadOnlyList<PlayableCityCuttable> all, System.Action<string> log)
        {
            PlayableCityCuttable.Held = true;
            int registered = all.Count(c => c != null && c.IsCutTarget), refused = all.Count(c => c != null && c.Failure != null);
            List<PlayableCityCuttable> waiting = all.Where(c => c != null && !c.IsCutTarget && c.Failure == null).ToList();
            log("city walk registration cut short: registered " + registered + ", refused " + refused + ", still waiting " + waiting.Count + " (held: none of them registers from here); storage: "
                + (world.Storage != null ? world.Storage.DescribeRoom() : "none") + "; the ordinary ending follows (the world's reclaim)");
            yield return ending.Run(world, new[] { new CheckEnding.Part("registration cut short", () => log("city walk: no script (the registration did not finish)")) }, 15f);
            log("city walk registration cut short, after the ending: world released " + world.IsReleased + "; registered " + all.Count(c => c != null && c.IsCutTarget)
                + " (their actors ended with the world's owners), waiting ones registered since " + waiting.Count(c => c.IsCutTarget) + ", refused since " + waiting.Count(c => c.Failure != null));
        }

        private sealed partial class Walk
        {
            internal bool cityWalk;
            internal float cityWalkBudget = 565f;
            private StreamWriter _cwStages;
            private string _cwStage;
            private float _cwStageFrom, _cwStageBy;
            private readonly List<string> _cwDeadlinesReached = new List<string>();
            private double _cwRealAtScriptStart = double.NaN;

            /// <summary>
            /// The deadline of a stage <paramref name="seconds"/> long from now. In a city walk it is never past the run's
            /// budget, and the stage's start is written down at once; otherwise it is the plain deadline, as before.
            /// </summary>
            private float StageBegin(string stage, float seconds)
            {
                float own = Time.realtimeSinceStartup + seconds;
                if (!cityWalk) return own;
                _cwStage = stage;
                _cwStageFrom = Time.realtimeSinceStartup;
                _cwStageBy = CityWalkStageDeadline(_cwStageFrom, seconds, true, cityWalkBudget);
                StageLine("begin " + stage + " at " + _cwStageFrom.ToString("F2", Inv) + " s (process), deadline " + _cwStageBy.ToString("F2", Inv)
                    + " s (" + (own <= cityWalkBudget ? "its own " + seconds.ToString("R", Inv) + " s" : "the run's budget " + cityWalkBudget.ToString("R", Inv) + " s") + ")");
                return _cwStageBy;
            }

            /// <summary>The stage's end: whether it ended by reaching its deadline (a failure in a city walk), with what it was waiting for.</summary>
            private void StageEnd(bool reachedDeadline, string detail)
            {
                if (!cityWalk || _cwStage == null) return;
                float now = Time.realtimeSinceStartup;
                StageLine("end " + _cwStage + " at " + now.ToString("F2", Inv) + " s after " + (now - _cwStageFrom).ToString("F2", Inv) + " s: "
                    + (reachedDeadline ? "REACHED ITS DEADLINE" : "done") + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
                if (reachedDeadline) _cwDeadlinesReached.Add(_cwStage + (string.IsNullOrEmpty(detail) ? "" : " (" + detail + ")"));
                _cwStage = null;
            }

            /// <summary>Whether the run's budget is still open (always, outside a city walk).</summary>
            private bool WithinBudget => !cityWalk || Time.realtimeSinceStartup < cityWalkBudget;

            private void StageLine(string line)
            {
                Log("city walk stage: " + line);
                try
                {
                    if (_cwStages == null)
                    {
                        Directory.CreateDirectory(directory);
                        _cwStages = new StreamWriter(Path.Combine(directory, "city-walk-stages.txt")) { AutoFlush = true };
                    }

                    _cwStages.WriteLine(Time.frameCount + " " + line);
                }
                catch (IOException e)
                {
                    Log("city walk stage file: " + e.Message);
                }
            }

            private void CityWalkClose()
            {
                _cwStages?.Dispose();
                _cwStages = null;
            }

            /// <summary>
            /// Pictures of the views given by CityWalkViewsArgument ("name:x,y,z,yaw,pitch;..." in the world), through the
            /// check's own capture camera (CheckCaptureCamera: fixed shots, 1280 x 720, listed with the world's camera drawing
            /// and registered with its display, so what the cut world draws is in them as it is in the game's camera) into
            /// view-&lt;name&gt;-&lt;when&gt;.png; the XR camera untouched. Each shot is rendered two frames before it is read.
            /// </summary>
            private IEnumerator CityWalkViews(string when)
            {
                string spec = Value(CityWalkViewsArgument);
                if (string.IsNullOrEmpty(spec)) yield break;
                var shots = new List<CheckCaptureCamera.Shot>();
                foreach (string item in spec.Split(';'))
                {
                    string[] nameAndPose = item.Split(':');
                    if (nameAndPose.Length != 2) continue;
                    float[] v = nameAndPose[1].Split(',').Select(x => float.Parse(x, Inv)).ToArray();
                    var at = new Vector3(v[0], v[1], v[2]);
                    shots.Add(new CheckCaptureCamera.Shot { name = nameAndPose[0], position = at, lookAt = at + Quaternion.Euler(v[4], v[3], 0f) * Vector3.forward * 10f, fieldOfView = 60f });
                }

                var capture = new CheckCaptureCamera(_world, Camera.main, null, 0f, 0f, 0f, 60f, 1280, 720, shots);
                var pixels = new Texture2D(capture.width, capture.height, TextureFormat.RGB24, false);
                try
                {
                    for (int s = 0; s < shots.Count; s++)
                    {
                        capture.SetShot(s, "the " + when + " picture");
                        int rendered = capture.Rendered;
                        yield return null;
                        yield return null;
                        yield return new WaitForEndOfFrame();
                        RenderTexture active = RenderTexture.active;
                        RenderTexture.active = capture.Target;
                        pixels.ReadPixels(new Rect(0, 0, capture.width, capture.height), 0, 0);
                        pixels.Apply();
                        RenderTexture.active = active;
                        string file = Path.Combine(directory, "view-" + shots[s].name + "-" + when + ".png");
                        File.WriteAllBytes(file, pixels.EncodeToPNG());
                        Log("city walk view " + shots[s].name + " " + when + ": renders since the switch " + (capture.Rendered - rendered) + ", taken by the display " + capture.TakenByDisplay
                            + ", listed with the camera drawing " + capture.ListedWithDrawing + ", at frame " + Time.frameCount + " into " + file);
                    }
                }
                finally
                {
                    capture.Dispose();
                    Object.Destroy(pixels);
                }
            }

            /// <summary>
            /// Before the script (a city walk only): the placed cuttables, held since the scene's load, are let register here
            /// -- the whole registration inside this stage, bounded -- and every one registered or refused, the refused ones
            /// by name with their reasons. With CityWalkViewsArgument, pictures of the views before (the city as authored)
            /// and after (as the cut world draws it). At the stage's end: its time and frames, the registrations ended and the
            /// refused ones, and its deadline (TL, 2026-10-03: no per-part cost).
            /// </summary>
            private IEnumerator CityWalkWaitRegistered()
            {
                PlayableCityCuttable[] all = Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                int registeredBefore = all.Count(c => c.IsCutTarget);
                Log("city walk registration: held until now " + PlayableCityCuttable.Held + " (registered before the stage " + registeredBefore + ")");
                yield return CityWalkViews("before");
                float by = StageBegin("cuttables registration", 120f);
                bool Open(PlayableCityCuttable c) => c != null && !c.IsCutTarget && c.Failure == null && c.isActiveAndEnabled;
                float from = Time.realtimeSinceStartup;
                int frames = 0;
                PlayableCityCuttable.Held = false;
                while (all.Any(Open) && Time.realtimeSinceStartup < by)
                {
                    yield return null;
                    frames++;
                }

                // Cut short at the deadline: nothing more is registered from here (the run goes to the ending; CityWalkRegistrationCutShort).
                if (all.Any(Open))
                {
                    PlayableCityCuttable.Held = true;
                    _cwRegistrationCutShort = true;
                }

                Log("city walk registration: the stage took " + (Time.realtimeSinceStartup - from).ToString("F2", Inv) + " s over " + frames + " frames; registrations ended "
                    + all.Count(c => c.IsCutTarget || c.Failure != null) + " (refused " + all.Count(c => c.Failure != null) + "); deadline " + by.ToString("F2", Inv) + " s (process)");

                if (!_cwRegistrationCutShort) yield return CityWalkViews("after");

                int buildings = all.Count(c => c.building), props = all.Length - buildings;
                int registeredBuildings = all.Count(c => c.building && c.IsCutTarget), registeredProps = all.Count(c => !c.building && c.IsCutTarget);
                int scaled = all.Count(c => c.IsCutTarget && c.RegisteredScale != 1f);
                List<PlayableCityCuttable> refused = all.Where(c => c.Failure != null).ToList();
                List<PlayableCityCuttable> open = all.Where(Open).ToList();
                List<PlayableCityCuttable> idle = all.Where(c => c != null && !c.IsCutTarget && c.Failure == null && !c.isActiveAndEnabled).ToList();
                Log("city walk registration: placed cuttables " + all.Length + " (buildings " + buildings + ", props " + props + "); registered buildings " + registeredBuildings + ", props " + registeredProps
                    + " (at a uniform scale other than 1: " + scaled + "); refused " + refused.Count + ", still waiting " + open.Count + ", never run (inactive or disabled) " + idle.Count
                    + "; hull groups " + (_world.Hulls != null ? _world.Hulls.GroupCount : 0) + "; storage: " + _world.Storage.DescribeRoom());
                Log("city walk placed search (after the registrations): placement told for " + all.Count(c => c.PlacementTold) + " of " + all.Count(c => c.Candidate != null) + " deferred cut targets; " + CityWalkPlacedSearch());
                Log("city walk display collection (after the registrations): " + CityWalkDisplayCollection());
                Log("city walk fragment reach (after the registrations): " + CityWalkFragmentReach());
                foreach (PlayableCityCuttable c in refused) Log("city walk NOT registered: " + c.gameObject.name + ": " + c.Failure);
                foreach (PlayableCityCuttable c in open) Log("city walk NOT registered: " + c.gameObject.name + ": still waiting at the deadline");
                foreach (PlayableCityCuttable c in idle) Log("city walk NOT registered: " + c.gameObject.name + ": its registrar never ran (inactive or disabled)");
                StageEnd(open.Count > 0, open.Count > 0 ? open.Count + " still waiting" : registeredBuildings + registeredProps + " registered, " + refused.Count + " refused");
                Expect(refused.Count == 0 && open.Count == 0 && idle.Count == 0,
                    "[city walk] every placed cuttable the scene builder took was registered (" + (registeredBuildings + registeredProps) + " of " + all.Length + "; refused " + refused.Count + ", waiting " + open.Count + ", never run " + idle.Count + ")");
            }

            // The step script's account (2026-10-03): every visit reached or not, and each step's record (mobplan-steps.csv).
            private void CityWalkStepsSummary()
            {
                foreach ((string visit, bool ended, string detail) v in _mpSteps.Visits()) Log("city walk visit " + v.visit + ": " + (v.ended ? "done, " : "NOT done, ") + v.detail);
                try
                {
                    using (var w = new StreamWriter(Path.Combine(directory, "mobplan-steps.csv")))
                    {
                        w.WriteLine("index,line,visit,kind,targetX,targetZ,yaw,start,rows,deadline,begunAt,endedAt,outcome,endX,endZ,endYaw");
                        for (int i = 0; i < _mpSteps.Steps.Count; i++)
                        {
                            CityWalkSteps.Step t = _mpSteps.Steps[i];
                            w.WriteLine(string.Join(",", i, t.line, t.visit, t.kind, t.target.x.ToString("F3", Inv), t.target.y.ToString("F3", Inv), t.yaw.ToString("F2", Inv), t.start, t.rows,
                                t.deadline.ToString("R", Inv), t.begunAt.ToString("F3", Inv), t.endedAt.ToString("F3", Inv), (t.outcome ?? "").Replace(",", ";"),
                                t.endedAtPosition.x.ToString("F3", Inv), t.endedAtPosition.y.ToString("F3", Inv), t.endedAtYaw.ToString("F2", Inv)));
                        }
                    }
                }
                catch (IOException e)
                {
                    Log("city walk steps file: " + e.Message);
                }
            }

            private void CityWalkBegin()
            {
                if (!cityWalk) return;
                _cwRealAtScriptStart = Time.realtimeSinceStartupAsDouble;
                if (Has(CityWalkWalkShotsArgument)) StartCoroutine(CityWalkWalkShots(Value(CityWalkWalkShotsArgument)));
                StartCoroutine(CityWalkSkinningWatch());
            }

            private bool _cwRegistrationCutShort;

            // The scene detector's placed search (DESIGN 19.1.7, D-192), for the log: how many placed cut targets it finds by
            // their box in its index and how many it reads every update, and what it has read and searched so far.
            private static string CityWalkPlacedSearch()
            {
                SandboxSlashPropHit hit = Object.FindAnyObjectByType<SandboxSlashPropHit>();
                SlashHitDetector d = hit != null ? hit.Detector : null;
                if (d == null) return "no detector";
                return "placed candidates " + d.PlacedCount + ": in the index " + d.PlacedIndexedCount + ", read every update " + d.PlacedReadEveryUpdateCount
                    + "; so far state reads " + d.PlacedStateReads + ", listed " + d.PlacedListed + ", passed over by the box " + d.PlacedPassedOver
                    + "; index builds " + d.PlacedIndexBuilds + " (nodes " + d.PlacedIndexNodes + ", the last " + (d.PlacedIndexLastBuildSeconds * 1e6).ToString("F0", Inv) + " us, all "
                    + (d.PlacedIndexBuildSecondsInAll * 1e6).ToString("F0", Inv) + " us), searches " + d.PlacedIndexSearches
                    + ", nodes visited " + d.PlacedIndexNodesVisited + ", entries tested " + d.PlacedIndexEntriesTested + ", found " + d.PlacedIndexEntriesFound
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    + "; time in the searches (their builds inside) " + (d.PlacedSearchSeconds * 1e6).ToString("F0", Inv) + " us; told boxes made again in " + d.PlacedToldUpdates + " updates, "
                    + (d.PlacedToldSeconds * 1e6).ToString("F0", Inv) + " us"
#endif
                    + "; found moved untold " + d.PlacedMovedUntold;
            }

            // The display's collections (DESIGN 5.6, D-194), for the log: what they have read, assembled, written and sent
            // since the display was made. An update is one sending (the arguments: two buffers; the transforms and the
            // clips: two buffers, the same range); the SetData calls are the buffer writes themselves.
            private string CityWalkDisplayCollection()
            {
                if (_world == null || _world.Display == null || _world.Display.IsDisposed) return "no display";
                VpLogicalCutDisplay d = _world.Display;
                long argumentBytes = 2L * Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<GraphicsBuffer.IndirectDrawIndexedArgs>();
                long instanceBytes = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<Matrix4x4>()
                    + (long)Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<Zantetsu.Rendering.VpInstanceClip>();
                return "collections " + d.SettledCollections + "; now shown " + d.ShownCount + ", render fragments " + d.RenderFragmentCount + ", instances " + d.SideCount
                    + "; so far ledger state reads " + d.LedgerStateReads + ", registration lists " + d.RegistrationListBuilds + ", structures kept whole " + d.StructuresKeptWhole
                    + ", structure walks " + d.StructureWalks + ", draw arrangements " + d.DrawArrangements + ", draw data assemblies " + d.CandidateAssemblies
                    + ", instance records written " + d.InstanceRecordsWritten + ", given from the other side " + d.InstanceRecordsCaughtUp
                    + "; sent: arguments " + d.BodyArgumentTransfers + " updates, " + d.BodyArgumentSetDataCalls + " SetData calls, " + d.BodyArgumentElementsTransferred + " commands, "
                    + (d.BodyArgumentElementsTransferred * argumentBytes) + " bytes; transforms and clips " + d.BodyInstanceTransfers + " updates, " + d.BodyInstanceSetDataCalls + " SetData calls, "
                    + d.BodyInstanceElementsTransferred + " records, " + (d.BodyInstanceElementsTransferred * instanceBytes) + " bytes; cap normals " + d.CapNormalTransfers + " SetData calls, "
                    + d.CapNormalVerticesTransferred + " vertices, " + (d.CapNormalVerticesTransferred * 16L) + " bytes (made on the CPU " + d.CapNormalsMade + ")";
            }

            // Unity's skinning of the uncut characters (DESIGN 9, D-196), for the log. The counts are of this check's own
            // frames; the times and calls are the Profiler's recorders of Unity's own markers, each frame's read in the next.
            private long _cwSkinFrames, _cwSkinLive, _cwSkinDrawn, _cwSkinVisible, _cwSkinOnlyWhenSeen, _cwSkinVisibleOnlyWhenSeen;
            private long _cwSkinBoundsSamples, _cwSkinBoundsOutside;
            private float _cwSkinBoundsLargest = float.NegativeInfinity;
            private string _cwSkinBoundsWorst = "";
            private static readonly string[] k_cwSkinMarkers = { "MeshSkinning.CalcMatrices", "MeshSkinning.Update", "PostLateUpdate.UpdateAllSkinnedMeshes", "MeshSkinning.SkinOnGPU" };
            private readonly Unity.Profiling.ProfilerRecorder[] _cwSkinRecorders = new Unity.Profiling.ProfilerRecorder[k_cwSkinMarkers.Length];
            private readonly double[] _cwSkinNanoseconds = new double[k_cwSkinMarkers.Length];
            private readonly long[] _cwSkinCalls = new long[k_cwSkinMarkers.Length];
            private readonly long[] _cwSkinFramesWithCalls = new long[k_cwSkinMarkers.Length];

            private IEnumerator CityWalkSkinningWatch()
            {
                SandboxNpcCharacter[] slots = Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                var handles = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
                Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
                foreach (Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle handle in handles)
                {
                    int m = System.Array.IndexOf(k_cwSkinMarkers, Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(handle).Name);
                    if (m >= 0 && !_cwSkinRecorders[m].Valid)
                    {
                        _cwSkinRecorders[m] = new Unity.Profiling.ProfilerRecorder(handle, 1, Unity.Profiling.ProfilerRecorderOptions.Default);
                        _cwSkinRecorders[m].Start();
                    }
                }

                while (true)
                {
                    for (int m = 0; m < k_cwSkinMarkers.Length; m++)
                    {
                        if (!_cwSkinRecorders[m].Valid || _cwSkinRecorders[m].Count == 0) continue;
                        Unity.Profiling.ProfilerRecorderSample sample = _cwSkinRecorders[m].GetSample(_cwSkinRecorders[m].Count - 1);
                        _cwSkinNanoseconds[m] += sample.Value;
                        _cwSkinCalls[m] += sample.Count;
                        if (sample.Count > 0) _cwSkinFramesWithCalls[m]++;
                    }

                    _cwSkinFrames++;
                    for (int i = 0; i < slots.Length; i++)
                    {
                        SandboxNpcCharacter c = slots[i];
                        if (c == null || !c.IsTarget) continue;
                        _cwSkinLive++;
                        SkinnedMeshRenderer r = c.Renderer;
                        if (r == null || !r.enabled) continue;
                        _cwSkinDrawn++;
                        bool visible = r.isVisible;
                        if (visible) _cwSkinVisible++;
                        if (c.SkinsOnlyWhenSeen)
                        {
                            _cwSkinOnlyWhenSeen++;
                            if (visible) _cwSkinVisibleOnlyWhenSeen++;
                        }

                        if ((Time.frameCount + i) % 8 == 0 && c.TryDrawBoundsExcess(out float excess))
                        {
                            _cwSkinBoundsSamples++;
                            if (excess > 0f) _cwSkinBoundsOutside++;
                            if (excess > _cwSkinBoundsLargest)
                            {
                                _cwSkinBoundsLargest = excess;
                                _cwSkinBoundsWorst = c.name;
                            }
                        }
                    }

                    yield return null;
                }
            }

            private string CityWalkSkinning()
            {
                var text = new System.Text.StringBuilder();
                text.Append("skinned when unseen: ").Append(SandboxNpcCharacter.SkinWhenUnseen ? "kept (every frame)" : "skipped")
                    .Append("; frames watched ").Append(_cwSkinFrames).Append("; characters, summed over those frames: live ").Append(_cwSkinLive).Append(", drawn (renderer enabled) ").Append(_cwSkinDrawn)
                    .Append(", visible to Unity (a camera or a shadow pass) ").Append(_cwSkinVisible).Append(", drawn and not visible ").Append(_cwSkinDrawn - _cwSkinVisible)
                    .Append(", skinned only when seen ").Append(_cwSkinOnlyWhenSeen).Append(" (of them visible ").Append(_cwSkinVisibleOnlyWhenSeen).Append(")");
                for (int m = 0; m < k_cwSkinMarkers.Length; m++)
                {
                    text.Append("; ").Append(k_cwSkinMarkers[m]).Append(": ");
                    if (!_cwSkinRecorders[m].Valid) text.Append("no recorder");
                    else text.Append((_cwSkinNanoseconds[m] / 1e6).ToString("F2", Inv)).Append(" ms in all, ").Append(_cwSkinCalls[m]).Append(" calls, frames with a call ").Append(_cwSkinFramesWithCalls[m]);
                    if (_cwSkinRecorders[m].Valid) _cwSkinRecorders[m].Dispose();
                }

                text.Append("; Unity's bounds against the draw bounds (characters skinned every frame, one in eight a frame): samples ").Append(_cwSkinBoundsSamples)
                    .Append(", outside ").Append(_cwSkinBoundsOutside);
                if (_cwSkinBoundsSamples > 0)
                {
                    text.Append(", the farthest ").Append(_cwSkinBoundsLargest.ToString("F4", Inv)).Append(" m (").Append(_cwSkinBoundsWorst).Append("; below zero: inside)");
                }

                return text.ToString();
            }

            // The scene detector's fragment reach (DESIGN 19.1.7, D-193), for the log: of the fragment shapes it went through
            // for its sweeps, how many it passed over by their reach before reading their frame.
            private static string CityWalkFragmentReach()
            {
                SandboxSlashPropHit hit = Object.FindAnyObjectByType<SandboxSlashPropHit>();
                SlashHitDetector d = hit != null ? hit.Detector : null;
                if (d == null) return "no detector";
                return "fragment shapes gone through (a shape a sweep) " + d.FragmentsVisited + ", positions read " + d.FragmentPositionsRead + ", passed over by their reach " + d.FragmentsBeyondReach
                    + ", frames read " + d.FragmentFramesRead;
            }

            /// <summary>
            /// A city walk whose registration stage reached its deadline (TL, 2026-10-03: the scenario's own path): no script;
            /// the registrars held (nothing more registered), then the ordinary ending -- the world's reclaim, bounded -- and the
            /// run's completion, a failure (the registration's own expectation is not met).
            /// </summary>
            private IEnumerator CityWalkEndRegistrationCutShort()
            {
                PlayableCityCuttable[] all = Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                var ending = new CheckEnding(Log);
                StageBegin("ending (the registration cut short: the world's reclaim)", 15f);
                yield return CityWalkRegistrationCutShort(_world, ending, all, Log);
                StageEnd(!ending.WorldReleased, "world released " + ending.WorldReleased + ", ending failures " + ending.Failures);
                _failures += ending.Failures;
                yield return null;
                Finish(ending, null);
            }

            /// <summary>Pictures while walking (2026-10-03): script seconds "t1,t2,..."; each through a camera made for it alone.</summary>
            public const string CityWalkWalkShotsArgument = "-zantetsuCityWalkWalkShots";

            /// <summary>
            /// A few pictures while walking: at each asked script time, a capture camera following the head (3 m behind it, 1 m
            /// up, pitched 10 degrees down; the check's CheckCaptureCamera, so the cut world draws into it) made for the one
            /// picture, rendered two frames, read and given back. The frames it lived in -- its own rendering and the read --
            /// are written to walk-shot-frames.csv (first and last frame, real time), so a performance reading leaves them out.
            /// </summary>
            private IEnumerator CityWalkWalkShots(string spec)
            {
                List<float> times = spec.Split(',').Select(x => float.Parse(x, Inv)).OrderBy(t => t).ToList();
                using (var frames = new StreamWriter(Path.Combine(directory, "walk-shot-frames.csv")) { AutoFlush = true })
                {
                    frames.WriteLine("name,scriptSeconds,firstFrame,lastFrame,firstReal,lastReal");
                    foreach (float t in times)
                    {
                        while (_replaying && MobPlanNow < t) yield return null;
                        if (!_replaying || _world == null || _world.IsEnding) yield break;
                        string name = "walk-" + t.ToString("F1", Inv);
                        int first = Time.frameCount;
                        double firstReal = Time.realtimeSinceStartupAsDouble;
                        var capture = new CheckCaptureCamera(_world, Camera.main, null, 3f, 1f, -10f, 60f, 1280, 720);
                        var pixels = new Texture2D(capture.width, capture.height, TextureFormat.RGB24, false);
                        try
                        {
                            yield return null;
                            yield return null;
                            yield return new WaitForEndOfFrame();
                            RenderTexture active = RenderTexture.active;
                            RenderTexture.active = capture.Target;
                            pixels.ReadPixels(new Rect(0, 0, capture.width, capture.height), 0, 0);
                            pixels.Apply();
                            RenderTexture.active = active;
                            File.WriteAllBytes(Path.Combine(directory, name + ".png"), pixels.EncodeToPNG());
                            Log("city walk walk shot " + name + ": script seconds " + MobPlanNow.ToString("F2", Inv) + ", renders " + capture.Rendered + ", taken by the display " + capture.TakenByDisplay
                                + ", listed with the camera drawing " + capture.ListedWithDrawing + ", head " + Camera.main.transform.position.ToString("F2") + ", frames " + first + ".." + Time.frameCount);
                        }
                        finally
                        {
                            capture.Dispose();
                            Object.Destroy(pixels);
                        }

                        frames.WriteLine(name + "," + t.ToString("R", Inv) + "," + first + "," + (Time.frameCount + 1) + "," + firstReal.ToString("F4", Inv) + "," + Time.realtimeSinceStartupAsDouble.ToString("F4", Inv));
                    }
                }
            }

            /// <summary>
            /// Pictures before and after each visit's Slashes (TL, 2026-10-03: the cut's drawing, before and after, through a
            /// camera the display draws for). Before the first Slash of a visit -- the player arrived and faced, the Slash held
            /// back (CityWalkSteps.HoldBeforeSlash) -- a pose is fixed 3 m behind the head and 1.2 m up, looking 3 m ahead and
            /// 0.6 m below it, and the picture taken there; HitShotAfterSeconds after the visit's last Slash is fed, the picture
            /// again from the same pose. Each through a CheckCaptureCamera of its own (one fixed shot), rendered two frames, read,
            /// given back: hit-&lt;visit&gt;-before.png / -after.png. A picture whose camera the display did not draw for is
            /// INVALID, said so. The frames each lived in go to hit-shot-frames.csv, so a performance reading leaves them out.
            /// </summary>
            public const string CityWalkHitShotsArgument = "-zantetsuCityWalkHitShots";
            private const double HitShotAfterSeconds = 2.0;
            private readonly HashSet<string> _cwBeforeTaken = new HashSet<string>(), _cwAfterTaken = new HashSet<string>();
            private readonly Dictionary<string, CheckCaptureCamera.Shot> _cwHitPose = new Dictionary<string, CheckCaptureCamera.Shot>();
            private readonly List<string> _cwHitShotsInvalid = new List<string>();
            private int _cwHitShotsValid;
            private bool _cwShotBusy;
            private StreamWriter _cwHitFrames;

            // Held before the first Slash of a visit until its picture is taken.
            private bool CityWalkHoldBeforeSlash(CityWalkSteps.Step s)
            {
                int i = _mpSteps.Current;
                bool first = i == 0 || _mpSteps.Steps[i - 1].kind != CityWalkSteps.Kind.Slash || _mpSteps.Steps[i - 1].visit != s.visit;
                if (!first) return false;
                if (_cwBeforeTaken.Contains(s.visit)) return _cwShotBusy;
                if (_cwShotBusy) return true;   // the previous visit's picture after is being taken
                if (_world == null || _world.IsEnding) return false;
                Transform head = Camera.main != null ? Camera.main.transform : _mpInput.player.transform;
                Vector3 f = head.forward;
                f.y = 0f;
                f = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
                var pose = new CheckCaptureCamera.Shot { name = s.visit, position = head.position - f * 3f + Vector3.up * 1.2f, lookAt = head.position + f * 3f - Vector3.up * 0.6f, fieldOfView = 70f };
                _cwHitPose[s.visit] = pose;
                _cwBeforeTaken.Add(s.visit);
                StartCoroutine(CityWalkHitShot(pose, "before"));
                return true;
            }

            // After each visit pictured before: once its Slash steps all ended (fed, or the walk halted) and HitShotAfterSeconds passed.
            private void CityWalkHitShotsAfter(double now)
            {
                if (_mpSteps == null || _cwShotBusy || _world == null || _world.IsEnding) return;
                foreach (string visit in _cwBeforeTaken)
                {
                    if (_cwAfterTaken.Contains(visit)) continue;
                    List<CityWalkSteps.Step> slashes = _mpSteps.Steps.Where(st => st.visit == visit && st.kind == CityWalkSteps.Kind.Slash).ToList();
                    if (slashes.Any(st => st.outcome == null) && !_mpSteps.Done) continue;
                    double last = slashes.Where(st => !double.IsNaN(st.endedAt)).Select(st => st.endedAt).DefaultIfEmpty(now).Max();
                    if (now < last + HitShotAfterSeconds) continue;
                    _cwAfterTaken.Add(visit);
                    StartCoroutine(CityWalkHitShot(_cwHitPose[visit], "after"));
                    return;
                }
            }

            private IEnumerator CityWalkHitShot(CheckCaptureCamera.Shot pose, string when)
            {
                _cwShotBusy = true;
                string name = "hit-" + pose.name + "-" + when;
                int first = Time.frameCount;
                double firstReal = Time.realtimeSinceStartupAsDouble;
                CheckCaptureCamera capture = null;
                Texture2D pixels = null;
                try
                {
                    if (_cwHitFrames == null)
                    {
                        _cwHitFrames = new StreamWriter(Path.Combine(directory, "hit-shot-frames.csv")) { AutoFlush = true };
                        _cwHitFrames.WriteLine("name,scriptSeconds,firstFrame,lastFrame,firstReal,lastReal,takenByDisplay");
                    }

                    capture = new CheckCaptureCamera(_world, Camera.main, null, 0f, 0f, 0f, pose.fieldOfView, 1280, 720, new[] { pose });
                    pixels = new Texture2D(capture.width, capture.height, TextureFormat.RGB24, false);
                    yield return null;
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    RenderTexture active = RenderTexture.active;
                    RenderTexture.active = capture.Target;
                    pixels.ReadPixels(new Rect(0, 0, capture.width, capture.height), 0, 0);
                    pixels.Apply();
                    RenderTexture.active = active;
                    File.WriteAllBytes(Path.Combine(directory, name + ".png"), pixels.EncodeToPNG());
                    bool valid = capture.TakenByDisplay && capture.Rendered > 0;
                    if (valid) _cwHitShotsValid++;
                    else _cwHitShotsInvalid.Add(name);
                    Log("city walk hit shot " + name + ": script seconds " + MobPlanNow.ToString("F2", Inv) + ", renders " + capture.Rendered + ", taken by the display " + capture.TakenByDisplay
                        + ", listed with the camera drawing " + capture.ListedWithDrawing + ", slots waiting to be given back " + CheckCaptureCamera.PendingUnregistrations + ", camera " + pose.position.ToString("F2")
                        + " looking at " + pose.lookAt.ToString("F2") + ", frames " + first + ".." + Time.frameCount + (valid ? "" : ": INVALID (the display did not draw for its camera)"));
                    _cwHitFrames.WriteLine(name + "," + MobPlanNow.ToString("R", Inv) + "," + first + "," + (Time.frameCount + 1) + "," + firstReal.ToString("F4", Inv) + "," + Time.realtimeSinceStartupAsDouble.ToString("F4", Inv) + "," + valid);
                }
                finally
                {
                    capture?.Dispose();
                    if (pixels != null) Object.Destroy(pixels);
                    _cwShotBusy = false;
                }
            }

            /// <summary>
            /// After the script (a city walk): the pictures after still owed taken (bounded, 10 s), then the views again as the
            /// walk left the city ("end").
            /// </summary>
            private IEnumerator CityWalkAfterScript()
            {
                if (_mpSteps != null && _mpSteps.HoldBeforeSlash != null)
                {
                    float by = Time.realtimeSinceStartup + 10f;
                    while ((_cwShotBusy || _cwAfterTaken.Count < _cwBeforeTaken.Count) && Time.realtimeSinceStartup < by && _world != null && !_world.IsEnding)
                    {
                        CityWalkHitShotsAfter(double.MaxValue);
                        yield return null;
                    }

                    Log("city walk hit shots: before " + _cwBeforeTaken.Count + ", after " + _cwAfterTaken.Count + ", valid " + _cwHitShotsValid + ", INVALID " + _cwHitShotsInvalid.Count
                        + (_cwHitShotsInvalid.Count > 0 ? " (" + string.Join(" ", _cwHitShotsInvalid) + ")" : "") + "; slots waiting to be given back " + CheckCaptureCamera.PendingUnregistrations);
                    _cwHitFrames?.Dispose();
                    _cwHitFrames = null;
                    // Each visit against its pictures (TL, 2026-10-03): a visit with a Slash is pictured before and after it;
                    // one without (a "via" leg) has none by design; one not reached, or whose picture was not taken, said so.
                    var visits = _mpSteps.Visits().ToList();
                    foreach ((string visit, bool ended, string detail) v in visits)
                    {
                        int slashes = _mpSteps.Steps.Count(st => st.visit == v.visit && st.kind == CityWalkSteps.Kind.Slash);
                        string pictures = slashes == 0 ? "no Slash, so no picture (by design)"
                            : (_cwBeforeTaken.Contains(v.visit) ? "before " + (_cwHitShotsInvalid.Contains("hit-" + v.visit + "-before") ? "INVALID" : "taken") : "before NOT taken")
                              + ", " + (_cwAfterTaken.Contains(v.visit) ? "after " + (_cwHitShotsInvalid.Contains("hit-" + v.visit + "-after") ? "INVALID" : "taken") : "after NOT taken");
                        Log("city walk visit pictures " + v.visit + ": " + slashes + " Slashes; " + (v.ended ? "done" : v.detail) + "; " + pictures);
                    }

                    int withSlash = visits.Count(v => _mpSteps.Steps.Any(st => st.visit == v.visit && st.kind == CityWalkSteps.Kind.Slash));
                    Log("city walk visit pictures: " + visits.Count + " visits, " + withSlash + " with a Slash (" + 2 * withSlash + " pictures owed), " + (visits.Count - withSlash)
                        + " without (no picture by design); taken before " + _cwBeforeTaken.Count + ", after " + _cwAfterTaken.Count + ", INVALID " + _cwHitShotsInvalid.Count);
                }

                if (_world != null && !_world.IsEnding) yield return CityWalkViews("end");
            }

            // The VRS's order against the check's readiness (TL, 2026-10-03: the start delay does not ensure it): the XR
            // camera's local pose watched every frame from the check's start; its first move past the head wait's threshold
            // (or 0.005 m) or 0.5 degrees from its first pose, with the stage it fell in. At ready: a move already seen means
            // the replay began before the check was ready -- the synchronisation is not a success (a failure of the run).
            private float _cwHeadMovedAt = float.NaN;
            private int _cwHeadMovedFrame = -1;
            private string _cwHeadMovedStage;

            private IEnumerator CityWalkHeadOrderWatch()
            {
                float threshold = startOnHeadMove > 0f ? startOnHeadMove : 0.005f;
                Log("city walk VRS order: clock pair real " + Time.realtimeSinceStartupAsDouble.ToString("F4", Inv) + " s = wall " + System.DateTime.Now.ToString("HH:mm:ss.fff", Inv)
                    + "; the XR camera's local pose watched every frame from frame " + Time.frameCount + " (a move past " + threshold.ToString("R", Inv) + " m or 0.5 deg from its first pose)");
                bool seen = false;
                Vector3 p0 = Vector3.zero;
                Quaternion r0 = Quaternion.identity;
                while (_world == null || !_world.IsEnding)
                {
                    Camera head = Camera.main;
                    if (head != null)
                    {
                        Transform h = head.transform;
                        if (!seen)
                        {
                            // The first pose is the tracked one (w2, 2026-10-03: the camera stood at the origin until the
                            // tracking's first pose -- the Simulator's resting head at 1.7 m -- was applied a frame later,
                            // which read as a move): the head device tracked and its pose on the camera.
                            UnityEngine.XR.InputDevice device = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
                            bool tracked = device.isValid && device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked, out bool t) && t;
                            if (tracked && (h.localPosition != Vector3.zero || h.localRotation != Quaternion.identity))
                            {
                                seen = true;
                                p0 = h.localPosition;
                                r0 = h.localRotation;
                                Log("city walk VRS order: the head's first tracked pose at frame " + Time.frameCount + " real " + Time.realtimeSinceStartup.ToString("F2", Inv) + " s: local " + p0.ToString("F4") + " " + r0.eulerAngles.ToString("F2"));
                            }
                        }
                        else if (Vector3.Distance(h.localPosition, p0) > threshold || Quaternion.Angle(h.localRotation, r0) > 0.5f)
                        {
                            _cwHeadMovedAt = Time.realtimeSinceStartup;
                            _cwHeadMovedFrame = Time.frameCount;
                            _cwHeadMovedStage = _cwStage ?? _phase;
                            StageLine("VRS order: the replayed head first moved at " + _cwHeadMovedAt.ToString("F2", Inv) + " s (process, frame " + _cwHeadMovedFrame + ", wall " + System.DateTime.Now.ToString("HH:mm:ss.fff", Inv)
                                + ") during " + _cwHeadMovedStage + ": local " + h.localPosition.ToString("F4") + " " + h.localRotation.eulerAngles.ToString("F2"));
                            yield break;
                        }
                    }

                    yield return null;
                }
            }

            /// <summary>At ready (the registration done): whether the replayed head had already moved -- the VRS began first.</summary>
            private bool CityWalkVrsBeganBeforeReady()
            {
                bool before = !float.IsNaN(_cwHeadMovedAt);
                StageLine("VRS order: ready at " + Time.realtimeSinceStartup.ToString("F2", Inv) + " s (process, frame " + Time.frameCount + ", wall " + System.DateTime.Now.ToString("HH:mm:ss.fff", Inv) + "); "
                    + (before ? "the head had moved already at " + _cwHeadMovedAt.ToString("F2", Inv) + " s during " + _cwHeadMovedStage + ": the VRS began before ready, NOT a synchronisation"
                              : "the head has not moved yet: the VRS begins after ready"));
                return before;
            }

            /// <summary>The city walk's own account: what was cut, by kind and by name, the re-cuts, the walk against the script's.</summary>
            private void CityWalkSummary()
            {
                if (!cityWalk) return;
                var names = new Dictionary<int, string>();   // hull building -> the placed cuttable's name
                foreach (PlayableCityCuttable c in _pcCuttables)
                {
                    if (c != null && c.IsRegistered && c.Registration.Group != null) names[c.Registration.Group.Building] = c.gameObject.name;
                }

                // Buildings: the hull trial's published hits, by building, with the script's time of each.
                var buildingCuts = new SortedDictionary<string, List<double>>();
                var groupBuilding = new Dictionary<int, int>();
                if (_world.Hulls != null)
                {
                    foreach (HullGroup g in _world.Hulls.Groups) groupBuilding[g.Id] = g.Building;
                    foreach (BuildingHullFusion.HullHit hit in _world.Hulls.Hits)
                    {
                        if (hit.outcome != "Published") continue;
                        string name = groupBuilding.TryGetValue(hit.group, out int b) && names.TryGetValue(b, out string n) ? n : "group " + hit.group;
                        if (!buildingCuts.TryGetValue(name, out List<double> at)) buildingCuts[name] = at = new List<double>();
                        at.Add(hit.askedAt - _cwRealAtScriptStart);
                    }
                }

                // Props and NPCs: the accepted cuts committed, roots and children apart, by lineage.
                List<Accepted> committed = _accepted.Where(a => a.committedFrame >= 0).ToList();
                var propRoots = committed.Where(a => !a.child && KindOf(LineageOf(a.fragment)) == "prop").GroupBy(a => LineageOf(a.fragment)).OrderBy(g => g.Key).ToList();
                var propChildren = committed.Where(a => a.child && KindOf(LineageOf(a.fragment)) == "prop").GroupBy(a => LineageOf(a.fragment)).OrderBy(g => g.Key).ToList();
                var npcRoots = committed.Where(a => !a.child && KindOf(LineageOf(a.fragment)) == "npc").GroupBy(a => LineageOf(a.fragment)).OrderBy(g => g.Key).ToList();
                var npcChildren = committed.Where(a => a.child && KindOf(LineageOf(a.fragment)) == "npc").GroupBy(a => LineageOf(a.fragment)).OrderBy(g => g.Key).ToList();
                string Times(IEnumerable<double> ts) => string.Join(" ", ts.Select(t => double.IsNaN(t) ? "?" : t.ToString("F1", Inv) + "s"));
                foreach (KeyValuePair<string, List<double>> b in buildingCuts) Log("city walk cut building " + b.Key + ": " + b.Value.Count + " cuts at " + Times(b.Value));
                foreach (var g in propRoots) Log("city walk cut prop " + g.Key + ": root cut at " + Times(g.Select(a => a.acceptedTime)) + "; its pieces cut again " + (propChildren.FirstOrDefault(c => c.Key == g.Key)?.Count() ?? 0));
                foreach (var g in npcRoots) Log("city walk cut npc " + g.Key + ": at " + Times(g.Select(a => a.acceptedTime)) + "; its pieces cut again " + (npcChildren.FirstOrDefault(c => c.Key == g.Key)?.Count() ?? 0));
                int buildingRecuts = buildingCuts.Count(b => b.Value.Count >= 2);
                int replacementsCut = npcRoots.Count(g => g.Key.StartsWith("npc-r", System.StringComparison.Ordinal));

                // The walk: the script's own distance (forward x speed x span, or the step script's legs) against the player's.
                float speed = _mpInput != null ? _mpInput.speed : 0f;
                double planned = _mpSteps != null ? _mpSteps.PlannedMetres : _mpMoves.Sum(m => System.Math.Max(0f, m.forward) * speed * (System.Math.Min(m.to, _mpEnd) - m.from));
                int slashesPlanned = _mpSteps != null ? _mpSteps.SlashSteps : _mpSlashes.Count;
                // The chunks the attacks on NPCs began are not the plan's Slashes.
                int attackChunks = _mpSteps != null ? _mpSteps.Engagements.Count(e => !double.IsNaN(e.chunkAt)) : 0;
                int plannedBegun = _mpChunk + 1 - attackChunks;
                if (_mpSteps != null) CityWalkStepsSummary();
                CityWalkEngagementSummary();
                Log("city walk summary: buildings cut " + buildingCuts.Count + " (cuts " + buildingCuts.Sum(b => b.Value.Count) + ", cut again " + buildingRecuts + "), props cut " + propRoots.Count
                    + " (their pieces cut again " + propChildren.Sum(g => g.Count()) + "), NPCs cut " + npcRoots.Count + " (replacements among them " + replacementsCut + "; their pieces cut again " + npcChildren.Sum(g => g.Count())
                    + "); walked " + _mpTravel.ToString("F1", Inv) + " m (the script's own " + planned.ToString("F1", Inv) + " m"
                    + (_mpSteps != null ? " from the walk's start " + _mpSteps.PlannedFrom.ToString("F2") : "") + " at " + speed.ToString("R", Inv) + " m/s); script seconds " + MobPlanNow.ToString("F1", Inv)
                    + " (end " + _mpEnd.ToString("R", Inv) + "); slashes of the plan begun " + plannedBegun + " of " + slashesPlanned + (attackChunks > 0 ? " (and " + attackChunks + " attacks on NPCs)" : "") + "; process " + Time.realtimeSinceStartup.ToString("F1", Inv) + " s of the budget " + cityWalkBudget.ToString("R", Inv) + " s");
                Log("city walk placed search (the run): " + CityWalkPlacedSearch());
                Log("city walk display collection (the run): " + CityWalkDisplayCollection());
                Log("city walk fragment reach (the run): " + CityWalkFragmentReach());
                Log("city walk skinning (the run): " + CityWalkSkinning());
                Expect(buildingCuts.Count > 0, "[city walk] a building was cut by the katana's Slash (" + buildingCuts.Count + " buildings)");
                Expect(propRoots.Count > 0, "[city walk] a prop was cut by the katana's Slash and its geometry committed (" + propRoots.Count + " props)");
                Expect(npcRoots.Count > 0, "[city walk] an NPC was cut by the katana's Slash and its geometry committed (" + npcRoots.Count + " NPCs)");
                Expect(buildingRecuts > 0 || propChildren.Count > 0 || npcChildren.Count > 0, "[city walk] a building or a piece was cut again (buildings cut again " + buildingRecuts + ", prop pieces " + propChildren.Count + ", NPC pieces " + npcChildren.Count + ")");
                Expect(plannedBegun == slashesPlanned, "[city walk] every Slash of the script was begun (" + plannedBegun + " of " + slashesPlanned + ")");
                if (_mpSteps != null)
                {
                    Expect(!_mpSteps.Halted, "[city walk] every step of the script ended where it was planned, each Slash begun only there (" + (_mpSteps.Halted ? _mpSteps.HaltReason : "all " + _mpSteps.Steps.Count + " steps") + ")");
                }
                Expect(_mpTravel >= 0.8 * planned, "[city walk] the player walked at least 80% of the script's own distance (" + _mpTravel.ToString("F1", Inv) + " of " + planned.ToString("F1", Inv) + " m)");
                Expect(_cwDeadlinesReached.Count == 0, "[city walk] no stage reached its deadline (" + (_cwDeadlinesReached.Count == 0 ? "none" : string.Join("; ", _cwDeadlinesReached)) + ")");
                // The placed cuttables by the city's group (TL, 2026-10-03: building, prop, landscape and vehicle apart): candidates, cut, refused.
                foreach (var family in _pcCuttables.Where(c => c != null && c.Candidate != null).GroupBy(c => CityGroupOf(c.target)).OrderBy(g => g.Key))
                {
                    var touched = family.Where(c => c.Candidate.State != PlacedCuttableCandidate.CandidateState.Candidate).ToList();
                    Log("city walk placed cuttables " + family.Key + ": " + family.Count() + "; cut " + family.Count(c => c.Candidate.State == PlacedCuttableCandidate.CandidateState.Cut)
                        + ", refused " + family.Count(c => c.Candidate.State == PlacedCuttableCandidate.CandidateState.Refused)
                        + (touched.Count > 0 ? " (" + string.Join(" ", touched.Select(c => c.gameObject.name + "=" + c.Candidate.State)) + ")" : ""));
                }

                if (_mpSteps != null && _mpSteps.HoldBeforeSlash != null)
                {
                    Expect(_cwBeforeTaken.Count > 0 && _cwHitShotsInvalid.Count == 0 && _cwAfterTaken.Count == _cwBeforeTaken.Count,
                        "[city walk] a picture before and after each visit's Slashes, every one drawn for by the display (before " + _cwBeforeTaken.Count + ", after " + _cwAfterTaken.Count + ", INVALID " + _cwHitShotsInvalid.Count + ")");
                }
            }

            // The city's group a placed instance stands under (Buildings, Props, Landscape, Vehicle): its ancestor just below the City root.
            private static string CityGroupOf(Transform t)
            {
                if (t == null) return "(none)";
                while (t.parent != null && t.parent.parent != null) t = t.parent;
                return t.parent != null ? t.name : "(outside a group)";
            }
        }
    }
}
