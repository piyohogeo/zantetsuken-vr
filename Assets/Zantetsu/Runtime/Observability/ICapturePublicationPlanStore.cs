namespace Zantetsu.Observability
{
    /// <summary>Durable canonical publication-plan boundary used across process restarts.</summary>
    internal interface ICapturePublicationPlanStore
    {
        CapturePublicationPlanWriteReceipt WritePlan(CapturePublicationPlan plan);
        CapturePublicationPlan ReadPlan(int maximumCanonicalByteCount);
    }
}
