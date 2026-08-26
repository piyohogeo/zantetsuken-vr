using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>
    /// Applies the world gravity from a <see cref="WorldPhysicsProfile"/> at
    /// startup (DESIGN.md D-074 / D-075). The profile is assigned through the
    /// Inspector; no resource lookup, singleton or service locator is used.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-1000)]
    public sealed class WorldPhysicsBootstrap : MonoBehaviour
    {
        [SerializeField]
        private WorldPhysicsProfile profile;

        private bool profileErrorLogged;

        /// <summary>
        /// The configured profile, or null when none is assigned.
        /// </summary>
        public WorldPhysicsProfile Profile => profile;

        private void Awake()
        {
            Apply();
        }

        /// <summary>
        /// Applies the configured profile to <see cref="Physics.gravity"/>.
        /// When no profile is assigned, logs a single error, disables this
        /// component and returns false.
        /// </summary>
        public bool Apply()
        {
            if (profile == null)
            {
                if (!profileErrorLogged)
                {
                    Debug.LogError("WorldPhysicsBootstrap has no WorldPhysicsProfile assigned; the world gravity was not applied.", this);
                    profileErrorLogged = true;
                }

                enabled = false;
                return false;
            }

            profile.Apply();
            return true;
        }
    }
}
