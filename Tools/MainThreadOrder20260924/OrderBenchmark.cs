// Diagnostic template. The runner installs this into the PlayMode test assembly.
// Requires the temporary Zantetsu.PhysicsCut.CutOrderProbe instrumentation.
// This is a local Build/Publish experiment, not a CutWorld/worker/end-to-end benchmark.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public sealed class OrderBenchmark
    {
        private const int Rounds = 8;
        private const double ParentMass = 12.0;
        private static readonly float3 Inertia = new float3(4f);
        // Each row contains all four modes; each mode occupies every position over four rounds.
        private static readonly int[,] Latin =
        {
            { 0, 1, 3, 2 }, { 1, 2, 0, 3 }, { 2, 3, 1, 0 }, { 3, 0, 2, 1 },
        };
        private static readonly string[] SpanNames =
        {
            "buildtotal", "objects+", "objects-", "colliders+", "colliders-",
            "publishtotal", "SetActive+", "SetActive-", "BuildSide+", "BuildSide-",
            "newRoot+", "newRoot-",
        };

        private sealed class Sample
        {
            internal string Case;
            internal int CaseOrder, Round, Order, Mode, PositiveColliders, NegativeColliders;
            internal bool Published, Validated, Cleaned;
            internal readonly long[] Ticks = new long[12];
            internal readonly ulong[] Cycles = new ulong[12];
            internal readonly int[] Counts = new int[12];
        }

        private readonly List<Sample> _samples = new List<Sample>(72);
        private string _output;
        private Assets _assets;
        private PhysicsOwnerRegistry _registry;
        private PhysicsCutClassification _classification;
        private PhysicsOwnerShape _sourceShape;
        private ProvisionalOwnerCandidate _candidate;
        private ProvisionalOwnerPair _pair;
        private LogicalFragmentId _source;
        private CutOperationId _operation;
        private GameObject _sourceRoot, _positiveRoot, _negativeRoot;
        private bool _registered;
        private Sample _sample;

        [UnityTest]
        public IEnumerator MeasureBuildAndPublishOrder()
        {
            _output = Environment.GetEnvironmentVariable("ORDER_OUTPUT");
            Assert.That(_output, Is.Not.Null.And.Not.Empty, "ORDER_OUTPUT must name this run's new directory");
            Directory.CreateDirectory(_output);
            bool reverse = Flag("ORDER_REVERSE");
            bool characterFirst = Flag("ORDER_CHARACTER_FIRST");
            WriteEnvironment(reverse, characterFirst);

            try
            {
                for (int caseOrder = 0; caseOrder < 2; caseOrder++)
                {
                    bool character = (caseOrder == 0) == characterFirst;
                    _assets = new Assets();
                    _assets.Initialize(character);
                    yield return null;

                    // A separate warmup round supplies one first call per mode. All eight subsequent
                    // rounds are retained, including every slow sample. Nothing is removed after measurement.
                    for (int round = -1; round < Rounds; round++)
                    {
                        int row = round < 0 ? 0 : round % 4;
                        for (int order = 0; order < 4; order++)
                        {
                            int mode = Latin[row, reverse ? 3 - order : order];
                            _sample = new Sample
                            {
                                Case = character ? "character" : "box",
                                CaseOrder = caseOrder, Round = round, Order = order, Mode = mode,
                            };
                            _samples.Add(_sample);
                            LogicalCutLedger ledger = MakeSourceAndAdmission();
                            Assert.That(_sourceRoot.activeInHierarchy, Is.True);
                            // Input setup, source collider registration and classification are outside all spans.
                            yield return null;

                            ProvisionalOwnerBuildInput build = MakeBuild(ledger);
                            bool built = false;
                            PhysicsOwnerBuildOutcome buildOutcome = default;
                            PhysicsPublicationOutcome publishOutcome = default;
                            CutOrderProbe.Reset(mode);
                            CutOrderProbe.Enabled = true;
                            try
                            {
                                using (CutOrderProbe.Span(0))
                                {
                                    built = ProvisionalOwnerBuilder.TryBuild(in build, out _candidate, out buildOutcome);
                                }

                                if (!built || _candidate == null)
                                    throw new InvalidOperationException("build failed: " + buildOutcome);
                                _positiveRoot = _candidate.Positive.Root;
                                _negativeRoot = _candidate.Negative.Root;
                                _sample.PositiveColliders = _candidate.Positive.Colliders.Count;
                                _sample.NegativeColliders = _candidate.Negative.Colliders.Count;
                                var publication = new ProvisionalPhysicsPublicationInput
                                {
                                    ledger = ledger, registry = _registry, operation = _operation,
                                    source = _source, candidate = _candidate, builtFrom = _sourceShape,
                                    renderAnchor = float3.zero,
                                    positiveSeparationImpulse = 0f, negativeSeparationImpulse = 0f,
                                };
                                using (CutOrderProbe.Span(5))
                                {
                                    publishOutcome = ProvisionalPhysicsPublication.TryPublish(
                                        in publication, out _pair, out LogicalCutResultOutcome _);
                                }

                                _sample.Published = publishOutcome == PhysicsPublicationOutcome.Published;
                            }
                            finally
                            {
                                CutOrderProbe.Enabled = false;
                                Array.Copy(CutOrderProbe.Ticks, _sample.Ticks, 12);
                                Array.Copy(CutOrderProbe.Cycles, _sample.Cycles, 12);
                                Array.Copy(CutOrderProbe.Counts, _sample.Counts, 12);
                            }

                            Assert.That(buildOutcome, Is.EqualTo(PhysicsOwnerBuildOutcome.Ok));
                            Assert.That(publishOutcome, Is.EqualTo(PhysicsPublicationOutcome.Published));
                            Assert.That(_sample.PositiveColliders, Is.EqualTo(character ? 15 : 1));
                            Assert.That(_sample.NegativeColliders, Is.EqualTo(character ? 9 : 1));
                            AssertProbeCoverage(_sample);
                            Assert.That(_candidate.IsDetached, Is.True);
                            Assert.That(_sourceRoot.activeInHierarchy, Is.False);
                            AssertPair(false);

                            // The ordinary simulation, never Physics.Simulate or a manual dispatcher/worker pump.
                            yield return new WaitForFixedUpdate();
                            AssertPair(true);
                            _sample.Validated = true;
                            yield return CleanupSample();
                        }
                    }

                    yield return CleanupAssets();
                }

                Assert.That(_samples.Count, Is.EqualTo(72));
                for (int c = 0; c < 2; c++)
                {
                    for (int mode = 0; mode < 4; mode++)
                    {
                        int valid = 0, warm = 0;
                        foreach (Sample sample in _samples)
                        {
                            if (sample.CaseOrder != c || sample.Mode != mode) continue;
                            Assert.That(sample.Published && sample.Validated && sample.Cleaned, Is.True);
                            if (sample.Round < 0) warm++;
                            else valid++;
                        }
                        Assert.That(warm, Is.EqualTo(1));
                        Assert.That(valid, Is.EqualTo(Rounds));
                    }
                }

                SaveRows();
                // Written only after all source/pair objects, mesh borrowers and helper resources have ended.
                File.WriteAllText(Path.Combine(_output, "order-complete.txt"),
                    "Passed; cases=2; modes=4; warmup=8; valid=64; all sample cleanup confirmed\n",
                    new UTF8Encoding(false));
            }
            finally
            {
                CutOrderProbe.Enabled = false;
                CutOrderProbe.Reset(0);
                SaveRows(); // A failure keeps its partial measurements; no completion marker is written.
            }
        }

        private LogicalCutLedger MakeSourceAndAdmission()
        {
            _registry = new PhysicsOwnerRegistry();
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
            var indices = new int[_assets.Shape.ConvexCount];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            // Only this borrowed record is given to the registry. Assets keeps the owning authored shape,
            // its meshes and its native bank for the entire case; helper.Taken() is deliberately not called.
            _sourceShape = PhysicsOwnerShape.ProvisionalSide(_assets.Shape, indices);
            _sourceRoot = new GameObject("Order diagnostic source");
            Rigidbody body = _sourceRoot.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = Inertia;
            body.inertiaTensorRotation = Quaternion.identity;
            for (int c = 0; c < _sourceShape.ConvexCount; c++)
            {
                MeshCollider collider = _sourceRoot.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = _sourceShape.MeshOf(c);
            }

            _source = ledger.AddFragment();
            _registry.RegisterAuthored(_source, _sourceRoot, body, _sourceShape, false, Matrix4x4.identity);
            _registered = true;
            Assert.That(PhysicsCutClassification.TryClassify(
                _sourceShape, _assets.Plane, 1e-4f, ParentMass, 128, out _classification), Is.True);
            Assert.That(ledger.Admit(_source, _assets.Plane, true, out _operation),
                Is.EqualTo(LogicalCutAdmission.Admitted));
            return ledger;
        }

        private ProvisionalOwnerBuildInput MakeBuild(LogicalCutLedger ledger)
        {
            Assert.That(ledger.PrepareAnchorDistribution(_operation, 1e-5f, out AnchorDistributionResult anchors),
                Is.EqualTo(AnchorPreparationOutcome.Prepared));
            return new ProvisionalOwnerBuildInput
            {
                sourceShape = _sourceShape, sides = _classification.Sides, planeLocal = _assets.Plane,
                placement = PhysicsOwnerPlacement.Identity, sourceMotion = default, anchors = anchors,
                parentMass = ParentMass, sourceInertia = Inertia,
                sourceInertiaRotation = quaternion.identity, cooking = PhysicsCutCook.DefaultCooking,
                name = "Order diagnostic",
            };
        }

        private static void AssertProbeCoverage(Sample sample)
        {
            for (int span = 0; span < 12; span++)
                Assert.That(sample.Counts[span], Is.EqualTo(1), SpanNames[span] + " completed exactly once");
            Assert.That(sample.Ticks[8] + sample.Ticks[9], Is.LessThanOrEqualTo(sample.Ticks[0]));
            Assert.That(sample.Ticks[1] + sample.Ticks[3], Is.LessThanOrEqualTo(sample.Ticks[8]));
            Assert.That(sample.Ticks[2] + sample.Ticks[4], Is.LessThanOrEqualTo(sample.Ticks[9]));
            Assert.That(sample.Ticks[6] + sample.Ticks[7], Is.LessThanOrEqualTo(sample.Ticks[5]));
        }

        private void AssertPair(bool afterStep)
        {
            Assert.That(_pair, Is.Not.Null);
            Assert.That(_pair.IsStanding(true) && _pair.IsStanding(false), Is.True);
            Assert.That(_registry.ProvisionalPairCount, Is.EqualTo(1));
            ConfigurableJoint joint = _pair.Separation;
            Assert.That(joint != null, Is.True);
            Assert.That(joint.gameObject, Is.SameAs(_pair.Positive.Root));
            Assert.That(joint.connectedBody, Is.SameAs(_pair.Negative.Body));
            Assert.That(joint.autoConfigureConnectedAnchor, Is.False);
            Assert.That(joint.enableCollision, Is.False);
            Assert.That(math.length((float3)joint.anchor), Is.LessThan(1e-6f));
            Assert.That(math.length((float3)joint.connectedAnchor - math.normalize(_assets.Plane.xyz)),
                Is.LessThan(1e-5f));
            AssertSide(_pair.Positive, afterStep);
            AssertSide(_pair.Negative, afterStep);
            Assert.That(_pair.Positive.Mass + _pair.Negative.Mass, Is.EqualTo(ParentMass).Within(1e-8));
        }

        private static void AssertSide(PhysicsOwnerSide side, bool afterStep)
        {
            Rigidbody body = side.Body;
            Assert.That(side.Root.activeInHierarchy, Is.True);
            Assert.That(body.isKinematic, Is.False);
            Assert.That(body.automaticCenterOfMass || body.automaticInertiaTensor, Is.False);
            Assert.That(body.mass, Is.EqualTo((float)side.Mass).Within(1e-4f));
            Assert.That(math.length((float3)body.centerOfMass - side.CenterOfMass), Is.LessThan(1e-4f));
            float3x3 rotation = new float3x3((quaternion)body.inertiaTensorRotation);
            float3x3 expectedRotation = new float3x3(side.InertiaRotation);
            float3x3 actual = Tensor(rotation, body.inertiaTensor);
            float3x3 expected = Tensor(expectedRotation, side.InertiaTensor);
            float error = math.max(math.length(actual.c0 - expected.c0),
                math.max(math.length(actual.c1 - expected.c1), math.length(actual.c2 - expected.c2)));
            Assert.That(error, Is.LessThan(1e-2f));
            Assert.That(math.all(math.isfinite((float3)body.position)), Is.True);
            Assert.That(math.all(math.isfinite(((quaternion)body.rotation).value)), Is.True);
            Assert.That(math.all(math.isfinite((float3)body.linearVelocity)), Is.True);
            Assert.That(math.all(math.isfinite((float3)body.angularVelocity)), Is.True);
            if (!afterStep)
            {
                Assert.That(math.length((float3)body.linearVelocity - side.LinearVelocity), Is.LessThan(1e-4f));
                Assert.That(math.length((float3)body.angularVelocity - side.AngularVelocity), Is.LessThan(1e-4f));
            }
        }

        private static float3x3 Tensor(float3x3 rotation, float3 inertia)
        {
            float3x3 diagonal = float3x3.zero;
            diagonal.c0.x = inertia.x; diagonal.c1.y = inertia.y; diagonal.c2.z = inertia.z;
            return math.mul(math.mul(rotation, diagonal), math.transpose(rotation));
        }

        private IEnumerator CleanupSample()
        {
            CutOrderProbe.Enabled = false;
            // Capture the actors before ownership-ending APIs deliberately forget them.
            if (_positiveRoot == null) _positiveRoot = _pair?.Positive?.Root ?? _candidate?.Positive?.Root;
            if (_negativeRoot == null) _negativeRoot = _pair?.Negative?.Root ?? _candidate?.Negative?.Root;
            if (_registry != null)
            {
                _registry.EndProvisional(_operation);
                _registry.Retire(_source);
                _registry.Dispose();
                Assert.That(_registry.Count, Is.Zero);
                Assert.That(_registry.ProvisionalPairCount, Is.Zero);
                _registry = null;
            }

            _candidate?.Dispose();
            _candidate = null;
            _pair = null;
            if (!_registered && _sourceRoot != null) Object.Destroy(_sourceRoot);
            _sourceShape?.Dispose(); // idempotent after the registry ended its borrowed record
            _classification?.Dispose();
            _classification = null;
            yield return null;

            Assert.That(_sourceRoot == null && _positiveRoot == null && _negativeRoot == null, Is.True,
                "deferred destruction of all three actors completed before the next sample or asset disposal");
            if (_sourceShape != null) Assert.That(_sourceShape.IsFreed, Is.True);
            if (_assets?.Shape != null)
            {
                Assert.That(_assets.Shape.IsFreed, Is.False, "the helper still owns the source bank");
                Assert.That(_assets.Shape.BankUsers, Is.Zero, "all source and pair bank holds returned");
                foreach (Mesh mesh in _assets.Meshes) Assert.That(mesh != null, Is.True);
            }
            if (_sample != null) _sample.Cleaned = true;
            _sourceRoot = null; _positiveRoot = null; _negativeRoot = null;
            _sourceShape = null; _source = default; _operation = default; _registered = false;
        }

        private IEnumerator CleanupAssets()
        {
            if (_assets == null) yield break;
            Mesh[] meshes = _assets.Meshes;
            _assets.Dispose();
            _assets = null;
            yield return null; // SandboxCompoundBody's meshes use deferred destruction in PlayMode.
            if (meshes != null)
                foreach (Mesh mesh in meshes) Assert.That(mesh == null, Is.True, "helper mesh disposal completed");
        }

        [UnityTearDown]
        public IEnumerator CleanupAfterFailureOrCompletion()
        {
            CutOrderProbe.Enabled = false;
            // These fields survive a test assertion so resources are released in the same safe order on failure.
            yield return CleanupSample();
            yield return CleanupAssets();
            SaveRows();
        }

        private static bool Flag(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private void WriteEnvironment(bool reverse, bool characterFirst)
        {
            File.WriteAllText(Path.Combine(_output, "order-environment.txt"),
                "Unity=" + Application.unityVersion
                + "\nBackend=" + (Application.isEditor ? "Editor" : "Player; runner records scripting backend")
                + "\nMainManagedThread=" + Thread.CurrentThread.ManagedThreadId
                + "\nStopwatchFrequency=" + Stopwatch.Frequency
                + "\nEmptySpanMeanTicks=" + CutOrderProbe.EmptyTicks.ToString("R", CultureInfo.InvariantCulture)
                + "\nEmptySpanMeanCycles=" + CutOrderProbe.EmptyCycles.ToString("R", CultureInfo.InvariantCulture)
                + "\nGraphics=" + SystemInfo.graphicsDeviceType
                + "\nScreen=" + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode
                + "\nQuality=" + QualitySettings.names[QualitySettings.GetQualityLevel()]
                + "\nMSAA=" + QualitySettings.antiAliasing + "\nVSync=" + QualitySettings.vSyncCount
                + "\nTargetFrameRate=" + Application.targetFrameRate
                + "\nFixedDeltaTime=" + Time.fixedDeltaTime.ToString("R", CultureInfo.InvariantCulture)
                + "\nSimulationMode=" + Physics.simulationMode
                + "\nORDER_REVERSE=" + reverse + "\nORDER_CHARACTER_FIRST=" + characterFirst
                + "\nRounds=8 plus one separate warmup per mode per case"
                + "\nMode0=build positive first,activate negative first; mode1=reverse build; mode2=reverse activation; mode3=both"
                + "\nLocal TryBuild/TryPublish only; no CutWorldRoot, worker, renderer, Final handoff or full Request."
                + "\nOne source/pair at a time; each case reuses the same helper-owned authored meshes and bank."
                + "\nSource mass=12,inertia=(4,4,4),kinematic=true,useGravity=false; no anchors or separation impulse."
                + "\nActivation reversal keeps the joint on positive, so its connected negative body can still be inactive."
                + "\nStopwatch spans include interruptions; thread_cycles are query-thread-cycle deltas, not nanoseconds."
                + "\nNo forced GC, affinity/priority change, forced Physics.Simulate, or outlier removal.\n",
                new UTF8Encoding(false));
        }

        private void SaveRows()
        {
            if (string.IsNullOrEmpty(_output)) return;
            var csv = new StringBuilder();
            csv.AppendLine("case,case_order,round,order,mode,warmup,span_id,span,ticks,thread_cycles,count,frequency,positive_colliders,negative_colliders,build_positive_first,activate_positive_first,published,validated,cleaned");
            foreach (Sample sample in _samples)
            {
                for (int span = 0; span < 12; span++)
                {
                    csv.AppendFormat(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16},{17},{18}\n",
                        sample.Case, sample.CaseOrder, sample.Round, sample.Order, sample.Mode,
                        sample.Round < 0 ? 1 : 0, span, SpanNames[span], sample.Ticks[span], sample.Cycles[span],
                        sample.Counts[span], Stopwatch.Frequency, sample.PositiveColliders, sample.NegativeColliders,
                        (sample.Mode & 1) == 0 ? 1 : 0, (sample.Mode & 2) != 0 ? 1 : 0,
                        sample.Published ? 1 : 0, sample.Validated ? 1 : 0, sample.Cleaned ? 1 : 0);
                }
            }
            File.WriteAllText(Path.Combine(_output, "order.csv"), csv.ToString(), new UTF8Encoding(false));
        }

        private sealed class Assets : IDisposable
        {
            internal PhysicsOwnerShape Shape;
            internal float4 Plane;
            internal Mesh[] Meshes;
            private VpCpuGeometryStorage _storage;
            private IDisposable _helper;

            internal void Initialize(bool character)
            {
                _storage = new VpCpuGeometryStorage(262144, 1048576, 2048, 8192, 8192, Allocator.Persistent);
                if (character)
                {
                    string root = Environment.GetEnvironmentVariable("ORDER_INTAKE")
                        ?? @"C:\log\zantetsuken-vr\Phase41ActPlayer\intake\m8";
                    var body = SandboxCharacterBody.TryBuild(_storage, Path.Combine(root, "m_8.hulls.json"),
                        Path.Combine(root, "m_8.render.json"), 0);
                    _helper = body; Shape = body?.Shape; Plane = new float4(0, 0, 1, -0.9f);
                }
                else
                {
                    var body = SandboxCompoundBody.TryBuild(_storage, 1, 1, new float3(0.25f), 0);
                    _helper = body; Shape = body?.Shape; Plane = new float4(0, 1, 0, 0);
                }
                Assert.That(Shape, Is.Not.Null, "representative input loaded");
                Assert.That(Shape.ConvexCount, Is.EqualTo(character ? 19 : 1));
                Meshes = new Mesh[Shape.ConvexCount];
                for (int i = 0; i < Meshes.Length; i++) Meshes[i] = Shape.MeshOf(i);
            }

            public void Dispose()
            {
                // Called only after every actor is actually destroyed and all borrowed records have ended.
                if (Shape != null) Assert.That(Shape.BankUsers, Is.Zero);
                _helper?.Dispose(); _helper = null;
                _storage?.Dispose(); _storage = null; Shape = null;
            }
        }
    }
}
