using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// How a prepared character's own hierarchy leaves at its first cut's publication (DESIGN 19.1.9): its drawing, the
    /// update that poses its bones, its motion body and its being a hit target end there, once, and nothing it holds -- the
    /// bones, the meshes, the display input -- is given back.
    /// <para>
    /// By default the whole character root is deactivated. A registration that names the updates posing its bones may
    /// ask for the parts instead (<see cref="TryUseParts"/>): the hierarchy is checked once, at preparation, to hold
    /// nothing live beyond the renderer drawing it, those updates and the motion body -- no other enabled renderer,
    /// collider, joint, other body, or MonoBehaviour with an OnDisable of its own -- and if it holds anything more, the
    /// whole root is kept. Withdrawing the parts then turns off the renderer and those updates and deactivates the
    /// motion body's own object, and the character is no longer a hit target.
    /// </para>
    /// </summary>
    internal sealed class PreparedCharacterWithdrawal
    {
        private static readonly ProfilerMarker s_confirm = new ProfilerMarker("Zantetsu.CharacterCut.ConfirmWithdrawal");

        /// <summary>
        /// Measurement only: withdraw the whole root even where the parts were confirmed, to compare the two in one build.
        /// </summary>
        internal static bool CompareWholeRoot;

        private readonly GameObject _root;
        private readonly Renderer _renderer;
        private readonly Rigidbody _motionBody;
        private Behaviour[] _updates;

        internal PreparedCharacterWithdrawal(GameObject root, Renderer renderer, Rigidbody motionBody)
        {
            _root = root;
            _renderer = renderer;
            _motionBody = motionBody;
        }

        /// <summary>Whether the character has left: after this, it is not drawn, posed, simulated or hit as itself.</summary>
        internal bool IsWithdrawn { get; private set; }

        /// <summary>Whether the parts, not the whole root, are withdrawn (before <see cref="CompareWholeRoot"/>).</summary>
        internal bool UsesParts => _updates != null;

        /// <summary>
        /// Confirms that the parts are all that is live in the hierarchy, and uses them if so. The motion body has to be
        /// on its own object under the root, holding nothing but itself. Returns why not otherwise.
        /// </summary>
        internal bool TryUseParts(IReadOnlyList<Behaviour> updates, out string whyNot)
        {
            using (s_confirm.Auto())
            {
                whyNot = null;
                if (IsWithdrawn || _root == null || _renderer == null || _motionBody == null || updates == null)
                {
                    whyNot = "nothing to confirm";
                    return false;
                }

                GameObject motion = _motionBody.gameObject;
                if (motion == _root || motion.transform.childCount > 0 || motion.GetComponents<Component>().Length != 2)
                {
                    whyNot = "the motion body is not on an object of its own";
                    return false;
                }

                var named = new HashSet<Component> { _renderer, _motionBody };
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

        /// <summary>The withdrawal, once: the parts, or the whole root.</summary>
        internal void Withdraw()
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

            if (_motionBody != null) _motionBody.gameObject.SetActive(false);
        }
    }
}
