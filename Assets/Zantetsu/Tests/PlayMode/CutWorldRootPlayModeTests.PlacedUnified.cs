using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The walk city's second round (TL, 2026-10-03): a Landscape object with the anchor the pipeline now authors (a traffic
    /// light: its foot stays fixed, the rest falls free), a vehicle without any (a free body: before its cut the scene's
    /// own, its own body moving as it was; cut, free pieces that carry its motion on, nothing fixed), and one look before and
    /// after a cut (the same surface drawn by a MeshRenderer of "Zantetsu/VP Mesh Surface" and by the cut world's display,
    /// the same colour pixel by pixel). Ignored where the licensed inputs are absent.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const string LandscapeInputPath = "Assets/Licensed/WalkCity/Inputs/traffic_light_001.json";
        private const string VehicleInputPath = "Assets/Licensed/WalkCity/Inputs/car_001.json";

        [UnityTest]
        public IEnumerator PlacedDeferred_ALandscapeObjectWithItsAuthoredAnchor_KeepsItsFootFixed_TheRestFallsFree()
        {
            if (!File.Exists(LandscapeInputPath)) Assert.Ignore("the licensed Landscape input is not in this checkout: " + LandscapeInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(LandscapeInputPath));
            Assert.That(data.anchors.Length, Is.GreaterThan(0), "the pipeline's anchor");
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("traffic_light_001", at, out Renderer[] renderers, out Collider[] colliders);
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                Vector3[] all = data.hulls.SelectMany(h => h.vertices).ToArray();
                float y = at.y + 0.6f * all.Max(v => v.y);   // through the pole, above the foot
                Evaluate(detector, Level(1, y, at.x + all.Min(v => v.x) - 1f, at.x + all.Max(v => v.x) + 1f, at.z + all.Min(v => v.z) - 1f, at.z + all.Max(v => v.z) + 1f), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance == ProvisionalCutAcceptance.Published || hits[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut: " + hits[0].Acceptance + " " + candidate.LastRefusal);
                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                LogicalCutOperation cut = OperationOf(root, hits[0].Operation);
                yield return Until(() => root.Owners.TryGet(cut.positive, out PhysicsFragmentOwner p) && p.Body != null && root.Owners.TryGet(cut.negative, out PhysicsFragmentOwner n) && n.Body != null, "both sides have bodies");
                root.Owners.TryGet(cut.positive, out PhysicsFragmentOwner upper);
                root.Owners.TryGet(cut.negative, out PhysicsFragmentOwner lower);
                if (upper.Body.worldCenterOfMass.y < lower.Body.worldCenterOfMass.y) (upper, lower) = (lower, upper);
                Assert.That(lower.FixedByAnchors && lower.Body.isKinematic, Is.True, "the foot, with the anchor, stays fixed");
                Assert.That(upper.FixedByAnchors || upper.Body.isKinematic, Is.False, "the rest is free");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the instance has left");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_AVehicleWithoutAnchors_IsTheScenesOwnUntilCut_ThenFreePiecesCarryItsMotion()
        {
            if (!File.Exists(VehicleInputPath)) Assert.Ignore("the licensed vehicle input is not in this checkout: " + VehicleInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(VehicleInputPath));
            Assert.That(data.anchors.Length, Is.Zero, "no anchor is authored for a vehicle");
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, 2f, 0f);
                GameObject instance = RefusalInstance("car_001 moving", at, out Renderer[] renderers, out Collider[] colliders);
                // The instance's own body, moving (a car driving), as the scene could have it: no gravity, a velocity.
                var own = instance.AddComponent<Rigidbody>();
                own.useGravity = false;
                own.mass = 1200f;
                var velocity = new Vector3(3f, 0f, 1f);
                own.linearVelocity = velocity;
                int bodies = Bodies();
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                for (int f = 0; f < 5; f++) yield return null;
                Assert.That(own.isKinematic, Is.False, "before its cut: its own body as it was");
                Assert.That(Vector3.Distance(own.linearVelocity, velocity), Is.LessThan(1e-3f), "moving as it was");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of the cut world");

                Vector3 now = instance.transform.position;
                Vector3[] all = data.hulls.SelectMany(h => h.vertices).ToArray();
                float y = now.y + 0.5f * (all.Min(v => v.y) + all.Max(v => v.y));
                Evaluate(detector, Level(1, y, now.x + all.Min(v => v.x) - 1f, now.x + all.Max(v => v.x) + 1f, now.z + all.Min(v => v.z) - 1f, now.z + all.Max(v => v.z) + 1f), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance == ProvisionalCutAcceptance.Published || hits[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut: " + hits[0].Acceptance + " " + candidate.LastRefusal);
                Assert.That(root.Ledger.TryGetAnchorCount(candidate.Source, out int anchorCount) ? anchorCount : 0, Is.Zero, "its fragment carries no anchor");
                LogicalCutOperation cut = OperationOf(root, hits[0].Operation);
                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                // The pieces: every owner of this vehicle's lineage now (a side may have been split further into its parts).
                List<PhysicsFragmentOwner> pieces = null;
                yield return Until(() => (pieces = LineagePieces(root, candidate.Source)).Count >= 2 && pieces.All(o => o.Body != null), "the vehicle's pieces have bodies");
                foreach (LogicalFragmentId side in new[] { cut.positive, cut.negative })
                {
                    root.Ledger.TryGetFragmentState(side, out LogicalFragmentState state);
                    TestContext.Out.WriteLine("side " + side + ": " + state);
                }

                TestContext.Out.WriteLine("pieces " + pieces.Count + ": " + string.Join("; ", pieces.Select(o => o.Body.linearVelocity.ToString("F3") + (o.Body.isKinematic ? " kinematic" : "") + (o.FixedByAnchors ? " fixed" : ""))));
                Assert.That(pieces.Any(o => o.FixedByAnchors || o.Body.isKinematic), Is.False, "nothing fixed: free pieces");
                // Horizontally: the pieces fall under gravity from their publication on.
                Assert.That(pieces.All(o => Vector2.Distance(new Vector2(o.Body.linearVelocity.x, o.Body.linearVelocity.z), new Vector2(velocity.x, velocity.z)) < 0.5f), Is.True, "each piece carries its motion on");
                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the instance has left");
                Assert.That(own.isKinematic, Is.True, "its own body stopped with it");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        // A prop with its anchors left out (TL, 2026-10-03: no anchor is a normal input whatever the family), standing as
        // placed -- with no body of its own, or with one moving (a velocity and a spin) -- and cut through its middle.
        private IEnumerator AnchorlessProp(bool moving, System.Action<PlacedCuttableCandidate, List<PhysicsFragmentOwner>, Rigidbody, Vector3> check)
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            data.anchors = new Vector3[0];   // as an input without any authored anchor is
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, 2f, 0f);
                GameObject instance = RefusalInstance("bench without anchors" + (moving ? " moving" : ""), at, out Renderer[] renderers, out Collider[] colliders);
                Rigidbody own = null;
                if (moving)
                {
                    own = instance.AddComponent<Rigidbody>();
                    own.useGravity = false;
                    own.mass = 80f;
                    own.linearVelocity = new Vector3(2f, 0f, -1f);
                    own.angularVelocity = new Vector3(0f, 3f, 0f);   // spinning about the vertical
                }

                string asPlaced = States(renderers, colliders);
                int bodies = Bodies();
                PlacedCuttableCandidate candidate = Candidate(root, data, instance, renderers, colliders);
                detector.AddPlaced(candidate);
                Vector3 p0 = instance.transform.position;
                for (int f = 0; f < 5; f++) yield return null;
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "before its cut: drawn and colliding as placed");
                Assert.That(Bodies(), Is.EqualTo(bodies), "no body of the cut world: nothing starts falling for want of an anchor");
                if (!moving) Assert.That(instance.transform.position, Is.EqualTo(p0), "standing where it was placed");

                Vector3 now = instance.transform.position;
                Vector3 sourceCentre = own != null ? own.worldCenterOfMass : now;
                Vector3[] all = data.hulls.SelectMany(h => h.vertices).ToArray();
                float y = now.y + 0.5f * (all.Min(v => v.y) + all.Max(v => v.y));
                float reach = all.Max(v => new Vector2(v.x, v.z).magnitude) + 1f;
                // At rest: a level cut. Moving and spinning about the vertical: an upright cut through the bench's own middle
                // (x as placed, the bench turned by its spin), so that its pieces' centres lie apart across the spin's axis.
                if (moving)
                {
                    Vector3 middle = instance.transform.TransformPoint(new Vector3(0.5f * (all.Min(v => v.x) + all.Max(v => v.x)), 0.5f * (all.Min(v => v.y) + all.Max(v => v.y)), 0.5f * (all.Min(v => v.z) + all.Max(v => v.z))));
                    Vector3 across = instance.transform.right;
                    Vector3 along = Vector3.Cross(across, Vector3.up).normalized;
                    var plane = new Plane(across, middle);
                    detector.Evaluate(new[] { new SlashSweep(1, 0.0, false, plane, along, Vector3.up,
                        middle - along * 3f - Vector3.up * 3f, middle - along * 3f + Vector3.up * 3f, middle + along * 3f - Vector3.up * 3f, middle + along * 3f + Vector3.up * 3f) }, new long[] { 1 });
                }
                else
                {
                    Evaluate(detector, Level(1, y, now.x - reach, now.x + reach, now.z - reach, now.z + reach), 1);
                }
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance == ProvisionalCutAcceptance.Published || hits[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut: " + hits[0].Acceptance + " " + candidate.LastRefusal);
                Assert.That(root.Ledger.TryGetAnchorCount(candidate.Source, out int anchorCount) ? anchorCount : 0, Is.Zero, "its fragment carries no anchor");
                List<PhysicsFragmentOwner> pieces = null;
                yield return Until(() => (pieces = LineagePieces(root, candidate.Source)).Count >= 2 && pieces.All(o => o.Body != null), "its pieces have bodies");
                check(candidate, pieces, own, sourceCentre);
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_APropWithoutAnchors_NoBodyOfItsOwn_StandsUntilCut_ThenItsPiecesAreFree()
        {
            yield return AnchorlessProp(false, (candidate, pieces, own, sourceCentre) =>
            {
                TestContext.Out.WriteLine("pieces " + pieces.Count + ": " + string.Join("; ", pieces.Select(o => "v " + o.Body.linearVelocity.ToString("F3") + " w " + o.Body.angularVelocity.ToString("F3"))));
                Assert.That(pieces.Any(o => o.FixedByAnchors || o.Body.isKinematic), Is.False, "nothing fixed: no piece holds an anchor");
                Assert.That(pieces.All(o => new Vector2(o.Body.linearVelocity.x, o.Body.linearVelocity.z).magnitude < 0.05f && o.Body.angularVelocity.magnitude < 0.5f), Is.True,
                    "it was at rest: its pieces start from rest (falling only)");
            });
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_APropWithoutAnchors_MovingAndSpinning_ItsPiecesCarryItsVelocityAndItsSpin()
        {
            yield return AnchorlessProp(true, (candidate, pieces, own, sourceCentre) =>
            {
                Assert.That(own.isKinematic, Is.True, "its own body stopped with it at its withdrawal");
                var v = new Vector3(2f, 0f, -1f);
                var w = new Vector3(0f, 3f, 0f);
                Assert.That(pieces.Any(o => o.FixedByAnchors || o.Body.isKinematic), Is.False, "nothing fixed");
                var published = new Dictionary<PhysicsFragmentOwner, Vector3>();
                foreach (PhysicsFragmentOwner o in pieces)
                {
                    // As the moving body's point at the piece's centre moved: v + w x (centre - the body's centre at the hit), the
                    // piece's centre taken back to its publication (the time since from the fall gravity has given it).
                    float since = Mathf.Max(0f, -o.Body.linearVelocity.y / -Physics.gravity.y);
                    Vector3 v0 = new Vector3(o.Body.linearVelocity.x, 0f, o.Body.linearVelocity.z);
                    Vector3 centreAtPublication = o.Body.worldCenterOfMass - v0 * since;
                    published[o] = centreAtPublication;
                    Vector3 expected = v + Vector3.Cross(w, centreAtPublication - sourceCentre);
                    TestContext.Out.WriteLine("piece: v " + o.Body.linearVelocity.ToString("F3") + " expected " + expected.ToString("F3") + "; w " + o.Body.angularVelocity.ToString("F3")
                        + "; centre " + o.Body.worldCenterOfMass.ToString("F3") + " (the body's at the hit " + sourceCentre.ToString("F3") + ")");
                    // Read once the pieces stand in the physics scene: a step or so after the publication, so the drag on the spin and
                    // the step's own move are allowed for here; what the step cannot change is checked below.
                    Assert.That(Vector3.Distance(o.Body.angularVelocity, w), Is.LessThan(0.05f), "it spins as the body did");
                    Assert.That(Vector2.Distance(new Vector2(o.Body.linearVelocity.x, o.Body.linearVelocity.z), new Vector2(expected.x, expected.z)), Is.LessThan(0.1f), "it moves as that point of the body did");
                }

                // Between the pieces: their velocities differ by w x (the difference of their centres at the publication).
                PhysicsFragmentOwner a = pieces[0], b = pieces[1];
                Vector3 differs = a.Body.linearVelocity - b.Body.linearVelocity;
                Vector3 bySpin = Vector3.Cross(w, published[a] - published[b]);
                TestContext.Out.WriteLine("pieces differ by " + differs.ToString("F3") + "; w x (their centres' difference) " + bySpin.ToString("F3"));
                Assert.That(new Vector2(differs.x, differs.z).magnitude, Is.GreaterThan(0.3f), "the spin shows: the pieces move differently");
                Assert.That(Vector2.Distance(new Vector2(differs.x, differs.z), new Vector2(bySpin.x, bySpin.z)),
                    Is.LessThan(0.05f * new Vector2(bySpin.x, bySpin.z).magnitude), "and by w x r (within 5%: the Final handoff puts each piece's centre of mass at its final shape's, keeping that centre's velocity)");
            });
        }

        [UnityTest]
        public IEnumerator PlacedDeferred_ABuildingWithoutAnchors_GoesIntoTheHullTrial_AndIsHandedOverAsAnAnchoredOneIs()
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var college = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            college.anchors = new Vector3[0];
            CutWorldRoot root = NewKinematicWorld(BindSourceZero);
            try
            {
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("college_001 without anchors", at, out Renderer[] renderers, out Collider[] colliders);
                var candidate = new PlacedCuttableCandidate(root, college, instance.transform, renderers, colliders, 10000f, SideMaterial);
                _candidates.Add(candidate);
                detector.AddPlaced(candidate);
                Evaluate(detector, ThroughTheMiddle(1, college, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                TestContext.Out.WriteLine("building without anchors: " + hits[0].Acceptance + "; " + candidate.LastRefusal);
                Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "the group took the hit");
                HullGroup group = candidate.Registration.Group;
                Assert.That(group.Anchored, Is.False, "a group without an anchor");
                Assert.That(group.Kinematic, Is.True, "held kinematic by the always-kinematic mode, not by an anchor");
                yield return Until(() => !group.AwaitsFirstCut, "its first cut published");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "handed over");
                yield return EndWorld(root);
            }
            finally
            {
                DisposeCandidates();
            }
        }

        [UnityTest]
        public IEnumerator UnifiedLook_TheSameSurface_DrawnByItsMeshRendererAndByTheCutWorld_IsTheSameColour()
        {
            if (!File.Exists(RefusalPropInputPath)) Assert.Ignore("the licensed city walk input is not in this checkout: " + RefusalPropInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(RefusalPropInputPath));
            // One texture and colour for both: a ramp, so that a wrong texel or a wrong shade shows.
            var ramp = Track(new Texture2D(256, 256, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, name = "ramp" });
            var pixels = new Color32[256 * 256];
            for (int v = 0; v < 256; v++) for (int u = 0; u < 256; u++) pixels[v * 256 + u] = new Color32((byte)u, (byte)v, (byte)((u + v) / 2), 255);
            ramp.SetPixels32(pixels);
            ramp.Apply();
            Material display = null;
            CutWorldRoot root = NewHullWorld(0.9f, null, 0.3f, r =>
            {
                var field = typeof(CutWorldRoot).GetField("materials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var bound = (CutWorldRoot.MaterialBinding[])field.GetValue(r);
                display = Track(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")) { name = "display" });
                display.SetTexture("_BaseMap", ramp);
                display.SetColor("_BaseColor", Color.white);
                display.SetFloat("_VpUsePaletteAtlas", 0f);
                field.SetValue(r, bound.Concat(new[] { new CutWorldRoot.MaterialBinding { sourceIndex = 0, material = display } }).ToArray());
                // The display's shadow casters, as the city's world has them (CutWorldShadowCaster / the immediate one).
                Shader caster = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
                Material stable = Track(new Material(caster) { name = "stable caster" });
                stable.SetFloat("_Cull", (float)CullMode.Back);
                Material immediate = Track(new Material(caster) { name = "immediate caster" });
                immediate.SetFloat("_Cull", (float)CullMode.Off);
                SetPrivate(r, "shadowMaterial", stable);
                SetPrivate(r, "provisionalShadowMaterial", immediate);
            });
            yield return null;
            ShadowStage stage = NewShadowStage();
            stage.camera.orthographicSize = 1.5f;
            stage.camera.transform.SetPositionAndRotation(new Vector3(-2f, 4f, -3f), Quaternion.LookRotation(new Vector3(2f, -3.6f, 3f)));
            var surface = Track(new Material(Shader.Find("Zantetsu/VP Mesh Surface")) { name = "mesh surface" });
            surface.SetTexture("_BaseMap", ramp);
            surface.SetColor("_BaseColor", display.GetColor("_BaseColor"));
            surface.SetFloat("_VpUsePaletteAtlas", 0f);

            // The bench as its MeshRenderer draws it (the input's own triangles, positions, normals and UVs).
            var mesh = Track(new Mesh { name = "bench" });
            mesh.SetVertices(data.render.Select(v => v.position).ToArray());
            mesh.SetNormals(data.render.Select(v => v.normal).ToArray());
            mesh.SetUVs(0, data.render.Select(v => v.uv).ToArray());
            mesh.SetTriangles(data.indices.Select(i => (int)i).ToArray(), 0);
            GameObject instance = TrackActor(new GameObject("bench as placed"));
            instance.transform.position = new Vector3(0f, 0.01f, 0f);
            instance.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = instance.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = surface;
            Color32[] before = null, after = null, beforeNoShadow = null, afterNoShadow = null;
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => before = d);
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => beforeNoShadow = d);

            // Its first cut's moment: the cut world draws it, the renderer off.
            PlacedCuttableRegistration made = PlacedCuttableRegistration.Register(root, data, instance.transform, new Renderer[] { renderer }, new Collider[0], 50f, false);
            _actors.Add(made.Actor);
            Assert.That(renderer.enabled, Is.False);
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => after = d);
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => afterNoShadow = d);
            // The object's pixels: those differing from the ground in both images.
            (int compared, float mean, int p95, int largest) Compare(Color32[] x, Color32[] y)
            {
                int ground = Mode(x);
                var differences = new List<int>();
                for (int i = 0; i < x.Length; i++)
                {
                    if (Mathf.Abs(Grey(x[i]) - ground) <= 6 || Mathf.Abs(Grey(y[i]) - ground) <= 6) continue;   // the ground (or an edge with it)
                    differences.Add(Mathf.Max(Mathf.Abs(x[i].r - y[i].r), Mathf.Max(Mathf.Abs(x[i].g - y[i].g), Mathf.Abs(x[i].b - y[i].b))));
                }

                differences.Sort();
                return (differences.Count, differences.Count > 0 ? (float)differences.Average() : 0f, differences.Count > 0 ? differences[(int)(0.95f * (differences.Count - 1))] : 0, differences.Count > 0 ? differences[differences.Count - 1] : 0);
            }

            var shading = Compare(beforeNoShadow, afterNoShadow);
            var shadowed = Compare(before, after);
            TestContext.Out.WriteLine("without shadows: object pixels " + shading.compared + ", mean channel difference " + shading.mean.ToString("F2") + ", 95th percentile " + shading.p95 + ", largest " + shading.largest);
            TestContext.Out.WriteLine("with the light's shadows: object pixels " + shadowed.compared + ", mean channel difference " + shadowed.mean.ToString("F2") + ", 95th percentile " + shadowed.p95 + ", largest " + shadowed.largest);
            Assert.That(shading.compared, Is.GreaterThan(200), "the bench covers the image");
            Assert.That(shading.mean, Is.LessThanOrEqualTo(2f), "the same colour on average (the shading)");
            Assert.That(shading.p95, Is.LessThanOrEqualTo(6), "and nearly everywhere (edges and the normal's quantisation aside)");
            Assert.That(shadowed.mean, Is.LessThanOrEqualTo(2f), "the same with the light's shadows (cast and received alike)");
            Assert.That(shadowed.p95, Is.LessThanOrEqualTo(6), "nearly everywhere");
            yield return EndWorld(root);
        }

        // Every owner of a lineage now, its root excepted (each fragment walked up through the operations that made it).
        private static List<PhysicsFragmentOwner> LineagePieces(CutWorldRoot root, LogicalFragmentId lineage)
        {
            var fragments = new List<LogicalFragmentId>();
            root.Owners.CopyFragmentsTo(fragments);
            var pieces = new List<PhysicsFragmentOwner>();
            foreach (LogicalFragmentId f in fragments)
            {
                LogicalFragmentId at = f;
                while (root.Ledger.TryGetOrigin(at, out CutOperationId op, out _) && root.Ledger.TryGetOperation(op, out LogicalCutOperation made)) at = made.source;
                if (at == lineage && f != lineage && root.Owners.TryGet(f, out PhysicsFragmentOwner o) && !o.IsWithdrawn) pieces.Add(o);
            }

            return pieces;
        }

        private static int Grey(Color32 c) => (c.r + c.g + c.b) / 3;

        private static int Mode(Color32[] image)
        {
            var histogram = new int[256];
            foreach (Color32 p in image) histogram[Grey(p)]++;
            int best = 0;
            for (int v = 1; v < 256; v++) if (histogram[v] > histogram[best]) best = v;
            return best;
        }
    }
}
