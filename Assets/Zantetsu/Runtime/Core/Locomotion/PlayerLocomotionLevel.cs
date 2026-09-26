using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>
    /// Settles the level's fixed locomotion occupancy once, when the level initialises (DESIGN 7.2.3): from exactly the
    /// volumes listed here -- the level's authoring chooses them; nothing is searched for or inferred. After that the
    /// occupancy is what it was built as, whatever becomes of those objects.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-900)]
    public sealed class PlayerLocomotionLevel : MonoBehaviour
    {
        [Tooltip("The walls, large fixed props and level bounds the player's artificial movement may not overlap.")]
        [SerializeField] private List<PlayerLocomotionOccupancyVolume> volumes = new List<PlayerLocomotionOccupancyVolume>();

        private PlayerLocomotionOccupancy _occupancy;

        /// <summary>The level's occupancy: built on first use at the latest, and never rebuilt.</summary>
        public PlayerLocomotionOccupancy Occupancy => _occupancy ??= PlayerLocomotionOccupancy.Build(volumes);

        /// <summary>Sets the authoring list before the level is initialised (for building levels and tests from code).</summary>
        public void SetVolumes(IEnumerable<PlayerLocomotionOccupancyVolume> authored)
        {
            volumes = new List<PlayerLocomotionOccupancyVolume>(authored);
        }

        private void Awake()
        {
            _ = Occupancy;
        }
    }
}
