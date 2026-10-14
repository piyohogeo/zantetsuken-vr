#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Globalization;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// What the collection of cut fragments is doing in play, said every few seconds in the log (Editor and Development
    /// builds only; nothing of it in a Release Player). Play has no ending, and the collection's own record is written
    /// at a world's end: without this a play that fills up with fragments says nothing of why none are collected.
    /// One line: the fragment group collection's record in words -- the live cut fragments against its maximum, the
    /// turns, the groups looked at and retired, and how many turns were held and for what (a cut in flight, a
    /// structure change, the budget short...) -- then the empty owners' and the piece lifetime's retirements, and
    /// what was left of the frame's Main budget at the end of the frame's scripts over the frames since the last line.
    /// It reads counters the collections already keep and decides nothing.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class PlayCollectionReadout : MonoBehaviour
    {
        public const string Prefix = "PLAY COLLECTION: ";
        public const float EverySeconds = 5f;

        private static PlayCollectionReadout s_instance;

        private CutWorldRoot _world;
        private float _next;
        private int _frames, _underReserve, _spent;
        private double _leftSum, _leftLeast = double.PositiveInfinity;
        private float _longestIntervalMs;

        public static PlayCollectionReadout Install()
        {
            if (s_instance != null) return s_instance;
            var host = new GameObject("Zantetsu play collection readout");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            s_instance = host.AddComponent<PlayCollectionReadout>();
            return s_instance;
        }

        // After the frame's other scripts: what is left of the Main budget here is no more than what the collection had.
        private void LateUpdate()
        {
            double left = CutPhysicsStep.FrameRemainingMainSeconds;
            if (!double.IsInfinity(left))
            {
                _frames++;
                _leftSum += left;
                if (left < _leftLeast) _leftLeast = left;
                if (left < CutWorldRoot.CompactionReserveSeconds) _underReserve++;
                if (left <= 0.0) _spent++;
            }

            float intervalMs = Time.unscaledDeltaTime * 1000f;
            if (intervalMs > _longestIntervalMs) _longestIntervalMs = intervalMs;
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + EverySeconds;
            if (_world == null || !_world.IsReady)
            {
                _world = FindFirstObjectByType<CutWorldRoot>();
            }

            if (_world == null || !_world.IsReady) return;
            // Two lines: a device's log cuts a line at about a thousand characters.
            Debug.Log(Prefix + (_world.Groups != null ? _world.Groups.Describe() : "no fragment group collection in this world"));
            Debug.Log(Prefix + "now: " + Describe());
            _frames = _underReserve = _spent = 0;
            _leftSum = 0.0;
            _leftLeast = double.PositiveInfinity;
            _longestIntervalMs = 0f;
        }

        public string Describe()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            var b = new System.Text.StringBuilder(1024);
            CutFragmentGroupCollection groups = _world.Groups;
            b.Append("live cut fragments ").Append(_world.Ledger != null ? _world.Ledger.LiveCutFragmentCount : 0)
                .Append(", groups known ").Append(groups != null ? groups.GroupCount : 0)
                .Append("; whole groups retired ").Append(groups != null ? groups.GroupsRetired : 0)
                .Append(", fragments retired ").Append(groups != null ? groups.FragmentsRetired : 0)
                .Append(" (by pieces ").Append(groups != null ? groups.PiecesRetired : 0).Append(" in ").Append(groups != null ? groups.PieceTurns : 0)
                .Append(" turns; pieces seen ").Append(groups != null ? groups.PiecesSeen : 0).Append(", not ready ").Append(groups != null ? groups.PiecesNotReady : 0).Append(')');
            b.Append(" | last hold ").Append(groups != null ? groups.LastHold.ToString() : "-");
            b.Append(" | empty owners retired ").Append(_world.EmptyOwners != null ? _world.EmptyOwners.Retired : 0)
                .Append(", piece lifetime retired ").Append(_world.Lifetime != null ? _world.Lifetime.Retired : 0);
            b.Append(" | Main budget ").Append((CutPhysicsStep.MainBudgetSeconds * 1000.0).ToString("F1", c)).Append(" ms a frame; left of it at the end of the frame's scripts in the last ")
                .Append(_frames).Append(" frames: mean ").Append(_frames > 0 ? (_leftSum / _frames * 1000.0).ToString("F2", c) : "-")
                .Append(" ms, least ").Append(_frames > 0 ? (_leftLeast * 1000.0).ToString("F2", c) : "-")
                .Append(" ms; under the reserve of ").Append((CutWorldRoot.CompactionReserveSeconds * 1000.0).ToString("F1", c)).Append(" ms in ").Append(_underReserve)
                .Append(" frames, nothing left in ").Append(_spent).Append("; longest frame interval ").Append(_longestIntervalMs.ToString("F1", c)).Append(" ms")
                .Append(GamePause.IsPaused ? "; paused now" : "");
            return b.ToString();
        }

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }
    }
}
#endif
