using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Measurement (the MobPlanSlash unit, piece lifetime): the product's piece lifetime (DESIGN 7.10) seen from the
    // MobPlan run -- its switches for a control run (-zantetsuPieceLifetime off, -zantetsuPieceLifetimeThreshold N,
    // -zantetsuPieceLifetimeDistance metres), and frame by frame the target pieces, the retirements, what was looked at,
    // its Main time, and what the world holds: owners, hit shapes, display registrations, geometry references, the DAG's
    // entries and the storage and GPU room (mobplan-lifetime.csv, flushed). Only reads, apart from the switches.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private StreamWriter _mpLifetime;
            private readonly List<CurrentShape> _mpLifetimeShapes = new List<CurrentShape>();
            private int _mpLifetimeTargetMax, _mpLifetimeOwnersMax, _mpLifetimeShownMax;
            private bool _mpLifetimeOn;

            // "-zantetsuEyeHeight <metres>": the view condition of a measurement series. The XR Origin's Camera Offset --
            // the tracking space, which carries the head and the katana alike -- is moved up or down every frame from the
            // replay's start so the eye stands that far above the floor under the player. The replayed head rises some
            // frames after the start and then holds its height, so a single correction at the start is not enough; its
            // horizontal motion and its turns are left as they are.
            private Transform _mpEyeSpace;
            private float _mpEyeWanted = float.NaN;
            private float _mpEyeLow = float.PositiveInfinity, _mpEyeHigh = float.NegativeInfinity;

            private void MobPlanHoldEyeHeight()
            {
                Camera camera = Camera.main;
                if (float.IsNaN(_mpEyeWanted) || _mpEyeSpace == null || camera == null)
                {
                    return;
                }

                float floor = _mpInput.player.transform.position.y;
                float eye = camera.transform.position.y - floor;
                _mpEyeLow = Mathf.Min(_mpEyeLow, eye);
                _mpEyeHigh = Mathf.Max(_mpEyeHigh, eye);
                float delta = _mpEyeWanted - eye;
                if (Mathf.Abs(delta) > 0.0005f)
                {
                    _mpEyeSpace.position += Vector3.up * delta;
                }
            }

            private void MobPlanEyeHeight()
            {
                string[] arguments = Environment.GetCommandLineArgs();
                float wanted = float.NaN;
                for (int i = 0; i + 1 < arguments.Length; i++)
                {
                    if (arguments[i] == "-zantetsuEyeHeight") wanted = float.Parse(arguments[i + 1], CultureInfo.InvariantCulture);
                }

                Transform offset = GameObject.Find("XR Origin")?.transform.Find("Camera Offset");
                Camera camera = Camera.main;
                float floor = _mpInput.player.transform.position.y;
                float eye = camera != null ? camera.transform.position.y - floor : float.NaN;
                string line;
                if (float.IsNaN(wanted) || offset == null || camera == null)
                {
                    line = "mobplan eye height: " + eye.ToString("F3", Inv) + " m above the floor (unchanged)";
                }
                else
                {
                    float delta = wanted - eye;
                    offset.position += Vector3.up * delta;
                    _mpEyeSpace = offset;
                    _mpEyeWanted = wanted;
                    line = "mobplan eye height: " + eye.ToString("F3", Inv) + " m above the floor at the replay's start, set to "
                           + wanted.ToString("F3", Inv) + " m (tracking space moved " + delta.ToString("F3", Inv) + " m; now "
                           + (camera.transform.position.y - floor).ToString("F3", Inv) + " m), held there every frame";
                }

                Log(line);
                MobPlanRecord(line);
            }

            private void MobPlanLifetimeBegin()
            {
                MobPlanEyeHeight();
                CutFragmentLifetime lifetime = _world.Lifetime;
                CutFragmentLifetimeSettings s = lifetime.Settings;
                string[] arguments = Environment.GetCommandLineArgs();
                bool enabled = s.enabled;
                int threshold = s.threshold;
                float distance = s.minDistance;
                for (int i = 0; i + 1 < arguments.Length; i++)
                {
                    if (arguments[i] == "-zantetsuPieceLifetime") enabled = arguments[i + 1] != "off";
                    if (arguments[i] == "-zantetsuPieceLifetimeThreshold") threshold = int.Parse(arguments[i + 1], CultureInfo.InvariantCulture);
                    if (arguments[i] == "-zantetsuPieceLifetimeDistance") distance = float.Parse(arguments[i + 1], CultureInfo.InvariantCulture);
                }

                lifetime.Settings = new CutFragmentLifetimeSettings(
                    enabled, threshold, distance, s.examinePerFrame, s.retirePerFrame, s.minRemainingMainSeconds, s.maxStepSeconds);
                Camera view = lifetime.ViewCamera != null ? lifetime.ViewCamera : Camera.main;
                string line = "mobplan lifetime settings: enabled=" + enabled + " threshold=" + threshold + " distance=" + distance.ToString("F1", Inv)
                              + " examine/frame=" + s.examinePerFrame + " retire/frame=" + s.retirePerFrame
                              + " min remaining=" + (s.minRemainingMainSeconds * 1000.0).ToString("F2", Inv) + " ms max step="
                              + (s.maxStepSeconds * 1000.0).ToString("F2", Inv) + " ms view=" + (view != null ? view.name + (view.stereoEnabled ? " (stereo)" : " (mono)") : "none");
                Log(line);
                MobPlanRecord(line);
                _mpLifetimeOn = true;
                if (!_mpDetail) return;
                _mpLifetime = new StreamWriter(Path.Combine(directory, "mobplan-lifetime.csv")) { AutoFlush = MobPlanLive };
                _mpLifetime.WriteLine("frame,t,owners,targets,retired,refused,examined,candidates,passedUnusable,passedNear,passedSeen,farthestM,budgetSkips,rounds,stepMs,retireMs,maxStepMs,"
                                      + "shown,renderFragments,hitShapes,liveGeometryRefs,liveDisplayInstances,dagTracked,"
                                      + "committedVertexCapacity,committedIndexCapacity,gpuVertexCapacity,gpuIndexCapacity,"
                                      + "freeVertexRoom,vertexGroups,vertexGroupsReleased,verticesReleased");
            }

            private void MobPlanLifetimeFrame(int frame, double t)
            {
                if (!_mpLifetimeOn)
                {
                    return;
                }

                MobPlanHoldEyeHeight();
                CutFragmentLifetime lifetime = _world.Lifetime;
                _mpLifetimeTargetMax = Math.Max(_mpLifetimeTargetMax, lifetime.TargetCount);
                _mpLifetimeOwnersMax = Math.Max(_mpLifetimeOwnersMax, _world.Owners.Count);
                _mpLifetimeShownMax = Math.Max(_mpLifetimeShownMax, _world.Display.ShownCount);
                if (_mpLifetime == null)
                {
                    return;
                }

                _mpLifetimeShapes.Clear();
                _world.Owners.CollectCurrentShapes(_mpLifetimeShapes);
                _mpLifetime.WriteLine(string.Join(",", frame, t.ToString("F4", Inv), _world.Owners.Count, lifetime.TargetCount, lifetime.Retired,
                    lifetime.Refused, lifetime.Examined, lifetime.Candidates, lifetime.PassedUnusable, lifetime.PassedNear, lifetime.PassedSeen,
                    lifetime.FarthestExamined.ToString("F2", Inv), lifetime.BudgetSkips, lifetime.Rounds,
                    (lifetime.StepSeconds * 1000.0).ToString("F4", Inv), (lifetime.RetireSeconds * 1000.0).ToString("F4", Inv),
                    (lifetime.MaxStepSeconds * 1000.0).ToString("F4", Inv), _world.Display.ShownCount, _world.Display.RenderFragmentCount,
                    _mpLifetimeShapes.Count, _world.References.LiveGeometryCount, _world.References.LiveDisplayInstanceCount,
                    _world.Geometry.TrackedFragmentCount, _world.Storage.CommittedVertexCapacity, _world.Storage.CommittedIndexCapacity,
                    _world.Display.GpuVertexCapacity, _world.Display.GpuIndexCapacity, _world.Storage.FreeVertexRoom,
                    _world.Storage.VertexGroupCount, _world.Storage.VertexGroupsReleased, _world.Storage.VerticesReleased));
            }

            private void MobPlanLifetimeEnd()
            {
                CutFragmentLifetime lifetime = _world.Lifetime;
                string line = "mobplan lifetime at the end: targets=" + lifetime.TargetCount + " (max " + _mpLifetimeTargetMax + ") owners="
                              + _world.Owners.Count + " (max " + _mpLifetimeOwnersMax + ") shown=" + _world.Display.ShownCount + " (max "
                              + _mpLifetimeShownMax + ") retired=" + lifetime.Retired + " refused=" + lifetime.Refused
                              + (lifetime.LastRefusal != null ? " (last: " + lifetime.LastRefusal + ")" : "") + " examined=" + lifetime.Examined
                              + " candidates=" + lifetime.Candidates + " passed over: unusable=" + lifetime.PassedUnusable + " near="
                              + lifetime.PassedNear + " seen=" + lifetime.PassedSeen + " farthest examined="
                              + lifetime.FarthestExamined.ToString("F1", Inv) + " m rounds=" + lifetime.Rounds + " budget skips=" + lifetime.BudgetSkips
                              + " steps=" + lifetime.Steps + " step Main " + (lifetime.StepSeconds * 1000.0).ToString("F3", Inv) + " ms (max "
                              + (lifetime.MaxStepSeconds * 1000.0).ToString("F4", Inv) + " ms, retirements "
                              + (lifetime.RetireSeconds * 1000.0).ToString("F3", Inv) + " ms) geometry refs=" + _world.References.LiveGeometryCount
                              + " display instances=" + _world.References.LiveDisplayInstanceCount + " dag tracked=" + _world.Geometry.TrackedFragmentCount
                              + "; " + _world.Storage.DescribeRoom() + "; " + _world.Display.DescribeGpuRoom() + "; " + _world.Display.DescribeRoom();
                Log(line);
                MobPlanRecord(line);
                if (!float.IsNaN(_mpEyeWanted))
                {
                    string eyeLine = "mobplan eye height held at " + _mpEyeWanted.ToString("F3", Inv) + " m: seen before each correction between "
                                     + _mpEyeLow.ToString("F3", Inv) + " and " + _mpEyeHigh.ToString("F3", Inv) + " m";
                    Log(eyeLine);
                    MobPlanRecord(eyeLine);
                }
            }

            private void MobPlanLifetimeClose()
            {
                _mpLifetime?.Dispose();
                _mpLifetime = null;
            }
        }
    }
}
