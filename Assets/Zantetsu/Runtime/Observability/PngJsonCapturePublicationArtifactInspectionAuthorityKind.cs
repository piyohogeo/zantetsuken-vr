namespace Zantetsu.Observability
{
    /// <summary>
    /// Kind of the single exclusive authority that owns the authoritative
    /// publication plan handed to artifact inspection. Values are fixed,
    /// explicitly numbered, and append-only; existing values must never be
    /// renumbered or removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="None"/> is not a held kind: it is the derived result of an
    /// exclusive-state contradiction, such as an uninitialized authority or an
    /// authority that holds both references at once. It is never produced by
    /// either static factory.
    /// </para>
    /// </remarks>
    internal enum PngJsonCapturePublicationArtifactInspectionAuthorityKind : int
    {
        None = 0,
        RecoveryDecision = 1,
        FreshFrozenRun = 2
    }
}
