namespace Zantetsu.MeshCut
{
    /// <summary>
    /// What a snapshot's validations did, part by part, summed over every validation (2026-10-01, for observation): a
    /// structural validation's reflected-index build, its registrations' input checks, its walks from each registration
    /// root up its ancestors, and its pass over every operation (the owner lookup, the unreflected count of an aborted
    /// one, the plane check); a placement-only validation's input checks apart. Times in seconds, reads and comparisons
    /// counted. Never reset by a build: a frame with several builds adds them all.
    /// </summary>
    public sealed class VpValidateCounts
    {
        public long structural, registrations;
        public double indexSeconds, inputSeconds, ancestorSeconds, operationsSeconds;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // The numeric input-contract diagnosis's own counts (DESIGN 5.6; the Editor and Development Players only): the
        // placement-only validations, which are nothing else, and of the input checks the registrations' contract
        // (bounds, placement, rigid lineage frame).
        public long placementOnly, placementRegistrations;
        public double placementInputSeconds, contractSeconds, placementContractSeconds;
#endif
        public long ancestorSteps, ancestorLookups, planeChecks, operations, ownerLookups, ownerSteps, ownerCacheHits, unreflectedSteps;
        public long indexesBuilt, indexesReused;   // the registrations' reflected sets indexed at the build, and a display's own sets taken by their lookup
        public long ancestorReads;   // the validation's ancestors' ledger facts read (the rest of the steps took them from the build's arrays)
        public long ancestorHits;    // the validation's ancestors taken from the build's facts (2026-10-01; steps = reads + hits)

        // The structure build's Collect stage (2026-10-01): its time; within it the chains collected, the selections and the
        // cap identities (the branch walk is the rest); the branches, the collections, the chain boundaries and operations read,
        // the candidates made, the cap identities taken.
        public double collectSeconds, collectIntoSeconds, selectSeconds, capIdentitySeconds;
        public long branches, collectCalls, chainSteps, operationReads, candidatesMade, capIdentities;

        // The Collect stage's chain walks (2026-10-01): the ancestors visited, their facts read from the ledger, and those
        // taken from the facts this build had read already (the validation's or an earlier branch's). Never in ancestorReads.
        public long collectVisits, collectReads, collectHits;

        // The registration roots' chains (2026-10-01): the boundaries the validation kept for them; the collections that took
        // one in, the boundaries they took so (neither walked nor matched), and the reflected-set lookups the collections made.
        public long segmentEntries, collectSplices, collectSegmentBoundaries, collectLookups;

        // Every structure build whole, from the validation (its arrays prepared) to the end of the last registration's group.
        public double structureSeconds;

        public void Clear()
        {
            structural = registrations = 0;
            indexSeconds = inputSeconds = ancestorSeconds = operationsSeconds = 0.0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            placementOnly = placementRegistrations = 0;
            placementInputSeconds = contractSeconds = placementContractSeconds = 0.0;
#endif
            ancestorSteps = ancestorLookups = planeChecks = operations = ownerLookups = ownerSteps = ownerCacheHits = unreflectedSteps = 0;
            indexesBuilt = indexesReused = ancestorReads = 0;
            collectSeconds = collectIntoSeconds = selectSeconds = capIdentitySeconds = 0.0;
            branches = collectCalls = chainSteps = operationReads = candidatesMade = capIdentities = 0;
            ancestorHits = collectVisits = collectReads = collectHits = 0;
            segmentEntries = collectSplices = collectSegmentBoundaries = collectLookups = 0;
            structureSeconds = 0.0;
        }

        /// <summary>This plus <paramref name="a"/> minus <paramref name="b"/>, field by field.</summary>
        public void AddDifference(VpValidateCounts a, VpValidateCounts b)
        {
            structural += a.structural - b.structural;
            registrations += a.registrations - b.registrations;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            placementOnly += a.placementOnly - b.placementOnly;
            placementRegistrations += a.placementRegistrations - b.placementRegistrations;
            placementInputSeconds += a.placementInputSeconds - b.placementInputSeconds;
            contractSeconds += a.contractSeconds - b.contractSeconds;
            placementContractSeconds += a.placementContractSeconds - b.placementContractSeconds;
#endif
            indexSeconds += a.indexSeconds - b.indexSeconds;
            inputSeconds += a.inputSeconds - b.inputSeconds;
            ancestorSeconds += a.ancestorSeconds - b.ancestorSeconds;
            operationsSeconds += a.operationsSeconds - b.operationsSeconds;
            ancestorSteps += a.ancestorSteps - b.ancestorSteps;
            ancestorLookups += a.ancestorLookups - b.ancestorLookups;
            planeChecks += a.planeChecks - b.planeChecks;
            operations += a.operations - b.operations;
            ownerLookups += a.ownerLookups - b.ownerLookups;
            ownerSteps += a.ownerSteps - b.ownerSteps;
            ownerCacheHits += a.ownerCacheHits - b.ownerCacheHits;
            unreflectedSteps += a.unreflectedSteps - b.unreflectedSteps;
            indexesBuilt += a.indexesBuilt - b.indexesBuilt;
            indexesReused += a.indexesReused - b.indexesReused;
            ancestorReads += a.ancestorReads - b.ancestorReads;
            collectSeconds += a.collectSeconds - b.collectSeconds;
            collectIntoSeconds += a.collectIntoSeconds - b.collectIntoSeconds;
            selectSeconds += a.selectSeconds - b.selectSeconds;
            capIdentitySeconds += a.capIdentitySeconds - b.capIdentitySeconds;
            branches += a.branches - b.branches;
            collectCalls += a.collectCalls - b.collectCalls;
            chainSteps += a.chainSteps - b.chainSteps;
            operationReads += a.operationReads - b.operationReads;
            candidatesMade += a.candidatesMade - b.candidatesMade;
            capIdentities += a.capIdentities - b.capIdentities;
            ancestorHits += a.ancestorHits - b.ancestorHits;
            collectVisits += a.collectVisits - b.collectVisits;
            collectReads += a.collectReads - b.collectReads;
            collectHits += a.collectHits - b.collectHits;
            segmentEntries += a.segmentEntries - b.segmentEntries;
            collectSplices += a.collectSplices - b.collectSplices;
            collectSegmentBoundaries += a.collectSegmentBoundaries - b.collectSegmentBoundaries;
            collectLookups += a.collectLookups - b.collectLookups;
            structureSeconds += a.structureSeconds - b.structureSeconds;
        }

        public void Add(VpValidateCounts a) => AddDifference(a, s_zero);

        public void CopyFrom(VpValidateCounts a)
        {
            Clear();
            Add(a);
        }

        private static readonly VpValidateCounts s_zero = new VpValidateCounts();

        public string Describe() =>
            "structural " + structural + " (registrations " + registrations + ", indexes built " + indexesBuilt + " reused " + indexesReused + "; ms: index " + (indexSeconds * 1000).ToString("F3") + ", input " + (inputSeconds * 1000).ToString("F3")
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            + " (contract " + (contractSeconds * 1000).ToString("F3") + ")"
#endif
            + ", ancestors " + (ancestorSeconds * 1000).ToString("F3") + ", operations " + (operationsSeconds * 1000).ToString("F3") + "; ancestor steps " + ancestorSteps + " (ledger reads " + ancestorReads + ", kept " + ancestorHits + "), their reflected lookups " + ancestorLookups
            + ", plane checks " + planeChecks + ", operations " + operations + ", owner lookups " + ownerLookups + " (steps " + ownerSteps + ", answered by the cache " + ownerCacheHits + "), unreflected steps " + unreflectedSteps
            + "); collect ms " + (collectSeconds * 1000).ToString("F3") + " (chains " + (collectIntoSeconds * 1000).ToString("F3") + ", selection " + (selectSeconds * 1000).ToString("F3") + ", cap identities " + (capIdentitySeconds * 1000).ToString("F3")
            + "; branches " + branches + ", collections " + collectCalls + ", chain boundaries " + chainSteps + ", operations read " + operationReads + ", candidates " + candidatesMade + ", cap identities " + capIdentities
            + "; chain ancestors " + collectVisits + " (ledger reads " + collectReads + ", kept " + collectHits + "), reflected lookups " + collectLookups + ", segments taken in " + collectSplices + " (" + collectSegmentBoundaries + " boundaries; kept by the validation " + segmentEntries + ")); structure ms " + (structureSeconds * 1000).ToString("F3")
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            + "; placement only " + placementOnly + " (registrations " + placementRegistrations + ", input ms " + (placementInputSeconds * 1000).ToString("F3") + " (contract " + (placementContractSeconds * 1000).ToString("F3") + "))";
#else
            ;
#endif
    }
}
