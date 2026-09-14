namespace Zantetsu.Rendering
{
    /// <summary>The lifecycle state of an index range descriptor (DESIGN 4.5.3).</summary>
    public enum VpIndexRangeState : byte
    {
        /// <summary>Holds no live range; the descriptor can be registered again.</summary>
        Free,

        /// <summary>Registered and not yet published. Read leases are refused.</summary>
        Reserved,

        /// <summary>Published read-only. Read leases can be taken.</summary>
        Published,

        /// <summary>Retired while leases were held. New leases are refused; the last returned lease frees the range.</summary>
        Retiring,
    }
}
