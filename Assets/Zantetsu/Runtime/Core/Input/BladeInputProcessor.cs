using System;
using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Stateful, allocation-free processor connecting pose sampling, grip-to-
    /// katana adaptation, sample windowing, motion evaluation, and the edge
    /// direction gate, while tracking usable-tracking state. Main thread only.
    /// </summary>
    public sealed class BladeInputProcessor
    {
        private readonly BladePoseWindow _window;
        private readonly BladeEdgeGateSettings _gateSettings;
        private bool _hasObservedTracking;
        private bool _usableTracking;

        public BladeInputProcessor(int windowCapacity, in BladeEdgeGateSettings gateSettings)
        {
            if (windowCapacity < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(windowCapacity), windowCapacity, "Window capacity must be at least 2.");
            }

            if (!BladeEdgeGateSettings.IsValid(gateSettings))
            {
                throw new ArgumentException("Gate settings are invalid.", nameof(gateSettings));
            }

            _window = new BladePoseWindow(windowCapacity);
            _gateSettings = gateSettings;
            _hasObservedTracking = false;
            _usableTracking = false;
        }

        public int WindowCount => _window.Count;

        public bool HasUsableTracking => _usableTracking;

        /// <summary>
        /// Processes a blade pose sample and returns a status, a tracking
        /// transition, and any accumulated evaluation results.
        /// </summary>
        public BladeInputProcessingResult Process(
            in BladePoseSample sample,
            in Pose gripToKatanaOffset,
            in BladeFrame bladeFrame)
        {
            if (!sample.IsFullyTracked)
            {
                _window.Clear();
                return new BladeInputProcessingResult(
                    BladeInputProcessingStatus.WaitingForTracking,
                    TrackUsability(false),
                    default,
                    default,
                    default);
            }

            if (!BladePoseAdapter.TryEvaluate(sample, gripToKatanaOffset, bladeFrame, out EvaluatedBladePose evaluatedPose))
            {
                _window.Clear();
                return new BladeInputProcessingResult(
                    BladeInputProcessingStatus.InvalidSample,
                    TrackUsability(false),
                    default,
                    default,
                    default);
            }

            if (!_window.TryAppend(evaluatedPose))
            {
                // BladePoseWindow.TryAppend already cleared the window.
                return new BladeInputProcessingResult(
                    BladeInputProcessingStatus.InvalidSample,
                    TrackUsability(false),
                    default,
                    default,
                    default);
            }

            BladeTrackingTransition transition = TrackUsability(true);

            if (!_window.TryEvaluateLatest(_gateSettings.MinimumWindowSeconds, _gateSettings.MaximumWindowSeconds, out BladeMotionSample motion))
            {
                return new BladeInputProcessingResult(
                    BladeInputProcessingStatus.WindowAccumulating,
                    transition,
                    evaluatedPose,
                    default,
                    default);
            }

            BladeEdgeGateDecision gateDecision = BladeEdgeGate.Evaluate(motion, _gateSettings);
            BladeInputProcessingStatus status = gateDecision.IsAccepted
                ? BladeInputProcessingStatus.GateAccepted
                : BladeInputProcessingStatus.GateRejected;

            return new BladeInputProcessingResult(status, transition, evaluatedPose, motion, gateDecision);
        }

        /// <summary>
        /// Clears the pose window and resets the tracking transition baseline.
        /// The first sample after a reset produces no tracking transition.
        /// </summary>
        public void Reset()
        {
            _window.Clear();
            _hasObservedTracking = false;
            _usableTracking = false;
        }

        private BladeTrackingTransition TrackUsability(bool usable)
        {
            BladeTrackingTransition transition;
            if (!_hasObservedTracking)
            {
                transition = BladeTrackingTransition.None;
            }
            else if (usable && !_usableTracking)
            {
                transition = BladeTrackingTransition.Restored;
            }
            else if (!usable && _usableTracking)
            {
                transition = BladeTrackingTransition.Lost;
            }
            else
            {
                transition = BladeTrackingTransition.None;
            }

            _hasObservedTracking = true;
            _usableTracking = usable;
            return transition;
        }
    }
}
