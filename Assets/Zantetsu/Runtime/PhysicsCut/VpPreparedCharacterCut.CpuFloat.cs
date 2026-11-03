namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The CPU-only float input of a prepared character's synchronous bake (CPU-SKIN-FLOAT-1, <c>-zantetsuSkinBake cpu-float</c>):
    /// what the owner prepares cold, what the owner says of the body's drawing, and what the last request recorded of its
    /// bake -- the route asked for and the route taken, the bake's own time, the time to the storage and the time of the
    /// display's show -- for the benches. Nothing of the order of a hit changes: the float input is read where the 24-byte
    /// input was, inside the same synchronous append.
    /// </summary>
    public sealed partial class VpPreparedCharacterCut
    {
        /// <summary>
        /// Prepares the model's CPU-only float input (cold: with the handle, never at a hit); once a model, shared by its
        /// individuals. False, with the reason, when the mesh is outside the accepted conditions: the hits then take the
        /// 24-byte bake and say so (<see cref="DeferredFallbackReason"/>).
        /// </summary>
        public bool TryPrepareFloatSkinInput(out string refusal)
        {
            refusal = null;
            if (disposed || direct == null || direct.IsDisposed) { refusal = "no direct skin input"; return false; }
            bool ok = direct.Model.TryPrepareFloatInput(out refusal);
            floatSkinInputRefusal = refusal;
            return ok;
        }

        string floatSkinInputRefusal;

        /// <summary>Why the model's float input was refused at this handle's preparation (CPU-SKIN-FLOAT-DEFAULT-1); null when made, or not asked (another mode).</summary>
        public string FloatSkinInputRefusal => floatSkinInputRefusal;

        /// <summary>
        /// Set by the character's owner while the body is drawn by the skin draw (DESIGN 4.5.12), whose vertex stage reads the
        /// 24-byte input: a cut of such a body keeps the 24-byte bake, so that the cut's geometry is the display's.
        /// </summary>
        public bool DisplayedBySkinDraw { get; set; }

        /// <summary>The route the last request took ("cpu", "cpu-float", "float", "gpu"); null before any, or when it ended before the bake.</summary>
        public string LastRoute { get; private set; }

        /// <summary>The route the launch asked for at the last request.</summary>
        public string LastRequestedRoute { get; private set; }

        /// <summary>The last 24-byte / cpu-float bake's kernel time (the skinning alone), seconds; NaN = not measured (the GPU-first and the experimental float routes, or no request yet).</summary>
        public double LastBakeKernelSeconds { get; private set; } = double.NaN;

        /// <summary>The last 24-byte / cpu-float append's time (the kernel, the index copy, the storage's spans and publication), seconds; NaN = not measured.</summary>
        public double LastBakeAppendSeconds { get; private set; } = double.NaN;

        /// <summary>The last 24-byte / cpu-float request's display show (the root shown, the vertices and indices transferred to the GPU copy), seconds; NaN = not measured.</summary>
        public double LastShowSeconds { get; private set; } = double.NaN;
    }
}
