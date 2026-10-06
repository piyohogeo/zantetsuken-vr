namespace Zantetsu.MeshCut
{
    /// <summary>
    /// A snapshot's build by stage, counted and timed whole a stage (2026-10-07, for observation where the Profiler's
    /// markers are not there): the structural validations, the registrations' collections and groupings -- siblings
    /// inside a family's structure build, a registration each -- the structure builds whole, the Place passes; what the
    /// rebuilt families held; and the growth of the snapshot's managed work arrays (its native room is the display's
    /// and is counted there). Cumulative in a snapshot; a display takes a collection's share as a difference.
    /// <para>
    /// Containment, so that nothing is added twice: a structure build (structureSeconds, a family rebuilt) holds one
    /// validation and, a registration of the family, one collection and one grouping, plus its own loop; a Place pass
    /// is beside the structure builds, not inside one.
    /// </para>
    /// </summary>
    public struct VpSnapshotStageTotals
    {
        public long validateCalls, collectCalls, groupCalls, placeCalls;
        public double validateSeconds, collectSeconds, groupSeconds, structureSeconds, placeSeconds;

        // Builds that kept the structure whole (placement only) and builds that went through every registration; of the
        // latter's families, the ones rebuilt, with their registrations, render fragments, branches and candidates as
        // the rebuild made them; and the registrations whose family was taken over as it was.
        public long keptWhole, walks, familiesRebuilt, registrationsRebuilt, renderFragmentsRebuilt, branchesRebuilt, candidatesRebuilt, registrationsReused;

        // Managed work arrays made larger (the parts' arrays and the list of the render fragments placed anew): how
        // often, and the new arrays' lengths in elements, added. Not bytes, and not what the heap grew by.
        public long arrayGrowths, arrayGrowthElements;

        // Collections in which the adopted snapshot stood as it was and no Place pass ran (D-204), and the placement
        // queries not made for it: the render fragments it held, one a render fragment a collection.
        public long placementsReused, queriesOmitted;

        public void Add(in VpSnapshotStageTotals a)
        {
            placementsReused += a.placementsReused; queriesOmitted += a.queriesOmitted;
            validateCalls += a.validateCalls; collectCalls += a.collectCalls; groupCalls += a.groupCalls; placeCalls += a.placeCalls;
            validateSeconds += a.validateSeconds; collectSeconds += a.collectSeconds; groupSeconds += a.groupSeconds;
            structureSeconds += a.structureSeconds; placeSeconds += a.placeSeconds;
            keptWhole += a.keptWhole; walks += a.walks; familiesRebuilt += a.familiesRebuilt; registrationsRebuilt += a.registrationsRebuilt;
            renderFragmentsRebuilt += a.renderFragmentsRebuilt; branchesRebuilt += a.branchesRebuilt; candidatesRebuilt += a.candidatesRebuilt;
            registrationsReused += a.registrationsReused; arrayGrowths += a.arrayGrowths; arrayGrowthElements += a.arrayGrowthElements;
        }

        public void Subtract(in VpSnapshotStageTotals a)
        {
            placementsReused -= a.placementsReused; queriesOmitted -= a.queriesOmitted;
            validateCalls -= a.validateCalls; collectCalls -= a.collectCalls; groupCalls -= a.groupCalls; placeCalls -= a.placeCalls;
            validateSeconds -= a.validateSeconds; collectSeconds -= a.collectSeconds; groupSeconds -= a.groupSeconds;
            structureSeconds -= a.structureSeconds; placeSeconds -= a.placeSeconds;
            keptWhole -= a.keptWhole; walks -= a.walks; familiesRebuilt -= a.familiesRebuilt; registrationsRebuilt -= a.registrationsRebuilt;
            renderFragmentsRebuilt -= a.renderFragmentsRebuilt; branchesRebuilt -= a.branchesRebuilt; candidatesRebuilt -= a.candidatesRebuilt;
            registrationsReused -= a.registrationsReused; arrayGrowths -= a.arrayGrowths; arrayGrowthElements -= a.arrayGrowthElements;
        }
    }

    /// <summary>
    /// A frame's collections of a display, timed with the clock itself (2026-10-07): how many, their time whole, and by
    /// stage (0 Room, 1 Read, 2 Snapshot, 3 Draw, 4 Instances, 5 Candidate, 6 Stencil, 7 Upload, 8 Adopt, 9 Release --
    /// the stages follow one another, so they add up to the whole less the little before the first); inside stage 2,
    /// the snapshot's build calls and their time, and the managed heap's change across them in bytes (what the builds
    /// took, less whatever a collector freed meanwhile: it can be negative).
    /// </summary>
    public struct VpCollectTimes
    {
        public bool known;
        public int collections, buildCalls;
        public double collectSeconds, s0, s1, s2, s3, s4, s5, s6, s7, s8, s9, buildSeconds;
        public long buildHeapDelta;

        public void Add(double whole, double[] stages, int builds, double buildTime, long heapDelta)
        {
            known = true;
            collections++;
            collectSeconds += whole;
            s0 += stages[0]; s1 += stages[1]; s2 += stages[2]; s3 += stages[3]; s4 += stages[4];
            s5 += stages[5]; s6 += stages[6]; s7 += stages[7]; s8 += stages[8]; s9 += stages[9];
            buildCalls += builds;
            buildSeconds += buildTime;
            buildHeapDelta += heapDelta;
        }
    }
}
