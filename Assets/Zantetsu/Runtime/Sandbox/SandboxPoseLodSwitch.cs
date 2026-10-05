using System;
using UnityEngine;
using Zantetsu.Core.Animation;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Chooses, once per run, how a sandbox scene's characters play, from <c>-zantetsuPoseLod</c> on the command line, read
    /// before any character is prepared, so that one Player runs all three and they can be compared:
    /// <list type="bullet">
    /// <item><c>off</c> (or nothing): every table bone every frame, the hit as before.</item>
    /// <item><c>needed</c>: the needed bones only, every frame (<see cref="PoseLodDirector.NeededOnly"/>); the hit as before.</item>
    /// <item><c>on</c>: the needed bones under the level of detail by view and distance, and the hit's range test with the
    /// whole current pose when a sweep may meet the character.</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [DisallowMultipleComponent]
    public sealed class SandboxPoseLodSwitch : MonoBehaviour
    {
        public const string Argument = "-zantetsuPoseLod";

        /// <summary>
        /// <c>-zantetsuSkinUnseen keep</c>: Unity skins every uncut character every frame, seen or not, as before.
        /// <c>skip</c> (or nothing): only while a camera sees its renderer (<see cref="SandboxNpcCharacter.SkinWhenUnseen"/>).
        /// </summary>
        public const string SkinArgument = "-zantetsuSkinUnseen";

        [SerializeField] private PoseLodDirector director;

        [Tooltip("Whether the director is on when the command line says nothing.")]
        [SerializeField] private bool onByDefault;

        /// <summary>Whether this run plays under the director (needed or on).</summary>
        public bool On { get; private set; }

        /// <summary>Whether this run plays the needed bones only, with no level of detail and the hit as before.</summary>
        public bool NeededOnly { get; private set; }

        private void Awake()
        {
            string value = null;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], Argument, StringComparison.OrdinalIgnoreCase))
                {
                    value = arguments[i + 1];
                }
            }

            string skin = null;
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], SkinArgument, StringComparison.OrdinalIgnoreCase))
                {
                    skin = arguments[i + 1];
                }
            }

            SandboxNpcCharacter.SkinWhenUnseen = skin != null && string.Equals(skin, "keep", StringComparison.OrdinalIgnoreCase);
            Debug.Log("SKIN UNSEEN: " + (SandboxNpcCharacter.SkinWhenUnseen ? "keep (skinned every frame)" : "skip (skinned only while a camera sees the renderer, under the level of detail)")
                + " (" + SkinArgument + " " + (skin ?? "not given") + ")");

            NeededOnly = value != null && string.Equals(value, "needed", StringComparison.OrdinalIgnoreCase);
            On = NeededOnly || (value != null ? string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) : onByDefault);
            if (director != null)
            {
                director.enabled = On;
                director.NeededOnly = NeededOnly;
            }

            Debug.Log("POSE LOD: " + (NeededOnly ? "needed" : On ? "on" : "off") + " (" + Argument + " " + (value ?? "not given") + ")"
                + (director != null ? "" : " -- no director in the scene"));
        }
    }
}
