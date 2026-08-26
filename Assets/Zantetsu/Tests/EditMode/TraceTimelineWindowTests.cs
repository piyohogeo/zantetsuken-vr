using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;
using Zantetsu.Observability;
using Zantetsu.Observability.Editor;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceTimelineWindowTests
    {
        private static TraceEvent MakeEvent(long timestamp, long frameId = 1)
        {
            TraceEvent e = default;
            e.Timestamp = timestamp;
            e.FrameId = frameId;
            return e;
        }

        private static TraceTimelineFilter Filter(
            long? slashId = null,
            long? objectId = null,
            uint? objectGeneration = null,
            long? mobId = null,
            uint? planGeneration = null,
            long? taskId = null,
            TraceEventType? eventType = null,
            TraceReason? reason = null)
        {
            return new TraceTimelineFilter(slashId, objectId, objectGeneration, mobId, planGeneration, taskId, eventType, reason);
        }

        private static TraceTimelineWindow CreateWindow()
        {
            return ScriptableObject.CreateInstance<TraceTimelineWindow>();
        }

        private static TraceEvent SelectEventAt(TraceTimelineWindow window, int visibleIndex)
        {
            Assert.That(window.TrySelectVisibleEvent(visibleIndex), Is.True);
            Assert.That(window.TryGetSelectedEvent(out TraceEvent e), Is.True);
            return e;
        }

        private static string CreateBundle(long[] timestamps, out TraceRunManifest manifest)
        {
            string parent = Path.Combine(Path.GetTempPath(), "zantetsu-timeline-bundle", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(parent);

            TraceLogger logger = new TraceLogger(timestamps.Length + 1);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
            foreach (long ts in timestamps)
            {
                logger.Enqueue(MakeEvent(ts));
            }

            logger.Drain();
            recorder.TryTrigger();
            TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
            logger.Dispose();

            string sha64 = new string('a', 64);
            TraceRunContext context = new TraceRunContext(7, 1234, "build-x", "6000.3.22f1", sha64, "scene-y", 42, 0.016, 2, "Ultra", 3, new Vector3(0f, -9.81f, 0f));
            manifest = TraceRunManifest.Create(snapshot, context);

            string bundlePath = Path.Combine(parent, "bundle");
            TraceRunBundleStore.SaveAtomic(bundlePath, snapshot, manifest);
            return bundlePath;
        }

        private static void DeleteBundle(string bundlePath)
        {
            string parent = Path.GetDirectoryName(bundlePath);
            try
            {
                if (Directory.Exists(parent))
                {
                    Directory.Delete(parent, true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }

        [Test]
        public void Window_DerivesFromEditorWindow()
        {
            Assert.That(typeof(TraceTimelineWindow).IsSubclassOf(typeof(EditorWindow)), Is.True);
        }

        [Test]
        public void ShowWindow_HasCorrectMenuItem()
        {
            MethodInfo method = typeof(TraceTimelineWindow).GetMethod(
                "ShowWindow", BindingFlags.Public | BindingFlags.Static);

            Assert.That(method, Is.Not.Null);

            MenuItem attr = method.GetCustomAttribute<MenuItem>();
            Assert.That(attr, Is.Not.Null);
            Assert.That(attr.menuItem, Is.EqualTo("Window/Zantetsu/Trace Timeline"));
        }

        [Test]
        public void InitialState_IsEmptyWithNoSelection()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                Assert.That(window.EventCount, Is.EqualTo(0));
                Assert.That(window.VisibleEventCount, Is.EqualTo(0));
                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadArray_SetsCountAndChronologicalOrder()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[]
                {
                    MakeEvent(30),
                    MakeEvent(10),
                    MakeEvent(20),
                });

                Assert.That(window.EventCount, Is.EqualTo(3));
                Assert.That(window.VisibleEventCount, Is.EqualTo(3));
                Assert.That(SelectEventAt(window, 0).Timestamp, Is.EqualTo(10));
                Assert.That(SelectEventAt(window, 1).Timestamp, Is.EqualTo(20));
                Assert.That(SelectEventAt(window, 2).Timestamp, Is.EqualTo(30));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadArray_IsDefensiveCopy()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                TraceEvent[] source = { MakeEvent(1), MakeEvent(2) };
                window.Load(source);

                source[0].Timestamp = 999;

                Assert.That(window.EventCount, Is.EqualTo(2));
                Assert.That(SelectEventAt(window, 0).Timestamp, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadLogger_DoesNotDrain()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                using (TraceLogger logger = new TraceLogger(8))
                {
                    logger.Enqueue(MakeEvent(1));
                    logger.Enqueue(MakeEvent(2));

                    window.Load(logger);

                    Assert.That(window.EventCount, Is.EqualTo(0));
                    Assert.That(logger.HistoryCount, Is.EqualTo(0));
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadLogger_DoesNotDisposeLogger()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                using (TraceLogger logger = new TraceLogger(8))
                {
                    logger.Enqueue(MakeEvent(1));
                    logger.Drain();

                    window.Load(logger);

                    Assert.That(window.EventCount, Is.EqualTo(1));
                    Assert.That(logger.IsCreated, Is.True);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Clear_ResetsEventsAndSelection()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[] { MakeEvent(1), MakeEvent(2) });
                Assert.That(window.TrySelectVisibleEvent(0), Is.True);

                window.Clear();

                Assert.That(window.EventCount, Is.EqualTo(0));
                Assert.That(window.VisibleEventCount, Is.EqualTo(0));
                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Reload_ResetsScrollAndSelection()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[] { MakeEvent(1), MakeEvent(2), MakeEvent(3) });
                Assert.That(window.TrySelectVisibleEvent(1), Is.True);
                window.ScrollPosition = new Vector2(0f, 100f);

                window.Load(new[] { MakeEvent(4), MakeEvent(5) });

                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(-1));
                Assert.That(window.ScrollPosition, Is.EqualTo(Vector2.zero));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Lane_Set_IsReflected()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[] { MakeEvent(1), MakeEvent(2) });

                window.Lane = TraceTimelineLane.Slash;

                Assert.That(window.Lane, Is.EqualTo(TraceTimelineLane.Slash));
                Assert.That(window.VisibleEventCount, Is.EqualTo(2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LaneChange_PreservesSelection()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[] { MakeEvent(1), MakeEvent(2), MakeEvent(3) });
                Assert.That(window.TrySelectVisibleEvent(1), Is.True);

                window.Lane = TraceTimelineLane.Object;
                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(1));

                window.Lane = TraceTimelineLane.Thread;
                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Filter_Set_ChangesVisibleCount()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                TraceEvent e1 = MakeEvent(1); e1.TaskId = 1;
                TraceEvent e2 = MakeEvent(2); e2.TaskId = 2;
                TraceEvent e3 = MakeEvent(3); e3.TaskId = 1;
                window.Load(new[] { e1, e2, e3 });

                window.Filter = Filter(taskId: 1);

                Assert.That(window.VisibleEventCount, Is.EqualTo(2));
                Assert.That(window.EventCount, Is.EqualTo(3));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void FilterChange_ClearsSelection()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                TraceEvent e1 = MakeEvent(1); e1.TaskId = 1;
                TraceEvent e2 = MakeEvent(2); e2.TaskId = 2;
                window.Load(new[] { e1, e2 });

                Assert.That(window.TrySelectVisibleEvent(0), Is.True);
                window.Filter = Filter(taskId: 2);

                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Filter_ExplicitZeroId_IsSearchable()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                TraceEvent e1 = MakeEvent(1); e1.SlashId = 0;
                TraceEvent e2 = MakeEvent(2); e2.SlashId = 5;
                window.Load(new[] { e1, e2 });

                window.Filter = Filter(slashId: 0);

                Assert.That(window.VisibleEventCount, Is.EqualTo(1));
                Assert.That(SelectEventAt(window, 0).SlashId, Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Filter_CompoundConditions_ApplyAsAnd()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                TraceEvent e1 = MakeEvent(1); e1.SlashId = 5; e1.EventType = TraceEventType.SlashPrimed;
                TraceEvent e2 = MakeEvent(2); e2.SlashId = 5; e2.EventType = TraceEventType.SlashLatched;
                TraceEvent e3 = MakeEvent(3); e3.SlashId = 6; e3.EventType = TraceEventType.SlashPrimed;
                window.Load(new[] { e1, e2, e3 });

                window.Filter = Filter(slashId: 5, eventType: TraceEventType.SlashPrimed);

                Assert.That(window.VisibleEventCount, Is.EqualTo(1));
                Assert.That(SelectEventAt(window, 0).FrameId, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void TrySelectVisibleEvent_ValidIndex_Succeeds()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[] { MakeEvent(1), MakeEvent(2) });

                Assert.That(window.TrySelectVisibleEvent(0), Is.True);
                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void TrySelectVisibleEvent_OutOfRange_KeepsExistingSelection()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[] { MakeEvent(1), MakeEvent(2) });
                Assert.That(window.TrySelectVisibleEvent(1), Is.True);

                Assert.That(window.TrySelectVisibleEvent(-1), Is.False);
                Assert.That(window.TrySelectVisibleEvent(2), Is.False);
                Assert.That(window.TrySelectVisibleEvent(99), Is.False);

                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void TryGetSelectedEvent_ReturnsSelectedEvent()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                TraceEvent source = MakeEvent(42);
                source.SlashId = 7;
                window.Load(new[] { source });

                Assert.That(window.TrySelectVisibleEvent(0), Is.True);
                Assert.That(window.TryGetSelectedEvent(out TraceEvent e), Is.True);
                Assert.That(e.Timestamp, Is.EqualTo(42));
                Assert.That(e.SlashId, Is.EqualTo(7));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void TryGetSelectedEvent_NoSelection_ReturnsFalseAndDefault()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[] { MakeEvent(1), MakeEvent(2) });

                Assert.That(window.TryGetSelectedEvent(out TraceEvent e), Is.False);
                Assert.That(e, Is.EqualTo(default(TraceEvent)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Reload_ClearsPreviousSelection()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                window.Load(new[] { MakeEvent(1), MakeEvent(2) });
                Assert.That(window.TrySelectVisibleEvent(0), Is.True);

                window.Load(new[] { MakeEvent(3), MakeEvent(4) });

                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void ClearFilter_RestoresAllEvents()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                TraceEvent e1 = MakeEvent(1); e1.TaskId = 1;
                TraceEvent e2 = MakeEvent(2); e2.TaskId = 2;
                TraceEvent e3 = MakeEvent(3); e3.TaskId = 1;
                window.Load(new[] { e1, e2, e3 });

                window.Filter = Filter(taskId: 1);
                Assert.That(window.VisibleEventCount, Is.EqualTo(2));

                window.Filter = default;

                Assert.That(window.VisibleEventCount, Is.EqualTo(window.EventCount));
                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void EmptyData_IsSafe()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                Assert.That(window.TrySelectVisibleEvent(0), Is.False);
                Assert.That(window.TryGetSelectedEvent(out _), Is.False);

                window.Clear();
                window.Lane = TraceTimelineLane.Thread;
                window.Filter = Filter(slashId: 0);

                Assert.That(window.EventCount, Is.EqualTo(0));
                Assert.That(window.VisibleEventCount, Is.EqualTo(0));
                Assert.That(window.SelectedVisibleIndex, Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void NormalizeTimestamp_ZeroRange_DoesNotDivideByZero()
        {
            Assert.That(TraceTimelineWindow.NormalizeTimestamp(5L, 5L, 5L), Is.EqualTo(0f));
            Assert.That(TraceTimelineWindow.NormalizeTimestamp(5L, 0L, 5L), Is.EqualTo(1f));
            Assert.That(TraceTimelineWindow.NormalizeTimestamp(0L, 0L, 5L), Is.EqualTo(0f));
            Assert.That(TraceTimelineWindow.NormalizeTimestamp(2L, 0L, 4L), Is.EqualTo(0.5f).Within(1e-6f));
        }

        [Test]
        public void NormalizeTimestamp_ClampsOutsideRange()
        {
            Assert.That(TraceTimelineWindow.NormalizeTimestamp(-5L, 0L, 10L), Is.EqualTo(0f));
            Assert.That(TraceTimelineWindow.NormalizeTimestamp(99L, 0L, 10L), Is.EqualTo(1f));
        }

        [Test]
        public void ComputeVisibleRowRange_Empty_ReturnsFalse()
        {
            bool hasRows = TraceTimelineWindow.ComputeVisibleRowRange(0, 0f, 20f, 100f, out int first, out int last);

            Assert.That(hasRows, Is.False);
            Assert.That(first, Is.EqualTo(0));
            Assert.That(last, Is.EqualTo(-1));
        }

        [Test]
        public void ComputeVisibleRowRange_FirstRows()
        {
            bool hasRows = TraceTimelineWindow.ComputeVisibleRowRange(10, 0f, 20f, 100f, out int first, out int last);

            Assert.That(hasRows, Is.True);
            Assert.That(first, Is.EqualTo(0));
            Assert.That(last, Is.EqualTo(4));
        }

        [Test]
        public void ComputeVisibleRowRange_MiddleRows()
        {
            bool hasRows = TraceTimelineWindow.ComputeVisibleRowRange(10, 50f, 20f, 100f, out int first, out int last);

            Assert.That(hasRows, Is.True);
            Assert.That(first, Is.EqualTo(2));
            Assert.That(last, Is.EqualTo(7));
        }

        [Test]
        public void ComputeVisibleRowRange_LastRows()
        {
            bool hasRows = TraceTimelineWindow.ComputeVisibleRowRange(10, 140f, 20f, 100f, out int first, out int last);

            Assert.That(hasRows, Is.True);
            Assert.That(first, Is.EqualTo(7));
            Assert.That(last, Is.EqualTo(9));
        }

        [Test]
        public void ComputeVisibleRowRange_ViewportExceedsContent()
        {
            bool hasRows = TraceTimelineWindow.ComputeVisibleRowRange(5, 0f, 20f, 1000f, out int first, out int last);

            Assert.That(hasRows, Is.True);
            Assert.That(first, Is.EqualTo(0));
            Assert.That(last, Is.EqualTo(4));
        }

        [Test]
        public void ComputeVisibleRowRange_NeverLeavesBounds()
        {
            int[] counts = { 1, 2, 5, 100 };
            float[] scrolls = { 0f, 1f, 20f, 150f, 99999f };
            float[] heights = { 1f, 20f, 100f, 10000f };

            foreach (int count in counts)
            {
                foreach (float scroll in scrolls)
                {
                    foreach (float height in heights)
                    {
                        bool hasRows = TraceTimelineWindow.ComputeVisibleRowRange(count, scroll, 20f, height, out int first, out int last);

                        if (!hasRows)
                        {
                            continue;
                        }

                        Assert.That(first, Is.InRange(0, count - 1));
                        Assert.That(last, Is.InRange(0, count - 1));
                        Assert.That(first, Is.LessThanOrEqualTo(last));
                    }
                }
            }
        }

        [Test]
        public void ComputeVisibleRowRange_RejectsInvalidRowHeight()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceTimelineWindow.ComputeVisibleRowRange(10, 0f, 0f, 100f, out _, out _));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TraceTimelineWindow.ComputeVisibleRowRange(10, 0f, -5f, 100f, out _, out _));
        }

        [Test]
        public void ClampToUInt_ClampsToRangeWithoutWrapping()
        {
            Assert.That(TraceTimelineWindow.ClampToUInt(-1L), Is.EqualTo(0U));
            Assert.That(TraceTimelineWindow.ClampToUInt(long.MinValue), Is.EqualTo(0U));
            Assert.That(TraceTimelineWindow.ClampToUInt(0L), Is.EqualTo(0U));
            Assert.That(TraceTimelineWindow.ClampToUInt(1L), Is.EqualTo(1U));
            Assert.That(TraceTimelineWindow.ClampToUInt((long)uint.MaxValue), Is.EqualTo(uint.MaxValue));
            Assert.That(TraceTimelineWindow.ClampToUInt((long)uint.MaxValue + 1L), Is.EqualTo(uint.MaxValue));
            Assert.That(TraceTimelineWindow.ClampToUInt(long.MaxValue), Is.EqualTo(uint.MaxValue));

            // Overflowing values must clamp, never wrap back to 0 or another value.
            Assert.That(TraceTimelineWindow.ClampToUInt((long)uint.MaxValue + 1L), Is.Not.EqualTo(0U));
            Assert.That(TraceTimelineWindow.ClampToUInt(long.MaxValue), Is.Not.EqualTo(0U));
        }

        [Test]
        public void Filter_ObjectGeneration_BoundaryValues_MatchCorrectly()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                TraceEvent eMax = MakeEvent(1); eMax.ObjectGeneration = uint.MaxValue;
                TraceEvent eZero = MakeEvent(2); eZero.ObjectGeneration = 0;
                window.Load(new[] { eMax, eZero });

                window.Filter = Filter(objectGeneration: uint.MaxValue);
                Assert.That(window.VisibleEventCount, Is.EqualTo(1));
                Assert.That(SelectEventAt(window, 0).ObjectGeneration, Is.EqualTo(uint.MaxValue));

                window.Filter = Filter(objectGeneration: 0);
                Assert.That(window.VisibleEventCount, Is.EqualTo(1));
                Assert.That(SelectEventAt(window, 0).ObjectGeneration, Is.EqualTo(0U));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void DefaultMaximumBundleEventCount_Is250000()
        {
            Assert.That(TraceTimelineWindow.DefaultMaximumBundleEventCount, Is.EqualTo(250000));
        }

        [Test]
        public void LoadBundle_LoadsNormalBundle()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 30, 10, 20 }, out TraceRunManifest manifest);
                try
                {
                    window.LoadBundle(bundlePath, 100);

                    Assert.That(window.HasLoadedBundle, Is.True);
                    Assert.That(window.LoadedBundlePath, Is.EqualTo(Path.GetFullPath(bundlePath)));
                    Assert.That(window.LoadedManifest, Is.Not.Null);
                    Assert.That(window.LoadedManifest.TestRunId, Is.EqualTo(7));
                    Assert.That(window.LoadedManifestContentSha256, Is.Not.Null);
                    Assert.That(window.LoadedManifestContentSha256.Length, Is.EqualTo(64));
                    Assert.That(window.LoadedTraceContentSha256, Is.Not.Null);
                    Assert.That(window.LoadedTraceContentSha256.Length, Is.EqualTo(64));

                    Assert.That(window.EventCount, Is.EqualTo(3));
                    Assert.That(window.VisibleEventCount, Is.EqualTo(3));
                    Assert.That(SelectEventAt(window, 0).Timestamp, Is.EqualTo(10));
                    Assert.That(SelectEventAt(window, 1).Timestamp, Is.EqualTo(20));
                    Assert.That(SelectEventAt(window, 2).Timestamp, Is.EqualTo(30));
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadBundle_PreservesLaneAndFilter()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10, 20, 30 }, out _);
                try
                {
                    window.Lane = TraceTimelineLane.Slash;
                    window.Filter = Filter(slashId: 99);

                    window.LoadBundle(bundlePath, 100);

                    Assert.That(window.Lane, Is.EqualTo(TraceTimelineLane.Slash));
                    Assert.That(window.Filter.SlashId, Is.EqualTo(99L));
                    Assert.That(window.EventCount, Is.EqualTo(3));
                    Assert.That(window.VisibleEventCount, Is.EqualTo(0)); // filter applied to new events
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadBundle_ResetsSelectionAndScroll()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10, 20 }, out _);
                try
                {
                    window.Load(new[] { MakeEvent(1), MakeEvent(2) });
                    Assert.That(window.TrySelectVisibleEvent(1), Is.True);
                    window.ScrollPosition = new Vector2(0f, 100f);

                    window.LoadBundle(bundlePath, 100);

                    Assert.That(window.SelectedVisibleIndex, Is.EqualTo(-1));
                    Assert.That(window.ScrollPosition, Is.EqualTo(Vector2.zero));
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadBundle_MaximumEventCountExceeded_Rejected()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10, 20, 30 }, out _);
                try
                {
                    Assert.Throws<InvalidDataException>(() => window.LoadBundle(bundlePath, 2));
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadBundle_CorruptBundle_Fails()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10, 20 }, out _);
                try
                {
                    File.WriteAllText(Path.Combine(bundlePath, "bundle.index"), "garbage");

                    Assert.Throws<InvalidDataException>(() => window.LoadBundle(bundlePath, 100));
                    Assert.That(window.HasLoadedBundle, Is.False);
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadBundle_Failure_StateUnchanged()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundleA = CreateBundle(new long[] { 10, 20 }, out _);
                string bundleB = CreateBundle(new long[] { 30, 40 }, out _);
                try
                {
                    window.LoadBundle(bundleA, 100);
                    string pathA = window.LoadedBundlePath;

                    byte[] trace = File.ReadAllBytes(Path.Combine(bundleB, "trace.bin"));
                    trace[0] ^= 0x01;
                    File.WriteAllBytes(Path.Combine(bundleB, "trace.bin"), trace);

                    Assert.Throws<InvalidDataException>(() => window.LoadBundle(bundleB, 100));

                    Assert.That(window.LoadedBundlePath, Is.EqualTo(pathA));
                    Assert.That(window.EventCount, Is.EqualTo(2));
                    Assert.That(window.HasLoadedBundle, Is.True);
                }
                finally
                {
                    DeleteBundle(bundleA);
                    DeleteBundle(bundleB);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadEvents_ClearsBundleMetadata()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10 }, out _);
                try
                {
                    window.LoadBundle(bundlePath, 100);
                    Assert.That(window.HasLoadedBundle, Is.True);

                    window.Load(new[] { MakeEvent(1) });

                    Assert.That(window.HasLoadedBundle, Is.False);
                    Assert.That(window.LoadedManifest, Is.Null);
                    Assert.That(window.LoadedBundlePath, Is.Null);
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadLogger_ClearsBundleMetadata()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10 }, out _);
                try
                {
                    window.LoadBundle(bundlePath, 100);

                    using (TraceLogger logger = new TraceLogger(4))
                    {
                        logger.Enqueue(MakeEvent(1));
                        logger.Drain();
                        window.Load(logger);
                    }

                    Assert.That(window.HasLoadedBundle, Is.False);
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadEvents_Failure_KeepsBundleMetadata()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10 }, out _);
                try
                {
                    window.LoadBundle(bundlePath, 100);

                    Assert.Throws<ArgumentNullException>(() => window.Load((TraceEvent[])null));

                    Assert.That(window.HasLoadedBundle, Is.True);
                    Assert.That(window.LoadedManifest, Is.Not.Null);
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Clear_ClearsBundleMetadata()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10 }, out _);
                try
                {
                    window.LoadBundle(bundlePath, 100);
                    Assert.That(window.HasLoadedBundle, Is.True);

                    window.Clear();

                    Assert.That(window.HasLoadedBundle, Is.False);
                    Assert.That(window.LoadedBundlePath, Is.Null);
                    Assert.That(window.LoadedManifest, Is.Null);
                    Assert.That(window.LoadedManifestContentSha256, Is.Null);
                    Assert.That(window.LoadedTraceContentSha256, Is.Null);
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void LoadBundle_ReleasesHandles()
        {
            TraceTimelineWindow window = CreateWindow();
            try
            {
                string bundlePath = CreateBundle(new long[] { 10 }, out _);
                try
                {
                    window.LoadBundle(bundlePath, 100);

                    string moved = bundlePath + ".moved";
                    Directory.Move(bundlePath, moved);
                    Directory.Delete(moved, true);
                }
                finally
                {
                    DeleteBundle(bundlePath);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void Window_NoSnapshotOrMutableArrayPublicApi()
        {
            Type type = typeof(TraceTimelineWindow);

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(TraceCaptureSnapshot)), "Snapshot exposed via " + property.Name);
                Assert.That(property.PropertyType.IsArray, Is.False, "Array exposed via " + property.Name);
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, "Array field exposed: " + field.Name);
            }
        }
    }
}
