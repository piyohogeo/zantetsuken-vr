using System;
using Unity.Mathematics;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    public sealed partial class ProvisionalCutDriver
    {
        long _preparedBindingEpoch;
        internal enum FreshCutEligibility { Invalid, EmptySide, Full, Ready }

        // Main-only internal capability, not a public classify-now / consume-later API.
        internal sealed class PreparedCutLease : IDisposable
        {
            readonly ProvisionalCutDriver driver;
            readonly VpPreparedPhysicsInput input;
            readonly PhysicsOwnerShape shape;
            readonly float4 plane;
            readonly double mass;
            readonly int frame;
            readonly long bindingEpoch;
            bool holdsShape;
            internal PhysicsCutClassification Classification { get; private set; }
            internal bool Consumed { get; private set; }

            internal PreparedCutLease(ProvisionalCutDriver driver, VpPreparedPhysicsInput input,
                PhysicsOwnerShape shape, float4 plane, double mass, PhysicsCutClassification classification)
            {
                this.driver = driver; this.input = input; this.shape = shape; this.plane = plane; this.mass = mass;
                frame = driver.CurrentFrame; bindingEpoch = driver._preparedBindingEpoch;
                shape.AcquireForWork(); holdsShape = true; Classification = classification;
            }

            bool IsCurrent(ProvisionalCutDriver caller) => ReferenceEquals(driver, caller)
                && bindingEpoch == caller._preparedBindingEpoch && frame == caller.CurrentFrame
                && !Consumed && Classification != null && !Classification.IsDisposed && !shape.IsFreed;

            internal bool IsFresh(ProvisionalCutDriver caller) => IsCurrent(caller)
                && input.TryBorrowPosedShape(out var current) && ReferenceEquals(current, shape);

            internal bool TryConsume(ProvisionalCutDriver caller, PhysicsFragmentOwner owner,
                in ProvisionalCutAsk ask, out PhysicsCutClassification classification)
            {
                classification = null;
                if (!IsCurrent(caller) || !ReferenceEquals(owner.Shape, shape)
                    || owner.Mass != mass || !math.all(plane == ask.plane)) return false;
                classification = Classification; Classification = null; Consumed = true;
                return true;
            }

            public void Dispose()
            {
                Classification?.Dispose(); Classification = null;
                if (holdsShape) { holdsShape = false; shape.ReleaseFromWork(); }
            }
        }

        internal bool TryPrepareFreshCut(VpPreparedPhysicsInput input, float4 plane, double mass,
            out PreparedCutLease prepared)
        {
            prepared = null;
            if (!IsBound || (_latch != null && _latch.TerminationRequested)
                || input == null || !input.TryBorrowPosedShape(out var shape)) return false;
            if (!PhysicsCutClassification.TryClassify(shape, plane, _supportEpsilon, mass, _vertexLimit, out var c)) return false;
            try { prepared = new PreparedCutLease(this, input, shape, plane, mass, c); return true; }
            catch { c.Dispose(); throw; }
        }

        // Ready is NOT a reservation. The adapter continues synchronously without callbacks before registration.
        internal FreshCutEligibility AssessFreshCut(PreparedCutLease prepared)
        {
            if (!IsBound || (_latch != null && _latch.TerminationRequested)
                || prepared == null || !prepared.IsFresh(this)) return FreshCutEligibility.Invalid;
            if (!prepared.Classification.SplitsBothSides) return FreshCutEligibility.EmptySide;
            return _ledger.Budget.IsFull ? FreshCutEligibility.Full : FreshCutEligibility.Ready;
        }

        internal ProvisionalCutAcceptance RequestPreparedCut(in ProvisionalCutAsk ask, PreparedCutLease prepared,
            out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission)
        {
            if (prepared == null)
            {
                transaction = null; admission = LogicalCutAdmission.NoOp;
                return ProvisionalCutAcceptance.InvalidRequest;
            }
            return RequestCutCore(in ask, prepared, out transaction, out admission);
        }
    }
}
