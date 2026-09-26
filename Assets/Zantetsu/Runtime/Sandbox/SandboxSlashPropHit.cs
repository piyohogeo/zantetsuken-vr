using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Joins the sandbox katana's waves to a cut world (Phase 4.51): it makes the world's
    /// <see cref="SlashHitDetector"/> once the world is built and gives it to the katana, which hands it every core
    /// update's sweeps. Everything after that is the product's -- the hit, the consumption and the acceptance.
    /// <para>
    /// It holds the scene's own value for what a hit's acceptance is asked with: the two separation impulses (a value
    /// for looking at the result, as the probe's is; DESIGN 7.2 leaves the formula open). It asks for no cut and decides
    /// no hit.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SandboxSlashPropHit : MonoBehaviour
    {
        [SerializeField] private CutWorldRoot world;
        [SerializeField] private SandboxRightHandKatana katana;

        [Tooltip("The impulse each child is given at a cut, in newton-seconds. A value of this scene.")]
        [SerializeField] private float separationImpulse = 1.5f;

        /// <summary>The detector, once the world has been built.</summary>
        public SlashHitDetector Detector { get; private set; }

        private void Start()
        {
            if (world == null || !world.IsReady || katana == null)
            {
                Debug.LogError(name + ": this needs a built CutWorldRoot and a katana.", this);
                enabled = false;
                return;
            }

            var settings = new SlashHitSettings
            {
                positiveSeparationImpulse = separationImpulse,
                negativeSeparationImpulse = separationImpulse,
            };
            Detector = new SlashHitDetector(world, in settings);
            katana.HitDetector = Detector;
        }

        private void OnDestroy()
        {
            if (katana != null && ReferenceEquals(katana.HitDetector, Detector))
            {
                katana.HitDetector = null;
            }

            Detector = null;
        }
    }
}
