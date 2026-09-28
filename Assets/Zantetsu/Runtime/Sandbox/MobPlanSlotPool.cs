using System.Collections.Generic;

namespace Zantetsu.Sandbox
{
    /// <summary>What a crowd's slot pool asks of one prepared slot (a <see cref="SandboxNpcCharacter"/>; a fake in tests).</summary>
    public interface IMobPlanSlot
    {
        /// <summary>Prepared and dormant: it may be activated.</summary>
        bool IsPrepared { get; }

        /// <summary>Why it could not be prepared, or null.</summary>
        string Failure { get; }

        /// <summary>The individual it carried has let go of it: nothing of its cut refers to the slot any more.</summary>
        bool IsReturnReady { get; }

        /// <summary>Prepares it again for a new individual; false when it could not be.</summary>
        bool TryReprepare();
    }

    /// <summary>
    /// A crowd's fixed set of prepared slots. Slots are added only before the scenario; from then on a slot is free
    /// (prepared, dormant), taken (carrying an individual), returning (its individual retired, waiting until it is let go),
    /// preparing (prepared again, or for the first time), or broken. <see cref="Advance"/> moves prepared slots to the
    /// free ones, and prepares at most one returning slot again per call once it is let go. <see cref="TryTake"/> gives
    /// the first free slot, or nothing -- it neither makes a slot nor waits for one.
    /// </summary>
    public sealed class MobPlanSlotPool<TSlot> where TSlot : class, IMobPlanSlot
    {
        private readonly List<TSlot> _slots = new List<TSlot>();
        private readonly List<TSlot> _free = new List<TSlot>();
        private readonly List<TSlot> _returning = new List<TSlot>();
        private readonly List<TSlot> _preparing = new List<TSlot>();
        private readonly List<TSlot> _broken = new List<TSlot>();

        public IReadOnlyList<TSlot> Slots => _slots;
        public IReadOnlyList<TSlot> Broken => _broken;
        public int SlotCount => _slots.Count;
        public int FreeCount => _free.Count;
        public int ReturningCount => _returning.Count;
        public int PreparingCount => _preparing.Count;
        public int BrokenCount => _broken.Count;

        /// <summary>Takes that found no free slot.</summary>
        public int Refused { get; private set; }

        /// <summary>Slots prepared again after an individual.</summary>
        public int Reprepared { get; private set; }

        /// <summary>Every slot added has finished its preparation, or failed it.</summary>
        public bool AllPrepared => _slots.Count > 0 && _preparing.Count == 0;

        /// <summary>A new slot, being prepared.</summary>
        public void Add(TSlot slot)
        {
            _slots.Add(slot);
            _preparing.Add(slot);
        }

        /// <summary>
        /// Prepared slots become free (in the slots' own order when the first preparation of all of them ends); with
        /// <paramref name="prepareAgain"/>, the first returning slot that is let go is prepared again.
        /// </summary>
        public void Advance(bool prepareAgain)
        {
            bool wasPreparing = _preparing.Count > 0;
            for (int i = 0; i < _preparing.Count; i++)
            {
                TSlot slot = _preparing[i];
                if (slot.IsPrepared) { _preparing.RemoveAt(i--); _free.Add(slot); }
                else if (slot.Failure != null) { _preparing.RemoveAt(i--); _broken.Add(slot); }
            }

            if (wasPreparing && _preparing.Count == 0)
            {
                _free.Sort((a, b) => _slots.IndexOf(a).CompareTo(_slots.IndexOf(b)));
            }

            if (!prepareAgain)
            {
                return;
            }

            for (int i = 0; i < _returning.Count; i++)
            {
                TSlot slot = _returning[i];
                if (!slot.IsReturnReady)
                {
                    continue;
                }

                _returning.RemoveAt(i);
                if (slot.TryReprepare()) { _preparing.Add(slot); Reprepared++; }
                else _broken.Add(slot);
                return;
            }
        }

        /// <summary>The first free slot, taken; false, taking nothing, when there is none.</summary>
        public bool TryTake(out TSlot slot)
        {
            if (_free.Count == 0)
            {
                slot = null;
                Refused++;
                return false;
            }

            slot = _free[0];
            _free.RemoveAt(0);
            return true;
        }

        /// <summary>A taken slot whose individual retired: it waits until it is let go.</summary>
        public void Return(TSlot slot)
        {
            if (slot != null && _slots.Contains(slot) && !_returning.Contains(slot)) _returning.Add(slot);
        }

        /// <summary>A slot that could not be used.</summary>
        public void MarkBroken(TSlot slot)
        {
            if (slot != null && !_broken.Contains(slot)) _broken.Add(slot);
        }
    }
}
