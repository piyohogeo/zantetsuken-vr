using System;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The identity of one logical fragment (DESIGN 7.1, 8): the unit that is cut again or retired, one per side of a
    /// published cut. It is a positive number issued by a <see cref="LogicalCutLedger"/> and is **never reused** for
    /// the lifetime of that ledger's object, so an old result naming a fragment can never be mistaken for a newer one
    /// that happened to take the same slot. Zero is "unset" and names nothing; a negative value is not an id at all
    /// and is refused where it is constructed, so no ledger ever sees one.
    /// </summary>
    public readonly struct LogicalFragmentId : IEquatable<LogicalFragmentId>
    {
        public const int Unset = 0;

        public readonly int value;

        public LogicalFragmentId(int value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "an id is zero (unset) or positive");
            }

            this.value = value;
        }

        public bool IsSet => value > Unset;

        public bool Equals(LogicalFragmentId other) => value == other.value;
        public override bool Equals(object obj) => obj is LogicalFragmentId other && Equals(other);
        public override int GetHashCode() => value;
        public static bool operator ==(LogicalFragmentId a, LogicalFragmentId b) => a.value == b.value;
        public static bool operator !=(LogicalFragmentId a, LogicalFragmentId b) => a.value != b.value;
        public override string ToString() => IsSet ? "fragment " + value : "fragment (unset)";
    }

    /// <summary>
    /// The identity of one admitted cut (DESIGN 4.2, 7.7): issued only when a request passes admission, and never for
    /// a request that was skipped. Like <see cref="LogicalFragmentId"/> it is positive, unique and never reused within
    /// its ledger, zero is unset, and a negative value is refused at construction. The operation is also what DESIGN
    /// 7.1 calls the transaction: a source has at most one active operation, and no separate transaction id or state
    /// exists beside it.
    /// </summary>
    public readonly struct CutOperationId : IEquatable<CutOperationId>
    {
        public const int Unset = 0;

        public readonly int value;

        public CutOperationId(int value)
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "an id is zero (unset) or positive");
            }

            this.value = value;
        }

        public bool IsSet => value > Unset;

        public bool Equals(CutOperationId other) => value == other.value;
        public override bool Equals(object obj) => obj is CutOperationId other && Equals(other);
        public override int GetHashCode() => value;
        public static bool operator ==(CutOperationId a, CutOperationId b) => a.value == b.value;
        public static bool operator !=(CutOperationId a, CutOperationId b) => a.value != b.value;
        public override string ToString() => IsSet ? "operation " + value : "operation (unset)";
    }
}
