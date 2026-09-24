using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Who owns the cut surface colours and the one debug switch that goes with them (DESIGN 5.3). The values live as
    /// **global shader constants**, not as material properties: the switch is one switch for every cut surface being
    /// drawn, so putting it on a material or a per-draw property block would mean setting it again for each object and
    /// getting it wrong for the one that was missed.
    /// <para>
    /// A cap's vertices carry the marker <c>uv0 = (-0.5, 0)</c> from the moment they are generated
    /// (<see cref="Zantetsu.MeshCut.RenderCutMarker"/>), and the shader reads that raw uv0 to choose between this
    /// colour and the ordinary texture. Changing the colour or the switch therefore changes **nothing** in the
    /// geometry: no vertex is rewritten, no index touched, no buffer re-made or transferred again, and no second
    /// material, submesh or shader variant appears.
    /// </para>
    /// <para>
    /// **When these values are put in place.** A play session always begins from the defaults — grey, green, switch
    /// off — because <see cref="BeginPlaySession"/> runs before the first scene loads, on entering Play Mode and in a
    /// player alike. That does not depend on the domain being reloaded: with Domain Reload turned off the statics of
    /// this class survive from edit mode, and the session start puts the defaults back over them anyway, so a debug
    /// switch left on in edit mode never leaks into a play session. Within a session nothing resets what has been set:
    /// building another display, or any call to <see cref="EnsureInitialized"/>, leaves the current colours and switch
    /// exactly as they are. Nothing here runs per frame or per object, and no draw applies the defaults.
    /// </para>
    /// <para>
    /// **Two cut surfaces, one switch.** A real cap committed into the geometry reads <see cref="Current"/>: the
    /// ordinary colour, or the debug colour (green) while the switch is on. A temporary cut face drawn through the
    /// stencil reads <see cref="CurrentProvisional"/>: the same ordinary colour, or
    /// <see cref="DefaultProvisionalDebugColour"/> (red) while that same switch is on. There is one switch for both, and
    /// the ordinary colour is the same colour definition for both; only the two debug colours differ, which is what
    /// tells a temporary cut face from a real one while debugging.
    /// </para>
    /// <para>
    /// This is the colour choice alone. Both kinds of cut surface then go through the one shading the VP surfaces share
    /// (<c>VpShadeSurface</c> in VpCutSurfaceShading.hlsl), a temporary cap with its own outward normal. The common toon
    /// shading of DESIGN 5.3 -- its shading steps, outline and light response -- is still not part of this.
    /// </para>
    /// </summary>
    public static class VpCutSurfaceColour
    {
        /// <summary>The ordinary look: a low-saturation grey, the same for every cut surface.</summary>
        public static readonly Color DefaultColour = new Color(0.32f, 0.33f, 0.35f, 1f);

        /// <summary>What a real cap becomes while the debug switch is on.</summary>
        public static readonly Color DefaultDebugColour = new Color(0.15f, 0.75f, 0.2f, 1f);

        /// <summary>
        /// What a temporary cut face -- one the stencil draws before the geometry is committed -- becomes while the same
        /// debug switch is on: red (DESIGN 5.3). It is one definition for every such face; unlike the two colours above
        /// it is not set from outside, so nothing can make a temporary face take the real cap's debug colour.
        /// </summary>
        public static readonly Color DefaultProvisionalDebugColour = Color.red;

        private static readonly int ColourId = Shader.PropertyToID("_VpCutSurfaceColor");
        private static readonly int DebugColourId = Shader.PropertyToID("_VpCutSurfaceDebugColor");
        private static readonly int DebugId = Shader.PropertyToID("_VpCutSurfaceDebug");

        // Whether all three globals have been put in place in this domain. The shader has no defaults of its own, so
        // without this a normal start would draw cut surfaces with whatever the globals happened to hold - black, on a
        // fresh domain. It is set in one place only, by WriteDefaults, so it can never say "initialized" while some of
        // the three are still unset.
        private static bool s_initialized;

        /// <summary>
        /// Puts the defaults in place if this domain has not done so yet, and does nothing at all otherwise. It never
        /// takes back a colour or a switch someone has set since, so a display being built may call it freely.
        /// </summary>
        public static void EnsureInitialized()
        {
            if (s_initialized)
            {
                return;
            }

            WriteDefaults();
        }

        /// <summary>
        /// What a play session starts from: the defaults, whatever was set before. Run before the first scene loads,
        /// on entering Play Mode and in a player, so it is in place before anything is drawn.
        /// <para>
        /// It applies the defaults **unconditionally** rather than asking <see cref="EnsureInitialized"/>, because a
        /// play session beginning is not the same event as a domain being loaded. With Domain Reload turned off the
        /// statics of this class carry over from edit mode already marked as initialized, and only an unconditional
        /// write gives the session the same start as a reloaded one.
        /// </para>
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void BeginPlaySession()
        {
            WriteDefaults();
        }

#if UNITY_EDITOR
        // A freshly loaded editor domain has no globals set at all, so editor drawing and EditMode tests would
        // otherwise start from black. This only fills in what is missing; it is not a session start.
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitializeInEditorDomain()
        {
            EnsureInitialized();
        }
#endif

        /// <summary>The whole of what this owns, for a caller that wants to put it back as it found it.</summary>
        public readonly struct State
        {
            internal State(Color colour, Color debugColour, bool debug)
            {
                this.colour = colour;
                this.debugColour = debugColour;
                this.debug = debug;
            }

            public readonly Color colour;
            public readonly Color debugColour;
            public readonly bool debug;
        }

        /// <summary>Whether cut surfaces are being drawn in the debug colour. One switch, for all of them.</summary>
        public static bool DebugEnabled
        {
            get
            {
                EnsureInitialized();
                return Shader.GetGlobalFloat(DebugId) > 0f;
            }
        }

        /// <summary>The colour a cut surface takes now, which is the debug one while the switch is on.</summary>
        public static Color Current
        {
            get
            {
                EnsureInitialized();
                return Shader.GetGlobalFloat(DebugId) > 0f ? Shader.GetGlobalColor(DebugColourId) : Shader.GetGlobalColor(ColourId);
            }
        }

        /// <summary>
        /// The colour a temporary cut face takes now: the ordinary cut surface colour, the same one a real cap takes,
        /// or <see cref="DefaultProvisionalDebugColour"/> while the debug switch is on. A caller that draws temporary
        /// faces reads this rather than <see cref="Current"/>, which would give them the real cap's debug colour.
        /// </summary>
        public static Color CurrentProvisional
        {
            get
            {
                EnsureInitialized();
                return Shader.GetGlobalFloat(DebugId) > 0f ? DefaultProvisionalDebugColour : Shader.GetGlobalColor(ColourId);
            }
        }

        /// <summary>
        /// Puts the ordinary colour, the debug colour and the switch in place, with the switch off, whatever was set
        /// before. This is what a play session starts from; a caller that wants "leave it alone if it is already set"
        /// wants <see cref="EnsureInitialized"/> instead.
        /// </summary>
        public static void ApplyDefaults()
        {
            WriteDefaults();
        }

        /// <summary>
        /// The two colours, unchanged until someone sets them again. Neither affects any geometry. Everything this
        /// does not name — the debug switch — is left at a known value rather than unset: if this is the first call in
        /// the domain, the defaults go in first and only then the two colours given here.
        /// </summary>
        public static void SetColours(Color colour, Color debugColour)
        {
            EnsureInitialized();
            Shader.SetGlobalColor(ColourId, colour);
            Shader.SetGlobalColor(DebugColourId, debugColour);
        }

        /// <summary>
        /// Turns the one debug switch on or off for every cut surface at once. As with <see cref="SetColours"/>, the
        /// defaults go in first if this is the domain's first call, so turning the switch on before anything else has
        /// happened shows the default green rather than an unset black.
        /// </summary>
        public static void SetDebugEnabled(bool enabled)
        {
            EnsureInitialized();
            Shader.SetGlobalFloat(DebugId, enabled ? 1f : 0f);
            VpCutSurfaceAtlas.SelectDebug(enabled);
        }

        /// <summary>
        /// What is set now, so that a test or a check can put it back afterwards. The defaults are put in place first
        /// if this domain has not done so yet, so what comes back is never an uninitialized black.
        /// </summary>
        public static State Capture()
        {
            EnsureInitialized();
            return new State(Shader.GetGlobalColor(ColourId), Shader.GetGlobalColor(DebugColourId), Shader.GetGlobalFloat(DebugId) > 0f);
        }

        /// <summary>Puts back what <see cref="Capture"/> returned, exactly.</summary>
        public static void Restore(State state)
        {
            SetColours(state.colour, state.debugColour);
            SetDebugEnabled(state.debug);
        }

        // The one place the defaults are written, and the one place the flag is set. It talks to the globals directly
        // rather than through the setters above, which is what keeps those setters free to ask for initialization
        // first without coming back round through here.
        private static void WriteDefaults()
        {
            Shader.SetGlobalColor(ColourId, DefaultColour);
            Shader.SetGlobalColor(DebugColourId, DefaultDebugColour);
            Shader.SetGlobalFloat(DebugId, 0f);
            VpCutSurfaceAtlas.SelectDebug(false);
            s_initialized = true;
        }

        /// <summary>
        /// Makes this class believe it has not been initialized, without touching the globals, so that a test can put
        /// the globals into a state of its own and then watch what the first ordinary call does about it. Nothing in
        /// the product calls this.
        /// </summary>
        internal static void ForgetInitializationForTests()
        {
            s_initialized = false;
        }
    }
}
