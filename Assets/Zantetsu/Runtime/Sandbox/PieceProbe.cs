using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.XR;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// DIAGNOSIS ONLY (NPC-BONE-PROBE-1, 2026-11-04): which actor a piece is, and what the additional split last did with it. A line from
    /// the left controller (10 m, drawn every frame: green where it rests on a piece of the cut world, grey otherwise) aims; the left X
    /// button logs one line on the piece it rests on: the collider, the piece (<see cref="CutSpatialSplit.DescribePiece"/>: its actor and
    /// body, convexes and bones, NPC record, what made it, its last attempt's end and the bones' decision) and the other pieces within
    /// 0.4 m of the point -- to tell two actors lying against each other from one. The aim is a sphere of 0.1 m swept along the line, a
    /// piece before the floor or anything else; a short pulse on the left controller says it was taken. Reads only; nothing is written
    /// to the world (the line is no collider).
    /// <para>On only with the Player argument <see cref="Argument"/> (a normal launch creates nothing).</para>
    /// </summary>
    public sealed class PieceProbe : MonoBehaviour
    {
        public const string Prefix = "PIECE PROBE: ";

        /// <summary>The Player argument that turns the probe on; without it nothing is created.</summary>
        public const string Argument = "-zantetsuPieceProbe";

        private const float Reach = 10f, Radius = 0.1f;
        private static readonly InputFeatureUsage<Vector3> PointerPosition = new InputFeatureUsage<Vector3>("PointerPosition");
        private static readonly InputFeatureUsage<Quaternion> PointerRotation = new InputFeatureUsage<Quaternion>("PointerRotation");

        public static PieceProbe Instance { get; private set; }

        public static PieceProbe InstallIfAsked()
        {
            string[] arguments = System.Environment.GetCommandLineArgs();
            if (arguments == null || System.Array.IndexOf(arguments, Argument) < 0) return null;
            if (Instance != null) return Instance;
            var host = new GameObject("Zantetsu piece probe");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            Instance = host.AddComponent<PieceProbe>();
            Debug.Log(Prefix + "on (" + Argument + "): a line from the left controller aims (green on a piece), the left X button logs the piece it rests on");
            return Instance;
        }

        private CutWorldRoot _world;
        private LineRenderer _line;
        private Material _material;
        private bool _wasPressed;
        private int _probes;

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
        }

        private void Update()
        {
            if (_world == null) _world = FindFirstObjectByType<CutWorldRoot>();
            InputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            bool aimed = TryAim(left, out Vector3 origin, out Vector3 direction);
            bool onPiece = false;
            RaycastHit hit = default;
            bool found = aimed && TryTarget(origin, direction, out hit, out onPiece);
            Draw(aimed, origin, found ? hit.point : origin + (direction * Reach), onPiece);

            bool pressed = left.isValid && left.TryGetFeatureValue(CommonUsages.primaryButton, out bool down) && down;
            if (pressed && !_wasPressed) Log(left, aimed, found, hit, onPiece);
            _wasPressed = pressed;
        }

        /// <summary>The left controller's pointer as a ray of the world: tracked in the rig's tracking space (the camera's parent).</summary>
        private static bool TryAim(InputDevice left, out Vector3 origin, out Vector3 direction)
        {
            origin = default;
            direction = Vector3.forward;
            if (!left.isValid || !left.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState state)
                || (state & InputTrackingState.Position) == 0 || (state & InputTrackingState.Rotation) == 0)
            {
                return false;
            }

            if (!(left.TryGetFeatureValue(PointerPosition, out Vector3 position) && left.TryGetFeatureValue(PointerRotation, out Quaternion rotation))
                && !(left.TryGetFeatureValue(CommonUsages.devicePosition, out position) && left.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation)))
            {
                return false;
            }

            Camera eye = Camera.main;
            PlayMenuDeviceInput.ToWorld(eye != null ? eye.transform.parent : null, position, rotation, out origin, out direction);
            return true;
        }

        /// <summary>What the line rests on: the nearest piece of the cut world the swept sphere meets, else the nearest thing a thin ray meets.</summary>
        private bool TryTarget(Vector3 origin, Vector3 direction, out RaycastHit hit, out bool onPiece)
        {
            onPiece = false;
            hit = default;
            if (_world != null && _world.Owners != null)
            {
                float best = float.PositiveInfinity;
                foreach (RaycastHit h in Physics.SphereCastAll(origin, Radius, direction, Reach, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (h.rigidbody == null || h.distance >= best || !_world.Owners.TryResolveFragment(h.rigidbody, out LogicalFragmentId _, out float _)) continue;
                    best = h.distance;
                    hit = h;
                    onPiece = true;
                }

                if (onPiece)
                {
                    if (hit.point == Vector3.zero && hit.distance == 0f) hit.point = origin;   // a collider the sphere starts in
                    return true;
                }
            }

            return Physics.Raycast(origin, direction, out hit, Reach, ~0, QueryTriggerInteraction.Ignore);
        }

        private void Draw(bool aimed, Vector3 from, Vector3 to, bool onPiece)
        {
            if (_line == null)
            {
                Shader shader = Resources.Load<Shader>(PlayMenuView.ShaderResource);
                if (shader == null || !shader.isSupported) return;
                _material = new Material(shader) { name = "Zantetsu piece probe", hideFlags = HideFlags.HideAndDontSave };
                _line = gameObject.AddComponent<LineRenderer>();
                _line.sharedMaterial = _material;
                _line.positionCount = 2;
                _line.useWorldSpace = true;
                _line.widthMultiplier = 0.006f;
                _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _line.receiveShadows = false;
            }

            _line.enabled = aimed;
            if (!aimed) return;
            Color colour = onPiece ? new Color(0.2f, 1f, 0.3f, 1f) : new Color(0.6f, 0.6f, 0.6f, 1f);
            _line.startColor = colour;
            _line.endColor = colour;
            _line.SetPosition(0, from);
            _line.SetPosition(1, to);
        }

        private void Log(InputDevice left, bool aimed, bool found, RaycastHit hit, bool onPiece)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            _probes++;
            var text = new StringBuilder(1024);
            text.Append(Prefix).Append('#').Append(_probes).Append(" frame ").Append(Time.frameCount).Append(", ").Append(Time.realtimeSinceStartup.ToString("F1", inv)).Append(" s");
            if (_world == null || _world.SpatialSplit == null || _world.Owners == null)
            {
                Debug.Log(text.Append(": no cut world").ToString());
                return;
            }

            if (!aimed)
            {
                Debug.Log(text.Append(": the left controller is not tracked").ToString());
                return;
            }

            if (!found)
            {
                Debug.Log(text.Append(": nothing within ").Append(Reach.ToString("F0", inv)).Append(" m along the line").ToString());
                left.SendHapticImpulse(0, 0.2f, 0.05f);
                return;
            }

            Vector3 at = hit.point;
            text.Append(": ").Append(onPiece ? "a piece" : "not a piece").Append(", hit ").Append(hit.collider != null ? hit.collider.name : "?").Append(" at (")
                .Append(at.x.ToString("F2", inv)).Append(", ").Append(at.y.ToString("F2", inv)).Append(", ").Append(at.z.ToString("F2", inv)).Append("), ")
                .Append(hit.distance.ToString("F2", inv)).Append(" m along the line; ");
            if (onPiece && _world.Owners.TryResolveFragment(hit.rigidbody, out LogicalFragmentId fragment, out float _))
            {
                text.Append(_world.SpatialSplit.DescribePiece(fragment));
            }
            else
            {
                text.Append("not a piece of the cut world");
            }

            text.Append("; the pieces within 0.4 m:");
            var seen = new HashSet<int>();
            foreach (Collider near in Physics.OverlapSphere(at, 0.4f, ~0, QueryTriggerInteraction.Ignore))
            {
                Rigidbody body = near.attachedRigidbody;
                if (body == null || !seen.Add(body.GetInstanceID())) continue;
                if (!_world.Owners.TryResolveFragment(body, out LogicalFragmentId other, out float _)) continue;
                text.Append(' ').Append(other.value).Append(" (body #").Append(body.GetInstanceID().ToString(inv)).Append(_world.NpcBones != null && _world.NpcBones.Knows(other) ? ", an NPC's)" : ")");
            }

            Debug.Log(text.ToString());
            left.SendHapticImpulse(0, 0.5f, 0.08f);
        }
    }
}
