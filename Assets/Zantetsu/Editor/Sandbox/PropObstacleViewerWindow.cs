using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// PROP-OBSTACLE-1 (evaluation only): a top-down view of a capture of ordinary play and of its replays -- pick a section,
    /// replay it with two variants from the same saved start (ON and OFF by default), step through the virtual time. What is
    /// judged is the product's Core (PropReplay); this window only draws. After the probe's Locomotion Search window.
    /// Zantetsu > Prop Obstacle Viewer.
    /// </summary>
    public sealed class PropObstacleViewerWindow : EditorWindow
    {
        private string folder = string.Empty;
        private string variantA = "on", variantB = "off";
        private PropObstacleReplayTool.Loaded loaded;
        private List<PropObstacleReplayTool.Section> sections;
        private int selected = -1;
        private double from, to;
        private PropReplayResult runA, runB;
        private int frame;
        private bool playing, showA = true, showB = true;
        private float speed = 1f, trail = 8f;
        private double lastTick;
        private PropTopDownPainter painter;
        private Texture2D texture;
        private int paintedFrame = -1;
        private bool dirty = true;
        private Vector2 listScroll, stayScroll;
        private DwellInterval shownStay;
        private PropReplayResult shownStayRun;
        private float staySpan = 12f;
        private string status = "no capture loaded";

        [MenuItem("Zantetsu/Prop Obstacle Viewer")]
        public static void Open() => GetWindow<PropObstacleViewerWindow>("Prop Obstacles");

        private void OnEnable() { EditorApplication.update += Tick; }
        private void OnDisable() { EditorApplication.update -= Tick; if (texture != null) DestroyImmediate(texture); texture = null; }

        private void Tick()
        {
            if (!playing || runB == null && runA == null) return;
            double now = EditorApplication.timeSinceStartup;
            int count = FrameCount();
            if (count == 0) return;
            double step = (runA ?? runB).Frames.Count > 1 ? (runA ?? runB).Frames[1].Time - (runA ?? runB).Frames[0].Time : 1.0 / 72.0;
            int advance = (int)((now - lastTick) * speed / step);
            if (advance <= 0) return;
            lastTick = now;
            frame = Math.Min(count - 1, frame + advance);
            if (frame >= count - 1) playing = false;
            Repaint();
        }

        private int FrameCount() => Math.Min(runA != null ? runA.Frames.Count : int.MaxValue, runB != null ? runB.Frames.Count : int.MaxValue) == int.MaxValue ? 0 : Math.Min(runA != null ? runA.Frames.Count : int.MaxValue, runB != null ? runB.Frames.Count : int.MaxValue);

        private void OnGUI()
        {
            EditorGUILayout.BeginHorizontal();
            folder = EditorGUILayout.TextField("Capture folder", folder);
            if (GUILayout.Button("...", GUILayout.Width(28)))
            {
                string picked = EditorUtility.OpenFolderPanel("A capture's folder (prop-capture-*)", folder, string.Empty);
                if (!string.IsNullOrEmpty(picked)) folder = picked;
            }

            if (GUILayout.Button("Load", GUILayout.Width(60))) LoadCapture();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
            if (loaded == null) return;

            EditorGUILayout.BeginHorizontal();
            // ---- the sections ----
            EditorGUILayout.BeginVertical(GUILayout.Width(330));
            EditorGUILayout.LabelField("Sections (30 s; click to select)", EditorStyles.boldLabel);
            listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.Height(260));
            for (int i = 0; i < sections.Count; i++)
            {
                PropObstacleReplayTool.Section s = sections[i];
                string label = FormattableString.Invariant($"{s.From,7:F0}-{s.To:F0} s  cells {s.MostCells}  cross {s.RecordedCrossingSeconds:F0} s  {s.Tags}");
                if (GUILayout.Toggle(selected == i, label, EditorStyles.miniButtonLeft) && selected != i) { selected = i; from = s.From; to = s.To; }
            }

            EditorGUILayout.EndScrollView();
            from = EditorGUILayout.DoubleField("From (s)", from);
            to = EditorGUILayout.DoubleField("To (s)", to);
            variantA = EditorGUILayout.TextField(new GUIContent("Variant A (green)", "on, off, cell0.1, plane1.0, every5, delay0.5, field; joined by +"), variantA);
            variantB = EditorGUILayout.TextField("Variant B (blue)", variantB);
            if (GUILayout.Button("Replay the section (both variants)")) RunSection();
            showA = EditorGUILayout.ToggleLeft("Show A", showA); showB = EditorGUILayout.ToggleLeft("Show B", showB);
            trail = EditorGUILayout.Slider("Paths (s)", trail, 0f, 20f);
            if (runA != null) EditorGUILayout.LabelField("A " + Summary(runA), EditorStyles.wordWrappedMiniLabel);
            if (runB != null) EditorGUILayout.LabelField("B " + Summary(runB), EditorStyles.wordWrappedMiniLabel);
            // the local stays of both runs, the rotating ones first: click one to look at it (its middle frame, a view about it, its headings)
            if (runA != null || runB != null)
            {
                EditorGUILayout.LabelField("Stays (3 s within 0.5 m; click to look)", EditorStyles.boldLabel);
                EditorGUILayout.BeginHorizontal();
                staySpan = EditorGUILayout.Slider("View (m)", staySpan, 4f, 50f);
                if (GUILayout.Button("Whole window", GUILayout.Width(100))) { shownStay = null; shownStayRun = null; dirty = true; }
                EditorGUILayout.EndHorizontal();
                stayScroll = EditorGUILayout.BeginScrollView(stayScroll, GUILayout.Height(220));
                foreach (PropReplayResult run in new[] { runA, runB })
                {
                    if (run == null) continue;
                    var ordered = new List<DwellInterval>(run.Dwells);
                    ordered.Sort((x, y) => y.AbsYaw.CompareTo(x.AbsYaw));
                    foreach (DwellInterval stay in ordered)
                    {
                        string label = FormattableString.Invariant($"{(run == runA ? "A" : "B")} id {stay.Id}  {stay.Start:F0}-{stay.End:F0} s  {stay.AbsYaw:F0} deg  {stay.Kind(run.DwellSettings)}{(stay.Near ? "  near" : "")}{(stay.GoalEvents.Count > 0 ? "  goal x" + stay.GoalEvents.Count : "")}");
                        if (GUILayout.Toggle(shownStay == stay, label, EditorStyles.miniButtonLeft) && shownStay != stay)
                        {
                            shownStay = stay; shownStayRun = run;
                            double step = run.Frames.Count > 1 ? run.Frames[1].Time - run.Frames[0].Time : 1.0 / 72.0;
                            frame = Math.Max(0, Math.Min(FrameCount() - 1, (int)Math.Round(((stay.Start + stay.End) * 0.5 - run.Frames[0].Time) / step)));
                            dirty = true;
                        }
                    }
                }

                EditorGUILayout.EndScrollView();
                if (shownStay != null) EditorGUILayout.LabelField(shownStay.ToCsv(shownStayRun.DwellSettings, loaded.Dataset).Replace(",", ", "), EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndVertical();

            // ---- the picture and the time ----
            EditorGUILayout.BeginVertical();
            int count = FrameCount();
            if (count > 0)
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(playing ? "Pause" : "Play", GUILayout.Width(60))) { playing = !playing; lastTick = EditorApplication.timeSinceStartup; if (playing && frame >= count - 1) frame = 0; }
                if (GUILayout.Button("-1 s", GUILayout.Width(44))) frame = Math.Max(0, frame - 72);
                if (GUILayout.Button("-1", GUILayout.Width(32))) frame = Math.Max(0, frame - 1);
                if (GUILayout.Button("+1", GUILayout.Width(32))) frame = Math.Min(count - 1, frame + 1);
                if (GUILayout.Button("+1 s", GUILayout.Width(44))) frame = Math.Min(count - 1, frame + 72);
                speed = EditorGUILayout.Slider(speed, 0.1f, 8f, GUILayout.Width(160));
                EditorGUILayout.LabelField("x speed", GUILayout.Width(52));
                EditorGUILayout.EndHorizontal();
                frame = EditorGUILayout.IntSlider(frame, 0, count - 1);
                PaintIfNeeded();
                Rect area = GUILayoutUtility.GetAspectRect(1f, GUILayout.MaxWidth(900), GUILayout.MaxHeight(900));
                if (texture != null) GUI.DrawTexture(area, texture, ScaleMode.ScaleToFit);
                EditorGUILayout.LabelField(painter != null ? painter.Caption : string.Empty, EditorStyles.wordWrappedMiniLabel);
            }
            else EditorGUILayout.HelpBox("Select a section and replay it.", MessageType.Info);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            if (GUI.changed) dirty = true;
        }

        private static string Summary(PropReplayResult r) => FormattableString.Invariant($"{r.Name}: crossings {r.Crossings} ({r.CrossingSeconds:F1} s inside, {r.OverlapSeconds:F1} s overlapping), starts {r.Starts} (inside {r.StartsInsideProps}, refused in a piece {r.StartsRefusedInProps}), standing near {r.StandingNearSeconds:F1} s, goals {r.GoalsReached:F0}, stuck {r.StuckGoalEvents}, cycles {r.Cycles} (published {r.CyclesPublished}), map age mean {r.MapAgeAtCycleMean * 1000:F0} ms, map {r.MapBuildMillisecondsMean:F2} ms, cycle {r.CycleMillisecondsMean:F0} ms (Editor)");

        private void LoadCapture()
        {
            try
            {
                loaded = PropObstacleReplayTool.Load(folder);
                sections = PropObstacleReplayTool.Sections(loaded);
                selected = -1; runA = runB = null; frame = 0; dirty = true;
                PropCapture.Session s = loaded.Session;
                status = FormattableString.Invariant($"{Path.GetFileName(folder)}: shapes {s.Shapes.Count}, builds {s.Builds.Count}, saved cycles {s.Starts.Count}, player samples {s.Player.Count}; {loaded.Settings.Describe()}; {loaded.Identity}");
            }
            catch (Exception e)
            {
                loaded = null;
                status = "NOT LOADED: " + e.Message;
            }
        }

        private void RunSection()
        {
            try
            {
                PropReplayOptions a = PropObstacleReplayTool.Variant(variantA); a.KeepFrames = true;
                PropReplayOptions b = PropObstacleReplayTool.Variant(variantB); b.KeepFrames = true;
                EditorUtility.DisplayProgressBar("Prop obstacle replay", variantA, 0.1f);
                runA = loaded.Replay.Run(from, to, a);
                EditorUtility.DisplayProgressBar("Prop obstacle replay", variantB, 0.6f);
                runB = loaded.Replay.Run(from, to, b);
                frame = 0; dirty = true; paintedFrame = -1;
                shownStay = null; shownStayRun = null;
            }
            catch (Exception e)
            {
                status = "REPLAY FAILED: " + e.Message;
                runA = runB = null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private void PaintIfNeeded()
        {
            if (!dirty && paintedFrame == frame) return;
            if (painter == null) painter = new PropTopDownPainter(900);
            if (texture == null) texture = new Texture2D(painter.Size, painter.Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            painter.Highlight = shownStay; painter.HighlightRun = shownStayRun;
            if (shownStay != null) { painter.ViewX = shownStay.StartX; painter.ViewZ = shownStay.StartZ; painter.ViewSpan = staySpan; } else painter.ViewSpan = 0f;
            painter.Paint(loaded, runA, runB, frame, trail, showA, showB);
            texture.SetPixels32(painter.Pixels);
            texture.Apply(false);
            paintedFrame = frame; dirty = false;
        }
    }
}
