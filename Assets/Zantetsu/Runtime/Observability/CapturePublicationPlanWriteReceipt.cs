using System;

namespace Zantetsu.Observability
{
    internal sealed class CapturePublicationPlanWriteReceipt
    {
        internal CapturePublicationPlanWriteReceipt(
            ICapturePublicationPlanStore issuedBy,
            CapturePublicationPlan plan,
            string absolutePath,
            int byteCount)
        {
            IssuedBy = issuedBy ?? throw new ArgumentNullException(nameof(issuedBy));
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            AbsolutePath = absolutePath ?? throw new ArgumentNullException(nameof(absolutePath));
            if (byteCount <= 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
            ByteCount = byteCount;
        }

        internal ICapturePublicationPlanStore IssuedBy { get; }
        internal CapturePublicationPlan Plan { get; }
        internal string AbsolutePath { get; }
        internal int ByteCount { get; }
        internal bool IsIssuedFor(ICapturePublicationPlanStore store, CapturePublicationPlan plan) =>
            ReferenceEquals(IssuedBy, store) && ReferenceEquals(Plan, plan);
    }
}
