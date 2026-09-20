using System;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.Rendering;
using Projection = Zantetsu.MeshCut.VpScreenProjection.Projection;

namespace Zantetsu.MeshCut
{
    /// <summary>What a cap job classification came to.</summary>
    public enum VpCapJobOutcome
    {
        /// <summary>Every cap job, volume group and colour is settled and readable.</summary>
        Classified = 0,

        /// <summary>The snapshot holds more caps or render fragments than this classification was made for.</summary>
        CapacityExceeded = 1,

        /// <summary>
        /// **No longer returned** (DESIGN D-185, D-186): what does not fit the ordinary colours is drawn in the last
        /// colour instead of refusing. Kept so that no other value is renumbered; nothing produces it.
        /// </summary>
        ColorLimitExceeded = 2,
    }

    /// <summary>
    /// The geometry one registration's stencil volumes are drawn from, as the caller who registered it states it: every
    /// draw range the volume would be issued with -- one per submesh, in the order drawn -- read as they are. It is the
    /// caller's existing range information, not a new identity; nothing is copied, transferred or acquired from it.
    /// </summary>
    public readonly struct VpCapJobGeometry
    {
        public VpCapJobGeometry(VpArrayRange<VpGeometryRange> ranges)
        {
            this.ranges = ranges;
        }

        public readonly VpArrayRange<VpGeometryRange> ranges;
    }

    /// <summary>
    /// One cap job (DESIGN 5.6, D-183): one render fragment and one of its selected boundaries, whose drawing polygon is
    /// not empty and which the both-eye visibility test kept.
    /// <para>
    /// **What it carries apart.** The render fragment and the boundary with its side; the volume clip -- the cap's own
    /// face only, with the kept half folded in -- which is not the render fragment's clip of every selected face, the
    /// body's, depth's and shadow's; and, by the cap index, the drawing polygon (up to fourteen vertices) and the
    /// initial section (up to six) the snapshot keeps, both in world space at the render fragment's own placement. The
    /// polygons are read from the snapshot, not held here.
    /// </para>
    /// </summary>
    public readonly struct VpCapJob
    {
        internal VpCapJob(
            int capIndex, int renderFragment, int registration, VpClipBoundary boundary, Vector4 signedPlane,
            VpInstanceClip volumeClip, int polygonVertexCount, int initialVertexCount, int volumeGroup, int colour)
        {
            this.capIndex = capIndex;
            this.renderFragment = renderFragment;
            this.registration = registration;
            this.boundary = boundary;
            this.signedPlane = signedPlane;
            this.volumeClip = volumeClip;
            this.polygonVertexCount = polygonVertexCount;
            this.initialVertexCount = initialVertexCount;
            this.volumeGroup = volumeGroup;
            this.colour = colour;
        }

        /// <summary>The snapshot's cap this job draws: its drawing polygon and its initial section are read by this index.</summary>
        public readonly int capIndex;

        public readonly int renderFragment;

        /// <summary>The registration's index in the snapshot this classification read; meaningless for another snapshot.</summary>
        public readonly int registration;

        /// <summary>The boundary this cap closes: the adopted face (ledger and cut) and the side kept.</summary>
        public readonly VpClipBoundary boundary;

        /// <summary>The face in world space, with the kept half non-negative.</summary>
        public readonly Vector4 signedPlane;


        /// <summary>The volume's clip: this cap's own face and side only, at <see cref="offset"/>.</summary>
        public readonly VpInstanceClip volumeClip;

        public readonly int polygonVertexCount;
        public readonly int initialVertexCount;
        public readonly int volumeGroup;
        public readonly int colour;
    }

    /// <summary>
    /// Cap jobs whose volumes are the same volume -- the same registration, the same boundary and side, and exactly the
    /// same draw ranges, placement and signed plane -- issued once for all of them.
    /// </summary>
    public readonly struct VpCapVolumeGroup
    {
        internal VpCapVolumeGroup(
            int registration, int renderFragment, VpClipBoundary boundary, Vector4 signedPlane,
            VpInstanceClip volumeClip, int colour, bool inLastColour, int jobStart, int jobCount)
        {
            this.inLastColour = inLastColour;
            this.registration = registration;
            this.renderFragment = renderFragment;
            this.boundary = boundary;
            this.signedPlane = signedPlane;
            this.volumeClip = volumeClip;
            this.colour = colour;
            this.jobStart = jobStart;
            this.jobCount = jobCount;
        }

        public readonly int registration;

        /// <summary>The render fragment of the group's first job, whose placement and draw ranges the volume is drawn with.</summary>
        public readonly int renderFragment;

        public readonly VpClipBoundary boundary;
        public readonly Vector4 signedPlane;
        public readonly VpInstanceClip volumeClip;
        public readonly int colour;

        /// <summary>
        /// Whether the group fitted no ordinary colour and went, whole, to the last colour (D-186). Its own-face volume
        /// is then not issued: the last colour issues one volume per render fragment, clipped by every selected face.
        /// </summary>
        public readonly bool inLastColour;

        /// <summary>The group's jobs: <see cref="VpCapJobClassification.TryGetJobOfGroup"/> from this start, this many.</summary>
        public readonly int jobStart;

        public readonly int jobCount;
    }

    /// <summary>
    /// One colour: its volume groups, all drawn before any of its cap jobs. An ordinary colour issues each group's
    /// own-face volume; the last colour (<see cref="last"/>) issues instead one volume per render fragment of its jobs
    /// (<see cref="VpCapJobClassification.TryGetLastColourRenderFragment"/>), clipped by every selected face.
    /// </summary>
    public readonly struct VpCapJobColour
    {
        internal VpCapJobColour(int groupStart, int groupCount, int jobCount, bool last)
        {
            this.groupStart = groupStart;
            this.groupCount = groupCount;
            this.jobCount = jobCount;
            this.last = last;
        }

        /// <summary>Whether this is the last colour of D-186, drawn the old way; false for an ordinary colour.</summary>
        public readonly bool last;

        /// <summary>The colour's groups: <see cref="VpCapJobClassification.TryGetGroupOfColour"/> from this start, this many.</summary>
        public readonly int groupStart;

        public readonly int groupCount;

        /// <summary>How many cap jobs the colour's groups hold together.</summary>
        public readonly int jobCount;
    }

    /// <summary>
    /// The cap-job stencil preparation of DESIGN 5.6 / D-183 on the CPU, over an adopted <see cref="VpMultiCutSnapshot"/>
    /// for one pair of eyes: cap jobs, then volume groups of exactly the same volume, then colours from the projection
    /// of the initial sections. Nothing is drawn, uploaded or issued; the display's current path is not changed by this.
    /// <para>
    /// **Order.** (1) Every cap whose drawing polygon is not empty is judged by the existing both-eye visibility test
    /// (<see cref="VpCapVisibility"/>) -- its drawing polygon and its own outward normal; a cap one eye keeps, or within
    /// the facing epsilon, is kept -- and each kept cap is a job. (2) Jobs are grouped by volume. (3) Groups are given
    /// colours, first fit in input order. (4) The result lists, for each colour, its groups and then their jobs.
    /// </para>
    /// <para>
    /// **One volume, exactly.** Two jobs share a volume only when they are of the same registration, close the same
    /// boundary on the same side, and everything the volume would actually be drawn with is identical, compared
    /// component by component and never within an epsilon or by Unity's approximate equality: the registration's draw
    /// ranges, the placement and the signed plane. The other selected faces are no part of it,
    /// and neither is a sign or a winding of the geometry. The registration's draw ranges are the caller's
    /// (<see cref="VpCapJobGeometry"/>); a registration without them is an argument error, never guessed from a box or
    /// a plane.
    /// </para>
    /// <para>
    /// **Colours.** Two different groups may share a colour only when, in both eyes, every initial section of one is
    /// shown apart on the screen from every initial section of the other, each grown by the margin -- the sections
    /// before the other faces cut them, the region a volume's count can be left in, never the smaller drawing polygon.
    /// Touching, lying within the margin, reaching the eye's plane, and anything missing, malformed or not finite are
    /// "may overlap". A group is checked against every group already in a colour, not a representative. A job that could
    /// leave a negative count is not left out of the test.
    /// </para>
    /// <para>
    /// **The last colour (DESIGN D-185, D-186).** Of a limit of N colours, at most the first N - 1 are ordinary colours
    /// and the last one is reserved. A group that fits no ordinary colour goes, whole, to the last colour -- its jobs are
    /// never split between the two -- so with N = 1 every job is there. The last colour's volumes are not its groups':
    /// it lists the render fragments of its jobs, each once (<see cref="TryGetLastColourRenderFragment"/>), whose
    /// volumes are drawn clipped by every selected face, and then its jobs' caps. It exists only when something is left
    /// for it; it then takes the colour index after the ordinary ones, so <see cref="ColourCount"/> is the colours used,
    /// not the reserved slot's number. What it draws wrongly is accepted (DESIGN 5.2, exception 8); the ordinary colours'
    /// rule is not loosened. A classification is never refused for the colour limit.
    /// </para>
    /// <para>
    /// **Sections.** The initial section is the snapshot's own, kept for the drawn cap when it was built
    /// (<see cref="VpMultiCutSnapshot.InitialSection"/>), read through a look and projected as each point is read,
    /// where it stands. No section is taken, copied or rebuilt here.
    /// </para>
    /// <para>
    /// **Room and lifetime.** All room is made once from the snapshot capacities: jobs and groups at most one per cap.
    /// Only a successful classification is readable; any other ending -- an argument refused, room short, an exception
    /// partway -- leaves nothing readable, not even an earlier success, and changes no input. The
    /// looks at the snapshot and the caller's list are held only while a classification runs and let go in a finally.
    /// The results are indices and values; they belong to the snapshot and the build they were made from and mean
    /// nothing once it is built again. The getters do not check that: a caller confirms <see cref="IsFor"/> before using
    /// a result. A classification must not be started from inside another on the same instance, the caller's list
    /// included; that is refused before anything changes. The snapshot and the list are only read, and the list must not
    /// change while a classification reads it. A result that stops being readable lets go of every ledger it named.
    /// </para>
    /// </summary>
    public sealed class VpCapJobClassification
    {
        private const int SectionVertices = VpCapBoundsPolygon.MaxVertices;

        private readonly VpMultiCutCapacities _capacities;
        private readonly VpCapJob[] _jobs;
        private readonly VpCapVolumeGroup[] _groups;
        private readonly VpCapJobColour[] _colours;

        // Working room.
        private readonly int[] _groupOfJob;
        private readonly int[] _groupFirstJob;
        private readonly int[] _groupJobCount;
        private readonly int[] _jobOfGroup;
        private readonly int[] _colourOfGroup;
        private readonly int[] _groupOfColour;
        private readonly int[] _colourGroupCount;
        private readonly Projection[] _leftState;
        private readonly Projection[] _rightState;
        private readonly bool[] _sectionValid;
        private readonly Vector2[] _leftPoints;
        private readonly Vector2[] _rightPoints;
        private readonly int[] _lastRenderFragments;
        private readonly bool[] _renderFragmentListed;

        // Held only while a classification runs.
        private VpMultiCutSnapshot _reading;
        private IReadOnlyList<VpCapJobGeometry> _geometries;
        private bool _classifying;

        // What the last success was made from.
        private VpMultiCutSnapshot _source;
        private long _sourceGeneration;

        private int _jobCount;
        private int _groupCount;
        private int _colourCount;
        private int _lastColour = -1;
        private int _lastGroupCount;
        private int _lastJobCount;
        private int _lastRenderFragmentCount;

        /// <summary>
        /// Makes the room for snapshots made with <paramref name="capacities"/>. Every size is worked out in 64-bit
        /// arithmetic and must be an int before anything is made.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A capacity is not positive, or a derived size does not fit.</exception>
        public VpCapJobClassification(VpMultiCutCapacities capacities)
        {
            if (capacities.renderFragments <= 0 || capacities.caps <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacities));
            }

            long points = (long)capacities.caps * SectionVertices;
            if (points > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(capacities), "a derived size does not fit an int");
            }

            int caps = capacities.caps;
            _capacities = capacities;
            _jobs = new VpCapJob[caps];
            _groups = new VpCapVolumeGroup[caps];
            _colours = new VpCapJobColour[caps];
            _groupOfJob = new int[caps];
            _groupFirstJob = new int[caps];
            _groupJobCount = new int[caps];
            _jobOfGroup = new int[caps];
            _colourOfGroup = new int[caps];
            _groupOfColour = new int[caps];
            _colourGroupCount = new int[caps];
            _leftState = new Projection[caps];
            _rightState = new Projection[caps];
            _sectionValid = new bool[caps];
            _leftPoints = new Vector2[(int)points];
            _rightPoints = new Vector2[(int)points];
            _lastRenderFragments = new int[capacities.renderFragments];
            _renderFragmentListed = new bool[capacities.renderFragments];
        }

        public VpMultiCutCapacities Capacities => _capacities;

        /// <summary>Whether the last classification succeeded; nothing is readable otherwise.</summary>
        public bool IsClassified { get; private set; }

        /// <summary>What the last classification came to; meaningful only when it returned.</summary>
        public VpCapJobOutcome LastOutcome { get; private set; }

        public int JobCount => IsClassified ? _jobCount : 0;

        /// <summary>Every volume group made, whichever colour it went to.</summary>
        public int VolumeGroupCount => IsClassified ? _groupCount : 0;

        /// <summary>The colours used: the ordinary ones, and the last colour when anything went to it.</summary>
        public int ColourCount => IsClassified ? _colourCount : 0;

        /// <summary>The ordinary colours used, at most the limit less one.</summary>
        public int OrdinaryColourCount => IsClassified ? (_lastColour >= 0 ? _colourCount - 1 : _colourCount) : 0;

        /// <summary>The volume groups given an ordinary colour: each issued as one own-face volume.</summary>
        public int OrdinaryVolumeGroupCount => IsClassified ? _groupCount - _lastGroupCount : 0;

        /// <summary>The index of the last colour among the colours used, or -1 when nothing went to it.</summary>
        public int LastColourIndex => IsClassified ? _lastColour : -1;

        /// <summary>The volume groups sent to the last colour. Their own-face volumes are not issued.</summary>
        public int LastColourGroupCount => IsClassified ? _lastGroupCount : 0;

        /// <summary>The cap jobs sent to the last colour: its caps, each drawn once.</summary>
        public int LastColourJobCount => IsClassified ? _lastJobCount : 0;

        /// <summary>The render fragments of the last colour's jobs, each once: its volumes. Not a count of groups.</summary>
        public int LastColourRenderFragmentCount => IsClassified ? _lastRenderFragmentCount : 0;

        /// <summary>Caps of the last successful classification's snapshot whose drawing polygon was empty.</summary>
        public int EmptyCapCount { get; private set; }

        /// <summary>Non-empty caps the visibility test left out.</summary>
        public int HiddenCapCount { get; private set; }

        /// <summary>
        /// Whether the readable result was made from <paramref name="snapshot"/> as it is now built. A caller checks this
        /// before using the result: once the snapshot is built again the getters below still return what was classified
        /// from the earlier build -- nothing refuses them automatically -- and that is no longer this snapshot's.
        /// </summary>
        public bool IsFor(VpMultiCutSnapshot snapshot)
        {
            return IsClassified && snapshot != null && ReferenceEquals(snapshot, _source)
                && snapshot.BuildGeneration == _sourceGeneration && snapshot.IsBuilt;
        }

        public bool TryGetJob(int index, out VpCapJob job)
        {
            bool ok = IsClassified && index >= 0 && index < _jobCount;
            job = ok ? _jobs[index] : default;
            return ok;
        }

        public bool TryGetVolumeGroup(int index, out VpCapVolumeGroup group)
        {
            bool ok = IsClassified && index >= 0 && index < _groupCount;
            group = ok ? _groups[index] : default;
            return ok;
        }

        public bool TryGetColour(int index, out VpCapJobColour colour)
        {
            bool ok = IsClassified && index >= 0 && index < _colourCount;
            colour = ok ? _colours[index] : default;
            return ok;
        }

        /// <summary>The job at <paramref name="position"/> of the list grouped by volume group (see <see cref="VpCapVolumeGroup.jobStart"/>).</summary>
        public bool TryGetJobOfGroup(int position, out int jobIndex)
        {
            bool ok = IsClassified && position >= 0 && position < _jobCount;
            jobIndex = ok ? _jobOfGroup[position] : -1;
            return ok;
        }

        /// <summary>The group at <paramref name="position"/> of the list ordered by colour (see <see cref="VpCapJobColour.groupStart"/>).</summary>
        public bool TryGetGroupOfColour(int position, out int groupIndex)
        {
            bool ok = IsClassified && position >= 0 && position < _groupCount;
            groupIndex = ok ? _groupOfColour[position] : -1;
            return ok;
        }

        /// <summary>
        /// The render fragment at <paramref name="position"/> of the last colour's list: the render fragments its jobs
        /// belong to, each once, in the order its jobs are listed.
        /// </summary>
        public bool TryGetLastColourRenderFragment(int position, out int renderFragment)
        {
            bool ok = IsClassified && position >= 0 && position < _lastRenderFragmentCount;
            renderFragment = ok ? _lastRenderFragments[position] : -1;
            return ok;
        }

        /// <summary>Called with the count of jobs written so far, each time one is written. For tests only.</summary>
        internal Action<int> AfterJobWritten { get; set; }

        /// <summary>Whether a look at a snapshot or the caller's list is held: only while a classification runs. For tests.</summary>
        internal int HeldViews => (_reading != null ? 1 : 0) + (_geometries != null ? 1 : 0);

        /// <summary>
        /// Classifies <paramref name="snapshot"/> for these eyes.
        /// </summary>
        /// <param name="geometries">
        /// One entry per registration of the snapshot, in the order the snapshot was built from, each with every draw
        /// range its volumes would be issued with. Read only while this runs, and not changed by it.
        /// </param>
        /// <exception cref="ArgumentNullException">The snapshot or the list is null.</exception>
        /// <exception cref="ArgumentException">
        /// The snapshot is not built, or the list does not have one entry of at least one draw range per registration.
        /// </exception>
        /// <param name="maxColours">
        /// The colour limit N: at most N - 1 ordinary colours, and the last one for what they cannot take (D-186).
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">The epsilon, the margin or the colour limit is out of range.</exception>
        /// <exception cref="InvalidOperationException">Called while a classification of this instance is running.</exception>
        public VpCapJobOutcome TryClassify(
            VpMultiCutSnapshot snapshot,
            IReadOnlyList<VpCapJobGeometry> geometries,
            in VpCapEye left,
            in VpCapEye right,
            float facingEpsilon,
            Vector2 ndcMargin,
            int maxColours)
        {
            // Refused before anything changes, and before any of the caller's code -- a list's Count or indexer -- runs.
            if (_classifying)
            {
                throw new InvalidOperationException("a classification of this instance is already running");
            }

            // From here to the end, argument checks included, one guard and one finally: a call made from inside the
            // caller's list is refused, and whatever ends this attempt leaves nothing readable unless it succeeded.
            _classifying = true;
            bool succeeded = false;
            try
            {
                // Unreadable from the first moment of any attempt: an earlier result never survives a later one.
                Reset();
                if (snapshot == null)
                {
                    throw new ArgumentNullException(nameof(snapshot));
                }

                if (geometries == null)
                {
                    throw new ArgumentNullException(nameof(geometries));
                }

                if (!snapshot.IsBuilt)
                {
                    throw new ArgumentException("The snapshot is not built.", nameof(snapshot));
                }

                if (float.IsNaN(facingEpsilon) || float.IsInfinity(facingEpsilon) || facingEpsilon < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(facingEpsilon), facingEpsilon, "A finite length, zero or more.");
                }

                if (!VpScreenProjection.IsFinite(ndcMargin.x) || !VpScreenProjection.IsFinite(ndcMargin.y) || ndcMargin.x < 0f || ndcMargin.y < 0f)
                {
                    throw new ArgumentOutOfRangeException(nameof(ndcMargin), ndcMargin, "Finite and zero or more on each axis.");
                }

                if (maxColours < 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(maxColours), maxColours, "At least one colour.");
                }

                _reading = snapshot;
                _geometries = geometries;
                int registrations = snapshot.RegistrationCount;
                int given = geometries.Count;
                if (given != registrations)
                {
                    throw new ArgumentException(
                        "One geometry per registration of the snapshot: " + registrations + " expected, " + given + " given.",
                        nameof(geometries));
                }

                for (int g = 0; g < given; g++)
                {
                    VpArrayRange<VpGeometryRange> ranges = geometries[g].ranges;
                    if (ranges.IsNull || ranges.Count < 1)
                    {
                        throw new ArgumentException("Registration " + g + " has no draw range stated.", nameof(geometries));
                    }
                }

                if (snapshot.CapCount > _jobs.Length || snapshot.RenderFragmentCount > _capacities.renderFragments)
                {
                    LastOutcome = VpCapJobOutcome.CapacityExceeded;
                    return LastOutcome;
                }

                LastOutcome = Classify(snapshot, left, right, facingEpsilon, ndcMargin, maxColours);
                succeeded = LastOutcome == VpCapJobOutcome.Classified;
                if (succeeded)
                {
                    _source = snapshot;
                    _sourceGeneration = snapshot.BuildGeneration;
                    IsClassified = true;
                }

                return LastOutcome;
            }
            finally
            {
                _reading = null;
                _geometries = null;
                if (!succeeded)
                {
                    Reset();
                }

                _classifying = false;
            }
        }

        /// <summary>
        /// Makes the last result unreadable and lets go of every snapshot and ledger it referred to, for a caller that
        /// has read what it needs and keeps no result of its own here (a display, after a camera's preparation). Refused
        /// while a classification of this instance is running.
        /// </summary>
        /// <exception cref="InvalidOperationException">Called while a classification of this instance is running.</exception>
        public void Release()
        {
            if (_classifying)
            {
                throw new InvalidOperationException("a classification of this instance is running");
            }

            Reset();
        }

        /// <summary>
        /// Makes nothing readable, and lets go of what the used part of the result room refers to -- each job's and each
        /// group's boundary names its ledger -- before the counts are set back, so that no slot outside a readable
        /// result keeps an earlier ledger alive. The room itself is kept.
        /// </summary>
        private void Reset()
        {
            IsClassified = false;
            _source = null;
            _sourceGeneration = 0;
            Array.Clear(_jobs, 0, _jobCount);
            Array.Clear(_groups, 0, _groupCount);
            Array.Clear(_colours, 0, _colourCount);
            _jobCount = 0;
            _groupCount = 0;
            _colourCount = 0;
            _lastColour = -1;
            _lastGroupCount = 0;
            _lastJobCount = 0;
            _lastRenderFragmentCount = 0;
            EmptyCapCount = 0;
            HiddenCapCount = 0;
        }

        /// <summary>
        /// How many slots of the whole job and group room hold a reference to a ledger, used or not. For tests, which
        /// expect it to be exactly the readable jobs and groups.
        /// </summary>
        internal int LedgerReferencesHeld
        {
            get
            {
                int held = 0;
                for (int i = 0; i < _jobs.Length; i++)
                {
                    held += _jobs[i].boundary.face.scope != null ? 1 : 0;
                    held += _groups[i].boundary.face.scope != null ? 1 : 0;
                }

                return held;
            }
        }
        private VpCapJobOutcome Classify(
            VpMultiCutSnapshot snapshot, in VpCapEye left, in VpCapEye right, float facingEpsilon, Vector2 margin, int maxColours)
        {
            // 1. Jobs: the non-empty caps the both-eye visibility test keeps.
            int caps = snapshot.CapCount;
            int jobs = 0;
            int empty = 0;
            int hidden = 0;
            for (int c = 0; c < caps; c++)
            {
                snapshot.TryGetCap(c, out VpMultiCutCap cap);
                if (cap.vertexCount == 0)
                {
                    empty++;
                    continue;
                }

                if (!VpCapVisibility.Classify(snapshot.CapPolygon(c).AsSpan(), cap.outwardNormal, left, right, facingEpsilon).Keep)
                {
                    hidden++;
                    continue;
                }

                snapshot.TryGetRenderFragment(cap.renderFragment, out VpMultiCutRenderFragment rf);
                var world = new Vector4(cap.worldPlane.x, cap.worldPlane.y, cap.worldPlane.z, cap.worldPlane.w);
                float side = cap.boundary.side;
                Vector4 signed = side > 0f ? world : -world;
                VpArrayRange<Vector3> section = snapshot.InitialSection(c);
                _jobs[jobs] = new VpCapJob(
                    c, cap.renderFragment, rf.registration, cap.boundary, signed,
                    VpInstanceClip.Keep(world, side), cap.vertexCount, section.Count, -1, -1);
                jobs++;
                _jobCount = jobs;
                AfterJobWritten?.Invoke(jobs);
            }

            EmptyCapCount = empty;
            HiddenCapCount = hidden;

            // 2. Volume groups: exactly the same volume.
            int groups = 0;
            for (int j = 0; j < jobs; j++)
            {
                int found = -1;
                for (int g = 0; g < groups && found < 0; g++)
                {
                    found = SameVolume(snapshot, _jobs[_groupFirstJob[g]], _jobs[j]) ? g : -1;
                }

                if (found < 0)
                {
                    found = groups;
                    _groupFirstJob[groups] = j;
                    _groupJobCount[groups] = 0;
                    groups++;
                }

                _groupOfJob[j] = found;
                _groupJobCount[found]++;
            }

            _groupCount = groups;

            // The jobs listed group by group, each group's in input order.
            int position = 0;
            for (int g = 0; g < groups; g++)
            {
                int start = position;
                for (int j = 0; j < jobs; j++)
                {
                    if (_groupOfJob[j] == g)
                    {
                        _jobOfGroup[position++] = j;
                    }
                }

                _groupJobCount[g] = position - start;
                _groupFirstJob[g] = start;
            }

            // 3. Each job's initial section, projected once per eye, where it stands.
            for (int j = 0; j < jobs; j++)
            {
                VpArrayRange<Vector3> section = snapshot.InitialSection(_jobs[j].capIndex);
                bool valid = !section.IsNull && section.Count >= 1 && section.Count <= SectionVertices;
                _sectionValid[j] = valid;
                if (!valid)
                {
                    continue;
                }

                _leftState[j] = ProjectFor(section, left, margin, j, _leftPoints);
                _rightState[j] = ProjectFor(section, right, margin, j, _rightPoints);
            }

            // 4. Ordinary colours, first fit, at most the limit less one; a group that fits none goes whole to the last.
            const int ToLast = -2;
            int ordinaryLimit = maxColours - 1;
            int colours = 0;
            int lastGroups = 0;
            int lastJobs = 0;
            for (int g = 0; g < groups; g++)
            {
                int chosen = -1;
                for (int c = 0; c < colours && chosen < 0; c++)
                {
                    bool conflict = false;
                    for (int h = 0; h < g && !conflict; h++)
                    {
                        if (_colourOfGroup[h] == c)
                        {
                            conflict = GroupsMayOverlap(g, h, margin);
                        }
                    }

                    chosen = conflict ? -1 : c;
                }

                if (chosen < 0 && colours < ordinaryLimit)
                {
                    chosen = colours++;
                }

                if (chosen < 0)
                {
                    chosen = ToLast;
                    lastGroups++;
                    lastJobs += _groupJobCount[g];
                }

                _colourOfGroup[g] = chosen;
            }

            // The last colour takes the index after the ordinary colours used, and exists only when something went to it.
            int last = lastGroups > 0 ? colours : -1;
            if (last >= 0)
            {
                for (int g = 0; g < groups; g++)
                {
                    if (_colourOfGroup[g] == ToLast)
                    {
                        _colourOfGroup[g] = last;
                    }
                }

                colours++;
            }

            _colourCount = colours;
            _lastColour = last;
            _lastGroupCount = lastGroups;
            _lastJobCount = lastJobs;

            // 5. The groups listed colour by colour, and the results written out.
            position = 0;
            for (int c = 0; c < colours; c++)
            {
                int start = position;
                int jobsInColour = 0;
                for (int g = 0; g < groups; g++)
                {
                    if (_colourOfGroup[g] == c)
                    {
                        _groupOfColour[position++] = g;
                        jobsInColour += _groupJobCount[g];
                    }
                }

                _colours[c] = new VpCapJobColour(start, position - start, jobsInColour, c == last);
            }

            // The last colour's render fragments: those of its jobs, each once, in the order its jobs are listed.
            int listed = 0;
            if (last >= 0)
            {
                Array.Clear(_renderFragmentListed, 0, snapshot.RenderFragmentCount);
                VpCapJobColour lastColour = _colours[last];
                for (int p = lastColour.groupStart; p < lastColour.groupStart + lastColour.groupCount; p++)
                {
                    int g = _groupOfColour[p];
                    for (int k = _groupFirstJob[g]; k < _groupFirstJob[g] + _groupJobCount[g]; k++)
                    {
                        int rf = _jobs[_jobOfGroup[k]].renderFragment;
                        if (!_renderFragmentListed[rf])
                        {
                            _renderFragmentListed[rf] = true;
                            _lastRenderFragments[listed++] = rf;
                        }
                    }
                }
            }

            _lastRenderFragmentCount = listed;

            for (int g = 0; g < groups; g++)
            {
                VpCapJob first = _jobs[_jobOfGroup[_groupFirstJob[g]]];
                _groups[g] = new VpCapVolumeGroup(
                    first.registration, first.renderFragment, first.boundary, first.signedPlane, first.volumeClip,
                    _colourOfGroup[g], _colourOfGroup[g] == last, _groupFirstJob[g], _groupJobCount[g]);
            }

            for (int j = 0; j < jobs; j++)
            {
                VpCapJob job = _jobs[j];
                int g = _groupOfJob[j];
                _jobs[j] = new VpCapJob(
                    job.capIndex, job.renderFragment, job.registration, job.boundary, job.signedPlane, job.volumeClip,
                    job.polygonVertexCount, job.initialVertexCount, g, _colourOfGroup[g]);
            }

            return VpCapJobOutcome.Classified;
        }

        private static Projection ProjectFor(
            VpArrayRange<Vector3> section, in VpCapEye eye, Vector2 margin, int job, Vector2[] points)
        {
            if (!VpScreenProjection.IsFinite(eye.worldToClip))
            {
                return Projection.NotFinite;
            }

            return VpScreenProjection.Project(
                section.AsSpan(), eye.worldToClip, margin, new Span<Vector2>(points, job * SectionVertices, section.Count));
        }

        /// <summary>Whether any job of one group may overlap any job of the other on the screen, in either eye.</summary>
        private bool GroupsMayOverlap(int g, int h, Vector2 margin)
        {
            for (int a = 0; a < _groupJobCount[g]; a++)
            {
                int ja = _jobOfGroup[_groupFirstJob[g] + a];
                for (int b = 0; b < _groupJobCount[h]; b++)
                {
                    int jb = _jobOfGroup[_groupFirstJob[h] + b];
                    if (MayOverlapInEye(ja, jb, _leftState, _leftPoints, margin) || MayOverlapInEye(ja, jb, _rightState, _rightPoints, margin))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool MayOverlapInEye(int a, int b, Projection[] state, Vector2[] points, Vector2 margin)
        {
            if (!_sectionValid[a] || !_sectionValid[b])
            {
                return true;
            }

            Projection sa = state[a];
            Projection sb = state[b];
            if (sa == Projection.NotFinite || sb == Projection.NotFinite)
            {
                return true;
            }

            if (sa == Projection.Nothing || sb == Projection.Nothing)
            {
                return false;
            }

            if (sa == Projection.Everywhere || sb == Projection.Everywhere)
            {
                return true;
            }

            var pa = new ReadOnlySpan<Vector2>(points, a * SectionVertices, _jobs[a].initialVertexCount);
            var pb = new ReadOnlySpan<Vector2>(points, b * SectionVertices, _jobs[b].initialVertexCount);
            return !VpScreenProjection.PolygonsApart(pa, pb, margin);
        }

        /// <summary>
        /// D-183's condition, exactly: the same registration, boundary and side, and everything the volume is drawn with
        /// identical component by component -- the registration's draw ranges, the placement and the signed plane.
        /// No epsilon, no Unity approximate equality, and nothing about the other selected faces.
        /// </summary>
        private bool SameVolume(VpMultiCutSnapshot snapshot, in VpCapJob a, in VpCapJob b)
        {
            if (a.registration != b.registration || !a.boundary.Equals(b.boundary) || !Exact(a.signedPlane, b.signedPlane))
            {
                return false;
            }

            snapshot.TryGetRenderFragment(a.renderFragment, out VpMultiCutRenderFragment ra);
            snapshot.TryGetRenderFragment(b.renderFragment, out VpMultiCutRenderFragment rb);
            for (int i = 0; i < 16; i++)
            {
                if (ra.geometryLocalToWorld[i] != rb.geometryLocalToWorld[i])
                {
                    return false;
                }
            }

            // The same registration names one entry of the caller's list, which this call relies on not changing while
            // it runs (the input contract); its ranges are compared all the same so that the condition is stated whole.
            VpArrayRange<VpGeometryRange> x = _geometries[a.registration].ranges;
            VpArrayRange<VpGeometryRange> y = _geometries[b.registration].ranges;
            if (x.Count != y.Count)
            {
                return false;
            }

            for (int i = 0; i < x.Count; i++)
            {
                VpGeometryRange p = x[i];
                VpGeometryRange q = y[i];
                if (p.vertexStart != q.vertexStart || p.vertexCount != q.vertexCount || p.indexStart != q.indexStart
                    || p.indexCount != q.indexCount)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Exact(Vector3 p, Vector3 q)
        {
            return p.x == q.x && p.y == q.y && p.z == q.z;
        }

        private static bool Exact(Vector4 p, Vector4 q)
        {
            return p.x == q.x && p.y == q.y && p.z == q.z && p.w == q.w;
        }
    }
}
