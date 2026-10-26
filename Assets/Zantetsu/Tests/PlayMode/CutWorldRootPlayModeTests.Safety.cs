using System.Collections;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static CutFragmentGroupSettings SafetyOn(int normal, int blockThreshold = int.MaxValue) =>
            new CutFragmentGroupSettings(true, normal, 1, CutWorldRoot.CompactionReserveSeconds, 0.00005, 0.0002, true, blockThreshold);

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_FragmentBoundary_IsStrict_AndDisabledStaysOff()
        {
            var root = NewWorld(out Shader _); root.Driver.RemainingMainSeconds = () => 1;
            var groups = GroupsOf(root, new Vector3(0, 0, -8));
            var a = AddBody(root, Vector3.zero); var b = AddBody(root, new Vector3(2, 0, 0));
            groups.Register(a, null); groups.Register(b, null); yield return null;
            yield return CutInTwo(root, a, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            yield return CutInTwo(root, b, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            groups.Settings = SafetyOn(2); groups.RemainingMainSeconds = () => -1;
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(4));
            Assert.That(groups.SafetyReason, Is.EqualTo(SafetyCollectionReason.None));
            Assert.That(groups.SafetyTurns, Is.Zero, "exactly twice the normal threshold is not pressure");
            groups.Settings = new CutFragmentGroupSettings(false, 0, 1, 0, 0, 0, true, 1);
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(groups.SafetyTurns, Is.Zero, "collection off is still off under both pressure sources");
            groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_StopsAtUpperThreshold_WithoutFinishingTheFamily()
        {
            var root = NewWorld(out Shader _); root.Driver.RemainingMainSeconds = () => 1;
            var groups = GroupsOf(root, new Vector3(0, 0, -8));
            var old = AddBody(root, Vector3.zero); var other = AddBody(root, new Vector3(2, 0, 2));
            groups.Register(old, null); groups.Register(other, null); yield return null;
            var a = new LogicalFragmentId[2]; var b = new LogicalFragmentId[2]; var c = new LogicalFragmentId[2];
            yield return CutInTwo(root, old, new float4(0, 1, 0, 0), a);
            yield return CutInTwo(root, a[0], new float4(1, 0, 0, 0), b);
            yield return CutInTwo(root, a[1], new float4(1, 0, 0, 0), c);
            yield return CutInTwo(root, b[0], new float4(0, 0, 1, 0), new LogicalFragmentId[2]);
            yield return CutInTwo(root, c[0], new float4(0, 0, 1, 0), new LogicalFragmentId[2]);
            yield return CutInTwo(root, other, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(8));
            groups.Settings = SafetyOn(3); groups.RemainingMainSeconds = () => -1;
            yield return Until(() => groups.SafetyPiecesRetired > 0, "fragment pressure retired only the excess");
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(6));
            Assert.That(root.Ledger.LiveCutCountOf(old), Is.EqualTo(4));
            Assert.That(groups.SafetyReason, Is.EqualTo(SafetyCollectionReason.None));
            int turns = groups.SafetyTurns;
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(groups.SafetyTurns, Is.EqualTo(turns), "no latched mode below the upper threshold");
            Assert.That(groups.SafetyGroupsCompleted, Is.Zero, "no forced lineage completion");
            groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_GlobalAge_IgnoresViewBudgetAndNormalWalkWindow()
        {
            var root = NewWorld(out Shader _); root.Driver.RemainingMainSeconds = () => 1;
            var groups = GroupsOf(root, new Vector3(0, 0, -8));
            var young = AddBody(root, new Vector3(0, 0, -80));
            var old = AddBody(root, Vector3.zero); var middle = AddBody(root, new Vector3(0, 0, -60));
            groups.Register(old, null); groups.Register(middle, null); groups.Register(young, null); yield return null;
            yield return CutInTwo(root, young, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            var a = new LogicalFragmentId[2];
            yield return CutInTwo(root, old, new float4(0, 1, 0, 0), a);
            yield return CutInTwo(root, middle, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            groups.Register(old, null); // duplicate registration does not refresh age
            groups.Settings = SafetyOn(2); groups.RemainingMainSeconds = () => -1;
            yield return Until(() => root.Ledger.LiveCutFragmentCount == 4, "old visible family ended despite younger unseen choices");
            Assert.That(root.Ledger.LiveCutCountOf(old), Is.Zero);
            Assert.That(root.Ledger.LiveCutCountOf(middle), Is.EqualTo(2));
            Assert.That(root.Ledger.LiveCutCountOf(young), Is.EqualTo(2));
            Assert.That(groups.SafetyEvents.All(e => e.root == old.value), Is.True);
            Assert.That(groups.SafetyEvents.All(e => e.reason == (int)SafetyCollectionReason.Fragments), Is.True);
            Assert.That(root.TerminationRequested || root.Display.IsHalted, Is.False);
            groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_BlockBoundary_ContinuesBelowNormalCount_UntilRealRoomReturns()
        {
            var root = NewWorld(out Shader _); root.Driver.RemainingMainSeconds = () => 1;
            var groups = GroupsOf(root, new Vector3(0, 0, -8));
            var old = AddBody(root, Vector3.zero); var young = AddBody(root, new Vector3(2, 0, 0));
            groups.Register(old, null); groups.Register(young, null); yield return null;
            yield return CutInTwo(root, old, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            yield return CutInTwo(root, young, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            int usage = root.Storage.VertexBlockCapacity - root.Storage.FreeVertexBlockRoom;
            int released = root.Storage.VertexGroupsReleased;
            groups.Settings = SafetyOn(256, usage + 1); groups.RemainingMainSeconds = () => -1;
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(groups.SafetyTurns, Is.Zero);
            groups.Settings = SafetyOn(256, usage);
            Assert.That(groups.SafetyReason, Is.EqualTo(SafetyCollectionReason.VertexBlocks), "block boundary is inclusive");
            yield return Until(() => groups.SafetyPiecesRetired == 1, "one partial retirement");
            Assert.That(root.Storage.VertexBlockCapacity - root.Storage.FreeVertexBlockRoom, Is.EqualTo(usage), "a surviving sibling retains the whole lineage's blocks");
            Assert.That(groups.SafetyReason, Is.EqualTo(SafetyCollectionReason.VertexBlocks));
            yield return Until(() => root.Storage.VertexGroupsReleased > released, "the final piece and readers released the old lineage");
            Assert.That(root.Ledger.LiveCutCountOf(old), Is.Zero);
            Assert.That(root.Ledger.LiveCutCountOf(young), Is.EqualTo(2));
            Assert.That(groups.SafetyReason, Is.EqualTo(SafetyCollectionReason.None));
            int turns = groups.SafetyTurns;
            for (int i = 0; i < 4; i++) yield return null;
            Assert.That(groups.SafetyTurns, Is.EqualTo(turns));
            Assert.That(root.Storage.VertexBlockCount, Is.GreaterThanOrEqualTo(usage), "high-water address remains high after actual room returned");
            Assert.That(groups.SafetyGroupsCompleted, Is.EqualTo(1));
            groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_BothSources_AndNoView_AreSupported()
        {
            var root = NewWorld(out Shader _); root.Driver.RemainingMainSeconds = () => 1;
            var groups = GroupsOf(root, new Vector3(0, 0, -8));
            var a = AddBody(root, Vector3.zero); groups.Register(a, null); yield return null;
            yield return CutInTwo(root, a, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            groups.ViewCamera = null;
            groups.Settings = SafetyOn(0, 1); groups.RemainingMainSeconds = () => -1;
            Assert.That(groups.SafetyReason, Is.EqualTo(SafetyCollectionReason.Fragments | SafetyCollectionReason.VertexBlocks));
            yield return Until(() => groups.SafetyGroupsCompleted == 1, "no camera needed for safety");
            Assert.That(root.Ledger.LiveCutCountOf(a), Is.Zero);
            Assert.That(root.TerminationRequested || root.Display.IsHalted, Is.False);
            groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_BusySiblingProtected_AdmissionAndOriginalReturnStillWork()
        {
            HoldingExecutor held = null;
            var root = NewWorld(out Shader _, d => d == WorkDestination.UnityJob
                ? held = Held(new HoldingExecutor(new UnityJobWorkExecutor(8))) : null, null);
            root.Driver.RemainingMainSeconds = () => 1;
            var groups = GroupsOf(root, new Vector3(0, 0, -8));
            var old = AddBody(root, Vector3.zero); var other = AddBody(root, new Vector3(2, 0, 2));
            var origin = new OriginDouble { restore = new Bounds(Vector3.zero, Vector3.one) };
            groups.Register(old, origin); groups.Register(other, null); yield return null;
            var a = new LogicalFragmentId[2];
            yield return CutInTwo(root, old, new float4(0, 1, 0, 0), a);
            yield return CutInTwo(root, other, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            held.HoldEverything = true;
            var ask = Ask(a[0], new float4(1, 0, 0, 0)); Assert.That(root.TryAsk(in ask), Is.True); yield return null;
            Assert.That(root.Ledger.TryGetActiveOperation(a[0], out _), Is.True);
            groups.Settings = SafetyOn(256, 1); groups.RemainingMainSeconds = () => -1;
            yield return Until(() => IsRetired(root, a[1]), "ready sibling retired while the cut remains held");
            Assert.That(root.Ledger.IsCurrentTarget(a[0]), Is.True);
            Assert.That(origin.ended, Is.Zero);
            Assert.That(groups.SafetyPassedAs(CutGroupPass.CutUnderWay), Is.GreaterThan(0));
            var fresh = AddBody(root, new Vector3(4, 0, 2));
            var more = Ask(fresh, new float4(1, 0, 0, 0)); Assert.That(root.TryAsk(in more), Is.True);
            yield return null;
            Assert.That(AdmittedFor(root, new[] { fresh }).Count, Is.EqualTo(1), "safety does not stop new cuts");
            held.HoldEverything = false; held.ReleaseEverything();
            yield return Until(() => origin.rearmed == 1, "last piece, display handoff, original return and rearm completed");
            Assert.That(origin.ended, Is.EqualTo(1)); Assert.That(origin.returned, Is.EqualTo(1));
            Assert.That(root.TerminationRequested || root.Display.IsHalted, Is.False);
            Assert.That(groups.SafetyEvents.All(e => e.liveBefore - e.liveAfter <= CutFragmentGroupCollection.SafetyRetireLimit), Is.True);
            groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_SharedBusyFamilyDoesNotBlockOtherFamilies()
        {
            var root = NewWorld(out Shader _); root.Driver.RemainingMainSeconds = () => 1;
            var groups = GroupsOf(root, new Vector3(0, 0, -8));
            var old = AddBody(root, Vector3.zero); var one = AddBody(root, new Vector3(2, 0, 2)); var two = AddBody(root, new Vector3(-2, 0, 2));
            var origin = new OriginDouble { mayEnd = false, restore = new Bounds(Vector3.zero, Vector3.one) };
            groups.Register(old, origin); groups.Register(one, null); groups.Register(two, null); yield return null;
            var a = new LogicalFragmentId[2];
            yield return CutInTwo(root, old, new float4(0, 1, 0, 0), a);
            yield return CutInTwo(root, one, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            yield return CutInTwo(root, two, new float4(0, 1, 0, 0), new LogicalFragmentId[2]);
            groups.Settings = SafetyOn(1); groups.RemainingMainSeconds = () => -1;
            yield return Until(() => root.Ledger.LiveCutFragmentCount == 2, "other families reclaimed");
            Assert.That(a.All(p => root.Ledger.IsCurrentTarget(p)), Is.True);
            Assert.That(origin.ended, Is.Zero);
            Assert.That(groups.SafetyPassedAs(CutGroupPass.SharedBusy), Is.GreaterThan(0));
            groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_SmallActualBlockCapacity_ReclaimsAndReusesRoom()
        {
            var root = NewWorld(out Shader _, null, null, p => SetPrivate(p, "vertexBlockCapacity", 16));
            root.Driver.RemainingMainSeconds = () => 1;
            var groups = GroupsOf(root, new Vector3(0, 0, -8));
            var old = AddBody(root, Vector3.zero); var young = AddBody(root, new Vector3(2, 0, 0));
            groups.Register(old, null); groups.Register(young, null); yield return null;
            var a = new LogicalFragmentId[2]; var b = new LogicalFragmentId[2]; var y = new LogicalFragmentId[2];
            yield return CutInTwo(root, old, new float4(0, 1, 0, 0), a);
            yield return CutInTwo(root, a[0], new float4(1, 0, 0, 0), b);
            yield return CutInTwo(root, b[0], new float4(0, 0, 1, 0), new LogicalFragmentId[2]);
            yield return CutInTwo(root, young, new float4(0, 1, 0, 0), y);
            Assert.That(root.Storage.VertexBlockCapacity, Is.EqualTo(16));
            Assert.That(root.Storage.VertexBlockCapacity - root.Storage.FreeVertexBlockRoom, Is.EqualTo(13));
            groups.Settings = SafetyOn(256, 12); groups.RemainingMainSeconds = () => -1;
            yield return Until(() => root.Storage.VertexBlockCapacity - root.Storage.FreeVertexBlockRoom < 12, "old lineage returns block spans in the small real allocator");
            Assert.That(root.Ledger.LiveCutCountOf(old), Is.Zero);
            Assert.That(root.Ledger.LiveCutCountOf(young), Is.EqualTo(2));
            var next = new LogicalFragmentId[2];
            yield return CutInTwo(root, y[0], new float4(1, 0, 0, 0), next);
            Assert.That(next.All(p => root.Ledger.IsCurrentTarget(p)), Is.True, "returned block spans can be allocated again");
            Assert.That(root.TerminationRequested || root.Display.IsHalted, Is.False);
            groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentGroups_Safety_RuntimeThresholdOverridePreservesSafetySettings()
        {
            var root = NewWorld(out Shader _);
            Assert.That(root.EnableFragmentGroups(17, "test"), Is.True);
            Assert.That(root.Groups.Settings.safetyEnabled, Is.True);
            Assert.That(root.Groups.Settings.SafetyFragmentThreshold, Is.EqualTo(34));
            Assert.That(root.Groups.Settings.safetyVertexBlockThreshold, Is.EqualTo(49152));
            root.Groups.Settings = GroupsOff(); yield return EndWorld(root);
        }

        [Test]
        public void FragmentGroups_Safety_ProfileDefaults_AndThresholdValidation()
        {
            var profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                var settings = profile.FragmentGroupCollection;
                Assert.That(settings.safetyEnabled, Is.True);
                Assert.That(settings.SafetyFragmentThreshold, Is.EqualTo(512));
                Assert.That(settings.safetyVertexBlockThreshold, Is.EqualTo(49152));
                Assert.That(settings.IsValid, Is.True);
                Assert.That(SafetyOn(1, 0).IsValid, Is.False);
                Assert.That(SafetyOn(int.MaxValue).SafetyFragmentThreshold, Is.EqualTo(int.MaxValue), "twice the normal threshold does not overflow");
            }
            finally { Object.DestroyImmediate(profile); }
        }
    }
}
