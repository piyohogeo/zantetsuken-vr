using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEditor;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Observability.Editor
{
    /// <summary>
    /// Minimal editor window for browsing a <see cref="TraceTimelineModel"/>
    /// snapshot of trace events. Displays a lane/filter toolbar, a raw
    /// timestamp strip, a virtualized chronological event list and a detail
    /// panel for the selected event.
    /// </summary>
    /// <remarks>
    /// The window holds no reference to any <see cref="TraceLogger"/>; it only
    /// snapshots the logger's drained history. It does not drain, dispose, or
    /// otherwise mutate the logger. Closing the window has no effect on the
    /// logger.
    /// </remarks>
    public sealed class TraceTimelineWindow : EditorWindow
    {
        /// <summary>Default maximum number of events loaded from a saved bundle.</summary>
        public const int DefaultMaximumBundleEventCount = 250000;

        private const float RowHeight = 18f;
        private const float TimelineStripHeight = 44f;
        private const float ListMinHeight = 140f;

        private static readonly float[] ColumnWidths =
        {
            110f, // Timestamp
            55f,  // FrameId
            65f,  // Key (lane)
            140f, // EventType
            70f,  // FromState
            70f,  // ToState
            80f,  // Reason
            80f,  // Value0
            80f,  // Value1
        };

        private static readonly string[] ColumnHeaders =
        {
            "Timestamp", "FrameId", "Key", "EventType", "FromState", "ToState", "Reason", "Value0", "Value1",
        };

        private static readonly Color StripBackground = new Color(0.15f, 0.15f, 0.16f, 1f);
        private static readonly Color RowAlternateBackground = new Color(0.24f, 0.24f, 0.24f, 1f);
        private static readonly Color SelectedRowBackground = new Color(0.20f, 0.36f, 0.55f, 1f);
        private static readonly Color SelectedMarker = new Color(1f, 1f, 0f, 1f);

        private static readonly Color RejectMarker = new Color(1f, 0.35f, 0.3f, 1f);
        private static readonly Color SuccessMarker = new Color(0.35f, 0.9f, 0.35f, 1f);
        private static readonly Color StateMarker = new Color(0.35f, 0.55f, 1f, 1f);
        private static readonly Color DefaultMarker = new Color(0.6f, 0.6f, 0.6f, 1f);

        private TraceTimelineModel _model = new TraceTimelineModel();
        private int _selectedVisibleIndex = -1;
        private Vector2 _scrollPosition;

        private string _loadedBundlePath;
        private TraceRunManifest _loadedManifest;
        private string _loadedManifestContentSha256;
        private string _loadedTraceContentSha256;
        private string _loadError;
        private bool _showManifestPanel = true;

        // Filter panel UI state. Kept separate from the model so that an
        // explicit zero value is distinguishable from a disabled filter.
        private bool _slashIdEnabled;
        private long _slashIdValue;
        private bool _objectIdEnabled;
        private long _objectIdValue;
        private bool _objectGenerationEnabled;
        private long _objectGenerationValue;
        private bool _mobIdEnabled;
        private long _mobIdValue;
        private bool _planGenerationEnabled;
        private long _planGenerationValue;
        private bool _taskIdEnabled;
        private long _taskIdValue;
        private bool _eventTypeEnabled;
        private TraceEventType _eventTypeValue;
        private bool _reasonEnabled;
        private TraceReason _reasonValue;

        /// <summary>Total number of loaded (chronological) events.</summary>
        public int EventCount => _model.Count;

        /// <summary>Number of events matching the current filter.</summary>
        public int VisibleEventCount => _model.VisibleCount;

        /// <summary>Current lane. Changing the lane never hides events.</summary>
        public TraceTimelineLane Lane
        {
            get => _model.Lane;
            set => _model.Lane = value;
        }

        /// <summary>
        /// Current filter. Setting a filter clears the current selection.
        /// </summary>
        public TraceTimelineFilter Filter
        {
            get => _model.Filter;
            set
            {
                _model.Filter = value;
                _selectedVisibleIndex = -1;
                SyncFilterPanelFromFilter();
            }
        }

        /// <summary>Index of the selected event in visible (filtered) order, or -1.</summary>
        public int SelectedVisibleIndex => _selectedVisibleIndex;

        /// <summary>Event-list scroll position. Provided for tests and automation.</summary>
        public Vector2 ScrollPosition
        {
            get => _scrollPosition;
            set => _scrollPosition = value;
        }

        /// <summary>Whether a saved bundle has been loaded.</summary>
        public bool HasLoadedBundle => _loadedManifest != null;

        /// <summary>Normalized absolute path of the loaded bundle, or null.</summary>
        public string LoadedBundlePath => _loadedBundlePath;

        /// <summary>Manifest of the loaded bundle, or null.</summary>
        public TraceRunManifest LoadedManifest => _loadedManifest;

        /// <summary>SHA-256 of the loaded bundle's manifest.json, or null.</summary>
        public string LoadedManifestContentSha256 => _loadedManifestContentSha256;

        /// <summary>SHA-256 of the loaded bundle's trace.bin, or null.</summary>
        public string LoadedTraceContentSha256 => _loadedTraceContentSha256;

        /// <summary>
        /// Verifies and loads a saved bundle, then swaps it into the window's
        /// model. On failure the window state is left unchanged (strong
        /// exception safety).
        /// </summary>
        public void LoadBundle(string bundleDirectoryPath, int maximumEventCount)
        {
            TraceRunBundle bundle = TraceRunBundleStore.Load(bundleDirectoryPath, maximumEventCount);

            TraceTimelineModel newModel = new TraceTimelineModel();
            newModel.Lane = _model.Lane;
            newModel.Filter = _model.Filter;
            newModel.Load(bundle.Snapshot);

            _model = newModel;
            _selectedVisibleIndex = -1;
            _scrollPosition = Vector2.zero;
            _loadedBundlePath = Path.GetFullPath(bundleDirectoryPath);
            _loadedManifest = bundle.Manifest;
            _loadedManifestContentSha256 = bundle.ManifestContentSha256;
            _loadedTraceContentSha256 = bundle.TraceContentSha256;
            _loadError = null;
        }

        [MenuItem("Window/Zantetsu/Trace Timeline")]
        public static TraceTimelineWindow ShowWindow()
        {
            TraceTimelineWindow window = GetWindow<TraceTimelineWindow>("Zantetsu Trace Timeline");
            window.Show();
            return window;
        }

        /// <summary>Loads a defensive, timestamp-sorted copy of the source events.</summary>
        public void Load(TraceEvent[] events)
        {
            _model.Load(events);
            _selectedVisibleIndex = -1;
            _scrollPosition = Vector2.zero;
            ClearBundleMetadata();
        }

        /// <summary>
        /// Loads a snapshot of the logger's drained history without draining
        /// or disposing the logger.
        /// </summary>
        public void Load(TraceLogger logger)
        {
            _model.Load(logger);
            _selectedVisibleIndex = -1;
            _scrollPosition = Vector2.zero;
            ClearBundleMetadata();
        }

        /// <summary>Clears all events, the selection and bundle metadata.</summary>
        public void Clear()
        {
            _model.Clear();
            _selectedVisibleIndex = -1;
            _scrollPosition = Vector2.zero;
            ClearBundleMetadata();
        }

        /// <summary>
        /// Selects the event at the given visible (filtered) index. Returns
        /// false, leaving any existing selection untouched, when the index is
        /// out of range.
        /// </summary>
        public bool TrySelectVisibleEvent(int visibleIndex)
        {
            if (visibleIndex < 0 || visibleIndex >= _model.VisibleCount)
            {
                return false;
            }

            _selectedVisibleIndex = visibleIndex;
            return true;
        }

        /// <summary>
        /// Returns the currently selected event. Returns false and
        /// <c>default</c> when nothing is selected.
        /// </summary>
        public bool TryGetSelectedEvent(out TraceEvent traceEvent)
        {
            if (_selectedVisibleIndex < 0 || _selectedVisibleIndex >= _model.VisibleCount)
            {
                traceEvent = default;
                return false;
            }

            traceEvent = _model.GetVisibleEvent(_selectedVisibleIndex);
            return true;
        }

        /// <summary>
        /// Normalizes a raw timestamp to [0, 1] against the given range. When
        /// the range is zero or negative (for example a single distinct
        /// timestamp) the result is clamped to 0 without dividing by zero.
        /// </summary>
        public static float NormalizeTimestamp(long timestamp, long minimumTimestamp, long maximumTimestamp)
        {
            double min = minimumTimestamp;
            double max = maximumTimestamp;
            double range = max - min;
            if (range <= 0.0)
            {
                return 0f;
            }

            double t = ((double)timestamp - min) / range;
            if (t < 0.0)
            {
                t = 0.0;
            }
            else if (t > 1.0)
            {
                t = 1.0;
            }

            return (float)t;
        }

        /// <summary>
        /// Computes the inclusive row range [firstRow, lastRow] of a fixed
        /// height, virtualized list that intersects the viewport.
        /// </summary>
        /// <returns>
        /// True and a valid range when at least one row is visible; false with
        /// firstRow = 0 and lastRow = -1 when the list is empty or the
        /// viewport has no height.
        /// </returns>
        public static bool ComputeVisibleRowRange(
            int visibleCount,
            float scrollY,
            float rowHeight,
            float viewportHeight,
            out int firstRow,
            out int lastRow)
        {
            if (rowHeight <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(rowHeight), rowHeight, "Row height must be positive.");
            }

            if (visibleCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(visibleCount), visibleCount, "Visible count must be non-negative.");
            }

            if (visibleCount == 0 || viewportHeight <= 0f)
            {
                firstRow = 0;
                lastRow = -1;
                return false;
            }

            double scroll = Math.Max(0.0, (double)scrollY);
            double height = Math.Max(0.0, (double)viewportHeight);
            double rh = (double)rowHeight;

            int first = (int)Math.Floor(scroll / rh);
            int last = (int)Math.Ceiling((scroll + height) / rh) - 1;

            int maxIndex = visibleCount - 1;
            first = Math.Max(0, Math.Min(first, maxIndex));
            last = Math.Max(0, Math.Min(last, maxIndex));
            if (last < first)
            {
                last = first;
            }

            firstRow = first;
            lastRow = last;
            return true;
        }

        /// <summary>
        /// Clamps a signed value to the representable unsigned range [0,
        /// uint.MaxValue]. Values above uint.MaxValue clamp to uint.MaxValue
        /// rather than wrapping to a different value.
        /// </summary>
        public static uint ClampToUInt(long value)
        {
            if (value <= 0L)
            {
                return 0U;
            }

            if (value >= (long)uint.MaxValue)
            {
                return uint.MaxValue;
            }

            return (uint)value;
        }

        private void OnEnable()
        {
            if (_model == null)
            {
                _model = new TraceTimelineModel();
            }

            if (_selectedVisibleIndex >= _model.VisibleCount)
            {
                _selectedVisibleIndex = -1;
            }
        }

        private void OnGUI()
        {
            DrawToolbar();
            DrawLoadError();
            DrawManifestPanel();
            DrawFilterPanel();
            DrawTimelineStrip();
            DrawEventList();
            DrawDetailPanel();
        }

        private void OpenBundleFromDialog()
        {
            string selected = EditorUtility.OpenFolderPanel("Open Trace Bundle", "", "");
            if (string.IsNullOrEmpty(selected))
            {
                return;
            }

            try
            {
                LoadBundle(selected, DefaultMaximumBundleEventCount);
                Repaint();
            }
            catch (ArgumentException ex)
            {
                _loadError = ex.GetType().Name + ": " + ex.Message;
            }
            catch (IOException ex)
            {
                _loadError = ex.GetType().Name + ": " + ex.Message;
            }
            catch (InvalidDataException ex)
            {
                _loadError = ex.GetType().Name + ": " + ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                _loadError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private void DrawLoadError()
        {
            if (!string.IsNullOrEmpty(_loadError))
            {
                EditorGUILayout.HelpBox(_loadError, MessageType.Error);
            }
        }

        private void DrawManifestPanel()
        {
            if (_loadedManifest == null)
            {
                return;
            }

            _showManifestPanel = EditorGUILayout.Foldout(_showManifestPanel, "Bundle Manifest", true);
            if (!_showManifestPanel)
            {
                return;
            }

            EditorGUILayout.SelectableLabel("Bundle path: " + _loadedBundlePath, GUILayout.Height(18f));
            EditorGUILayout.LabelField("TestRunId", Inv(_loadedManifest.TestRunId));
            EditorGUILayout.LabelField("Captured UTC milliseconds", Inv(_loadedManifest.CapturedUtcUnixMilliseconds));
            EditorGUILayout.LabelField("BuildId", _loadedManifest.BuildId);
            EditorGUILayout.LabelField("Unity version", _loadedManifest.UnityVersion);
            EditorGUILayout.SelectableLabel("Package lock SHA-256: " + _loadedManifest.PackageLockSha256, GUILayout.Height(18f));
            EditorGUILayout.LabelField("Scene", _loadedManifest.SceneId);
            EditorGUILayout.LabelField("Random seed", Inv(_loadedManifest.RandomSeed));
            EditorGUILayout.LabelField("Fixed delta time", Inv(_loadedManifest.FixedDeltaTimeSeconds));
            EditorGUILayout.LabelField("Quality level", Inv(_loadedManifest.QualityLevel));
            EditorGUILayout.LabelField("Quality name", _loadedManifest.QualityName);
            EditorGUILayout.LabelField("WorldPhysicsProfile version", Inv(_loadedManifest.WorldPhysicsProfileVersion));
            EditorGUILayout.LabelField("Gravity", "(" + Inv(_loadedManifest.Gravity.x) + ", " + Inv(_loadedManifest.Gravity.y) + ", " + Inv(_loadedManifest.Gravity.z) + ")");
            EditorGUILayout.LabelField("Trace format major", Inv(_loadedManifest.TraceFormatMajorVersion));
            EditorGUILayout.LabelField("Trace format minor", Inv(_loadedManifest.TraceFormatMinorVersion));
            EditorGUILayout.LabelField("Event count", Inv(_loadedManifest.EventCount));
            EditorGUILayout.LabelField("Trigger-history count", Inv(_loadedManifest.TriggerHistoryCount));
            EditorGUILayout.LabelField("Post-roll count", Inv(_loadedManifest.CapturedPostRollCount));
            EditorGUILayout.LabelField("History overwritten", _loadedManifest.WasHistoryOverwrittenAtTrigger ? "true" : "false");
            EditorGUILayout.SelectableLabel("Manifest SHA-256: " + _loadedManifestContentSha256, GUILayout.Height(18f));
            EditorGUILayout.SelectableLabel("Trace SHA-256: " + _loadedTraceContentSha256, GUILayout.Height(18f));
        }

        private void ClearBundleMetadata()
        {
            _loadedBundlePath = null;
            _loadedManifest = null;
            _loadedManifestContentSha256 = null;
            _loadedTraceContentSha256 = null;
            _loadError = null;
        }

        private static string Inv(long value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Inv(ushort value) => value.ToString(CultureInfo.InvariantCulture);

        private static string Inv(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Inv(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("Open Bundle...", EditorStyles.toolbarButton))
            {
                OpenBundleFromDialog();
            }

            TraceTimelineLane newLane = (TraceTimelineLane)EditorGUILayout.EnumPopup(Lane, GUILayout.Width(96f));
            if (newLane != Lane)
            {
                Lane = newLane;
            }

            if (GUILayout.Button("Clear Filter", EditorStyles.toolbarButton))
            {
                Filter = default;
            }

            if (GUILayout.Button("Clear Events", EditorStyles.toolbarButton))
            {
                Clear();
            }

            GUILayout.FlexibleSpace();

            GUILayout.Label("Events: " + _model.VisibleCount + " / " + _model.Count, EditorStyles.miniLabel);
            GUILayout.Label("T: [" + _model.MinimumTimestamp + ", " + _model.MaximumTimestamp + "]", EditorStyles.miniLabel);

            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilterPanel()
        {
            EditorGUILayout.LabelField("Filter", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();

            DrawFilterLongRow("SlashId", ref _slashIdEnabled, ref _slashIdValue);
            DrawFilterLongRow("ObjectId", ref _objectIdEnabled, ref _objectIdValue);
            DrawFilterUIntRow("ObjectGeneration", ref _objectGenerationEnabled, ref _objectGenerationValue);
            DrawFilterLongRow("MobId", ref _mobIdEnabled, ref _mobIdValue);
            DrawFilterUIntRow("PlanGeneration", ref _planGenerationEnabled, ref _planGenerationValue);
            DrawFilterLongRow("TaskId", ref _taskIdEnabled, ref _taskIdValue);

            EditorGUILayout.BeginHorizontal();
            _eventTypeEnabled = EditorGUILayout.ToggleLeft("EventType", _eventTypeEnabled, GUILayout.Width(160f));
            if (_eventTypeEnabled)
            {
                _eventTypeValue = (TraceEventType)EditorGUILayout.EnumPopup(_eventTypeValue, GUILayout.Width(200f));
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _reasonEnabled = EditorGUILayout.ToggleLeft("Reason", _reasonEnabled, GUILayout.Width(160f));
            if (_reasonEnabled)
            {
                _reasonValue = (TraceReason)EditorGUILayout.EnumPopup(_reasonValue, GUILayout.Width(200f));
            }
            EditorGUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                Filter = BuildFilter();
            }
        }

        private void DrawTimelineStrip()
        {
            EditorGUILayout.LabelField("Timeline", EditorStyles.boldLabel);

            Rect stripRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(TimelineStripHeight));
            if (stripRect.width < 1f)
            {
                return;
            }

            EditorGUI.DrawRect(stripRect, StripBackground);

            int count = _model.VisibleCount;
            if (count == 0)
            {
                GUI.Label(stripRect, "No events", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            long min = _model.MinimumTimestamp;
            long max = _model.MaximumTimestamp;
            float left = stripRect.x + 2f;
            float width = stripRect.width - 4f;

            for (int i = 0; i < count; i++)
            {
                TraceEvent e = _model.GetVisibleEvent(i);
                float t = NormalizeTimestamp(e.Timestamp, min, max);
                float x = left + t * width;

                bool selected = i == _selectedVisibleIndex;
                Color color = selected ? SelectedMarker : GetEventColor(e.EventType);
                float markerWidth = selected ? 4f : 2f;

                Rect marker = new Rect(x - markerWidth * 0.5f, stripRect.y + 4f, markerWidth, stripRect.height - 8f);
                EditorGUI.DrawRect(marker, color);
            }
        }

        private void DrawEventList()
        {
            EditorGUILayout.LabelField("Events", EditorStyles.boldLabel);

            DrawListHeader();

            int count = _model.VisibleCount;
            Rect viewport = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.MinHeight(ListMinHeight), GUILayout.ExpandHeight(true));

            if (count == 0)
            {
                EditorGUI.DrawRect(viewport, RowAlternateBackground);
                GUI.Label(viewport, "No events", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            float contentWidth = Mathf.Max(0f, viewport.width - 16f);
            Rect contentRect = new Rect(0f, 0f, contentWidth, count * RowHeight);

            _scrollPosition = GUI.BeginScrollView(viewport, _scrollPosition, contentRect, false, true);

            if (ComputeVisibleRowRange(count, _scrollPosition.y, RowHeight, viewport.height, out int first, out int last))
            {
                for (int i = first; i <= last; i++)
                {
                    Rect rowRect = new Rect(0f, i * RowHeight, contentWidth, RowHeight);
                    DrawEventRow(i, rowRect);
                }
            }

            GUI.EndScrollView();

            HandleListClick(viewport, count);
        }

        private void DrawListHeader()
        {
            EditorGUILayout.BeginHorizontal();
            for (int c = 0; c < ColumnHeaders.Length; c++)
            {
                GUILayout.Label(ColumnHeaders[c], EditorStyles.miniBoldLabel, GUILayout.Width(ColumnWidths[c]));
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEventRow(int visibleIndex, Rect rowRect)
        {
            TraceEvent e = _model.GetVisibleEvent(visibleIndex);
            bool selected = visibleIndex == _selectedVisibleIndex;

            if (selected)
            {
                EditorGUI.DrawRect(rowRect, SelectedRowBackground);
            }
            else if ((visibleIndex & 1) == 0)
            {
                EditorGUI.DrawRect(rowRect, RowAlternateBackground);
            }

            float x = rowRect.x;
            for (int c = 0; c < ColumnWidths.Length; c++)
            {
                Rect cell = new Rect(x, rowRect.y, ColumnWidths[c], rowRect.height);
                GUI.Label(cell, GetCellText(visibleIndex, e, c), EditorStyles.miniLabel);
                x += ColumnWidths[c];
            }
        }

        private void HandleListClick(Rect viewport, int count)
        {
            Event evt = Event.current;
            if (evt == null || evt.type != EventType.MouseDown || evt.button != 0)
            {
                return;
            }

            float localY = evt.mousePosition.y - viewport.y;
            if (localY < 0f || localY >= viewport.height)
            {
                return;
            }

            int clickedRow = (int)((localY + _scrollPosition.y) / RowHeight);
            if (clickedRow < 0 || clickedRow >= count)
            {
                return;
            }

            if (TrySelectVisibleEvent(clickedRow))
            {
                evt.Use();
                Repaint();
            }
        }

        private void DrawDetailPanel()
        {
            EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);

            if (!TryGetSelectedEvent(out TraceEvent e))
            {
                EditorGUILayout.LabelField("No event selected");
                return;
            }

            EditorGUI.BeginDisabledGroup(true);

            EditorGUILayout.LongField("Timestamp", e.Timestamp);
            EditorGUILayout.LongField("FrameId", e.FrameId);
            EditorGUILayout.LongField("FixedStepId", e.FixedStepId);
            EditorGUILayout.IntField("ThreadId", e.ThreadId);

            EditorGUILayout.LongField("Slash", e.SlashId);
            EditorGUILayout.LongField("SlashGeneration", (long)e.SlashGeneration);
            EditorGUILayout.LongField("FrontEdge", e.FrontEdgeId);
            EditorGUILayout.LongField("Object", e.ObjectId);
            EditorGUILayout.LongField("ObjectGeneration", (long)e.ObjectGeneration);

            EditorGUILayout.LongField("Mob", e.MobId);
            EditorGUILayout.LongField("PlanGeneration", (long)e.PlanGeneration);
            EditorGUILayout.LongField("Task", e.TaskId);

            EditorGUILayout.LongField("Capture", e.CaptureFrameId);
            EditorGUILayout.LongField("OpenXR", e.OpenXRFrameId);
            EditorGUILayout.LongField("TestRun", e.TestRunId);

            EditorGUILayout.EnumPopup("EventType", e.EventType);
            EditorGUILayout.EnumPopup("TaskType", e.TaskType);

            EditorGUILayout.IntField("FromState", e.FromState);
            EditorGUILayout.IntField("ToState", e.ToState);
            EditorGUILayout.EnumPopup("Reason", e.Reason);

            EditorGUILayout.DoubleField("Value0", e.Value0);
            EditorGUILayout.DoubleField("Value1", e.Value1);

            EditorGUI.EndDisabledGroup();
        }

        private string GetCellText(int visibleIndex, in TraceEvent e, int column)
        {
            switch (column)
            {
                case 0: return e.Timestamp.ToString();
                case 1: return e.FrameId.ToString();
                case 2: return _model.GetVisibleLaneKey(visibleIndex).ToString();
                case 3: return e.EventType.ToString();
                case 4: return e.FromState.ToString();
                case 5: return e.ToState.ToString();
                case 6: return e.Reason.ToString();
                case 7: return e.Value0.ToString();
                case 8: return e.Value1.ToString();
                default: return string.Empty;
            }
        }

        private static Color GetEventColor(TraceEventType eventType)
        {
            switch (eventType)
            {
                case TraceEventType.BladeTrackingLost:
                case TraceEventType.EdgeGateRejected:
                case TraceEventType.FrontSampleIgnored:
                case TraceEventType.FrontTopologyRejected:
                case TraceEventType.SlashFinalizedByReversal:
                case TraceEventType.SlashFrontExpired:
                case TraceEventType.PredictionRejected:
                case TraceEventType.MobPredictionRejected:
                case TraceEventType.CaptureFrameDropped:
                case TraceEventType.CommitRejected:
                case TraceEventType.FallbackActivated:
                case TraceEventType.TaskCancelled:
                    return RejectMarker;

                case TraceEventType.BladeTrackingRestored:
                case TraceEventType.EdgeGateEntered:
                case TraceEventType.SlashPrimed:
                case TraceEventType.SlashLatched:
                case TraceEventType.SlashRecoveryStarted:
                case TraceEventType.SlashRearmed:
                case TraceEventType.BladeSamplesReset:
                    return StateMarker;

                case TraceEventType.SlashFrontCreated:
                case TraceEventType.FrontVertexAdded:
                case TraceEventType.FrontEdgeActivated:
                case TraceEventType.SlashFinalized:
                case TraceEventType.FrontHitConfirmed:
                case TraceEventType.CandidateDetected:
                case TraceEventType.TaskScheduled:
                case TraceEventType.TaskStarted:
                case TraceEventType.TaskCompleted:
                case TraceEventType.PredictionValidated:
                case TraceEventType.GenerationChanged:
                case TraceEventType.MobPlanCreated:
                case TraceEventType.MobPlanExtended:
                case TraceEventType.MobTierChanged:
                case TraceEventType.ReservationCreated:
                case TraceEventType.MobPlanInvalidated:
                case TraceEventType.MobReplanned:
                case TraceEventType.MobPredictionUsed:
                case TraceEventType.CaptureFrameQueued:
                case TraceEventType.CaptureFrameEncoded:
                case TraceEventType.CaptureRingFrozen:
                case TraceEventType.ProjectionCaptureCopied:
                case TraceEventType.CommitStarted:
                case TraceEventType.CommitSucceeded:
                case TraceEventType.ResultDisposed:
                    return SuccessMarker;

                default:
                    return DefaultMarker;
            }
        }

        private void DrawFilterLongRow(string label, ref bool enabled, ref long value)
        {
            EditorGUILayout.BeginHorizontal();
            enabled = EditorGUILayout.ToggleLeft(label, enabled, GUILayout.Width(160f));
            if (enabled)
            {
                value = EditorGUILayout.LongField(value, GUILayout.Width(200f));
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawFilterUIntRow(string label, ref bool enabled, ref long value)
        {
            EditorGUILayout.BeginHorizontal();
            enabled = EditorGUILayout.ToggleLeft(label, enabled, GUILayout.Width(160f));
            if (enabled)
            {
                long edited = EditorGUILayout.LongField(value, GUILayout.Width(200f));
                value = ClampToUInt(edited); // clamp to [0, uint.MaxValue]
            }
            EditorGUILayout.EndHorizontal();
        }

        private TraceTimelineFilter BuildFilter()
        {
            return new TraceTimelineFilter(
                _slashIdEnabled ? (long?)_slashIdValue : null,
                _objectIdEnabled ? (long?)_objectIdValue : null,
                _objectGenerationEnabled ? (uint?)ClampToUInt(_objectGenerationValue) : null,
                _mobIdEnabled ? (long?)_mobIdValue : null,
                _planGenerationEnabled ? (uint?)ClampToUInt(_planGenerationValue) : null,
                _taskIdEnabled ? (long?)_taskIdValue : null,
                _eventTypeEnabled ? (TraceEventType?)_eventTypeValue : null,
                _reasonEnabled ? (TraceReason?)_reasonValue : null);
        }

        private void SyncFilterPanelFromFilter()
        {
            TraceTimelineFilter f = _model.Filter;
            _slashIdEnabled = f.SlashId.HasValue;
            _slashIdValue = f.SlashId ?? 0L;
            _objectIdEnabled = f.ObjectId.HasValue;
            _objectIdValue = f.ObjectId ?? 0L;
            _objectGenerationEnabled = f.ObjectGeneration.HasValue;
            _objectGenerationValue = f.ObjectGeneration ?? 0U;
            _mobIdEnabled = f.MobId.HasValue;
            _mobIdValue = f.MobId ?? 0L;
            _planGenerationEnabled = f.PlanGeneration.HasValue;
            _planGenerationValue = f.PlanGeneration ?? 0U;
            _taskIdEnabled = f.TaskId.HasValue;
            _taskIdValue = f.TaskId ?? 0L;
            _eventTypeEnabled = f.EventType.HasValue;
            _eventTypeValue = f.EventType ?? TraceEventType.None;
            _reasonEnabled = f.Reason.HasValue;
            _reasonValue = f.Reason ?? TraceReason.None;
        }
    }
}
