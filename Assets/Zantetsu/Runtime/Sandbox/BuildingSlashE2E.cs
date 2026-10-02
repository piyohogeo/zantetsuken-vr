using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The building of the Slash-driven building E2E: one placed Megacity building (the scene builder names it and has
    /// checked that it is the asset the input was exported from) is taken into the cut world where it stands, from the
    /// author's own geometry, convex and anchors (the exported input), and the placed instance's own renderers and
    /// colliders are switched off, so that nothing of it is there twice. Nothing is cut here: the cuts come from the
    /// katana's Slashes through the product's hit detection (the Player check drives the recorded input).
    /// <para>
    /// Optionally (<c>-buildingSlashMovie &lt;dir&gt;</c>) a fixed camera that sees the whole building is saved every frame
    /// as JPEG, for a video, through the check's frame sink (CheckFrameSink: bounded, never waited for, each frame's real
    /// time kept). That run is an image run, never a timing sample.
    /// </para>
    /// </summary>
    public sealed class BuildingSlashE2E : MonoBehaviour
    {
        public const string MovieArgument = "-buildingSlashMovie";
        public const string MovieFramesArgument = "-buildingSlashMovieFrames";

        public CutWorldRoot world;
        public TextAsset input;

        /// <summary>The placed instance of the building; its pose is the actor's.</summary>
        public Transform target;

        /// <summary>The instance's own renderers and colliders, switched off when the building is taken in.</summary>
        public Renderer[] instanceRenderers = Array.Empty<Renderer>();
        public Collider[] instanceColliders = Array.Empty<Collider>();

        /// <summary>The mass the building is registered with: this test's setting, not an authored value.</summary>
        public float mass = 10000f;

        public Camera movieCamera;

        private PlacedCuttableRegistration _registration;
        private RenderTexture _movieTarget;
        private CheckFrameSink _movie;
        private Action<ScriptableRenderContext, Camera> _movieEndCamera;

        public bool IsRegistered { get; private set; }
        public LogicalFragmentId Fragment => _registration != null ? _registration.Fragment : default;
        public GameObject Actor => _registration?.Actor;

        /// <summary>The hull group the building became, when the world runs the hull trial; else null.</summary>
        public HullGroup Group => _registration?.Group;
        public int AnchorCount => _registration != null ? _registration.AnchorCount : 0;
        public string Registration { get; private set; } = "not yet";

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, MovieArgument);
            if (at >= 0 && at + 1 < args.Length && movieCamera != null)
            {
                int frames = Array.IndexOf(args, MovieFramesArgument);
                int limit = frames >= 0 && frames + 1 < args.Length && int.TryParse(args[frames + 1], out int n) ? n : 1200;
                // 960 x 540, saved by the sink away from the main thread: a capture run's own cost kept small (it still is one).
                _movieTarget = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1, name = "Building Slash Movie" };
                _movieTarget.Create();
                _movie = new CheckFrameSink("building slash movie", args[at + 1], 960, 540, limit, new CheckJpegFrameWriter(90));
                movieCamera.targetTexture = _movieTarget;
                movieCamera.enabled = true;
                _movieEndCamera = (context, camera) =>
                {
                    if (ReferenceEquals(camera, movieCamera)) _movie.Offer(_movieTarget, Time.frameCount, Time.realtimeSinceStartupAsDouble, "building");
                };
                RenderPipelineManager.endCameraRendering += _movieEndCamera;
            }
        }

        private void Update()
        {
            if (!IsRegistered && world != null && world.IsReady && !world.IsEnding)
            {
                Register();
                _movie?.StartSaving();   // the movie from the building's registration on
            }
        }

        // The shared registration of a placed cuttable (PlacedCuttableRegistration), as a building: into the hull trial when
        // the world runs it (one group, one hull; the world's profile decides), else as a body with its own convex.
        private void Register()
        {
            IsRegistered = true;
            var data = JsonUtility.FromJson<PlacedCuttableInput>(input.text);
            _registration = world.Hulls != null
                ? PlacedCuttableRegistration.RegisterHull(world, data, target, instanceRenderers, instanceColliders, mass)
                : PlacedCuttableRegistration.Register(world, data, target, instanceRenderers, instanceColliders, mass, true);
            Registration = _registration.Description;
            Debug.Log("BUILDING SLASH E2E: registered " + (world.Hulls != null ? "(hull trial) " : "") + Registration);
        }

        private void OnDestroy()
        {
            // Never a wait: the check's ending has drained the sink already; if not, it is finished from here on its own
            // frames (bounded by its deadline), and the texture is released by it once no readback of it is in flight.
            if (_movieEndCamera != null) RenderPipelineManager.endCameraRendering -= _movieEndCamera;
            if (_movie != null)
            {
                if (movieCamera != null) movieCamera.targetTexture = null;
                _movie.BeginFinish("the building's end");
                _movie.RetireSource(_movieTarget);
                _movieTarget = null;
            }

            if (world == null || world.IsReleased)
            {
                _registration?.Dispose();
            }
        }

        public int MovieFramesSaved => _movie != null ? _movie.ledger.Written : 0;
        public CheckFrameSink Movie => _movie;
    }
}
