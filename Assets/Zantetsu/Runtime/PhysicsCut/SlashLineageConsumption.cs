using System;
using System.Collections.Generic;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// What each live Slash has consumed (DESIGN 19.1.9): the fragments it hit directly, and nothing else. A fragment
    /// is consumed for a Slash when it or one of its ancestors is in that Slash's set, which is found by walking the
    /// ledger's own origin relation -- the operation that made a fragment, and that operation's source -- as many steps
    /// as the lineage has (DESIGN 7.1.2).
    /// <para>
    /// **Nothing else is kept.** No lineage cache, no notice of children being published, no second ledger and no
    /// record on a fragment: a child a Slash's own cut made is found consumed because its origin leads back to what that
    /// Slash hit. A fragment with no ancestor in the set -- another branch of the same object, or one never cut -- is
    /// not consumed, and a different Slash has a set of its own.
    /// </para>
    /// <para>
    /// **One slot per live wave.** There are as many slots as the core has wave capacity; a slot is given to a
    /// SlashId the first time it consumes something and taken back, cleared, once that Slash is no longer live
    /// (<see cref="KeepOnly"/>). The sets keep their storage between Slashes.
    /// </para>
    /// </summary>
    public sealed class SlashLineageConsumption
    {
        private readonly LogicalCutLedger _ledger;
        private readonly long[] _slashOf;
        private readonly List<LogicalFragmentId>[] _consumed;

        public SlashLineageConsumption(LogicalCutLedger ledger, int slots)
        {
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            if (slots <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slots), "at least one live Slash has to fit");
            }

            _slashOf = new long[slots];
            _consumed = new List<LogicalFragmentId>[slots];
            for (int i = 0; i < slots; i++)
            {
                _consumed[i] = new List<LogicalFragmentId>(8);
            }
        }

        /// <summary>
        /// Ends the set of every Slash not among <paramref name="live"/>: a Slash that has expired gives its set back
        /// here, and nothing of it is kept.
        /// </summary>
        public void KeepOnly(ReadOnlySpan<long> live)
        {
            for (int s = 0; s < _slashOf.Length; s++)
            {
                if (_slashOf[s] == 0)
                {
                    continue;
                }

                bool kept = false;
                for (int i = 0; i < live.Length && !kept; i++)
                {
                    kept = live[i] == _slashOf[s];
                }

                if (!kept)
                {
                    _slashOf[s] = 0;
                    _consumed[s].Clear();
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="slashId"/> has consumed <paramref name="fragment"/> or one of its ancestors.
        /// Reading it changes nothing.
        /// </summary>
        public bool IsConsumed(long slashId, LogicalFragmentId fragment)
        {
            int slot = slashId > 0 ? SlotOf(slashId) : -1;
            return slot >= 0 && Covers(_consumed[slot], fragment);
        }

        /// <summary>
        /// Consumes <paramref name="fragment"/> for <paramref name="slashId"/>, unless it or an ancestor already is:
        /// true when it was added now, which is the one time the hit is passed on (DESIGN 19.1.9 5). False for one
        /// consumed already, whatever became of the hit that consumed it.
        /// </summary>
        public bool TryConsume(long slashId, LogicalFragmentId fragment)
        {
            if (slashId <= 0 || !fragment.IsSet)
            {
                throw new ArgumentOutOfRangeException(nameof(slashId), "a Slash and a fragment are both set");
            }

            int slot = SlotOf(slashId);
            if (slot < 0)
            {
                slot = FreeSlot();
                if (slot < 0)
                {
                    // More live Slashes than the core can hold: not an input, an internal inconsistency.
                    throw new InvalidOperationException("no consumption slot for Slash " + slashId);
                }

                _slashOf[slot] = slashId;
            }
            else if (Covers(_consumed[slot], fragment))
            {
                return false;
            }

            _consumed[slot].Add(fragment);
            return true;
        }

        /// <summary>How many fragments a Slash has consumed directly. For tests and readouts.</summary>
        public int ConsumedCount(long slashId)
        {
            int slot = slashId > 0 ? SlotOf(slashId) : -1;
            return slot < 0 ? 0 : _consumed[slot].Count;
        }

        /// <summary>How many Slashes hold a set now. For tests and readouts.</summary>
        public int LiveSets
        {
            get
            {
                int live = 0;
                for (int s = 0; s < _slashOf.Length; s++)
                {
                    live += _slashOf[s] != 0 ? 1 : 0;
                }

                return live;
            }
        }

        private int SlotOf(long slashId)
        {
            for (int s = 0; s < _slashOf.Length; s++)
            {
                if (_slashOf[s] == slashId)
                {
                    return s;
                }
            }

            return -1;
        }

        private int FreeSlot()
        {
            return SlotOf(0);
        }

        // The fragment itself, then each ancestor through the operation that made it and that operation's source.
        private bool Covers(List<LogicalFragmentId> consumed, LogicalFragmentId fragment)
        {
            LogicalFragmentId at = fragment;
            while (true)
            {
                if (consumed.Contains(at))
                {
                    return true;
                }

                if (!_ledger.TryGetOrigin(at, out CutOperationId origin, out _)
                    || !_ledger.TryGetOperation(origin, out LogicalCutOperation made))
                {
                    return false;
                }

                at = made.source;
            }
        }
    }
}
