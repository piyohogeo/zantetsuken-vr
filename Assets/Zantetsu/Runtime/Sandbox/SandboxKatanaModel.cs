using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The katana's model (DESIGN 19.1.11): the sword that is drawn where a scene's katana root stands, in place of the
    /// boxes the sandbox rig was first given (a handle, a guard, a blade body, an edge stripe and a cut sample
    /// marker). The model is a prefab under Resources, so every scene and every Player shows the same sword without
    /// its scene being made again; a scene's own parts stay in the scene and are only not drawn.
    /// <para>
    /// **The frame is the katana root's.** The model's root has the root's axes -- +Z along the blade, -Y the edge,
    /// +X the side normal -- with the blade's base at the origin and the grip behind it, as the boxes had it. The
    /// blade is curved and the axis is straight: the blade's chord, from its base to its tip, lies along +Z. Nothing
    /// of the cut reads the model: the blade's length and frame are the katana component's own numbers.
    /// </para>
    /// </summary>
    public static class SandboxKatanaModel
    {
        /// <summary>The prefab's name under Resources.</summary>
        public const string ResourceName = "ZantetsuKatanaModel";

        /// <summary>The name of the model's instance under a katana root.</summary>
        public const string InstanceName = "Katana Model";

        /// <summary>
        /// Puts the model under a katana root, once: what the root's own parts draw is switched off, and the model is
        /// made its child on the root's layer. Returns the model -- the one already there on a second call -- or null
        /// where there is no root or the prefab is not in this build, and then nothing is changed.
        /// </summary>
        public static GameObject Apply(Transform katana)
        {
            if (katana == null)
            {
                return null;
            }

            Transform existing = katana.Find(InstanceName);
            if (existing != null)
            {
                return existing.gameObject;
            }

            GameObject prefab = Resources.Load<GameObject>(ResourceName);
            if (prefab == null)
            {
                return null;
            }

            foreach (Renderer part in katana.GetComponentsInChildren<Renderer>(true))
            {
                part.enabled = false;
            }

            GameObject model = Object.Instantiate(prefab, katana, false);
            model.name = InstanceName;
            foreach (Transform each in model.GetComponentsInChildren<Transform>(true))
            {
                each.gameObject.layer = katana.gameObject.layer;
            }

            return model;
        }
    }
}
