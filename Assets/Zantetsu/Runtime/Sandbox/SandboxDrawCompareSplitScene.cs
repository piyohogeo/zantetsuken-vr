using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The split scene of the draw comparison (2026-10-11, before the spatial split of DESIGN 7.9.2 is taken as a
    /// default): a fixed sequence of states around the split of an owner into two, each held still and placed by this
    /// code as the cut scene's are (<see cref="SandboxDrawCompareCutScene"/>), so that what the display draws before
    /// a split's publication and after it can be pictured from the same poses, by both eyes, and compared -- which a
    /// city walk cannot give (its view moves through every publication). A picture check, never a performance sample.
    /// <para>
    /// **The body is made here.** No placed prop of the city has convexes apart, so none can be split: the body is a
    /// trunk and a detached hand beside it -- two convexes and two closed parts of one geometry -- taken into the
    /// world as a placed prop's input is. One level cut through both leaves two pieces, each half of the trunk and
    /// half of the hand in one owner, each with the cut's faces on both parts: what the split is for.
    /// </para>
    /// <para>
    /// **The stages**, each pictured: the body whole; cut level and committed, its pieces put apart and turned so
    /// that the cut faces show, the split held off; the same state again (what two pictures of one state differ by);
    /// the split let through and both pieces replaced by two objects each, every one of them put where the piece it
    /// replaces stood -- a picture that differs from the one before shows something the split changed; the hands
    /// moved aside; one of the split's objects cut again with the cut's geometry held back (provisional caps, on an
    /// object the split made); that committed.
    /// </para>
    /// <para>
    /// **What the pictures do not show.** The two objects a split publishes are put back where the piece stood, by
    /// this code, and pictured some frames later: the stage after a split shows that the same geometry at the same
    /// placement is drawn the same -- body, caps, shadows. It does not show the frame of the publication itself, nor
    /// where the publication alone left the two objects (their inherited pose and motion): a fault that lasts only
    /// through the switch, or one of the inherited placement, is not in these pictures.
    /// </para>
    /// <para>
    /// **With another body** (<see cref="AnotherArgument"/>): a second body, one box, stands beside the first and is
    /// cut with its geometry held back before the split is let through -- provisional caps in the picture and two
    /// pieces waiting to be split. A cut whose geometry is still to come is not a cut in flight for the split's gate
    /// (the driver's record of it ends at its publication), so the split is issued and published while those caps
    /// are drawn. That state is pictured twice before the split is let through and once after its publications,
    /// the geometry still held: a picture that differs shows something the split changed beside provisional caps.
    /// Then the geometry is let go and the commit is pictured.
    /// </para>
    /// </summary>
    public static class SandboxDrawCompareSplitScene
    {
        /// <summary>After the measurement (mode unity only): the split scene's stages, pictured. Not a performance sample.</summary>
        public const string Argument = "-zantetsuDrawCompareSplitScene";

        /// <summary>Beside <see cref="Argument"/>: the sequence with another body, whose cut is held while the split is let through.</summary>
        public const string AnotherArgument = "-zantetsuDrawCompareSplitSceneAnother";

        /// <summary>The stages' names, in order.</summary>
        public static readonly string[] StageNames =
        {
            "s0-whole", "s1-cut-committed", "s2-same-again", "s3-split", "s4-split-apart", "s5-recut-held", "s6-recut-committed",
        };

        /// <summary>The stages' names with another body, in order.</summary>
        public static readonly string[] StageNamesWithAnother =
        {
            "s0-whole", "s1-cut-committed", "s2-same-again", "s3-other-cut-held", "s4-held-again", "s5-split-while-held", "s6-other-committed", "s7-split-apart", "s8-recut-held", "s9-recut-committed",
        };

        /// <summary>Where the other body stands, in the first body's frame: to the left of it, clear of everything the first's pieces are.</summary>
        public static readonly Vector3 AnotherAt = new Vector3(-2.2f, 0f, 0f);

        // The body in its own frame: the origin on the ground under the trunk, x to the right, y up, z forward.
        private static readonly Vector3 TrunkCentre = new Vector3(0f, 0.6f, 0f), TrunkHalf = new Vector3(0.4f, 0.6f, 0.3f);
        private static readonly Vector3 HandCentre = new Vector3(0.675f, 0.6f, 0f), HandHalf = new Vector3(0.125f, 0.4f, 0.15f);

        // Where the level cut goes through: the middle of the two together, at the height both are halved.
        private static readonly Vector3 Middle = new Vector3(0.2f, 0.6f, 0f);

        /// <summary>The body as made: its fragment, its frame, and what is given back once the world is released.</summary>
        public sealed class Made : IDisposable
        {
            public LogicalFragmentId fragment;
            public Vector3 position;
            public Quaternion rotation;
            public string described;
            internal PlacedCuttableRegistration registration;
            internal GameObject target;

            /// <summary>Only once the world no longer uses the body's shape (released or gone).</summary>
            public void Dispose()
            {
                registration?.Dispose();
                registration = null;
                if (target != null) UnityEngine.Object.Destroy(target);
                target = null;
            }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static long s_slash = 960000;

        private static readonly Vector3[] k_corners =
        {
            new Vector3(-1f, -1f, -1f), new Vector3(1f, -1f, -1f), new Vector3(1f, -1f, 1f), new Vector3(-1f, -1f, 1f),
            new Vector3(-1f, 1f, -1f), new Vector3(1f, 1f, -1f), new Vector3(1f, 1f, 1f), new Vector3(-1f, 1f, 1f),
        };

        private static readonly int[][] k_faces =
        {
            new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 }, new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 },
        };

        /// <summary>The trunk and the detached hand as a placed prop's input: one submesh, a convex each, no anchor. Or the trunk alone.</summary>
        public static PlacedCuttableInput Input(bool withHand = true)
        {
            (Vector3 centre, Vector3 half)[] boxes = withHand ? new[] { (TrunkCentre, TrunkHalf), (HandCentre, HandHalf) } : new[] { (TrunkCentre, TrunkHalf) };
            var faceIndices = new List<int>();
            foreach (int[] cycle in k_faces) faceIndices.AddRange(cycle);
            var uv = new[] { new Vector2(0.05f, 0.1f), new Vector2(0.95f, 0.1f), new Vector2(0.95f, 0.9f), new Vector2(0.05f, 0.9f) };
            var render = new List<PlacedCuttableInput.Vertex>();
            var indices = new List<uint>();
            var topology = new List<int>();
            var hulls = new List<PlacedCuttableInput.Hull>();
            for (int box = 0; box < boxes.Length; box++)
            {
                var corners = new Vector3[k_corners.Length];
                for (int i = 0; i < corners.Length; i++) corners[i] = boxes[box].centre + Vector3.Scale(k_corners[i], boxes[box].half);
                foreach (int[] c in k_faces)
                {
                    Vector3 normal = Vector3.Cross(corners[c[1]] - corners[c[0]], corners[c[2]] - corners[c[0]]).normalized;
                    uint first = (uint)render.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        render.Add(new PlacedCuttableInput.Vertex { position = corners[c[k]], normal = normal, uv = uv[k] });
                        topology.Add((k_corners.Length * box) + c[k]);
                    }

                    indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
                }

                hulls.Add(new PlacedCuttableInput.Hull { vertices = corners, faceOffsets = new[] { 0, 4, 8, 12, 16, 20, 24 }, faceIndices = faceIndices.ToArray() });
            }

            return new PlacedCuttableInput
            {
                schemaVersion = 1, topologyCount = k_corners.Length * boxes.Length, name = withHand ? "trunk_and_hand" : "trunk", source = "made by the split scene", sha256 = "",
                isBuilding = false, isCuttable = true,
                render = render.ToArray(), indices = indices.ToArray(), topology = topology.ToArray(), hulls = hulls.ToArray(),
                anchors = new Vector3[0],
            };
        }

        /// <summary>
        /// The body taken into the world, standing at <paramref name="position"/> (the ground under its trunk) and
        /// turned as <paramref name="rotation"/>: an ordinary body of two convexes, kinematic as placed. Without its
        /// hand it is the other body: one convex, which nothing splits.
        /// </summary>
        public static bool TryMake(CutWorldRoot world, Vector3 position, Quaternion rotation, out Made made, out string failure, bool withHand = true)
        {
            made = null;
            failure = null;
            var target = new GameObject(withHand ? "Split scene body (as placed)" : "Split scene other body (as placed)");
            target.transform.SetPositionAndRotation(position, rotation);
            try
            {
                PlacedCuttableRegistration registration = PlacedCuttableRegistration.Register(world, Input(withHand), target.transform, new Renderer[0], new Collider[0], 40f, false);
                made = new Made { fragment = registration.Fragment, position = position, rotation = rotation, described = registration.Description, registration = registration, target = target };
                return true;
            }
            catch (InvalidOperationException e)
            {
                UnityEngine.Object.Destroy(target);
                failure = "the body was not taken into the world: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// Where the body stands and where it is pictured from, for a Player: on the ground <paramref name="eye"/>
        /// looks over, 4.5 m or more from it in the first direction (from <paramref name="yaw"/>, by steps of 30
        /// degrees) where nothing of the scene stands in the room the pieces are put in and nothing is between the
        /// two views and the body. The body faces away from the eye, its hand to the eye's right; the first view is
        /// the eye's own place, the second 40 degrees round to the right, 3.8 m from the body. Where no direction is
        /// clear the first with ground is taken and <paramref name="described"/> says so (the pictures tell what
        /// stands in them); false only when no ground is found at all. With <paramref name="another"/> the room is
        /// wider by the other body's place (<see cref="AnotherAt"/>) and the views look at the middle of the two.
        /// </summary>
        public static bool TryStand(Vector3 eye, float yaw, bool another, out Vector3 position, out Quaternion rotation, out (Vector3 from, float yaw)[] views, out string described)
        {
            Vector3 roomCentre = another ? new Vector3(-0.5f, 1.85f, 0f) : new Vector3(0.6f, 1.85f, 0f);
            Vector3 roomHalf = another ? new Vector3(2.8f, 1.6f, 1f) : new Vector3(1.7f, 1.6f, 1f);
            float lookAt = another ? -0.5f : 0.6f;
            position = default;
            rotation = Quaternion.identity;
            views = null;
            Physics.SyncTransforms();
            int tried = 0, grounded = 0;
            bool any = false;
            Vector3 firstAt = default;
            float firstDirection = 0f, firstDistance = 0f;
            foreach (float distance in new[] { 4.5f, 6f, 8f })
            {
                for (int k = 0; k < 12; k++)
                {
                    tried++;
                    float direction = yaw + (30f * k);
                    Quaternion facing = Quaternion.Euler(0f, direction, 0f);
                    Vector3 over = eye + (facing * Vector3.forward * distance);
                    if (!Physics.Raycast(over, Vector3.down, out RaycastHit ground, 4f, ~0, QueryTriggerInteraction.Ignore)) continue;
                    grounded++;
                    if (!any)
                    {
                        any = true;
                        firstAt = ground.point;
                        firstDirection = direction;
                        firstDistance = distance;
                    }

                    // The room the pieces are put in, from a little above the ground: nothing of the scene in it.
                    if (Physics.CheckBox(ground.point + (facing * roomCentre), roomHalf, facing, ~0, QueryTriggerInteraction.Ignore)) continue;
                    Views(eye, ground.point, facing, lookAt, out views);
                    Vector3 seen = ground.point + (facing * new Vector3(lookAt, 1.2f, 0f));
                    if (Physics.Linecast(views[0].from, seen, ~0, QueryTriggerInteraction.Ignore) || Physics.Linecast(views[1].from, seen, ~0, QueryTriggerInteraction.Ignore)
                        || Physics.CheckSphere(views[1].from, 0.3f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    position = ground.point;
                    rotation = facing;
                    described = "stands " + distance.ToString("F1", Inv) + " m from the eye in direction " + direction.ToString("F0", Inv) + " (candidate " + tried + ", with ground " + grounded + "): the room and both views clear";
                    return true;
                }
            }

            if (!any)
            {
                described = "no ground was found under any of " + tried + " places 4.5 to 8 m round the eye";
                return false;
            }

            position = firstAt;
            rotation = Quaternion.Euler(0f, firstDirection, 0f);
            Views(eye, position, rotation, lookAt, out views);
            described = "stands " + firstDistance.ToString("F1", Inv) + " m from the eye in direction " + firstDirection.ToString("F0", Inv) + ": NO place of " + tried
                + " (with ground " + grounded + ") had its room and both views clear -- the first with ground was taken, something of the scene may stand in the pictures";
            return true;
        }

        // The two views of a body standing at a place: the eye's own, and one 40 degrees round to the right, 3.8 m from it.
        private static void Views(Vector3 eye, Vector3 at, Quaternion facing, float lookAt, out (Vector3 from, float yaw)[] views)
        {
            Vector3 look = at + (facing * new Vector3(lookAt, 0f, 0f));
            Vector3 back = new Vector3(eye.x - look.x, 0f, eye.z - look.z);
            Vector3 round = Quaternion.AngleAxis(-40f, Vector3.up) * back.normalized * 3.8f;
            var second = new Vector3(look.x + round.x, eye.y, look.z + round.z);
            views = new[]
            {
                (eye, Mathf.Atan2(look.x - eye.x, look.z - eye.z) * Mathf.Rad2Deg),
                (second, Mathf.Atan2(look.x - second.x, look.z - second.z) * Mathf.Rad2Deg),
            };
        }

        /// <summary>
        /// The stages, in order. A stage that cannot be made says why in <paramref name="failures"/> and the sequence
        /// ends there (the pictures taken so far stand); <paramref name="states"/> takes one line a stage, as the cut
        /// scene's, with what the split has done so far. With <paramref name="other"/> (a body of one convex) its cut
        /// is held back while the split is let through, and the stages are <see cref="StageNamesWithAnother"/>.
        /// </summary>
        public static IEnumerator Stages(SandboxDrawCompareCutScene.Io io, Made body, List<string> states, List<string> failures, Made other = null)
        {
            string[] names = other != null ? StageNamesWithAnother : StageNames;
            int stage = 0;
            CutWorldRoot world = io.world;
            CutSpatialSplit split = world.SpatialSplit;
            if (split == null || !split.Settings.enabled)
            {
                failures.Add("the world's spatial split is off (a Player is launched with " + CutWorldRoot.SpatialSplitArgument + ")");
                yield break;
            }

            Func<double> driverBudget = world.Driver.RemainingMainSeconds;
            Func<double> splitBudget = split.RemainingMainSeconds;
            world.Driver.RemainingMainSeconds = () => 1.0;
            // Held off until its stage: every turn of the split is held while nothing is left of the frame.
            split.RemainingMainSeconds = () => 0.0;
            var poses = new Dictionary<LogicalFragmentId, (Vector3 position, Quaternion rotation)>();
            Vector3 right = body.rotation * Vector3.right, forward = body.rotation * Vector3.forward;
            Vector3 At(Vector3 local) => body.position + (body.rotation * local);
            try
            {
                yield return Picture(io, names[stage++], states, poses, split);

                // ---- cut level through the trunk and the hand, committed; the pieces put apart, the cut faces shown ----
                var cutA = new List<LogicalCutOperation>();
                yield return CutAcross(io, At(Middle), Vector3.up, forward, 0.75f, 0.45f, "the body, level", cutA, failures);
                if (cutA.Count != 1)
                {
                    failures.Add("the level sweep through the body published " + cutA.Count + " cuts, not one");
                    yield break;
                }

                LogicalCutOperation a = cutA[0];
                yield return SandboxDrawCompareCutScene.UntilComplete(io, new[] { a.id }, "the body's cut", failures);
                if (failures.Count > 0) yield break;
                Further(world, a.positive, a.negative, Vector3.up, out LogicalFragmentId upper, out LogicalFragmentId lower);
                foreach ((LogicalFragmentId piece, string name) in new[] { (upper, "upper"), (lower, "lower") })
                {
                    int convexes = world.Owners.TryGet(piece, out PhysicsFragmentOwner owner) ? owner.Shape.ConvexCount : -1;
                    if (convexes != 2) failures.Add("the " + name + " piece holds " + convexes + " convexes, not half of the trunk and half of the hand");
                }

                if (failures.Count > 0) yield break;
                // The lower piece leans towards the eye and the upper one is lifted and leans away, so that the cut
                // faces of both -- on the trunk's half and on the hand's -- are seen from the views.
                SandboxDrawCompareCutScene.Place(io, poses, lower, body.position, body.rotation);
                SandboxDrawCompareCutScene.PlaceAbout(io, poses, lower, At(new Vector3(0.2f, 0.3f, 0f)), Quaternion.AngleAxis(-30f, right), Vector3.up * 0.2f);
                SandboxDrawCompareCutScene.Place(io, poses, upper, body.position, body.rotation);
                SandboxDrawCompareCutScene.PlaceAbout(io, poses, upper, At(new Vector3(0.2f, 0.9f, 0f)), Quaternion.AngleAxis(50f, right), Vector3.up * 1.1f);
                for (int i = 0; i < 3; i++) yield return null;
                if (split.Published != 0) failures.Add("a split was published while it was held off: " + split.Published);
                yield return Picture(io, names[stage++], states, poses, split);

                // ---- the same state again: what two pictures of one state differ by --------------------------------------
                for (int i = 0; i < 6; i++) yield return null;
                yield return Picture(io, names[stage++], states, poses, split);

                // ---- with another body: its cut held back, pictured twice before the split is let through --------------------
                LogicalCutOperation b = default;
                if (other != null)
                {
                    Vector3 OtherAt(Vector3 local) => other.position + (other.rotation * local);
                    io.hold(true);
                    var cutB = new List<LogicalCutOperation>();
                    yield return CutAcross(io, OtherAt(TrunkCentre), Vector3.up, forward, 0.55f, 0.45f, "the other body, level", cutB, failures);
                    if (cutB.Count != 1)
                    {
                        failures.Add("the level sweep through the other body published " + cutB.Count + " cuts, not one");
                        yield break;
                    }

                    b = cutB[0];
                    Further(world, b.positive, b.negative, Vector3.up, out LogicalFragmentId otherUpper, out LogicalFragmentId otherLower);
                    // Put as the first body's pieces are, so that the provisional caps of both pieces show.
                    SandboxDrawCompareCutScene.Place(io, poses, otherLower, other.position, other.rotation);
                    SandboxDrawCompareCutScene.PlaceAbout(io, poses, otherLower, OtherAt(new Vector3(0f, 0.3f, 0f)), Quaternion.AngleAxis(-30f, right), Vector3.up * 0.2f);
                    SandboxDrawCompareCutScene.Place(io, poses, otherUpper, other.position, other.rotation);
                    SandboxDrawCompareCutScene.PlaceAbout(io, poses, otherUpper, OtherAt(new Vector3(0f, 0.9f, 0f)), Quaternion.AngleAxis(50f, right), Vector3.up * 1.1f);
                    for (int i = 0; i < 3; i++) yield return null;
                    if (io.held() == 0) failures.Add("no finished geometry was held at the other body's cut: its picture is not of provisional caps");
                    if (SandboxDrawCompareCutScene.State(world, b.id) == LogicalCutOperationState.Completed) failures.Add("the other body's cut was complete while its geometry was to be held");
                    if (world.Display.CapRecordCount == 0) failures.Add("the display holds no cap at the other body's held cut");
                    if (split.Published != 0) failures.Add("a split was published while it was held off: " + split.Published);
                    yield return Picture(io, names[stage++], states, poses, split);
                    for (int i = 0; i < 6; i++) yield return null;
                    yield return Picture(io, names[stage++], states, poses, split);
                    io.log("split scene: the other body's cut is held (finished geometry held " + io.held() + ", caps " + world.Display.CapRecordCount + ", the driver's cuts in flight "
                        + world.Driver.Transactions.Count + "); the split is let through from here");
                }

                // ---- the split let through: both pieces become two objects each, put where the piece stood -----------------
                var parts = new Dictionary<LogicalFragmentId, (LogicalFragmentId first, LogicalFragmentId second)>();
                split.RemainingMainSeconds = () => 1.0;
                int seen = split.Published;
                for (int frame = 0; frame < 900 && parts.Count < 2; frame++)
                {
                    yield return null;
                    if (split.Published == seen)
                    {
                        if (frame > 60 && split.IsIdle && !split.HasAttempt) break;
                        continue;
                    }

                    if (split.Published != seen + 1) failures.Add((split.Published - seen) + " splits were published between two frames: only the last is known here");
                    seen = split.Published;
                    LogicalFragmentId source = split.LastSource, first = split.LastPositive, second = split.LastNegative;
                    if (!poses.TryGetValue(source, out (Vector3 position, Quaternion rotation) stood))
                    {
                        failures.Add("a split was published of a fragment this scene did not place");
                        continue;
                    }

                    io.log("split scene: published at frame " + split.LastPublishedFrame + ": " + (source == upper ? "the upper" : "the lower") + " piece became two; as published: "
                        + Where(world, first, stood) + " and " + Where(world, second, stood) + " (each root's distance from where the piece stood, where it is put)");
                    poses.Remove(source);   // replaced: the two stand for it
                    SandboxDrawCompareCutScene.Place(io, poses, first, stood.position, stood.rotation);
                    SandboxDrawCompareCutScene.Place(io, poses, second, stood.position, stood.rotation);
                    parts[source] = (first, second);
                }

                io.log("split scene: " + split.Describe());
                if (parts.Count != 2)
                {
                    failures.Add("the split published " + parts.Count + " of the two pieces (last refusal: " + (split.LastRefusal ?? "none") + ")");
                    yield break;
                }

                // The registration a split replaces goes at the display's next adoption: a few frames, then the picture.
                for (int i = 0; i < 8; i++) yield return null;
                if (other != null)
                {
                    // Both publications came while the other body's geometry was held: its caps are the provisional ones still.
                    io.log("split scene: after the split's publications the other body's cut is " + SandboxDrawCompareCutScene.State(world, b.id) + " (finished geometry held " + io.held()
                        + ", caps " + world.Display.CapRecordCount + ", the driver's cuts in flight " + world.Driver.Transactions.Count + ")");
                    if (io.held() == 0) failures.Add("no finished geometry was held any more when the split was published: its picture is not beside provisional caps");
                    if (SandboxDrawCompareCutScene.State(world, b.id) == LogicalCutOperationState.Completed) failures.Add("the other body's cut was complete when the split was published");
                    if (world.Display.CapRecordCount == 0) failures.Add("the display holds no cap after the split's publications, the other body's geometry held");
                }

                yield return Picture(io, names[stage++], states, poses, split);
                if (other != null)
                {
                    // ---- the other body's geometry let go: its commit ------------------------------------------------------------
                    io.hold(false);
                    yield return SandboxDrawCompareCutScene.UntilComplete(io, new[] { b.id }, "the other body's cut", failures);
                    if (failures.Count > 0) yield break;
                    yield return Picture(io, names[stage++], states, poses, split);
                }

                // ---- the hands moved aside: four objects ---------------------------------------------------------------------
                Further(world, parts[lower].first, parts[lower].second, right, out LogicalFragmentId lowerHand, out LogicalFragmentId lowerTrunk);
                Further(world, parts[upper].first, parts[upper].second, right, out LogicalFragmentId upperHand, out LogicalFragmentId _);
                SandboxDrawCompareCutScene.PlaceAbout(io, poses, lowerHand, body.position, Quaternion.identity, right * 0.8f);
                SandboxDrawCompareCutScene.PlaceAbout(io, poses, upperHand, body.position, Quaternion.identity, right * 0.8f);
                for (int i = 0; i < 3; i++) yield return null;
                yield return Picture(io, names[stage++], states, poses, split);

                // ---- one of the split's objects cut again, its geometry held back ----------------------------------------------
                io.hold(true);
                if (!SandboxDrawCompareCutScene.TryWorldBounds(world, lowerTrunk, out Bounds again))
                {
                    failures.Add("the trunk's lower part has no collider to sweep through");
                    yield break;
                }

                (Vector3 position, Quaternion rotation) from = poses[lowerTrunk];
                var cutC = new List<LogicalCutOperation>();
                yield return CutAcross(io, again.center, from.rotation * Vector3.right, from.rotation * Vector3.forward, 0.4f, 0.4f, "the trunk's lower part, upright", cutC, failures);
                if (cutC.Count != 1)
                {
                    failures.Add("the upright sweep through the trunk's lower part published " + cutC.Count + " cuts, not one");
                    yield break;
                }

                LogicalCutOperation c = cutC[0];
                if (c.source != lowerTrunk) failures.Add("the added cut's source is not the trunk's lower part the split made");
                Further(world, c.positive, c.negative, right, out LogicalFragmentId further, out LogicalFragmentId nearer);
                poses.Remove(lowerTrunk);   // cut: its children stand for it
                // Each half turned about itself so that its cut face shows from the front, the further one moved aside.
                SandboxDrawCompareCutScene.Place(io, poses, nearer, from.position, from.rotation);
                SandboxDrawCompareCutScene.Place(io, poses, further, from.position, from.rotation);
                if (SandboxDrawCompareCutScene.TryWorldBounds(world, nearer, out Bounds nearBox)) SandboxDrawCompareCutScene.PlaceAbout(io, poses, nearer, nearBox.center, Quaternion.AngleAxis(40f, Vector3.up), Vector3.zero);
                if (SandboxDrawCompareCutScene.TryWorldBounds(world, further, out Bounds farBox)) SandboxDrawCompareCutScene.PlaceAbout(io, poses, further, farBox.center, Quaternion.AngleAxis(-40f, Vector3.up), right * 0.45f);
                for (int i = 0; i < 3; i++) yield return null;
                if (io.held() == 0) failures.Add("no finished geometry was held at the added cut: the picture of the held stage is not of provisional caps");
                if (SandboxDrawCompareCutScene.State(world, c.id) == LogicalCutOperationState.Completed) failures.Add("the added cut was complete while its geometry was to be held");
                if (world.Display.CapRecordCount == 0) failures.Add("the display holds no cap at the held added cut");
                yield return Picture(io, names[stage++], states, poses, split);

                io.hold(false);
                yield return SandboxDrawCompareCutScene.UntilComplete(io, new[] { c.id }, "the added cut", failures);
                yield return Picture(io, names[stage++], states, poses, split);
            }
            finally
            {
                io.hold(false);
                world.Driver.RemainingMainSeconds = driverBudget;
                split.RemainingMainSeconds = splitBudget;
            }
        }

        // One stage's pictures and its line of state: the cut scene's, and what the split has done so far.
        private static IEnumerator Picture(SandboxDrawCompareCutScene.Io io, string name, List<string> states, Dictionary<LogicalFragmentId, (Vector3 position, Quaternion rotation)> poses, CutSpatialSplit split)
        {
            states.Add(SandboxDrawCompareCutScene.Describe(io.world, name, poses) + " split: published " + split.Published + ", issued " + split.Issued
                + ", not found " + split.EndedAs(SpatialSplitEnd.NotFound) + ", ended otherwise " + (split.Issued - split.Published - split.EndedAs(SpatialSplitEnd.NotFound) - (split.HasAttempt ? 1 : 0)));
            io.log("split scene stage " + states[states.Count - 1]);
            yield return io.pictures(name);
        }

        // One cut asked by one synthetic sweep given to the ordinary detector: a rectangle of the plane through
        // <centre> with the given normal, its edge along <span> travelling across the plane. As the cut scene's.
        private static IEnumerator CutAcross(SandboxDrawCompareCutScene.Io io, Vector3 centre, Vector3 normal, Vector3 span, float halfTravel, float halfSpan, string what,
            List<LogicalCutOperation> made, List<string> failures)
        {
            LogicalCutLedger ledger = io.world.Ledger;
            int before = ledger.OperationCount;
            Vector3 travel = Vector3.Cross(normal, span);
            for (int attempt = 0; attempt < 8 && ledger.OperationCount == before; attempt++)
            {
                long slash = ++s_slash;
                var sweep = new SlashSweep(slash, Time.unscaledTimeAsDouble, false, new Plane(normal, centre), travel, span,
                    centre - (travel * halfTravel) - (span * halfSpan), centre - (travel * halfTravel) + (span * halfSpan),
                    centre + (travel * halfTravel) - (span * halfSpan), centre + (travel * halfTravel) + (span * halfSpan));
                io.detector.Evaluate(new[] { sweep }, new[] { slash });
                io.log("split scene: synthetic sweep " + slash + " through " + what + " (through (" + centre.x.ToString("F3", Inv) + ", " + centre.y.ToString("F3", Inv) + ", " + centre.z.ToString("F3", Inv)
                    + "), normal (" + normal.x.ToString("F3", Inv) + ", " + normal.y.ToString("F3", Inv) + ", " + normal.z.ToString("F3", Inv) + ")), attempt " + (attempt + 1)
                    + ": hits " + io.detector.HitCount + ", operations " + before + " -> " + ledger.OperationCount);
                for (int i = 0; i < 6 && ledger.OperationCount == before; i++) yield return null;
            }

            if (ledger.OperationCount == before)
            {
                failures.Add("no cut was admitted for " + what);
                yield break;
            }

            // Published: both children are there (the geometry may be held back).
            int count = ledger.OperationCount;
            for (int frame = 0; frame < 600; frame++)
            {
                bool all = true;
                for (int i = before; i < count; i++)
                {
                    all &= ledger.TryGetOperationAtAdmission(i, out LogicalCutOperation operation) && operation.positive.IsSet && operation.negative.IsSet;
                }

                if (all) break;
                yield return null;
            }

            for (int i = before; i < count; i++)
            {
                if (ledger.TryGetOperationAtAdmission(i, out LogicalCutOperation operation) && operation.positive.IsSet && operation.negative.IsSet)
                {
                    made.Add(operation);
                }
                else
                {
                    failures.Add("a cut of " + what + " was not published within 600 frames");
                }
            }
        }

        // Two fragments by where each stands along an axis of the world: the further one first. As given when either
        // has no collider to read.
        private static void Further(CutWorldRoot world, LogicalFragmentId a, LogicalFragmentId b, Vector3 axis, out LogicalFragmentId further, out LogicalFragmentId nearer)
        {
            further = a;
            nearer = b;
            if (SandboxDrawCompareCutScene.TryWorldBounds(world, a, out Bounds first) && SandboxDrawCompareCutScene.TryWorldBounds(world, b, out Bounds second)
                && Vector3.Dot(second.center, axis) > Vector3.Dot(first.center, axis))
            {
                further = b;
                nearer = a;
            }
        }

        private static string Where(CutWorldRoot world, LogicalFragmentId fragment, (Vector3 position, Quaternion rotation) stood)
        {
            return world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && owner.Root != null
                ? Vector3.Distance(owner.Root.transform.position, stood.position).ToString("F4", Inv) + " m, " + Quaternion.Angle(owner.Root.transform.rotation, stood.rotation).ToString("F2", Inv) + " deg"
                : "no owner";
        }
    }
}
