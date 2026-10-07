#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// Whether the numeric input-contract diagnosis (DESIGN 5.6, D-191) runs in this process. The diagnosis is compiled
    /// for the Editor and for Development Players only, and this class with it: a non-Development Player has neither.
    /// <para>
    /// On unless a Development Player is started with <see cref="DisableArgument"/> (D-211) -- the configuration the
    /// performance comparisons are made in. Off, everything a non-Development Player leaves out by compilation is left
    /// out by a branch: the checks themselves, what exists only for them (the values a check last passed with and the
    /// comparisons that let it pass again unasked, their room, the diagnosis's counts and times) and the refusals they
    /// make. A number outside the contract then goes on into the build, as it does in a non-Development Player. What
    /// is not this diagnosis is not touched: the structure's validation, the cut plane's own check, capacities,
    /// ranges, lifetimes, and the comparisons that decide what is drawn. The Editor does not read the argument.
    /// </para>
    /// <para>
    /// Decided once, from the command line, when this class is first touched -- which is before anything of the
    /// diagnosis can run -- and not read from the command line again. A display and a snapshot take the answer when
    /// they are made and branch on their own copy at the entrances of the diagnosis; nothing asks per element.
    /// </para>
    /// </summary>
    public static class VpNumericDiagnosis
    {
        public const string DisableArgument = "-zantetsuDisableNumericDiagnostics";

        private static bool s_enabled = Decide();

        /// <summary>Whether the diagnosis runs in the displays and snapshots made from now on.</summary>
        public static bool Enabled => s_enabled;

        /// <summary>For a launch record: the state and what set it.</summary>
        public static string Describe() =>
            s_enabled ? "on (the default of the Editor and of a Development Player)" : "off (" + DisableArgument + ")";

        private static bool Decide()
        {
#if UNITY_EDITOR
            return true;
#else
            return EnabledFor(Environment.GetCommandLineArgs());
#endif
        }

        /// <summary>What a Development Player started with <paramref name="arguments"/> decides: off only when the argument is among them.</summary>
        internal static bool EnabledFor(string[] arguments)
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], DisableArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Tests only: what a Development Player started with the argument has. Objects made before keep what they took.</summary>
        internal static void SetForTest(bool enabled) => s_enabled = enabled;

        /// <summary>Tests only: back to what the process was started with.</summary>
        internal static void ResetForTest() => s_enabled = Decide();
    }
}
#endif
