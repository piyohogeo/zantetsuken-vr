using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One stencil colour's share of the two common transfers: which of the uploaded volume commands belong to it, and
    /// which run of the uploaded cap indices does (DESIGN 5.6).
    /// <para>
    /// A colour owns a range, not a buffer. Everything for every colour goes across in one transfer per kind, and what
    /// separates the colours at drawing time is the start and the count given to that colour's own draw — which is what
    /// lets a colour be drawn without touching a buffer and without a transfer of its own.
    /// </para>
    /// </summary>
    public readonly struct VpStencilCapColor
    {
        public VpStencilCapColor(int volumeStart, int volumeCount, int capIndexStart, int capIndexCount, Color capColour)
        {
            this.volumeStart = volumeStart;
            this.volumeCount = volumeCount;
            this.capIndexStart = capIndexStart;
            this.capIndexCount = capIndexCount;
            this.capColour = capColour;
        }

        /// <summary>The first of this colour's volume commands, in the one uploaded command list.</summary>
        public readonly int volumeStart;

        /// <summary>How many volume commands this colour has. One CPU draw covers them all.</summary>
        public readonly int volumeCount;

        /// <summary>The first of this colour's cap indices, in the one uploaded cap index buffer.</summary>
        public readonly int capIndexStart;

        /// <summary>How many cap indices this colour has. One CPU draw covers them all.</summary>
        public readonly int capIndexCount;

        /// <summary>
        /// The colour this colour's caps are painted. A plain fixed colour: the common toon material and the grey and
        /// red of DESIGN 5.3 are no part of this, and a colour difference never splits a group (DESIGN 5.6).
        /// </summary>
        public readonly Color capColour;

        public bool HasVolumes => volumeCount > 0;

        public bool HasCaps => capIndexCount > 0;
    }
}
