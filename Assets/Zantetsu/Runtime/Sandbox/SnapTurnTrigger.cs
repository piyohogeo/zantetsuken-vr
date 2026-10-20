using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A stick's snap turns (2026-10-20): one turn a deflection. A turn fires when the stick's axis reaches
    /// <see cref="FireThreshold"/>, and the next only after it has come back below <see cref="ReleaseThreshold"/> --
    /// two thresholds, so that a stick wavering about one of them fires once. Held over, it fires nothing more (no
    /// repeat). After <see cref="RequireCentre"/> (the input given back after the menu, the application's focus back,
    /// the turn's way changed) nothing fires until the stick has been at the centre once, whatever it stands at then.
    /// </summary>
    public sealed class SnapTurnTrigger
    {
        public const float FireThreshold = 0.7f;
        public const float ReleaseThreshold = 0.35f;

        private bool _armed;

        /// <summary>A new trigger waits for the centre first: a stick held at the start turns nothing.</summary>
        public SnapTurnTrigger()
        {
            _armed = false;
        }

        /// <summary>Whether the next deflection to the fire threshold turns.</summary>
        public bool Armed => _armed;

        /// <summary>Nothing fires until the stick has come back below the release threshold.</summary>
        public void RequireCentre()
        {
            _armed = false;
        }

        /// <summary>This frame's turn: +1 to the right, -1 to the left, 0 none.</summary>
        public int Step(float axis)
        {
            if (float.IsNaN(axis) || float.IsInfinity(axis))
            {
                _armed = false;   // an axis not read is no centre and no deflection
                return 0;
            }

            float deflection = Mathf.Abs(axis);
            if (!_armed)
            {
                if (deflection < ReleaseThreshold) _armed = true;
                return 0;
            }

            if (deflection < FireThreshold) return 0;
            _armed = false;
            return axis > 0f ? 1 : -1;
        }
    }
}
