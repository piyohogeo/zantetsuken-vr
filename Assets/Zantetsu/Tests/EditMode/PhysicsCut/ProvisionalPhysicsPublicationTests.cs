using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;
using Zantetsu.MeshCut.Tests;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The Provisional publication of one accepted cut (DESIGN 7.1.1): the two temporary actors enter the physics
    /// scene in place of their source, both go on resolving to the one source fragment, and each side is followed
    /// where it stands.
    /// <para>
    /// **What is watched is the scene and the correspondence**, not a stage or a flag: which objects are active,
    /// what the bodies really hold, what a query of the registry answers, and what the placement lookup gives back.
    /// No logical child exists at any point here, and the ledger is asked to publish nothing.
    /// </para>
    /// <para>
    /// **What these tests do not say.** Nothing here drives a frame: there is no product caller for this path yet, so
    /// the acceptance-frame timing of DESIGN 14 (T-091) is **not** checked, and neither is the handoff to a Final
    /// publication. The one case that steps the simulation says so where it is, and steps the shared scene by script.
    /// </para>
    /// </summary>
    public unsafe class ProvisionalPhysicsPublicationTests
    {
        private const double ParentMass = 12.0;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Mesh> _meshes = new List<Mesh>();

        [TearDown]
        public void Cleanup()
        {
            ProvisionalPhysicsPublication.publishedHook = null;
            ProvisionalPhysicsPublication.establishedHook = null;
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }

            _disposables.Clear();
            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _objects.Clear();
            foreach (Mesh mesh in _meshes)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            _meshes.Clear();
        }

        // ----- one authored source, its ledger fragment and its owner ------------------------------------------------

        /// <summary>The state a first cut starts from: a compound in the scene, a live fragment, and the two joined.</summary>
        private sealed class World : IDisposable
        {
            internal OwnerCutHarness harness;
            internal PhysicsShapeSource meshSource;
            internal PhysicsOwnerShape shape;
            internal PhysicsOwnerRegistry registry;
            internal LogicalCutLedger ledger;
            internal PhysicsOwnerPlacementLookup lookup;
            internal LogicalFragmentId source;
            internal GameObject root;

            internal PhysicsFragmentOwner SourceOwner
            {
                get
                {
                    registry.TryGet(source, out PhysicsFragmentOwner owner);
                    return owner;
                }
            }

            public void Dispose()
            {
                registry?.Dispose();
                harness?.Dispose();
            }
        }

        private static readonly Matrix4x4 k_geometryLocalToOwner =
            Matrix4x4.TRS(new Vector3(0f, 0.25f, 0f), Quaternion.identity, Vector3.one);

        /// <param name="arrangedElsewhere">
        /// True registers the lineage with **no** display correspondence at all, which is what a lineage whose
        /// display is arranged some other way says about itself. It is not the same as leaving the argument out.
        /// </param>
        private World NewWorld(
            float3[] anchors = null, Matrix4x4? geometryLocalToOwner = null, bool arrangedElsewhere = false,
            PhysicsOwnerRegistry registry = null, LogicalCutLedger ledger = null)
        {
            var w = new World
            {
                harness = new OwnerCutHarness(),
                registry = registry ?? new PhysicsOwnerRegistry(),
                ledger = ledger ?? new LogicalCutLedger(new LogicalCutIncompleteBudget(8)),
            };
            _disposables.Add(w);

            w.harness.planeN = new float3(0f, 1f, 0f);
            w.harness.planeW = 0f;
            w.harness.eps = 1e-5f;
            w.harness.parentMass = ParentMass;
            w.harness.Add(CaseGenerator.Box());
            w.harness.Build();

            var ranges = new ConvexBrepRange[w.harness.input.convexCount];
            for (int c = 0; c < ranges.Length; c++)
            {
                ranges[c] = w.harness.input.convexes[c];
            }

            var meshes = new List<Mesh>();
            for (int c = 0; c < ranges.Length; c++)
            {
                meshes.Add(BoxColliderOf(w.harness.input.bank, ranges[c], "Authored " + c));
            }

            w.meshSource = PhysicsShapeSource.External();
            w.shape = PhysicsOwnerShape.Authored(
                w.harness.input.bank, ranges, meshes, w.meshSource, float4x4.identity);

            w.root = new GameObject("Authored Source");
            _objects.Add(w.root);
            var body = w.root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            for (int c = 0; c < w.shape.ConvexCount; c++)
            {
                MeshCollider collider = w.root.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = w.shape.MeshOf(c);
            }

            w.source = w.ledger.AddFragment(anchors);
            w.registry.RegisterAuthored(
                w.source, w.root, body, w.shape, false,
                arrangedElsewhere ? null : geometryLocalToOwner ?? k_geometryLocalToOwner);
            w.lookup = new PhysicsOwnerPlacementLookup(w.registry);
            return w;
        }

        /// <summary>A cooked box collider for one convex, at that convex's own place.</summary>
        private Mesh BoxColliderOf(ConvexBrepBank bank, ConvexBrepRange range, string name)
        {
            var lo = new float3(float.PositiveInfinity);
            var hi = new float3(float.NegativeInfinity);
            for (int v = 0; v < range.vertexCount; v++)
            {
                float3 at = bank.vertices[range.vertexBase + v];
                lo = math.min(lo, at);
                hi = math.max(hi, at);
            }

            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, lo.y, lo.z),
                new Vector3(hi.x, hi.y, lo.z), new Vector3(lo.x, hi.y, lo.z),
                new Vector3(lo.x, lo.y, hi.z), new Vector3(hi.x, lo.y, hi.z),
                new Vector3(hi.x, hi.y, hi.z), new Vector3(lo.x, hi.y, hi.z),
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
            UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            _meshes.Add(mesh);
            return mesh;
        }

        /// <summary>Admits the cut and prepares its anchor distribution, which is what a build needs beforehand.</summary>
        private static CutOperationId Admit(World w)
        {
            Assert.That(
                w.ledger.Admit(w.source, new float4(w.harness.planeN, w.harness.planeW), true, out CutOperationId operation),
                Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(
                w.ledger.PrepareAnchorDistribution(operation, w.harness.eps, out AnchorDistributionResult _),
                Is.EqualTo(AnchorPreparationOutcome.Prepared));
            return operation;
        }

        /// <summary>Admits the cut and leaves its anchor distribution unprepared, which is an ordinary wait.</summary>
        private static CutOperationId AdmitWithoutAnchors(World w)
        {
            Assert.That(
                w.ledger.Admit(w.source, new float4(w.harness.planeN, w.harness.planeW), true, out CutOperationId operation),
                Is.EqualTo(LogicalCutAdmission.Admitted));
            return operation;
        }

        /// <summary>The 7.6 disposition of each convex against the plane, as the admitting side settles it.</summary>
        private static ConvexSide[] Classify(World w, float4 planeLocal)
        {
            var sides = new ConvexSide[w.shape.ConvexCount];
            for (int c = 0; c < sides.Length; c++)
            {
                ConvexBrepRange range = w.shape.Convex(c);
                int positive = 0, negative = 0;
                for (int i = 0; i < range.vertexCount; i++)
                {
                    float3 v = w.shape.BankOf(c).vertices[range.vertexBase + i];
                    float d = math.dot(planeLocal.xyz, v) + planeLocal.w;
                    if (d > w.harness.eps)
                    {
                        positive++;
                    }
                    else if (d < -w.harness.eps)
                    {
                        negative++;
                    }
                }

                sides[c] = positive > 0 && negative > 0 ? ConvexSide.Split
                    : negative > 0 ? ConvexSide.Negative
                    : positive > 0 ? ConvexSide.Positive
                    : ConvexSide.NearPlaneToPositive;
            }

            return sides;
        }

        /// <summary>A distribution of no anchors at all, for a candidate built before the ledger prepared one.</summary>
        private static AnchorDistributionResult Anchors()
        {
            FixedSupportAnchors.TryDistribute(
                new float3[0], new float4(0f, 1f, 0f, 0f), 1e-5f, new List<float3>(), new List<float3>(),
                out AnchorDistributionResult result);
            return result;
        }

        /// <summary>
        /// A source inertia that is **not uniform** and whose principal frame is **not the identity**, so that a
        /// tensor which was recomputed instead of carried over could not pass for the one the build decided.
        /// </summary>
        private static readonly float3 UnevenInertia = new float3(2f, 5f, 9f);

        private static readonly quaternion TurnedInertiaFrame =
            quaternion.AxisAngle(math.normalize(new float3(1f, 2f, 3f)), 0.7f);

        /// <summary>The unpublished pair of that cut, built from the source as it stands now.</summary>
        private ProvisionalOwnerCandidate Build(
            World w, CutOperationId operation, AnchorDistributionResult? distribution = null,
            bool unevenInertia = false)
        {
            PhysicsFragmentOwner owner = w.SourceOwner;
            AnchorDistributionResult anchors;
            if (distribution.HasValue)
            {
                anchors = distribution.Value;
            }
            else
            {
                Assert.That(
                    w.ledger.TryGetSettledAnchorDistribution(operation, out anchors), Is.True,
                    "the distribution a publication would use");
            }

            var plane = new float4(w.harness.planeN, w.harness.planeW);
            var input = new ProvisionalOwnerBuildInput
            {
                sourceShape = owner.Shape,
                sides = Classify(w, plane),
                planeLocal = plane,
                placement = owner.ReadPlacement(),
                sourceMotion = owner.ReadMotion(float3.zero),
                anchors = anchors,
                parentMass = owner.Mass,
                sourceInertia = unevenInertia ? UnevenInertia : new float3(4f, 4f, 4f),
                sourceInertiaRotation = unevenInertia ? TurnedInertiaFrame : quaternion.identity,
                cooking = PhysicsCutCook.DefaultCooking,
                name = "Provisional",
            };

            Assert.That(
                ProvisionalOwnerBuilder.TryBuild(
                    in input, out ProvisionalOwnerCandidate candidate, out PhysicsOwnerBuildOutcome outcome),
                Is.True,
                "the pair was built: " + outcome);
            _disposables.Add(candidate);
            return candidate;
        }

        private static ProvisionalPhysicsPublicationInput Publication(
            World w, CutOperationId operation, ProvisionalOwnerCandidate candidate,
            float positiveImpulse = 0f, float negativeImpulse = 0f)
        {
            return new ProvisionalPhysicsPublicationInput
            {
                ledger = w.ledger,
                registry = w.registry,
                operation = operation,
                source = w.source,
                candidate = candidate,
                builtFrom = w.SourceOwner.Shape,
                renderAnchor = float3.zero,
                positiveSeparationImpulse = positiveImpulse,
                negativeSeparationImpulse = negativeImpulse,
            };
        }

        private ProvisionalOwnerPair Publish(
            World w, CutOperationId operation, ProvisionalOwnerCandidate candidate,
            float positiveImpulse = 0f, float negativeImpulse = 0f)
        {
            ProvisionalPhysicsPublicationInput input =
                Publication(w, operation, candidate, positiveImpulse, negativeImpulse);
            Assert.That(
                ProvisionalPhysicsPublication.TryPublish(
                    in input, out ProvisionalOwnerPair pair, out LogicalCutResultOutcome ledgerOutcome),
                Is.EqualTo(PhysicsPublicationOutcome.Published),
                "the pair was published");
            Assert.That(ledgerOutcome, Is.EqualTo(LogicalCutResultOutcome.Applied), "the ledger allowed it");
            return pair;
        }

        [Test] public void D4_TwoRootsAndTwoPublishedPairs_UsePreparedMapsWithoutGrowth()
        {
            using var registry = new PhysicsOwnerRegistry();
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(2));
            registry.PrepareCapacity(2,2); ledger.PrepareCapacity(2,2);
            string[] names = {"_owners","_fragmentOfBody","_pairsByOperation","_pairsBySource","_pairOfBody"};
            var capacities = new int[names.Length];
            for(int i=0;i<names.Length;i++) capacities[i]=D4ColdPreparationTests.Capacity(registry,names[i]);
            using var a=NewWorld(registry:registry,ledger:ledger);
            using var b=NewWorld(registry:registry,ledger:ledger);
            b.root.transform.position=new Vector3(10,0,0);
            var oa=Admit(a);var ob=Admit(b);
            var pa=Publish(a,oa,Build(a,oa));var pb=Publish(b,ob,Build(b,ob));
            Assert.That(registry.Count,Is.EqualTo(2)); Assert.That(registry.ProvisionalPairCount,Is.EqualTo(2));
            Assert.That(ledger.FragmentCount,Is.EqualTo(2)); Assert.That(ledger.Budget.IncompleteCutOperationCount,Is.EqualTo(2));
            foreach(var pair in new[]{pa,pb})
            foreach(bool positive in new[]{true,false})
            {
                Assert.That(registry.TryResolveSource(pair.Side(positive).Body,out var source,out float side),Is.True);
                Assert.That(source,Is.EqualTo(pair.Source));Assert.That(side,Is.EqualTo(positive?1f:-1f));
            }
            for(int i=0;i<names.Length;i++) Assert.That(D4ColdPreparationTests.Capacity(registry,names[i]),Is.EqualTo(capacities[i]));
        }

        // ----- 1. the switch -----------------------------------------------------------------------------------------

        /// <summary>
        /// The switch is one thing: the source leaves the physics scene, both temporary actors enter it and hold the
        /// values the build decided, and the pair becomes what the cut has. **No logical child is made** -- the ledger
        /// has exactly the fragments it had, the cut is still accepted, and the source is still live.
        /// </summary>
        [Test]
        public void ThePairEntersTheSceneAsTheSourceLeavesIt_WithNoLogicalChild()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                int fragmentsBefore = w.ledger.FragmentCount;
                PhysicsOwnerSide positive = candidate.Positive;
                PhysicsOwnerSide negative = candidate.Negative;

                ProvisionalOwnerPair pair = Publish(w, operation, candidate);

                Assert.That(w.SourceOwner.IsWithdrawn, Is.True, "the source has left the scene");
                Assert.That(w.SourceOwner.Root, Is.Not.Null, "and is still registered, for whoever retires it");
                Assert.That(positive.Root.activeInHierarchy, Is.True, "the positive side is in the scene");
                Assert.That(negative.Root.activeInHierarchy, Is.True, "and so is the negative one");
                Assert.That(
                    positive.Body.mass, Is.EqualTo((float)positive.Mass).Within(1e-4f),
                    "and its body really holds the mass the build decided");
                Assert.That(
                    (float3)(Vector3)negative.Body.centerOfMass, Is.EqualTo(negative.CenterOfMass).Using(Float3Within(1e-4f)),
                    "and the negative side its centre of mass");
                Assert.That(
                    (float3)(Vector3)negative.Body.inertiaTensor, Is.EqualTo(negative.InertiaTensor).Using(Float3Within(1e-3f)),
                    "and its inertia");

                Assert.That(w.ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "no logical child was made");
                Assert.That(
                    w.ledger.TryGetActiveOperation(w.source, out CutOperationId still) && still.Equals(operation),
                    Is.True,
                    "the cut is still the accepted one of its source");
                Assert.That(
                    w.ledger.TryGetFragmentState(w.source, out LogicalFragmentState state)
                        && state == LogicalFragmentState.Live,
                    Is.True,
                    "which is still live");

                Assert.That(w.registry.ProvisionalPairCount, Is.EqualTo(1));
                Assert.That(w.registry.TryGetProvisional(operation, out ProvisionalOwnerPair found), Is.True);
                Assert.That(ReferenceEquals(found, pair), Is.True, "the cut has the pair this call made");
                Assert.That(candidate.IsDetached, Is.True, "and the candidate has given it up");

                // Disposing the candidate now destroys nothing: what it built belongs to the pair.
                candidate.Dispose();
                Assert.That(pair.IsStanding(true) && pair.IsStanding(false), Is.True, "both sides are still standing");
            }
        }

        // ----- 2. both actors are the same fragment --------------------------------------------------------------------

        /// <summary>
        /// Either actor of a published pair resolves to the **one source fragment** it stands in for, and says which
        /// side of the cut it is. The fragment is not split: that is what DESIGN 7.1.1 means by both hits resolving to
        /// the same source. A body of no pair resolves to nothing.
        /// </summary>
        [Test]
        public void EitherActorResolvesToTheOneSourceFragment()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                ProvisionalOwnerPair pair = Publish(w, operation, candidate);

                Assert.That(
                    w.registry.TryResolveSource(pair.Positive.Body, out LogicalFragmentId fromPositive, out float positiveSide),
                    Is.True);
                Assert.That(
                    w.registry.TryResolveSource(pair.Negative.Body, out LogicalFragmentId fromNegative, out float negativeSide),
                    Is.True);
                Assert.That(fromPositive, Is.EqualTo(w.source), "the positive actor is that fragment");
                Assert.That(fromNegative, Is.EqualTo(w.source), "and so is the negative one");
                Assert.That(positiveSide, Is.EqualTo(1f));
                Assert.That(negativeSide, Is.EqualTo(-1f));

                Assert.That(
                    w.registry.TryResolveSource(w.SourceOwner.Body, out LogicalFragmentId _, out float _), Is.False,
                    "the source's own body is not one of the pair's");

                w.registry.EndProvisional(operation);
                Assert.That(
                    w.registry.TryResolveSource(pair.Positive?.Body, out LogicalFragmentId _, out float _), Is.False,
                    "and once the pair has ended, neither of its actors resolves to anything");
            }
        }

        // ----- 3. each side is followed where it stands ----------------------------------------------------------------

        /// <summary>
        /// While the pair stands, each side of that cut is followed to its own actor, and moving one moves only what
        /// is drawn from it. The correspondence is the source's own, carried as it is: no side is displaced for the
        /// display.
        /// </summary>
        [Test]
        public void EachSideIsFollowedToItsOwnActor()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                ProvisionalOwnerPair pair = Publish(w, operation, candidate);

                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, 1f, out Matrix4x4 positive),
                    Is.EqualTo(VpFragmentPlacementKind.Following));
                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, -1f, out Matrix4x4 negative),
                    Is.EqualTo(VpFragmentPlacementKind.Following));
                Same(pair.Positive.Root.transform.localToWorldMatrix * k_geometryLocalToOwner, positive, "the positive side");
                Same(pair.Negative.Root.transform.localToWorldMatrix * k_geometryLocalToOwner, negative, "the negative side");

                // One side moves. The other is where it was.
                pair.Positive.Root.transform.position = new Vector3(5f, 1f, -2f);
                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, 1f, out Matrix4x4 movedPositive),
                    Is.EqualTo(VpFragmentPlacementKind.Following));
                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, -1f, out Matrix4x4 stillNegative),
                    Is.EqualTo(VpFragmentPlacementKind.Following));
                Same(pair.Positive.Root.transform.localToWorldMatrix * k_geometryLocalToOwner, movedPositive, "it moved");
                Same(negative, stillNegative, "and the other side did not move with it");
            }
        }

        /// <summary>
        /// A query that cannot name one of the two sides is <see cref="VpFragmentPlacementKind.Missing"/> while the
        /// pair stands: the body as a whole is nowhere, and neither half of it nor the withdrawn source is handed back
        /// in its place. No new reason is introduced for it.
        /// </summary>
        [Test]
        public void AQueryThatNamesNoSideOfTheStandingPair_IsMissing()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                Publish(w, operation, candidate);

                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, default, 0f, out Matrix4x4 _),
                    Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "the body as a whole is not standing anywhere");

                // Some other cut's side, of the same fragment: not this pair's, so not this pair's answer either.
                LogicalFragmentId stranger = w.ledger.AddFragment();
                Assert.That(
                    w.ledger.Admit(stranger, new float4(0f, 1f, 0f, 0f), true, out CutOperationId elsewhere),
                    Is.EqualTo(LogicalCutAdmission.Admitted));
                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, elsewhere, 1f, out Matrix4x4 _),
                    Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "and a side of some other cut is not one of these two");
            }
        }

        /// <summary>
        /// With no pair, the answer is what it always was — for the body and for a side alike. A caller that names a
        /// side of an accepted cut nobody has published a pair for is told where its fragment stands, which is where
        /// both of its sides are.
        /// </summary>
        [Test]
        public void WithNoPair_ASideIsAnsweredAsItsFragmentAlwaysWas()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);

                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, default, 0f, out Matrix4x4 whole),
                    Is.EqualTo(VpFragmentPlacementKind.Following));
                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, 1f, out Matrix4x4 positive),
                    Is.EqualTo(VpFragmentPlacementKind.Following));
                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, -1f, out Matrix4x4 negative),
                    Is.EqualTo(VpFragmentPlacementKind.Following));

                Matrix4x4 expected = w.root.transform.localToWorldMatrix * k_geometryLocalToOwner;
                Same(expected, whole, "the body stands where its owner does");
                Same(expected, positive, "and so does each side of a cut with no pair");
                Same(expected, negative, "both of them");
            }
        }

        /// <summary>
        /// A lineage whose display is arranged some other way keeps saying so through the pair: its sides are
        /// <see cref="VpFragmentPlacementKind.Static"/>, which is a statement and not a gap.
        /// </summary>
        [Test]
        public void APairOfALineageThatFollowsNothing_IsStatic()
        {
            using (World w = NewWorld(arrangedElsewhere: true))
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                Publish(w, operation, candidate);

                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, 1f, out Matrix4x4 _),
                    Is.EqualTo(VpFragmentPlacementKind.Static));
            }
        }

        // ----- 4. what a collection has already settled ---------------------------------------------------------------

        /// <summary>
        /// A publication changes what the **next** collection is built from and nothing that a collection has already
        /// settled. Nothing of the display is read or written while the switch happens.
        /// </summary>
        [Test]
        public void WhatWasAlreadyCollected_IsNotChangedByAPublication()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);

                // A collection before the switch: the side is drawn where the whole body is.
                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, 1f, out Matrix4x4 collected),
                    Is.EqualTo(VpFragmentPlacementKind.Following));

                ProvisionalOwnerPair pair = Publish(w, operation, candidate);
                pair.Positive.Root.transform.position = new Vector3(0f, 4f, 0f);

                Same(
                    w.root.transform.localToWorldMatrix * k_geometryLocalToOwner, collected,
                    "what that collection settled is what it settled");
                Assert.That(
                    w.lookup.TryGetGeometryLocalToWorld(w.source, operation, 1f, out Matrix4x4 next),
                    Is.EqualTo(VpFragmentPlacementKind.Following));
                Assert.That(
                    Approximately(collected, next), Is.False, "and the next collection is built from the pair");
            }
        }

        // ----- 5. refusal, failure and what follows them ---------------------------------------------------------------

        /// <summary>
        /// A cut that is no longer the accepted cut of a live fragment is refused, and **nothing changes**: the source
        /// keeps its physics, no pair is made, the candidate is still the caller's, and nothing is aborted for it.
        /// </summary>
        [Test]
        public void ACutTheLedgerNoLongerHasAsItsSources_IsRefusedAndChangesNothing()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                Assert.That(w.ledger.Abort(operation), Is.EqualTo(LogicalCutResultOutcome.Applied));

                // Aborting retires the source through the ledger, so a second world's worth of state is not needed:
                // what this checks is that the physics is not touched by the refusal itself.
                ProvisionalPhysicsPublicationInput input = Publication(w, operation, candidate);
                Assert.That(
                    ProvisionalPhysicsPublication.TryPublish(
                        in input, out ProvisionalOwnerPair pair, out LogicalCutResultOutcome ledgerOutcome),
                    Is.EqualTo(PhysicsPublicationOutcome.LedgerRefused));
                Assert.That(
                    ledgerOutcome, Is.EqualTo(LogicalCutResultOutcome.NotActive),
                    "with the ledger's own reason: " + ledgerOutcome);
                Assert.That(pair, Is.Null);
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "no pair was made");
                Assert.That(candidate.IsDetached, Is.False, "and the candidate is still the caller's");
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "with nothing of it in the scene");
            }
        }

        /// <summary>
        /// A cut whose anchor distribution was never prepared is **refused**, with the ledger's own reason, and
        /// nothing changes. It is not a pair that could not be built: the distribution is what a display's two sides
        /// are made from, so publishing this cut's physics would leave the body standing nowhere and no side standing
        /// anywhere. Waiting for it is an ordinary wait, and the cut is still there to publish once it is prepared.
        /// </summary>
        [Test]
        public void ACutWhoseAnchorsAreNotPrepared_IsRefusedAndTheCutSurvives()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = AdmitWithoutAnchors(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation, Anchors());

                ProvisionalPhysicsPublicationInput input = Publication(w, operation, candidate);
                Assert.That(
                    ProvisionalPhysicsPublication.TryPublish(
                        in input, out ProvisionalOwnerPair pair, out LogicalCutResultOutcome ledgerOutcome),
                    Is.EqualTo(PhysicsPublicationOutcome.LedgerRefused));
                Assert.That(
                    ledgerOutcome, Is.EqualTo(LogicalCutResultOutcome.AnchorsNotPrepared),
                    "and the ledger said why: " + ledgerOutcome);
                Assert.That(pair, Is.Null);
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero);
                Assert.That(w.SourceOwner.IsWithdrawn, Is.False, "the source keeps its own physics");
                Assert.That(candidate.IsDetached, Is.False, "and the candidate is still the caller's");
                Assert.That(
                    w.ledger.TryGetActiveOperation(w.source, out CutOperationId still) && still.Equals(operation),
                    Is.True,
                    "the cut is still the accepted one: waiting is not an abort");

                // Once it is prepared, the same cut publishes.
                Assert.That(
                    w.ledger.PrepareAnchorDistribution(operation, w.harness.eps, out AnchorDistributionResult _),
                    Is.EqualTo(AnchorPreparationOutcome.Prepared));
                Publish(w, operation, candidate);
                Assert.That(w.registry.ProvisionalPairCount, Is.EqualTo(1));
            }
        }

        /// <summary>
        /// A cut whose source's physical-ownership authority moved since admission is **stale**: the ledger says so,
        /// reclaims the operation once, and the physics is not touched. Being the fragment's active operation is not
        /// the same question, which is why the ledger's own preparation is what decides.
        /// </summary>
        [Test]
        public void ACutWhoseSourceAuthorityMoved_IsRefusedAsStale()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);

                // Somebody else took over this fragment's physics and told the ledger.
                w.ledger.NoteOwnershipChanged(w.source);

                ProvisionalPhysicsPublicationInput input = Publication(w, operation, candidate);
                Assert.That(
                    ProvisionalPhysicsPublication.TryPublish(
                        in input, out ProvisionalOwnerPair pair, out LogicalCutResultOutcome ledgerOutcome),
                    Is.EqualTo(PhysicsPublicationOutcome.LedgerRefused));
                Assert.That(
                    ledgerOutcome, Is.EqualTo(LogicalCutResultOutcome.Stale), "with the ledger's own reason");
                Assert.That(pair, Is.Null);
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero);
                Assert.That(w.SourceOwner.IsWithdrawn, Is.False, "the source keeps its own physics");
                Assert.That(candidate.IsDetached, Is.False);
            }
        }

        /// <summary>
        /// Both sides come out of the publication holding **the values the build decided**, not ones Unity worked out
        /// from their colliders. The two automatic mass properties are declared explicit before the body enters the
        /// scene now, so this is what says that the change did not cost the values: the mass, the centre of mass, the
        /// inertia and the motion are read back from the bodies themselves and compared with the build's, on both
        /// sides, and the automatic flags are off.
        /// </summary>
        [Test]
        public void BothPublishedSides_HoldTheBuildsMassPropertiesAndMotion_NotAutomaticOnes()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);

                // Not still, and not symmetric: the source is moving and spinning -- the publication reads the
                // motion from the source body itself -- and the inertia is uneven with a turned principal frame.
                SetSourceMoving(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation, null, true);
                PhysicsOwnerSide positive = candidate.Positive;
                PhysicsOwnerSide negative = candidate.Negative;

                Publish(w, operation, candidate);

                foreach (PhysicsOwnerSide side in new[] { positive, negative })
                {
                    string which = side.positive ? "the positive side" : "the negative side";
                    Assert.That(side.Root.activeInHierarchy, Is.True, which + " is in the scene");
                    Assert.That(
                        side.Body.automaticCenterOfMass, Is.False,
                        which + "'s centre of mass is given, not computed");
                    Assert.That(
                        side.Body.automaticInertiaTensor, Is.False, which + "'s inertia is given, not computed");
                    Assert.That(
                        side.Body.mass, Is.EqualTo((float)side.Mass).Within(1e-4f), which + " holds the build's mass");
                    Assert.That(
                        (float3)(Vector3)side.Body.centerOfMass, Is.EqualTo(side.CenterOfMass).Using(Float3Within(1e-4f)),
                        which + " holds the build's centre of mass");
                    Assert.That(
                        (float3)(Vector3)side.Body.inertiaTensor, Is.EqualTo(side.InertiaTensor).Using(Float3Within(1e-3f)),
                        which + " holds the build's inertia");
                    Assert.That(
                        math.length(side.InertiaTensor - new float3(side.InertiaTensor.x)), Is.GreaterThan(0.1f),
                        which + "'s inertia is uneven, so a recomputed one could not pass for it");
                    AssertSameInertiaTensor(side, which);
                    Assert.That(
                        (float3)(Vector3)side.Body.linearVelocity, Is.EqualTo(side.LinearVelocity).Using(Float3Within(1e-3f)),
                        which + " holds the first split's velocity");
                    Assert.That(
                        math.length(side.LinearVelocity), Is.GreaterThan(0.1f), which + " was given a velocity to hold");
                    Assert.That(
                        (float3)(Vector3)side.Body.angularVelocity, Is.EqualTo(side.AngularVelocity).Using(Float3Within(1e-3f)),
                        which + " holds the source's angular velocity");
                    Assert.That(
                        math.length(side.AngularVelocity), Is.GreaterThan(0.1f), which + " was given a spin to hold");
                }
            }
        }

        /// <summary>
        /// The side an anchor fixes is published kinematic **and holding the build's mass properties**: the flag and
        /// the values are two different things, and the ordering change touches the values' own flags. The side that
        /// carries the sibling constraint is the other one here, so the two cases stand side by side in one cut.
        /// </summary>
        [Test]
        public void TheAnchorFixedSide_IsKinematic_AndHoldsTheBuildsMassProperties()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);

                // One anchor above the plane fixes the positive side and leaves the negative one free (DESIGN 7.1).
                FixedSupportAnchors.TryDistribute(
                    new[] { new float3(0f, 0.5f, 0f) }, new float4(w.harness.planeN, w.harness.planeW), w.harness.eps,
                    new List<float3>(), new List<float3>(), out AnchorDistributionResult anchors);
                Assert.That(anchors.IsPositiveFixed, Is.True, "the anchor fixes the positive side");
                Assert.That(anchors.IsNegativeFixed, Is.False, "and leaves the negative one free");

                SetSourceMoving(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation, anchors, true);
                PhysicsOwnerSide fixedSide = candidate.Positive;
                PhysicsOwnerSide freeSide = candidate.Negative;

                Publish(w, operation, candidate);

                Assert.That(fixedSide.Body.isKinematic, Is.True, "the anchored side is fixed");
                Assert.That(freeSide.Body.isKinematic, Is.False, "and the other one is simulated");
                foreach (PhysicsOwnerSide side in new[] { fixedSide, freeSide })
                {
                    string which = side.FixedByAnchors ? "the anchored side" : "the free side";
                    Assert.That(side.Body.automaticCenterOfMass, Is.False, which + "'s centre of mass is given");
                    Assert.That(side.Body.automaticInertiaTensor, Is.False, which + "'s inertia is given");
                    Assert.That(
                        side.Body.mass, Is.EqualTo((float)side.Mass).Within(1e-4f), which + " holds the build's mass");
                    Assert.That(
                        (float3)(Vector3)side.Body.centerOfMass, Is.EqualTo(side.CenterOfMass).Using(Float3Within(1e-4f)),
                        which + " holds the build's centre of mass");
                    AssertSameInertiaTensor(side, which);
                }

                Assert.That(
                    (float3)(Vector3)fixedSide.Body.linearVelocity, Is.EqualTo(float3.zero).Using(Float3Within(1e-4f)),
                    "a fixed side takes no velocity");
                Assert.That(
                    (float3)(Vector3)fixedSide.Body.angularVelocity, Is.EqualTo(float3.zero).Using(Float3Within(1e-4f)),
                    "and no spin");
                Assert.That(
                    math.length((float3)(Vector3)freeSide.Body.linearVelocity), Is.GreaterThan(0.1f),
                    "while the free side carries the motion it was given");
            }
        }

        /// <summary>
        /// The source is given a velocity and a spin, so that the sides it is cut into have a motion to carry: the
        /// publication reads the motion from this body, not from what the build was handed.
        /// </summary>
        private static void SetSourceMoving(World w)
        {
            Rigidbody body = w.SourceOwner.Body;
            body.linearVelocity = new Vector3(0.4f, -0.2f, 0.9f);
            body.angularVelocity = new Vector3(0.3f, 1.1f, -0.5f);
        }

        /// <summary>The inertia the body really holds, as a tensor in its own frame: R diag(I) R^T.</summary>
        private static float3x3 TensorOf(Rigidbody body)
        {
            float3x3 rotation = new float3x3(body.inertiaTensorRotation);
            var diagonal = float3x3.zero;
            diagonal.c0.x = body.inertiaTensor.x;
            diagonal.c1.y = body.inertiaTensor.y;
            diagonal.c2.z = body.inertiaTensor.z;
            return math.mul(math.mul(rotation, diagonal), math.transpose(rotation));
        }

        /// <summary>
        /// The body's inertia is the build's, **compared as a tensor** so that the quaternion's sign and the order of
        /// its axes cannot hide a difference.
        /// </summary>
        private static void AssertSameInertiaTensor(PhysicsOwnerSide side, string which)
        {
            float3x3 held = TensorOf(side.Body);
            float3x3 rotation = new float3x3(side.InertiaRotation);
            var diagonal = float3x3.zero;
            diagonal.c0.x = side.InertiaTensor.x;
            diagonal.c1.y = side.InertiaTensor.y;
            diagonal.c2.z = side.InertiaTensor.z;
            float3x3 expected = math.mul(math.mul(rotation, diagonal), math.transpose(rotation));
            float difference = math.max(
                math.max(math.length(held.c0 - expected.c0), math.length(held.c1 - expected.c1)),
                math.length(held.c2 - expected.c2));
            Assert.That(
                difference, Is.LessThan(1e-2f),
                which + " holds the build's inertia as a tensor, principal frame included");
        }

        /// <summary>
        /// An exception raised **after one side is already in the scene** and before the switch is complete leaves
        /// nothing of the pair standing: both sides go back out, the source is where it was, and the two actors are
        /// still the candidate's. One half of a pair beside the source it was to replace is what this rules out.
        /// </summary>
        [Test]
        public void AnExceptionPartWayThroughTheSwitch_LeavesTheSourceAndTakesThePairBackOut()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                PhysicsOwnerSide positive = candidate.Positive;
                PhysicsOwnerSide negative = candidate.Negative;

                var established = new List<bool>();
                ProvisionalPhysicsPublication.establishedHook = carriesTheConstraint =>
                {
                    established.Add(carriesTheConstraint);
                    if (!carriesTheConstraint)
                    {
                        // The first side is in the scene by now. The switch is not complete.
                        throw new InvalidOperationException("part way through the switch");
                    }
                };

                ProvisionalPhysicsPublicationInput input = Publication(w, operation, candidate);
                Assert.That(
                    () => ProvisionalPhysicsPublication.TryPublish(
                        in input, out ProvisionalOwnerPair _, out LogicalCutResultOutcome _),
                    Throws.InvalidOperationException);

                Assert.That(established, Is.EqualTo(new[] { false }), "it got as far as the first side");
                Assert.That(negative.Root.activeInHierarchy, Is.False, "which is back out of the scene");
                Assert.That(positive.Root.activeInHierarchy, Is.False, "and the other never entered it");
                Assert.That(w.SourceOwner.IsWithdrawn, Is.False, "the source never left the scene");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "no pair was made");
                Assert.That(candidate.IsDetached, Is.False, "and the ownership did not move");
            }
        }

        /// <summary>
        /// A pair that cannot be established takes the ordinary continuation of DESIGN 7.1.1: the cut is aborted and
        /// the source retired. Nothing of the pair is in the scene.
        /// </summary>
        [Test]
        public void APairThatCannotBeEstablished_AbortsTheCutAndRetiresTheSource()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);

                // One side is gone by the time the publication is attempted. This is a physics failure, not a mistake
                // in the call: the pair cannot be established.
                UnityEngine.Object.DestroyImmediate(candidate.Negative.Root);

                ProvisionalPhysicsPublicationInput input = Publication(w, operation, candidate);
                Assert.That(
                    ProvisionalPhysicsPublication.TryPublish(
                        in input, out ProvisionalOwnerPair pair, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.PhysicsNotEstablished));
                Assert.That(pair, Is.Null);
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero);
                Assert.That(
                    w.ledger.TryGetFragmentState(w.source, out LogicalFragmentState state) && state == LogicalFragmentState.Live,
                    Is.False,
                    "the source was retired through the ledger");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner _), Is.False, "and its owner with it");
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "nothing of the pair is in the scene");
            }
        }

        /// <summary>
        /// After the switch there is nothing to convert: an unexpected exception is passed on as it is, and the pair
        /// it was raised after is published all the same.
        /// </summary>
        [Test]
        public void AnExceptionAfterTheSwitch_IsPassedOnAndThePairStands()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                ProvisionalPhysicsPublication.publishedHook = () => throw new InvalidOperationException("after the switch");

                ProvisionalPhysicsPublicationInput input = Publication(w, operation, candidate);
                Assert.That(
                    () => ProvisionalPhysicsPublication.TryPublish(
                        in input, out ProvisionalOwnerPair _, out LogicalCutResultOutcome _),
                    Throws.InvalidOperationException);

                Assert.That(w.registry.TryGetProvisional(operation, out ProvisionalOwnerPair pair), Is.True);
                Assert.That(pair.IsStanding(true) && pair.IsStanding(false), Is.True, "the pair is in the scene");
                Assert.That(w.SourceOwner.IsWithdrawn, Is.True, "and the source has left it");
            }
        }

        // ----- 6. ending a published pair --------------------------------------------------------------------------

        /// <summary>
        /// Ending a published pair takes both actors out of the scene, destroys them and gives their mesh holds back
        /// — once. What the source's own owner still holds stays held: a mesh goes back when the **last** holder lets
        /// go, and the source is one of them until it is retired in its turn.
        /// </summary>
        [Test]
        public void EndingThePairGivesBackWhatItHeld_Once()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                int heldByTheSource = w.meshSource.Users;
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                Assert.That(
                    w.meshSource.Users, Is.GreaterThan(heldByTheSource),
                    "the pair's two shapes took holds of their own");

                ProvisionalOwnerPair pair = Publish(w, operation, candidate);
                GameObject positiveRoot = pair.Positive.Root;
                GameObject negativeRoot = pair.Negative.Root;

                Assert.That(w.registry.EndProvisional(operation), Is.True);
                Assert.That(pair.IsEnded, Is.True);
                Assert.That(positiveRoot == null, Is.True, "the positive actor is gone");
                Assert.That(negativeRoot == null, Is.True, "and the negative one");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "and the cut has no pair any more");
                Assert.That(
                    w.meshSource.Users, Is.EqualTo(heldByTheSource), "the pair's holds went back, and only those");
                Assert.That(w.meshSource.IsReleased, Is.False, "the meshes are still the source's to use");

                Assert.That(w.registry.EndProvisional(operation), Is.False, "ending it again does nothing");
                Assert.That(
                    w.meshSource.Users, Is.EqualTo(heldByTheSource), "and nothing goes back twice");
                Assert.That(
                    w.registry.TryGetProvisionalOf(w.source, out ProvisionalOwnerPair _), Is.False,
                    "the source has no pair any more");
            }
        }

        /// <summary>
        /// The source's own owner is still there to retire after a pair has ended, and retiring it is what ends the
        /// last hold on the meshes. The order the two are ended in is the caller's: neither gives back what the other
        /// is still using.
        /// </summary>
        [Test]
        public void TheSourceIsStillThereToRetire_AndItsRetirementEndsTheLastHold()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                Publish(w, operation, candidate);

                // The source first, while the pair is still standing: what the pair shares stays.
                Assert.That(w.registry.Retire(w.source), Is.True);
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner _), Is.False);
                Assert.That(
                    w.meshSource.IsReleased, Is.False,
                    "the meshes are still held: the pair's colliders are using them");
                Assert.That(
                    w.registry.TryGetProvisional(operation, out ProvisionalOwnerPair standing) && standing.IsStanding(true),
                    Is.True,
                    "and the pair is still in the scene");

                Assert.That(w.registry.EndProvisional(operation), Is.True);
                Assert.That(w.meshSource.Users, Is.Zero, "with the last holder gone, nothing holds the meshes");
            }
        }

        // ----- 7. what the physics scene really does with the pair -------------------------------------------------

        /// <summary>
        /// **This one steps the simulation.** The two sides share the source's convexes, so their colliders overlap
        /// completely; with the sibling response suppressed they stay where they are put, and an outside body still
        /// pushes them. What is watched is the movement itself, not the value the constraint was configured with.
        /// <para>
        /// The scene is stepped by script and the mode is put back afterwards. Both sides are free — no anchor — and
        /// neither is given a separation impulse, so anything that moves them here is a contact.
        /// </para>
        /// </summary>
        [Test]
        public void TheSiblingsDoNotPushEachOtherApart_ButAnOutsideBodyStillDoes()
        {
            SimulationMode mode = UnityEngine.Physics.simulationMode;
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                ProvisionalOwnerPair pair = Publish(w, operation, candidate);
                pair.Positive.Body.useGravity = false;
                pair.Negative.Body.useGravity = false;

                try
                {
                    UnityEngine.Physics.simulationMode = SimulationMode.Script;
                    UnityEngine.Physics.SyncTransforms();
                    for (int i = 0; i < 10; i++)
                    {
                        UnityEngine.Physics.Simulate(0.02f);
                    }

                    float apart = (pair.Positive.Root.transform.position - pair.Negative.Root.transform.position).magnitude;
                    Assert.That(
                        apart, Is.LessThan(0.05f),
                        "two actors whose colliders lie on top of one another were not pushed apart: " + apart);

                    // An outside body, overlapping them, is not a sibling. It pushes.
                    Vector3 before = pair.Positive.Root.transform.position;
                    GameObject intruder = NewIntruder(new Vector3(0.2f, 0f, 0f));
                    UnityEngine.Physics.SyncTransforms();
                    for (int i = 0; i < 10; i++)
                    {
                        UnityEngine.Physics.Simulate(0.02f);
                    }

                    float moved = (pair.Positive.Root.transform.position - before).magnitude;
                    Assert.That(
                        moved, Is.GreaterThan(1e-3f),
                        "the outside body pushed the pair: it moved " + moved);
                    Assert.That(intruder != null, Is.True);
                }
                finally
                {
                    UnityEngine.Physics.simulationMode = mode;
                }
            }
        }

        /// <summary>
        /// The control for the case above: **the same input with the sibling response left on**. The two actors are
        /// then pushed apart by the very contact that the suppression is there to stop, so the measurement above is a
        /// measurement of the suppression and not of what the constraint allows.
        /// </summary>
        [Test]
        public void WithTheSiblingResponseLeftOn_TheSameInputDoesPushThemApart()
        {
            SimulationMode mode = UnityEngine.Physics.simulationMode;
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                ProvisionalOwnerPair pair = Publish(w, operation, candidate);
                pair.Positive.Body.useGravity = false;
                pair.Negative.Body.useGravity = false;

                // The one difference from the case above.
                Assert.That(pair.Separation.enableCollision, Is.False, "it is suppressed as built");
                pair.Separation.enableCollision = true;

                try
                {
                    UnityEngine.Physics.simulationMode = SimulationMode.Script;
                    UnityEngine.Physics.SyncTransforms();
                    for (int i = 0; i < 10; i++)
                    {
                        UnityEngine.Physics.Simulate(0.02f);
                    }

                    float apart = (pair.Positive.Root.transform.position - pair.Negative.Root.transform.position).magnitude;
                    Assert.That(
                        apart, Is.GreaterThan(0.05f),
                        "with the response on, the same two actors are pushed apart: " + apart);
                }
                finally
                {
                    UnityEngine.Physics.simulationMode = mode;
                }
            }
        }

        /// <summary>
        /// The order the two sides are activated in: the side carrying the constraint goes in last. What this shows is
        /// that the pair **is** published in that order and that, one step later, both actors and the constraint's
        /// reference to the other side are still there. It says nothing about what another order would do, and nothing
        /// about what the constraint holds.
        /// </summary>
        [Test]
        public void TheConstraintsOwnSideIsActivatedLast_AndBothActorsRemainAfterAStep()
        {
            SimulationMode mode = UnityEngine.Physics.simulationMode;
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                ConfigurableJoint joint = candidate.Separation;
                Assert.That(joint, Is.Not.Null, "the pair was built with its constraint");
                Assert.That(
                    ReferenceEquals(joint.gameObject, candidate.Positive.Root), Is.True,
                    "which is on the positive side, and so is activated last");
                Assert.That(
                    ReferenceEquals(joint.connectedBody, candidate.Negative.Body), Is.True,
                    "and names the negative side, which is in the scene by then");

                var order = new List<bool>();
                ProvisionalPhysicsPublication.establishedHook = carriesTheConstraint => order.Add(carriesTheConstraint);
                ProvisionalOwnerPair pair = Publish(w, operation, candidate);
                Assert.That(
                    order, Is.EqualTo(new[] { false, true }),
                    "the side without the constraint went in first, and the one with it last");

                try
                {
                    UnityEngine.Physics.simulationMode = SimulationMode.Script;
                    UnityEngine.Physics.SyncTransforms();
                    UnityEngine.Physics.Simulate(0.02f);
                    Assert.That(pair.Separation, Is.Not.Null, "the constraint is still there after a step");
                    Assert.That(
                        ReferenceEquals(pair.Separation.connectedBody, pair.Negative.Body), Is.True,
                        "still naming the other side");
                    Assert.That(pair.IsStanding(true) && pair.IsStanding(false), Is.True, "and both sides are standing");
                }
                finally
                {
                    UnityEngine.Physics.simulationMode = mode;
                }
            }
        }

        // ----- 8. the impulse is the caller's, one value per side ----------------------------------------------------

        /// <summary>
        /// Each side is given its own separation impulse, and each one's own mass turns it into that side's first
        /// velocity. Nothing here decides what those numbers should be: they are the caller's, and the direction is
        /// the existing one -- each free side away from the other along the adopted plane's normal.
        /// </summary>
        [Test]
        public void EachSideTakesItsOwnSeparationImpulse()
        {
            using (World w = NewWorld())
            {
                CutOperationId operation = Admit(w);
                ProvisionalOwnerCandidate candidate = Build(w, operation);
                PhysicsOwnerSide positive = candidate.Positive;
                PhysicsOwnerSide negative = candidate.Negative;

                Publish(w, operation, candidate, 6f, 1.5f);

                // The plane's normal is +y in the source's own frame, and the source is at the identity placement.
                float expectedPositive = 6f / (float)positive.Mass;
                float expectedNegative = 1.5f / (float)negative.Mass;
                Assert.That(
                    positive.Body.linearVelocity.y, Is.EqualTo(expectedPositive).Within(1e-3f),
                    "the positive side was given its own impulse, over its own mass");
                Assert.That(
                    negative.Body.linearVelocity.y, Is.EqualTo(-expectedNegative).Within(1e-3f),
                    "and the negative side its own, the other way");
                Assert.That(
                    expectedPositive, Is.Not.EqualTo(expectedNegative).Within(1e-3f),
                    "which are different numbers, so one value for both would not have done");
            }
        }

        // ----- helpers -------------------------------------------------------------------------------------------------

        private GameObject NewIntruder(Vector3 at)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _objects.Add(go);
            go.name = "Outside Body";
            go.transform.position = at;
            var body = go.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            return go;
        }

        private static void Same(Matrix4x4 expected, Matrix4x4 actual, string what)
        {
            for (int i = 0; i < 16; i++)
            {
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(1e-4f), what + ", element " + i);
            }
        }

        private static bool Approximately(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
            {
                if (math.abs(a[i] - b[i]) > 1e-4f)
                {
                    return false;
                }
            }

            return true;
        }

        private static IComparer<float3> Float3Within(float tolerance)
        {
            return new Float3Comparer(tolerance);
        }

        private sealed class Float3Comparer : IComparer<float3>
        {
            private readonly float _tolerance;

            internal Float3Comparer(float tolerance)
            {
                _tolerance = tolerance;
            }

            public int Compare(float3 x, float3 y)
            {
                return math.all(math.abs(x - y) <= _tolerance) ? 0 : 1;
            }
        }
    }
}
