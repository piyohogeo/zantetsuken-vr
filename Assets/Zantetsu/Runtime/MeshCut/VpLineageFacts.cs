using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The ledger's facts of each fragment the ancestor walks of one structure build have read (2026-10-01): its origin and
    /// side, whether the cut's operation is known, and the cut's source and plane. Opened for one ledger by the structural
    /// validation and closed when that build's structure part ends; while open, each fragment is read from the ledger once
    /// and its facts kept for the rest of the build (a stamp tells this build's from an earlier one's). Closed, or asked of
    /// another ledger, every fact is read from the ledger and nothing is kept. Nothing is held across builds: each build
    /// opens it again before reading anything, so a ledger changed between builds, another ledger, or a build that failed
    /// leaves nothing to be read later.
    /// </summary>
    internal sealed class VpLineageFacts
    {
        private int[] _stamp = System.Array.Empty<int>();
        private byte[] _state = System.Array.Empty<byte>();   // 1 no origin, 2 origin and its operation, 3 origin, operation not found
        private CutOperationId[] _origin = System.Array.Empty<CutOperationId>();
        private float[] _side = System.Array.Empty<float>();
        private LogicalFragmentId[] _source = System.Array.Empty<LogicalFragmentId>();
        private float4[] _plane = System.Array.Empty<float4>();
        private int _current;
        private LogicalCutLedger _ledger;

        /// <summary>Tests only: nothing kept, every fact read from the ledger at every visit.</summary>
        internal bool offForTest;

        /// <summary>Opens a new build's facts for <paramref name="ledger"/>: none of an earlier build's is read again.</summary>
        public void Open(LogicalCutLedger ledger)
        {
            int needed = ledger.FragmentCount + 2;
            if (_stamp.Length < needed)
            {
                int grown = System.Math.Max(needed, _stamp.Length * 2);
                _stamp = new int[grown];
                _state = new byte[grown];
                _origin = new CutOperationId[grown];
                _side = new float[grown];
                _source = new LogicalFragmentId[grown];
                _plane = new float4[grown];
                _current = 0;
            }

            _current++;
            _ledger = ledger;
        }

        /// <summary>Ends the build's facts: until the next <see cref="Open"/>, every fact is read from the ledger.</summary>
        public void Close() => _ledger = null;

        /// <summary>
        /// The fragment's origin (false: none) and, when it has one, whether its operation is known, with the cut's source
        /// and plane. Counts a fact read from the ledger in <paramref name="reads"/> and one kept from earlier in this build
        /// in <paramref name="hits"/>.
        /// </summary>
        public bool TryGet(
            LogicalCutLedger ledger, LogicalFragmentId at,
            out CutOperationId origin, out float side, out bool operationKnown, out LogicalFragmentId source, out float4 plane,
            ref long reads, ref long hits)
        {
            int i = at.value;
            bool held = !offForTest && _ledger != null && ReferenceEquals(ledger, _ledger) && (uint)i < (uint)_stamp.Length;
            if (held && _stamp[i] == _current)
            {
                hits++;
                byte s = _state[i];
                origin = _origin[i];
                side = _side[i];
                source = _source[i];
                plane = _plane[i];
                operationKnown = s == 2;
                return s != 1;
            }

            reads++;
            byte state;
            origin = default; side = 0f; source = default; plane = default;
            if (!ledger.TryGetOrigin(at, out origin, out side)) state = 1;
            else if (!ledger.TryGetOperation(origin, out LogicalCutOperation cut)) state = 3;
            else { state = 2; source = cut.source; plane = cut.plane; }
            if (held)
            {
                _stamp[i] = _current;
                _state[i] = state;
                _origin[i] = origin;
                _side[i] = side;
                _source[i] = source;
                _plane[i] = plane;
            }

            operationKnown = state == 2;
            return state != 1;
        }
    }
}
