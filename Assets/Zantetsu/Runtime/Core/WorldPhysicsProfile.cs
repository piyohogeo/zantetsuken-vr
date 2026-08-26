using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>
    /// The single source of truth for the world gravity vector shared by Unity
    /// Physics, future prediction, GPU micro debris and non-physical VFX.
    ///
    /// Consumers read <see cref="Gravity"/> instead of hard-coding gravity
    /// constants such as -9.81 (DESIGN.md D-074 / D-075).
    /// </summary>
    [CreateAssetMenu(menuName = "Zantetsu/World Physics Profile", fileName = "WorldPhysicsProfile")]
    public class WorldPhysicsProfile : ScriptableObject
    {
        // PoC tentative value from DESIGN.md D-074: approximately 0.5G.
        [SerializeField]
        private Vector3 gravity = new Vector3(0f, -4.9f, 0f);

        /// <summary>
        /// The authoritative gravity vector in m/s^2.
        /// </summary>
        public Vector3 Gravity => gravity;

        /// <summary>
        /// Applies the current gravity value to <see cref="Physics.gravity"/>.
        /// </summary>
        public void Apply()
        {
            Physics.gravity = gravity;
        }
    }
}
