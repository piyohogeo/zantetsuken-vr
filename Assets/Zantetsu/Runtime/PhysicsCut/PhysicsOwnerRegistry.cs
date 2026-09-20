using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The live physics owner of one logical fragment: the body the fragment is, and the convexes and cooked shapes
    /// it is made of.
    /// <para>
    /// This is the correspondence a hit, a query or a following cut goes through. Resolving a hit is not written
    /// here — what is here is the thing a resolution would land on, and the shape a cut of this fragment reads.
    /// </para>
    /// </summary>
    public sealed class PhysicsFragmentOwner
    {
        internal PhysicsFragmentOwner(
            GameObject root,
            Rigidbody body,
            PhysicsOwnerShape shape,
            bool fixedByAnchors,
            Matrix4x4? geometryLocalToOwner = null)
        {
            Root = root != null ? root : throw new ArgumentNullException(nameof(root));
            Body = body != null ? body : throw new ArgumentNullException(nameof(body));
            Shape = shape ?? throw new ArgumentNullException(nameof(shape));
            FixedByAnchors = fixedByAnchors;
            GeometryLocalToOwner = geometryLocalToOwner;
        }

        public GameObject Root { get; private set; }

        public Rigidbody Body { get; private set; }

        /// <summary>What this owner is made of, and the next cut's input.</summary>
        public PhysicsOwnerShape Shape { get; private set; }

        /// <summary>This owner holds an anchor, so it is fixed (DESIGN 7.1).</summary>
        public bool FixedByAnchors { get; }

        /// <summary>
        /// How the display geometry of this fragment sits in this owner's own coordinates, for an owner whose display
        /// follows it; none for one whose display is arranged some other way, which is a statement and not a gap
        /// (DESIGN 5.6, 7.1.2). It is the caller's to give when the first owner of a lineage is registered, and the
        /// children of a cut inherit it, because a publication does not rebuild a child's owner frame: both sides
        /// start at the very placement the source had. It is **not** the cut DAG's lineage-to-geometry mapping, which
        /// is about the plane a kernel cuts at and has nothing to do with where anything stands.
        /// <para>
        /// The separation the display draws with is no part of this: what a snapshot sums and what a geometry commit
        /// folds in are the display's own, and a physical position or a separation impulse is never one of them.
        /// </para>
        /// </summary>
        public Matrix4x4? GeometryLocalToOwner { get; }

        /// <summary>
        /// Where this owner's display geometry stands now: this owner's world transform, read at the moment it is
        /// needed, with the correspondence above. False for an owner that has none, or one that has left the scene —
        /// nothing of where it used to be is handed back.
        /// </summary>
        public bool TryReadGeometryLocalToWorld(out Matrix4x4 geometryLocalToWorld)
        {
            if (IsWithdrawn || Root == null || GeometryLocalToOwner == null)
            {
                geometryLocalToWorld = default;
                return false;
            }

            geometryLocalToWorld = Root.transform.localToWorldMatrix * GeometryLocalToOwner.Value;
            return true;
        }

        /// <summary>Whether it has left the physics scene.</summary>
        public bool IsWithdrawn { get; private set; }

        /// <summary>Whether its objects have been destroyed and its shape given up.</summary>
        public bool IsReleased { get; private set; }

        /// <summary>
        /// The mass the solver has now, which DESIGN 7.2 makes the parent mass of the next cut of this fragment. It is
        /// read from the body rather than remembered, because the body is authoritative once it is published.
        /// </summary>
        public float Mass => Body.mass;

        /// <summary>Where this owner is now. Read at the moment it is needed, never remembered from earlier.</summary>
        public PhysicsOwnerPlacement ReadPlacement()
        {
            Root.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            return new PhysicsOwnerPlacement(position, rotation);
        }

        /// <summary>
        /// How this owner is moving now, in the world, with the render anchor the caller names. Ordinary motion is
        /// what this reads: a source that has moved since a candidate was built is not stale, and the values a split
        /// inherits come from here rather than from anything recorded earlier.
        /// </summary>
        public PhysicsOwnerMotion ReadMotion(float3 renderAnchor)
        {
            return new PhysicsOwnerMotion(
                Body.worldCenterOfMass, Body.linearVelocity, Body.angularVelocity, renderAnchor);
        }

        /// <summary>
        /// Takes this owner out of the physics scene, at once: the object is deactivated, so its body and colliders
        /// stop being simulated and stop answering queries from here on. Nothing is destroyed and nothing is given
        /// back yet — <see cref="Release"/> does that. The two are separate because destruction is not immediate
        /// everywhere: in play mode <c>Destroy</c> happens later in the frame, and an owner that has been retired must
        /// not still be found by a query in the meantime.
        /// </summary>
        internal void Withdraw()
        {
            if (IsWithdrawn)
            {
                return;
            }

            IsWithdrawn = true;
            if (Root != null)
            {
                Root.SetActive(false);
            }
        }

        /// <summary>
        /// Destroys this owner's objects and gives up its shape. A mesh or a bank something else is still using is not
        /// given back here: another owner has its own hold on the meshes, and work still reading the bank has its own
        /// hold on the shape, so what is still in use goes back when that use ends.
        /// </summary>
        internal void Release()
        {
            if (IsReleased)
            {
                return;
            }

            Withdraw();
            IsReleased = true;
            PhysicsOwnerBuilder.DestroyObject(Root);
            Root = null;
            Body = null;
            Shape.Dispose();
            Shape = null;
        }
    }

    /// <summary>
    /// Which physics owner each live logical fragment is (DESIGN 7.1.2): the correspondence that a publication
    /// switches, and that a following cut reads its input from.
    /// <para>
    /// The key is the fragment the ledger already has; no identity is invented here, and no state of a publication is
    /// kept. A fragment has an owner or it does not.
    /// </para>
    /// </summary>
    public sealed class PhysicsOwnerRegistry : IDisposable
    {
        private readonly Dictionary<LogicalFragmentId, PhysicsFragmentOwner> _owners =
            new Dictionary<LogicalFragmentId, PhysicsFragmentOwner>();

        public int Count => _owners.Count;

        /// <summary>
        /// Makes room for a few more owners before anything is added, so that adding them cannot be what fails. It
        /// changes nothing else.
        /// </summary>
        public void Reserve(int count)
        {
            if (count > 0)
            {
                _owners.EnsureCapacity(_owners.Count + count);
            }
        }

        public bool TryGet(LogicalFragmentId fragment, out PhysicsFragmentOwner owner)
        {
            return _owners.TryGetValue(fragment, out owner);
        }

        /// <summary>
        /// Gives a fragment the owner it already has in the scene — an authored compound, before anything has been
        /// cut. A fragment that already has one is refused rather than replaced.
        /// </summary>
        /// <param name="geometryLocalToOwner">
        /// Where this lineage's display geometry sits in this owner's coordinates, for a display that follows it
        /// (<see cref="PhysicsFragmentOwner.GeometryLocalToOwner"/>). Left out for a lineage whose display is
        /// arranged some other way; every cut of this one inherits what is given here.
        /// </param>
        public PhysicsFragmentOwner RegisterAuthored(
            LogicalFragmentId fragment,
            GameObject root,
            Rigidbody body,
            PhysicsOwnerShape shape,
            bool fixedByAnchors = false,
            Matrix4x4? geometryLocalToOwner = null)
        {
            var owner = new PhysicsFragmentOwner(root, body, shape, fixedByAnchors, geometryLocalToOwner);
            Add(fragment, owner);
            return owner;
        }

        internal void Add(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            if (!fragment.IsSet)
            {
                throw new ArgumentException("not a fragment", nameof(fragment));
            }

            if (_owners.ContainsKey(fragment))
            {
                throw new InvalidOperationException("that fragment already has a physics owner");
            }

            _owners.Add(fragment, owner);
        }

        /// <summary>
        /// Takes one fragment's owner out of the physics scene without destroying it or giving anything back. The
        /// fragment keeps its owner here, so what was withdrawn can still be looked at; <see cref="Retire"/> is what
        /// ends it.
        /// </summary>
        public bool Withdraw(LogicalFragmentId fragment)
        {
            if (!_owners.TryGetValue(fragment, out PhysicsFragmentOwner owner))
            {
                return false;
            }

            owner.Withdraw();
            return true;
        }

        /// <summary>
        /// Ends one fragment's owner and forgets it: out of the physics scene first, then destroyed and given up. One
        /// side of a cut can be retired without the other — each holds its own cooked shapes, and work still reading a
        /// bank holds that — so this gives back only what nothing else is using.
        /// </summary>
        public bool Retire(LogicalFragmentId fragment)
        {
            if (!_owners.TryGetValue(fragment, out PhysicsFragmentOwner owner))
            {
                return false;
            }

            owner.Withdraw();
            _owners.Remove(fragment);
            owner.Release();
            return true;
        }

        /// <summary>Ends every owner left. What they were holding goes back with them.</summary>
        public void Dispose()
        {
            foreach (KeyValuePair<LogicalFragmentId, PhysicsFragmentOwner> pair in _owners)
            {
                pair.Value.Withdraw();
            }

            foreach (KeyValuePair<LogicalFragmentId, PhysicsFragmentOwner> pair in _owners)
            {
                pair.Value.Release();
            }

            _owners.Clear();
        }
    }
}
