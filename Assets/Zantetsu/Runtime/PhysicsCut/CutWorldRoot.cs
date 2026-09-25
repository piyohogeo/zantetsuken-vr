using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The one place a cut world is put together (DESIGN 4.5.6, 7.1, 7.2): the ledger, the physics correspondence and
    /// its placement lookup, the geometry storage with its reference table and the display, the shared dispatcher with
    /// its destinations and frame, the cook, the cut DAG with the real display commit, and the one
    /// <see cref="ProvisionalCutDriver"/> that takes cuts through all of it.
    /// <para>
    /// **It builds and it ends; it does not drive.** Unity's own <c>Update</c> and <c>LateUpdate</c> on the driver are
    /// what carry a cut forward, and nothing here calls them: one thing drives, and this is not it. What this owns is
    /// the parts — it creates them from a <see cref="CutWorldProfile"/> in <c>Awake</c> and ends them in the order
    /// their lifetimes require — and the registration of a body, which ties one fragment's physics shape, its display
    /// geometry and the two frames between them together in one call.
    /// </para>
    /// <para>
    /// **The ending is the delicate part.** Work that is still with a worker reads the storage and the shapes, so the
    /// frame, the cook and the DAG have to outlive the driver. An ending is asked for once and then **runs over the
    /// ordinary frames**: acceptance closes, the driver's cuts are asked to end, the driver stops driving, the DAG and
    /// the cook are closed so nothing new is offered, and this pumps the frame **once per real frame** — with the
    /// engine's own frame id, so no budget is refilled for it and nothing is polled. When nothing is out any more, the
    /// dispatcher is stopped and **its answer is what decides**: only a confirmed stop frees the owners, the display,
    /// the storage and the destinations. A deadline that passes is not a confirmation, and nothing a worker may still
    /// be reading is freed on one.
    /// </para>
    /// <para>
    /// **The termination request of DESIGN 4** lives here, because this is the composition root: one non-persistent
    /// latch, fixed one way by the first cause Main handled. A geometry failure of a shared geometry is such a cause.
    /// Fixing it closes new cut acceptance and the commit of unpublished products, then logs once and calls the
    /// Player's termination API once. It is **not** the ending above: nothing is waited for, no work is collected and
    /// nothing is freed by it.
    /// </para>
    /// <para>
    /// **What is not here.** Hit detection, the building constraint, Character, the separation impulses of a cut, XR,
    /// and the drawing itself: this settles what a collection is built from, and a caller that wants it drawn calls
    /// <see cref="VpLogicalCutDisplay.Render"/> with its own camera. Nothing here is a scheduler and nothing is
    /// registered into the player loop.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public sealed partial class CutWorldRoot : MonoBehaviour, ICutTerminationLatch
    {
        /// <summary>One submesh source index and the material it is drawn with.</summary>
        [Serializable]
        public struct MaterialBinding
        {
            public int sourceIndex;
            public Material material;
        }

        [SerializeField]
        private CutWorldProfile profile;

        [Tooltip("The material each submesh source index is drawn with. A geometry may use only these.")]
        [SerializeField]
        private MaterialBinding[] materials = Array.Empty<MaterialBinding>();

        [Tooltip("Optional: the material shadow casters are drawn with. None means no shadow pass.")]
        [SerializeField]
        private Material shadowMaterial;

        [Tooltip("Optional: the material a provisional side's shadow is drawn with.")]
        [SerializeField]
        private Material provisionalShadowMaterial;

        [Header("Optional shared Compact16uv atlas")]
        [Tooltip("Both textures or neither. This world exclusively owns the global binding until shutdown; shared forward materials opt in offline.")]
        [SerializeField] private Texture2D normalPaletteAtlas;
        [SerializeField] private Texture2D debugPaletteAtlas;
        private bool _ownsPaletteBinding;

        private IWorkExecutor _unityJob;
        private IWorkExecutor _geometryPool;
        private IWorkExecutor _backgroundPool;
        private TerminatingGeometryFault _fault;
        private bool _ending;
        private bool _released;
        private bool _stopRefused;

        /// <summary>
        /// The destinations this world runs its work on, for a test that has to decide when a finished work is handed
        /// back. Null -- the ordinary case -- means the product's own: the Unity job system and the two worker pools.
        /// It is read once, while building.
        /// </summary>
        internal Func<WorkDestination, IWorkExecutor> executors;

        /// <summary>
        /// The same, for the **next** world built in this domain: a world a scene builds has already run its Awake by
        /// the time anything outside could reach it, so a test that must decide when a finished work comes back puts
        /// its destinations here before the scene is loaded. It is read once, by the next build, and cleared there.
        /// </summary>
        internal static Func<WorkDestination, IWorkExecutor> nextWorldExecutors;

        /// <summary>
        /// The Player's termination API, called once when the termination request of DESIGN 4 is made. Null means the
        /// product's own; a test gives its own so that no test ends the editor.
        /// </summary>
        internal Action terminatePlayer;

        /// <summary>Whether everything was built and a cut may be asked for.</summary>
        public bool IsReady { get; private set; }

        /// <summary>The logical state of every cut in this world.</summary>
        public LogicalCutLedger Ledger { get; private set; }

        /// <summary>Which physics owner each live fragment is.</summary>
        public PhysicsOwnerRegistry Owners { get; private set; }

        /// <summary>Where the display asks for a fragment's placement: the physics owners, and nothing else.</summary>
        public PhysicsOwnerPlacementLookup Placement { get; private set; }

        /// <summary>The geometry every body and every cut side lives in.</summary>
        public VpCpuGeometryStorage Storage { get; private set; }

        public VpGeometryReferenceTable References { get; private set; }

        /// <summary>What is shown, and where a geometry commit really happens.</summary>
        public VpLogicalCutDisplay Display { get; private set; }

        public SharedWorkDispatcher Dispatcher { get; private set; }

        /// <summary>The frame every participant is carried by: the cook, the DAG, and the driver's endings.</summary>
        public SharedWorkFrame Frame { get; private set; }

        public PhysicsCutCook Cook { get; private set; }

        /// <summary>Where the display geometry of an accepted cut is carried to its commit.</summary>
        public CutDag Geometry { get; private set; }

        /// <summary>The one caller of the cut path. It is on this object and drives itself from Unity's update.</summary>
        public ProvisionalCutDriver Driver { get; private set; }

        /// <summary>How many geometry failures this world has been told about.</summary>
        public int GeometryFaults => _fault?.Count ?? 0;

        /// <summary>
        /// Whether the termination request of DESIGN 4 has been made. It is fixed one way: once true, no cut is
        /// accepted and no unpublished product is committed, whatever happens afterwards.
        /// </summary>
        public bool TerminationRequested { get; private set; }

        /// <summary>How many times the Player's termination API was called. One, at most, however many causes arrive.</summary>
        public int TerminationCalls { get; private set; }

        /// <summary>Whether an ending has been asked for and is still running.</summary>
        public bool IsEnding => _ending && !_released;

        /// <summary>Whether everything this world held has been given back.</summary>
        public bool IsReleased => _released;

        private void Awake()
        {
            Build();
        }

        private void OnDestroy()
        {
            if (TerminationRequested)
            {
                // The Player is ending. DESIGN 4 guarantees no collection and no release for it, and starting the
                // ordinary ending here -- ending every cut, collecting, confirming the workers -- would be exactly
                // the waiting that contract refuses. Nothing is done.
                return;
            }

            // An ending cannot span frames from here: this is the last call this component gets. What can be finished
            // now is finished; what cannot is **not freed**, because a worker may still be reading it.
            Shutdown();
            if (!_released)
            {
                UnityEngine.Debug.LogError(
                    name + ": this cut world was destroyed before its ending had finished. Nothing it holds has been "
                    + "freed, because work may still be reading it. End it with Shutdown over the frames before "
                    + "destroying it.", this);
            }
        }

        /// <summary>
        /// One turn of the ending, per ordinary frame. It runs only while an ending is going on: the driver has
        /// stopped driving by then, so this is what carries the frame, and it is one turn on **this frame's own id**,
        /// which refills nothing.
        /// </summary>
        private void LateUpdate()
        {
            if (_ending && !_released && !TerminationRequested)
            {
                CarryEnding();
            }
        }

        /// <summary>
        /// Builds the world from the profile. It is done once, in <c>Awake</c>, before the driver's first update: this
        /// component runs earlier than the driver by its execution order, so a cut asked for in the first frame finds
        /// everything already there.
        /// </summary>
        private void Build()
        {
            if (IsReady || _ending || _released)
            {
                return;
            }

            if (profile == null)
            {
                UnityEngine.Debug.LogError(name + ": a CutWorldRoot needs a CutWorldProfile.", this);
                enabled = false;
                return;
            }

            if (!profile.IsUsable(out string reason))
            {
                UnityEngine.Debug.LogError(name + ": the profile cannot be used -- " + reason + ".", this);
                enabled = false;
                return;
            }

            var materialsBySourceIndex = new Dictionary<int, Material>(materials.Length);
            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i].material == null)
                {
                    UnityEngine.Debug.LogError(
                        name + ": the material for source index " + materials[i].sourceIndex + " is missing.", this);
                    enabled = false;
                    return;
                }

                materialsBySourceIndex[materials[i].sourceIndex] = materials[i].material;
            }

            bool usePalette = normalPaletteAtlas != null || debugPaletteAtlas != null;
            if (usePalette && (normalPaletteAtlas == null || debugPaletteAtlas == null
                || normalPaletteAtlas.width != 256 || normalPaletteAtlas.height != 256
                || debugPaletteAtlas.width != 256 || debugPaletteAtlas.height != 256
                || VpCutSurfaceAtlas.IsBound))
            {
                UnityEngine.Debug.LogError(name + ": atlas setup requires a 256x256 pair and an unowned global binding.", this);
                enabled = false;
                return;
            }

            Ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(profile.MaxIncompleteCuts));
            Owners = new PhysicsOwnerRegistry();
            Placement = new PhysicsOwnerPlacementLookup(Owners);
            Storage = new VpCpuGeometryStorage(
                profile.VertexCapacity, profile.IndexCapacity, profile.GeometryDescriptorCapacity,
                profile.SubmeshCapacity, profile.VertexBlockCapacity, Allocator.Persistent);
            References = new VpGeometryReferenceTable(
                Storage, profile.GeometryReferenceCapacity, profile.DisplayInstanceCapacity);

            if (!VpLogicalCutDisplay.TryCreate(
                    Storage, References, Ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial,
                    profile.DrawCommandCapacity, profile.DrawInstanceCapacity, profile.BranchCapacity,
                    profile.CandidateCapacity, profile.ChainDepth, profile.StencilSettings,
                    out VpLogicalCutDisplay display))
            {
                // The room the display needs, or the stencil configuration it requires, could not be established.
                // Those are causes of the common Player termination of DESIGN 4, and the latch is here from before
                // this initialization -- so the same exit is taken here as during play. A profile whose values do not
                // hold together, or a missing material, is an authoring mistake and is reported as one above; this is
                // not.
                enabled = false;
                RequestTermination("the display could not be created: the room or the stencil configuration it needs "
                                   + "could not be established");
                Storage.Dispose();
                Storage = null;
                return;
            }

            Display = display;
            if (usePalette)
            {
                VpCutSurfaceAtlas.Bind(normalPaletteAtlas, debugPaletteAtlas);
                _ownsPaletteBinding = true;
                Display.SetCapPaletteAtlasEnabled(true);
            }

            // What is drawn follows the physics owners, and only them: a fragment with no owner in the scene is not
            // drawn somewhere it used to be.
            Display.Placement = Placement;

            Func<WorkDestination, IWorkExecutor> destinations = executors ?? nextWorldExecutors;
            nextWorldExecutors = null;
            _unityJob = destinations?.Invoke(WorkDestination.UnityJob)
                        ?? new UnityJobWorkExecutor(profile.UnityJobCapacity);
            _geometryPool = destinations?.Invoke(WorkDestination.GeometryPool)
                            ?? WorkerPoolExecutor.GeometryPool(profile.GeometryWorkerCount);
            _backgroundPool = destinations?.Invoke(WorkDestination.BackgroundPool)
                              ?? WorkerPoolExecutor.BackgroundPool(profile.BackgroundWorkerCount);
            Dispatcher = new SharedWorkDispatcher(
                profile.WaitingCapacity, profile.ReservedForUrgent, profile.FrameBudget, _unityJob, _geometryPool,
                _backgroundPool);

            Cook = new PhysicsCutCook(Dispatcher, profile.ConcurrentCookReservations);
            Frame = new SharedWorkFrame(Dispatcher);
            Frame.Add(Cook);

            _fault = new TerminatingGeometryFault(this);

            // The commit is the display's own, behind the latch: once a termination has been requested, an
            // unpublished product is not committed any more, which is a refusal and changes nothing.
            Geometry = new CutDag(
                Storage, Ledger, Dispatcher, new LatchedCommit(new VpDisplayGeometryCommit(Display), this), _fault);

            // The one driver, on this object, bound to everything above. It adds the DAG to the frame itself, so the
            // work left over by a driver that goes away is still carried by the frame this owns.
            Driver = gameObject.GetComponent<ProvisionalCutDriver>();
            if (Driver == null)
            {
                Driver = gameObject.AddComponent<ProvisionalCutDriver>();
            }

            Driver.Bind(
                Ledger, Owners, Cook, Frame, Display, profile.SupportEpsilon, profile.AnchorEpsilon,
                profile.VertexLimit, null, Geometry, this);

            IsReady = true;
        }

        /// <summary>
        /// Takes one authored body into this world: a live fragment of its own, the physics owner it already is, the
        /// display geometry it is shown as, and the geometry the first cut of it will be cut from — one call, so that
        /// the four cannot be given different answers.
        /// </summary>
        /// <param name="root">
        /// The object the body's Rigidbody is on. **On success it becomes this world's**: the correspondence destroys
        /// it when the fragment it is retires, which is what a cut of it does to the body it replaces. The caller
        /// places it and may read it, but does not destroy it. On a refusal it is untouched and stays the caller's.
        /// </param>
        /// <param name="shape">
        /// The physics shape: the convexes a cut of this body reads, with the cooked collider meshes they use.
        /// </param>
        /// <param name="geometry">The display geometry, appended to <see cref="Storage"/> as a cut input.</param>
        /// <param name="lineageToGeometryLocal">
        /// **Where the display geometry sits in the lineage's own logical frame** — the frame an adopted plane is
        /// given in. The kernel converts a plane into the geometry's coordinates with this, and every child of this
        /// body inherits it through the commits.
        /// </param>
        /// <param name="geometryLocalToOwner">
        /// **Where that geometry sits on its physics owner** — the owner's coordinates. What is drawn for this body is
        /// its owner's world transform with this applied, which is how the drawing follows the actor.
        /// </param>
        /// <param name="anchors">The fixed support anchors of this body, or null for a body that has none.</param>
        public bool TryAddBody(
            GameObject root,
            PhysicsOwnerShape shape,
            VpStoredGeometry geometry,
            Matrix4x4 lineageToGeometryLocal,
            Matrix4x4 geometryLocalToOwner,
            IReadOnlyList<float3> anchors,
            out LogicalFragmentId fragment)
        {
            fragment = default;
            if (!IsReady || root == null || shape == null)
            {
                return false;
            }

            var body = root.GetComponent<Rigidbody>();
            if (body == null)
            {
                UnityEngine.Debug.LogError(root.name + ": a body of a cut world needs a Rigidbody.", root);
                return false;
            }

            float3[] anchorArray = null;
            if (anchors != null && anchors.Count > 0)
            {
                anchorArray = new float3[anchors.Count];
                for (int i = 0; i < anchors.Count; i++)
                {
                    anchorArray[i] = anchors[i];
                }
            }

            // **The one that can refuse comes first.** The display needs the fragment to exist, so the fragment is
            // made and then shown; a refusal at that point is undone by retiring that fragment, which nothing else
            // has been told about yet. The physics and the geometry follow only once the display has taken it, so a
            // refusal never leaves a body half in the world for a caller to register a second time.
            fragment = Ledger.AddFragment(anchorArray);
            if (!Display.TryShow(
                    fragment, geometry, root.transform.localToWorldMatrix, lineageToGeometryLocal,
                    Array.Empty<VpClipBoundary>()))
            {
                Ledger.Retire(fragment);
                fragment = default;
                UnityEngine.Debug.LogError(
                    root.name + ": the display refused to show this body, so nothing of it was registered.", root);
                return false;
            }

            Owners.RegisterAuthored(
                fragment, root, body, shape, anchorArray != null && anchorArray.Length > 0, geometryLocalToOwner);
            Geometry.RegisterBaseGeometry(fragment, geometry, lineageToGeometryLocal);
            return true;
        }

        /// <summary>
        /// Asks for one cut, through the one driver. It is taken up in that driver's next update — or at once, if the
        /// caller is already in the update phase and calls <see cref="ProvisionalCutDriver.RequestCut"/> itself.
        /// After a shutdown nothing is accepted any more.
        /// </summary>
        public bool TryAsk(in ProvisionalCutAsk ask)
        {
            if (!IsReady || _ending || TerminationRequested || Driver == null)
            {
                return false;
            }

            Driver.Ask(in ask);
            return true;
        }

        /// <summary>
        /// Asks this world to end, and carries that ending as far as this frame allows. **It spans frames**: the
        /// first call closes acceptance, asks every accepted cut to end, stops the driver driving and closes the DAG
        /// and the cook; then this and every ordinary frame after it take one turn of the shared frame, on that
        /// frame's own id, until nothing is out any more. Nothing is polled and no frame id is invented.
        /// <para>
        /// **What frees things is a confirmed stop, not a deadline.** Once nothing is outstanding, the dispatcher is
        /// stopped and its answer decides: only when it says the workers stopped are the owners, the display, the
        /// storage and the destinations given up. If it does not, this holds everything and says so — memory a worker
        /// may still be reading is never freed to keep to a deadline.
        /// </para>
        /// <para>
        /// True once everything has been given back. A caller that must know may keep calling it on later frames, or
        /// read <see cref="IsReleased"/>; the component does it by itself while it lives.
        /// </para>
        /// </summary>
        public bool Shutdown()
        {
            // A character's OnDisable may request shutdown during the synchronous publication switch.
            // Do not release the world underneath that stack; its outer scope performs the ordinary ending.
            if (_preparedCharacterCall) { _preparedCharacterShutdown = true; return false; }
            if (_released)
            {
                return true;
            }

            if (TerminationRequested)
            {
                // The ordinary ending is not started after a Player termination request: that contract (DESIGN 4)
                // gives no guarantee of collection or release, and this would begin waiting for both.
                return false;
            }

            if (!_ending)
            {
                BeginEnding();
            }

            return CarryEnding();
        }

        /// <summary>
        /// The one-way part of an ending: nothing more is accepted, every accepted cut is asked to end, the driver
        /// stops driving -- from here the frame is carried by this component and by nothing else -- and the DAG and
        /// the cook are closed, so nothing more is offered or committed while what is running is left to finish.
        /// </summary>
        private void BeginEnding()
        {
            _ending = true;
            IsReady = false;
            Driver?.EndEveryCut();
            if (Driver != null)
            {
                Driver.enabled = false;
            }

            Geometry?.Dispose();
            Cook?.Dispose();
        }

        /// <summary>
        /// One turn of the ending on this frame, and the release when it may be done. It is called from this
        /// component's own late update and from <see cref="Shutdown"/>.
        /// </summary>
        private bool CarryEnding()
        {
            if (_released)
            {
                return true;
            }

            // One turn, on **this frame's** id. No frame id is invented, so no budget is refilled: every turn taken
            // in one frame -- this one, and one asked for by a caller in the same frame -- shares what is left of that
            // frame's budget, which collecting also spends.
            Frame?.Update(Time.frameCount);
            if (!IsDrained())
            {
                return false;
            }

            if (_stopRefused)
            {
                // Already asked once and refused. Nothing is freed for a second attempt either.
                return false;
            }

            DispatchShutdownResult stopped = Dispatcher != null
                ? Dispatcher.Shutdown(profile != null ? profile.ShutdownTimeoutMilliseconds : 0)
                : default;
            if (Dispatcher != null && !stopped.workersStopped)
            {
                _stopRefused = true;
                UnityEngine.Debug.LogError(
                    name + ": the workers did not confirm that they had stopped, so nothing this world holds has been "
                    + "freed. The storage and the shapes may still be read by them.", this);
                return false;
            }

            // What the stop handed back is taken here; after it nothing else can arrive.
            Frame?.Update(Time.frameCount);
            Release();
            return true;
        }

        private void Release()
        {
            Owners?.Dispose();
            Display?.Dispose();
            if (_ownsPaletteBinding)
            {
                // Never clear a replacement installed by a different caller. Texture assets remain caller-owned.
                if (VpCutSurfaceAtlas.Normal == normalPaletteAtlas && VpCutSurfaceAtlas.Debug == debugPaletteAtlas)
                    VpCutSurfaceAtlas.Clear();
                _ownsPaletteBinding = false;
            }
            Storage?.Dispose();
            (_geometryPool as IDisposable)?.Dispose();
            (_backgroundPool as IDisposable)?.Dispose();
            (_unityJob as IDisposable)?.Dispose();
            _released = true;
        }

        /// <summary>
        /// The termination request of DESIGN 4, made once by the first cause Main handled. The latch is fixed first;
        /// then new cut acceptance and the commit of unpublished products are closed, on this same main thread; then
        /// it is logged once, best effort; then the Player's termination API is called once.
        /// <para>
        /// **It is not an ending.** Nothing is waited for, no work is collected, nothing is freed and no fragment is
        /// retired. What becomes of the frames after it is the Player's.
        /// </para>
        /// </summary>
        private void RequestTermination(string cause)
        {
            if (TerminationRequested)
            {
                return;
            }

            // The latch, first and one way: "after the termination request" means from here, not from the call below.
            TerminationRequested = true;

            // Closed on this same main thread, before anything is logged or called: no ask is taken up any more, and
            // the commit behind the latch refuses every unpublished product from now on.
            IsReady = false;
            if (Driver != null)
            {
                Driver.enabled = false;
            }

            UnityEngine.Debug.LogError(
                name + ": the Player is being ended -- " + cause
                + ". New cuts are not accepted and no unpublished product is committed.", this);

            TerminationCalls++;
            Action terminate = terminatePlayer;
            if (terminate != null)
            {
                terminate();
                return;
            }

            Application.Quit();
        }

        /// <summary>Whether nothing of this world is still with a worker.</summary>
        public bool IsDrained()
        {
            return (Cook == null || Cook.IsDrained)
                   && (Geometry == null || Geometry.IsDrained)
                   && (Dispatcher == null || Dispatcher.SubmittedCount == 0)
                   && (Driver == null || Driver.Recovery == null || Driver.Recovery.Count == 0);
        }

        /// <summary>
        /// Where a geometry failure of this world is reported (DESIGN 4.5.6): the shared geometry of a cut failing is
        /// one of the causes of the common Player termination of DESIGN 4, so it is taken there — on the main thread,
        /// where it is handed over, and once. It is never read as a physics failure and it retires no fragment.
        /// </summary>
        private sealed class TerminatingGeometryFault : ICutGeometryFault
        {
            private readonly CutWorldRoot _root;

            internal TerminatingGeometryFault(CutWorldRoot root)
            {
                _root = root;
            }

            internal int Count { get; private set; }

            public void GeometryFailed(in CutGeometryFault fault)
            {
                Count++;
                _root.RequestTermination("the display geometry of a cut failed: " + fault);
            }
        }

        /// <summary>
        /// The display's own commit, behind this world's termination latch: once a termination has been requested, an
        /// unpublished product is not committed any more. A refusal establishes nothing and changes nothing, which is
        /// what the cut DAG already does with one.
        /// </summary>
        private sealed class LatchedCommit : ICutGeometryCommit
        {
            private readonly ICutGeometryCommit _commit;
            private readonly CutWorldRoot _root;

            internal LatchedCommit(ICutGeometryCommit commit, CutWorldRoot root)
            {
                _commit = commit;
                _root = root;
            }

            public bool TryCommit(in CutGeometryCommit commit, out CutGeometryCommitted committed)
            {
                if (_root.TerminationRequested)
                {
                    committed = default;
                    return false;
                }

                return _commit.TryCommit(in commit, out committed);
            }
        }
    }
}
