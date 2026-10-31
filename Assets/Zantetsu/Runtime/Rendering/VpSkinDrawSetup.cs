using System;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>What a launch asks of the skin draw (DESIGN 4.5.12).</summary>
    public enum VpSkinDrawRequest
    {
        /// <summary>The renderers (the default until the skin draw is verified on the device, review 1 of SKIN24-1).</summary>
        Default = 0,

        /// <summary>The renderers (SkinnedMeshRenderer), as before the skin draw: the comparison route.</summary>
        Renderers = 1,

        /// <summary>The skin draw, asked for by name: a world that cannot make it says so and takes no other route for the skins.</summary>
        SkinDraw = 2,
    }

    /// <summary>
    /// The launch's choice between the skin draw (the uncut skinned individuals through the indirect VP draw, DESIGN
    /// 4.5.12) and their SkinnedMeshRenderers (as before it). Read once from the Player's arguments; a test sets it.
    /// </summary>
    public static class VpSkinDrawSetup
    {
        /// <summary>The Player argument that keeps the renderers, for comparing the two in one build.</summary>
        public const string RenderersArgument = "-zantetsuSkinUnity";

        /// <summary>The Player argument that asks for the skin draw by name.</summary>
        public const string SkinDrawArgument = "-zantetsuSkinDraw";

        /// <summary>
        /// The Player arguments that override the profile's room for the skin draw's models and their bone boxes
        /// (SKIN-BENCH-3, TL review 3): the city walk's profile holds 8 models, the crowd carries 49, and a model past the
        /// room is drawn by its renderer; a whole-crowd skin draw needs the room for every model (and 66 boxes each).
        /// Each takes a whole number 1..1000000; absent (or 0) the profile's value stands. The route log says both.
        /// </summary>
        public const string ModelCapacityArgument = "-zantetsuSkinModelCapacity";
        public const string BoneBoxCapacityArgument = "-zantetsuSkinBoneBoxCapacity";
        public const int CapacityLimit = 1000000;

        private static bool s_read;
        private static VpSkinDrawRequest s_request;
        private static int s_modelCapacity, s_boneBoxCapacity;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_read = false;
            s_request = VpSkinDrawRequest.Default;
            s_modelCapacity = 0;
            s_boneBoxCapacity = 0;
        }

        /// <summary>The model room asked by the launch (0: the profile's). Read once; a configuration or a test sets it.</summary>
        public static int ModelCapacity
        {
            get
            {
                Read();
                return s_modelCapacity;
            }
            set
            {
                Read();
                s_modelCapacity = Math.Max(0, value);
            }
        }

        /// <summary>The bone box room asked by the launch (0: the profile's). Read once; a configuration or a test sets it.</summary>
        public static int BoneBoxCapacity
        {
            get
            {
                Read();
                return s_boneBoxCapacity;
            }
            set
            {
                Read();
                s_boneBoxCapacity = Math.Max(0, value);
            }
        }

        /// <summary>
        /// The capacity a set of arguments asks by <paramref name="argument"/> followed by a whole number 1..<see cref="CapacityLimit"/>;
        /// 0 when absent, and 0 with <paramref name="error"/> when the value is not such a number (nothing else changes).
        /// </summary>
        public static int ReadCapacity(string[] arguments, string argument, out string error)
        {
            error = null;
            int value = 0;
            if (arguments == null) return 0;
            for (int i = 0; i < arguments.Length; i++)
            {
                if (!string.Equals(arguments[i], argument, StringComparison.Ordinal)) continue;
                string text = i + 1 < arguments.Length ? arguments[i + 1] : null;
                if (text == null || !int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value) || value < 1 || value > CapacityLimit)
                {
                    error = argument + " takes a whole number 1.." + CapacityLimit + ", not '" + (text ?? "") + "'";
                    value = 0;
                }
            }

            return value;
        }

        public static VpSkinDrawRequest Request
        {
            get
            {
                Read();
                return s_request;
            }
            set
            {
                Read();
                s_request = value;
            }
        }

        private static void Read()
        {
            if (s_read) return;
            s_read = true;
            s_request = ReadRequest(Environment.GetCommandLineArgs());
            s_modelCapacity = ReadCapacity(Environment.GetCommandLineArgs(), ModelCapacityArgument, out _);
            s_boneBoxCapacity = ReadCapacity(Environment.GetCommandLineArgs(), BoneBoxCapacityArgument, out _);
        }

        /// <summary>What a set of arguments asks: <see cref="RenderersArgument"/> wins over <see cref="SkinDrawArgument"/>.</summary>
        public static VpSkinDrawRequest ReadRequest(string[] arguments)
        {
            bool renderers = false, skin = false;
            if (arguments != null)
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    renderers |= string.Equals(arguments[i], RenderersArgument, StringComparison.Ordinal);
                    skin |= string.Equals(arguments[i], SkinDrawArgument, StringComparison.Ordinal);
                }
            }

            return renderers ? VpSkinDrawRequest.Renderers : skin ? VpSkinDrawRequest.SkinDraw : VpSkinDrawRequest.Default;
        }

        /// <summary>
        /// Whether a world whose bodies are drawn by <paramref name="bodyRoute"/> ("VP3C-NATIVE", "VP3C", "VP3") makes
        /// the skin draw: only when asked for by name (<see cref="SkinDrawArgument"/>) and the selection runs (the
        /// plugin's route, or the Editor's checks by Unity's calls). By default, and with the renderers asked for,
        /// the renderers draw the skins (the skin draw is not the default until its device check, review 1).
        /// </summary>
        public static bool Wanted(VpSkinDrawRequest request, string bodyRoute)
        {
            switch (request)
            {
                case VpSkinDrawRequest.SkinDraw: return bodyRoute == "VP3C-NATIVE" || bodyRoute == "VP3C";
                default: return false;
            }
        }
    }
}
