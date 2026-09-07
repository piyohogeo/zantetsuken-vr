using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Single Run chunk local registry slot. It binds the exact
    /// <see cref="NvencRunChunkContext"/> at construction and registers the
    /// exact <see cref="NvencChunkFinalizationResult"/> exactly once, holding
    /// its exact descriptor and frame relation until commit or pre-commit
    /// discard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The transitions are <c>Empty -&gt; Registered</c>,
    /// <c>Registered -&gt; Committed</c>, and the pre-commit abort
    /// <c>Registered -&gt; Empty</c>. Re-registration after a discard is
    /// forbidden by a private latch that survives the discard. This is a
    /// one-Run, one-chunk slot, not a reusable registry.
    /// </para>
    /// <para>
    /// This type holds no thread, queue, filesystem, OS lock, ownership lease,
    /// or Publication duty, is not an <see cref="IDisposable"/>, and performs
    /// no Plan rename or file operation. It forwards the held references
    /// without copying the descriptor or frame relation.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunLocalRegistrySlot
    {
        private readonly NvencRunChunkContext _context;
        private readonly long _testRunId;
        private readonly object _gate;

        private int _state;
        private bool _registrationEntered;
        private NvencChunkFinalizationResult _result;
        private CaptureArtifactDescriptor _descriptor;
        private CaptureArtifactFrameRelation _relation;

        internal NvencRunLocalRegistrySlot(NvencRunChunkContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _testRunId = context.TestRunId;
            _gate = new object();
            _state = (int)NvencRunLocalRegistrySlotState.Empty;
        }

        internal NvencRunLocalRegistrySlotState State =>
            (NvencRunLocalRegistrySlotState)Volatile.Read(ref _state);

        internal NvencRunChunkContext Context => _context;

        internal long TestRunId => _testRunId;

        /// <summary>
        /// True only while a registered, not-yet-committed entry is held.
        /// </summary>
        internal bool HasRegisteredEntry =>
            (NvencRunLocalRegistrySlotState)Volatile.Read(ref _state) == NvencRunLocalRegistrySlotState.Registered;

        /// <summary>
        /// Registers the exact finalized result exactly once. All validation
        /// runs before any state is written, so a failure leaves no partial
        /// entry. The registration-path latch is set only on the first
        /// successful registration and survives a later discard, so
        /// re-registration is always rejected.
        /// </summary>
        internal bool TryRegister(NvencRunChunkContext context, NvencChunkFinalizationResult result)
        {
            try
            {
                lock (_gate)
                {
                    if (_registrationEntered)
                    {
                        return false;
                    }

                    if ((NvencRunLocalRegistrySlotState)Volatile.Read(ref _state) != NvencRunLocalRegistrySlotState.Empty)
                    {
                        return false;
                    }

                    if (!ReferenceEquals(context, _context))
                    {
                        return false;
                    }

                    if (context.State != NvencRunChunkContextState.Finalized)
                    {
                        return false;
                    }

                    if (!context.TryGetFinalizationResult(out NvencChunkFinalizationResult held))
                    {
                        return false;
                    }

                    if (!ReferenceEquals(held, result))
                    {
                        return false;
                    }

                    if (result == null || !result.IsValid)
                    {
                        return false;
                    }

                    if (result.Sink == null || !ReferenceEquals(result.Sink, _context.Sink))
                    {
                        return false;
                    }

                    CaptureArtifactDescriptor descriptor = result.Descriptor;
                    if (descriptor == null || !descriptor.IsValid || descriptor.ArtifactKind != CaptureArtifactKind.FrameSequence)
                    {
                        return false;
                    }

                    CaptureArtifactFrameRelation relation = result.FrameRelation;
                    if (relation == null || !relation.IsValid || !MatchesAcceptedRelation(relation))
                    {
                        return false;
                    }

                    _registrationEntered = true;
                    _result = result;
                    _descriptor = descriptor;
                    _relation = relation;
                    Volatile.Write(ref _state, (int)NvencRunLocalRegistrySlotState.Registered);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Commits the registered entry exactly once, only from
        /// <c>Registered</c>, with the exact context and result and an intact
        /// held correlation. The entry is kept, never cleared, and no Plan
        /// rename or file operation is performed.
        /// </summary>
        internal bool TryCommit(NvencRunChunkContext context, NvencChunkFinalizationResult result)
        {
            try
            {
                lock (_gate)
                {
                    if ((NvencRunLocalRegistrySlotState)Volatile.Read(ref _state) != NvencRunLocalRegistrySlotState.Registered)
                    {
                        return false;
                    }

                    if (!ReferenceEquals(context, _context) || !ReferenceEquals(result, _result))
                    {
                        return false;
                    }

                    if (!TryReadIntactEntry(out _, out _, out _))
                    {
                        return false;
                    }

                    Volatile.Write(ref _state, (int)NvencRunLocalRegistrySlotState.Committed);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Discards a registered entry, only from <c>Registered</c>, with the
        /// exact context and result. The entry is cleared and the state returns
        /// to <c>Empty</c>, but the registration-path latch survives, so
        /// re-registration is forbidden. No file, chunk, Plan, or lease is
        /// touched.
        /// </summary>
        internal bool TryDiscardRegistered(NvencRunChunkContext context, NvencChunkFinalizationResult result)
        {
            try
            {
                lock (_gate)
                {
                    if ((NvencRunLocalRegistrySlotState)Volatile.Read(ref _state) != NvencRunLocalRegistrySlotState.Registered)
                    {
                        return false;
                    }

                    if (!ReferenceEquals(context, _context) || !ReferenceEquals(result, _result))
                    {
                        return false;
                    }

                    _result = null;
                    _descriptor = null;
                    _relation = null;
                    Volatile.Write(ref _state, (int)NvencRunLocalRegistrySlotState.Empty);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Forwards the held entry references, without copying, only while the
        /// slot is <c>Registered</c> or <c>Committed</c> and the exact
        /// result, descriptor, and relation correlation still holds. A
        /// swapped, nulled, or corrupted entry fails closed without throwing.
        /// </summary>
        internal bool TryGetEntry(
            out NvencChunkFinalizationResult result,
            out CaptureArtifactDescriptor descriptor,
            out CaptureArtifactFrameRelation relation)
        {
            result = null;
            descriptor = null;
            relation = null;

            NvencRunLocalRegistrySlotState state = (NvencRunLocalRegistrySlotState)Volatile.Read(ref _state);
            if (state != NvencRunLocalRegistrySlotState.Registered && state != NvencRunLocalRegistrySlotState.Committed)
            {
                return false;
            }

            return TryReadIntactEntry(out result, out descriptor, out relation);
        }

        private bool TryReadIntactEntry(
            out NvencChunkFinalizationResult result,
            out CaptureArtifactDescriptor descriptor,
            out CaptureArtifactFrameRelation relation)
        {
            result = null;
            descriptor = null;
            relation = null;

            try
            {
                NvencChunkFinalizationResult heldResult = _result;
                CaptureArtifactDescriptor heldDescriptor = _descriptor;
                CaptureArtifactFrameRelation heldRelation = _relation;

                if (heldResult == null || !heldResult.IsValid ||
                    heldDescriptor == null || !heldDescriptor.IsValid || heldDescriptor.ArtifactKind != CaptureArtifactKind.FrameSequence ||
                    heldRelation == null || !heldRelation.IsValid ||
                    !ReferenceEquals(heldResult.Descriptor, heldDescriptor) ||
                    !ReferenceEquals(heldResult.FrameRelation, heldRelation) ||
                    heldResult.Sink == null || !ReferenceEquals(heldResult.Sink, _context.Sink) ||
                    !MatchesAcceptedRelation(heldRelation))
                {
                    return false;
                }

                result = heldResult;
                descriptor = heldDescriptor;
                relation = heldRelation;
                return true;
            }
            catch
            {
                result = null;
                descriptor = null;
                relation = null;
                return false;
            }
        }

        private bool MatchesAcceptedRelation(CaptureArtifactFrameRelation relation)
        {
            if ((long)relation.Count != _context.AcceptedFrameCount)
            {
                return false;
            }

            for (int i = 0; i < relation.Count; i++)
            {
                if (!_context.TryGetAcceptedFrameId(i, out long acceptedId) ||
                    relation.GetCaptureFrameId(i) != acceptedId)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
