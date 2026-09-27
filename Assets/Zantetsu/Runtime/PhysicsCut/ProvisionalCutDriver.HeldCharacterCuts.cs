using System.Collections.Generic;

namespace Zantetsu.PhysicsCut
{
    public sealed partial class ProvisionalCutDriver
    {
        // Prepared characters' requests held before acceptance (ProvisionalCutAcceptance.Held), in the order they were
        // held. A request here has no Operation and nothing in the ledger but its character's fragment; what it keeps is
        // the handle's own (the pose the hit met, its plane and impulses).
        private readonly List<VpPreparedCharacterCut> _heldCharacterCuts = new List<VpPreparedCharacterCut>(4);
        private readonly List<VpPreparedCharacterCut> _resuming = new List<VpPreparedCharacterCut>(4);

        /// <summary>How many prepared characters' requests are held before acceptance now.</summary>
        public int HeldCharacterCutCount => _heldCharacterCuts.Count;

        internal void HoldCharacterCut(VpPreparedCharacterCut cut)
        {
            if (!_heldCharacterCuts.Contains(cut))
            {
                _heldCharacterCuts.Add(cut);
            }
        }

        internal void ForgetHeldCharacterCut(VpPreparedCharacterCut cut)
        {
            _heldCharacterCuts.Remove(cut);
        }

        /// <summary>
        /// Once per update, before the asks: each held request is taken up once, in the order it was held, as the same
        /// request -- no new hit, no second classification of a hit, no second acceptance. One that is short of room
        /// that will come back again stays held for the next update; one that goes on reaches the ordinary acceptance.
        /// </summary>
        private void ResumeHeldCharacterCuts()
        {
            if (_heldCharacterCuts.Count == 0 || (_latch != null && _latch.TerminationRequested))
            {
                return;
            }

            _resuming.Clear();
            _resuming.AddRange(_heldCharacterCuts);
            for (int i = 0; i < _resuming.Count; i++)
            {
                _resuming[i].ResumeHeld();
            }

            _resuming.Clear();
            for (int i = _heldCharacterCuts.Count - 1; i >= 0; i--)
            {
                if (!_heldCharacterCuts[i].IsHeld)
                {
                    _heldCharacterCuts.RemoveAt(i);
                }
            }
        }

        /// <summary>The ordinary ending of every held request: each ends as its handle's, and nothing of it stays here.</summary>
        private void EndHeldCharacterCuts()
        {
            _resuming.Clear();
            _resuming.AddRange(_heldCharacterCuts);
            _heldCharacterCuts.Clear();
            for (int i = 0; i < _resuming.Count; i++)
            {
                _resuming[i].EndHold();
            }

            _resuming.Clear();
        }
    }
}
