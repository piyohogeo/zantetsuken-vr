using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// What an object drawn by its own renderers until its first cut (DESIGN 4.5.1: a prepared character, a placed
    /// object) does when the owner its first cut was registered with is withdrawn -- that cut's publication, or the
    /// world's ending: it stops being drawn, simulated and hit as itself, once. Nothing it holds is given back here.
    /// </summary>
    internal interface IPreparedSourceWithdrawal
    {
        void Withdraw();
    }

    /// <summary>
    /// How a prepared character's own hierarchy leaves at its first cut's publication (DESIGN 19.1.9): its drawing and
    /// the update that poses its bones end there, once, and nothing it holds -- the bones, the meshes, the display
    /// input -- is given back. No body is among them: a character not yet cut has none in the physics (DESIGN 9, D-197).
    /// <para>
    /// By default the whole character root is deactivated. A registration that names the updates posing its bones may
    /// ask for the parts instead (<see cref="TryUseParts"/>): the hierarchy is checked once, at preparation, to hold
    /// nothing live beyond the renderer drawing it and those updates -- no other enabled renderer, no collider, joint
    /// or body, no MonoBehaviour with an OnDisable of its own -- and if it holds anything more, the whole root is kept.
    /// Withdrawing the parts then turns off the renderer and those updates, and the character is no longer a hit target.
    /// The character's complete bone correspondence (<see cref="VpSkinBones"/>, DESIGN 9, D-195) is passed over by that
    /// check and left as it is by the withdrawal: it is data -- it runs nothing and does nothing on being enabled or
    /// disabled -- and the slot's next individual resolves its bones by it again. It is the one type so treated.
    /// If publication is Pending, the admitted cut display replaces the original mesh drawing earlier through
    /// <see cref="BeginCutDisplay"/>; posing still leaves at the publication boundary.
    /// </para>
    /// </summary>
    internal sealed class PreparedCharacterWithdrawal : IPreparedSourceWithdrawal
    {
        private static readonly ProfilerMarker s_confirm = new ProfilerMarker("Zantetsu.CharacterCut.ConfirmWithdrawal");

        /// <summary>
        /// Measurement only: withdraw the whole root even where the parts were confirmed, to compare the two in one build.
        /// </summary>
        internal static bool CompareWholeRoot;

        private readonly GameObject _root;
        private readonly Renderer _renderer;
        private Behaviour[] _updates;

        internal PreparedCharacterWithdrawal(GameObject root, Renderer renderer)
        {
            _root = root;
            _renderer = renderer;
        }

        /// <summary>Whether the character has left: after this, it is not drawn, posed, simulated or hit as itself.</summary>
        internal bool IsWithdrawn { get; private set; }

        /// <summary>Whether the parts, not the whole root, are withdrawn (before <see cref="CompareWholeRoot"/>).</summary>
        internal bool UsesParts => _updates != null;

        /// <summary>
        /// Confirms that the parts are all that is live in the hierarchy, and uses them if so. A hierarchy holding a
        /// body, a collider or a joint keeps its whole root: deactivating the root is what takes those out of the
        /// physics. Returns why not otherwise.
        /// </summary>
        internal bool TryUseParts(IReadOnlyList<Behaviour> updates, out string whyNot)
        {
            using (s_confirm.Auto())
            {
                whyNot = null;
                if (IsWithdrawn || _root == null || _renderer == null || updates == null)
                {
                    whyNot = "nothing to confirm";
                    return false;
                }

                var named = new HashSet<Component> { _renderer };
                foreach (Behaviour update in updates)
                {
                    if (update == null || !update.transform.IsChildOf(_root.transform))
                    {
                        whyNot = "an update is not in the character";
                        return false;
                    }

                    named.Add(update);
                }

                foreach (Component component in _root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || named.Contains(component) || component is Transform)
                    {
                        continue;
                    }

                    // The character's complete bone correspondence is data, not an update: a sealed type that
                    // declares no Unity message, so nothing of it runs and nothing happens when it is enabled or
                    // disabled. There is nothing of it to stop, and the withdrawal leaves it as it is. This type
                    // alone, by name: any other MonoBehaviour meets the refusals below as before.
                    if (component is VpSkinBones)
                    {
                        continue;
                    }

                    bool live = component.gameObject.activeInHierarchy;
                    if (component is Renderer other && other.enabled && live)
                    {
                        whyNot = "another renderer is drawing: " + other.name;
                        return false;
                    }

                    if (component is Collider || component is Joint || component is Rigidbody)
                    {
                        whyNot = "the character holds " + component.GetType().Name + " on " + component.name;
                        return false;
                    }

                    if (component is Behaviour behaviour && behaviour.enabled && live && !(component is Renderer))
                    {
                        whyNot = "an update not named is running: " + component.GetType().Name + " on " + component.name;
                        return false;
                    }

                    if (component is MonoBehaviour && component.GetType().GetMethod("OnDisable",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.NonPublic) != null)
                    {
                        whyNot = "a component has an OnDisable of its own: " + component.GetType().Name;
                        return false;
                    }
                }

                _updates = new Behaviour[updates.Count];
                for (int i = 0; i < updates.Count; i++) _updates[i] = updates[i];
                return true;
            }
        }

        /// <summary>
        /// An admitted cut already has its frozen display input registered. While physics publication is Pending,
        /// that display owns the mesh drawing; keep posing alive until the normal withdrawal boundary.
        /// </summary>
        internal void BeginCutDisplay()
        {
            if (_renderer != null) _renderer.enabled = false;
        }

        /// <summary>The withdrawal, once: the parts, or the whole root.</summary>
        public void Withdraw()
        {
            if (IsWithdrawn)
            {
                return;
            }

            IsWithdrawn = true;
            if (_updates == null || CompareWholeRoot)
            {
                if (_root != null) _root.SetActive(false);
                return;
            }

            if (_renderer != null) _renderer.enabled = false;
            foreach (Behaviour update in _updates)
            {
                if (update != null) update.enabled = false;
            }
        }
    }
}
