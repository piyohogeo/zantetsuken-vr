namespace Zantetsu.MeshCut
{
    /// <summary>
    /// What a snapshot's Place passes of one kind did (2026-10-01, for observation; a snapshot keeps one for its structural
    /// passes and one for its placement-only ones): the passes and their time; the render fragments, the placement queries
    /// and the placement checks; the selected boundaries, their plane transforms and the clips kept; the sections found in
    /// the build, reused from the previous snapshot or built, and how many section entries were compared to find them; the
    /// cap polygons clipped and their vertices. Counted, never timed per item. Never reset by a build: a frame with several
    /// builds adds them all.
    /// </summary>
    public sealed class VpPlaceCounts
    {
        public long passes;
        public double seconds;
        public long renderFragments, queries, following, staticPlacements;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public long placementChecks;   // the numeric contract diagnosis's checks of the answers (these configurations only)
#endif
        public long selected, planeTransforms, clipsKept;
        public long sectionsFoundHere, sectionsReused, sectionsBuilt, sectionEntriesCompared;
        public long capClips, capInputVertices, capOutputVertices;

        // Render fragments of a placement-only pass kept as they were taken over from the structure -- nothing selected,
        // standing bit for bit where the structure placed them -- and so neither placed nor built again (2026-10-05).
        public long keptAsSettled;

        // The diagnosis's blocks (VpMultiCutSnapshot.PlacePhasedDiagnosis; 0 otherwise): every query of the pass, every check
        // of their answers, everything built after them -- each block timed whole.
        public double providerSeconds, restSeconds;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public double checkSeconds;
#endif

        public void Clear()
        {
            passes = 0;
            seconds = 0.0;
            renderFragments = queries = following = staticPlacements = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            placementChecks = 0;
            checkSeconds = 0.0;
#endif
            selected = planeTransforms = clipsKept = 0;
            sectionsFoundHere = sectionsReused = sectionsBuilt = sectionEntriesCompared = 0;
            capClips = capInputVertices = capOutputVertices = 0;
            keptAsSettled = 0;
            providerSeconds = restSeconds = 0.0;
        }

        /// <summary>This plus <paramref name="a"/> minus <paramref name="b"/>, field by field.</summary>
        public void AddDifference(VpPlaceCounts a, VpPlaceCounts b)
        {
            passes += a.passes - b.passes;
            seconds += a.seconds - b.seconds;
            renderFragments += a.renderFragments - b.renderFragments;
            queries += a.queries - b.queries;
            following += a.following - b.following;
            staticPlacements += a.staticPlacements - b.staticPlacements;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            placementChecks += a.placementChecks - b.placementChecks;
            checkSeconds += a.checkSeconds - b.checkSeconds;
#endif
            selected += a.selected - b.selected;
            planeTransforms += a.planeTransforms - b.planeTransforms;
            clipsKept += a.clipsKept - b.clipsKept;
            sectionsFoundHere += a.sectionsFoundHere - b.sectionsFoundHere;
            sectionsReused += a.sectionsReused - b.sectionsReused;
            sectionsBuilt += a.sectionsBuilt - b.sectionsBuilt;
            sectionEntriesCompared += a.sectionEntriesCompared - b.sectionEntriesCompared;
            capClips += a.capClips - b.capClips;
            capInputVertices += a.capInputVertices - b.capInputVertices;
            capOutputVertices += a.capOutputVertices - b.capOutputVertices;
            keptAsSettled += a.keptAsSettled - b.keptAsSettled;
            providerSeconds += a.providerSeconds - b.providerSeconds;
            restSeconds += a.restSeconds - b.restSeconds;
        }

        public void Add(VpPlaceCounts a) => AddDifference(a, s_zero);

        public void CopyFrom(VpPlaceCounts a)
        {
            Clear();
            Add(a);
        }

        private static readonly VpPlaceCounts s_zero = new VpPlaceCounts();

        public string Describe() =>
            "passes " + passes + " (" + (seconds * 1000).ToString("F3") + " ms); render fragments " + renderFragments + ", queries " + queries + " (following " + following + ", static " + staticPlacements + ")"
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            + ", placement checks " + placementChecks
#endif
            + "; selected " + selected + " (plane transforms " + planeTransforms + "), clips kept " + clipsKept + ", kept as taken over " + keptAsSettled
            + "; sections found in the build " + sectionsFoundHere + ", reused " + sectionsReused + ", built " + sectionsBuilt + " (entries compared " + sectionEntriesCompared + ")"
            + "; cap clips " + capClips + " (vertices in " + capInputVertices + ", out " + capOutputVertices + ")"
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            + (providerSeconds + checkSeconds + restSeconds > 0.0 ? "; blocks ms: queries " + (providerSeconds * 1000).ToString("F3") + ", checks " + (checkSeconds * 1000).ToString("F3") + ", the rest " + (restSeconds * 1000).ToString("F3") : "");
#else
            + (providerSeconds + restSeconds > 0.0 ? "; blocks ms: queries " + (providerSeconds * 1000).ToString("F3") + ", the rest " + (restSeconds * 1000).ToString("F3") : "");
#endif
    }
}
