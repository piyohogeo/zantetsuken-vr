using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.Core.Slash;

namespace Zantetsu.Sandbox
{
    // Image evidence (the MobPlanSlash unit, a crowd of several models; "-zantetsuModelCloseups" only, never a timing run): close
    // pictures of the models of one family (-zantetsuModelCloseups <family prefix>, e.g. "professional-") as the product draws
    // them -- once walking, when one first stands within 6 m of the player, and after a real Slash cuts one, 2 and 60 frames
    // after the hit (its pieces) -- from a camera of the check's own (not the XR one), 3 m from the character, at 1.2 m,
    // registered with the display for the run and placed a frame before it renders.
    // PNGs in <run>/closeups/. Only reads, apart from that camera.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private string _mpCloseupFamily;
            private Camera _mpCloseupCamera;
            private RenderTexture _mpCloseupTarget;
            private readonly HashSet<string> _mpCloseupWalking = new HashSet<string>();
            private int _mpCloseupCuts;

            private void MobPlanCloseupsBegin()
            {
                string[] args = Environment.GetCommandLineArgs();
                int at = Array.IndexOf(args, "-zantetsuModelCloseups");
                if (at < 0) return;
                _mpCloseupFamily = at + 1 < args.Length ? args[at + 1] : "professional-";
                Directory.CreateDirectory(Path.Combine(directory, "closeups"));
                var go = new GameObject("Check Closeup Camera");
                _mpCloseupCamera = go.AddComponent<Camera>();
                _mpCloseupCamera.enabled = false;
                _mpCloseupCamera.stereoTargetEye = StereoTargetEyeMask.None;
                _mpCloseupCamera.fieldOfView = 45f;
                _mpCloseupCamera.nearClipPlane = 0.05f;
                _mpCloseupTarget = new RenderTexture(900, 900, 24);
                _mpCloseupCamera.targetTexture = _mpCloseupTarget;
                // The cut pieces are the display's: it draws for the cameras its camera drawing lists and the display took.
                // This camera is added to both, for the image run only.
                var drawing = UnityEngine.Object.FindFirstObjectByType<Zantetsu.PhysicsCut.CutWorldCameraDrawing>();
                System.Reflection.FieldInfo field = typeof(Zantetsu.PhysicsCut.CutWorldCameraDrawing).GetField("cameras",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                bool listed = false, taken = false;
                if (drawing != null && field != null)
                {
                    var cameras = (Camera[])field.GetValue(drawing);
                    field.SetValue(drawing, cameras.Concat(new[] { _mpCloseupCamera }).ToArray());
                    listed = true;
                }

                taken = _world.Display.TryRegisterCamera(_mpCloseupCamera);
                _mpCloseupCallback = CloseupAtRender;
                Application.onBeforeRender += _mpCloseupCallback;
                Log("mobplan closeups: family " + _mpCloseupFamily + " camera listed=" + listed + " taken by the display=" + taken);
            }

            // One job at a time on the one camera: it is placed when the job starts and renders only from the next frame on,
            // so the display has culled for where it stands (a camera moved and rendered in the same frame is culled for
            // where it stood before).
            private sealed class CloseupJob { public Vector3 at, facing; public string name; public List<int> due = new List<int>(); public int placedFrame = -1, hitFrame; }
            private readonly List<CloseupJob> _mpCloseupJobs = new List<CloseupJob>();

            private void MobPlanCloseupsFrame(int frame)
            {
                if (_mpCloseupCamera == null) return;
                Vector3 player = _mpInput.player.transform.position;
                foreach (SandboxNpcCharacter c in _crowd.Slots)
                {
                    if (c == null || c.Family == null || !c.Family.StartsWith(_mpCloseupFamily) || _mpCloseupWalking.Contains(c.Family)) continue;
                    if (!c.IsTarget || c.Renderer == null || !c.Renderer.enabled || c.CharacterRoot == null || _mpCloseupJobs.Count > 2) continue;
                    Vector3 p = c.CharacterRoot.transform.position;
                    if (Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(player.x, 0f, player.z)) > 6f) continue;
                    _mpCloseupWalking.Add(c.Family);
                    var walking = new CloseupJob { at = p, facing = c.CharacterRoot.transform.forward, name = c.Family + "-walking-f" + frame };
                    walking.due.Add(-1);   // the frame after it is placed
                    _mpCloseupJobs.Add(walking);
                }

                if (_mpCloseupJobs.Count == 0) return;
                CloseupJob job = _mpCloseupJobs[0];
                if (job.placedFrame < 0)
                {
                    Place(job.at, job.facing);
                    job.placedFrame = frame;
                    return;
                }

                int next = job.due[0] < 0 ? job.placedFrame + 1 : job.due[0];
                if (frame < next || frame <= job.placedFrame) return;
                string suffix = job.due[0] < 0 ? "" : "-d" + (job.due[0] - job.hitFrame);
                _mpCloseupPending = job.name + suffix + "-f" + frame;   // rendered at this frame's onBeforeRender
                job.due.RemoveAt(0);
                if (job.due.Count == 0) _mpCloseupJobs.RemoveAt(0);
            }

            // A root hit on a character of the family: its pieces 2 and 60 frames later, seen from where it faced.
            private void MobPlanCloseupsHit(SandboxNpcCharacter c, int frame)
            {
                if (_mpCloseupCamera == null || c.Family == null || !c.Family.StartsWith(_mpCloseupFamily) || _mpCloseupCuts >= 8 || _mpCloseupJobs.Count > 0) return;
                _mpCloseupCuts++;
                var job = new CloseupJob { at = c.CharacterRoot.transform.position, facing = c.CharacterRoot.transform.forward,
                                           name = c.Family + "-cut" + _mpCloseupCuts + "-hit" + frame, hitFrame = frame };
                job.due.Add(frame + 2);
                job.due.Add(frame + 60);
                Place(job.at, job.facing);
                job.placedFrame = frame;
                _mpCloseupJobs.Add(job);
            }

            // The display draws only while its frame is open (after this frame's collection has settled): the camera is
            // rendered just before the frame's rendering, as the XR camera is.
            private string _mpCloseupPending;
            private UnityEngine.Events.UnityAction _mpCloseupCallback;

            private void CloseupAtRender()
            {
                if (_mpCloseupPending == null || _mpCloseupCamera == null) return;
                string name = _mpCloseupPending;
                _mpCloseupPending = null;
                Render(name + (_world.Display.IsFrameOpen ? "" : "-frameNotOpen"));
            }

            private void MobPlanCloseupsClose()
            {
                if (_mpCloseupCallback != null) Application.onBeforeRender -= _mpCloseupCallback;
                _mpCloseupCallback = null;
                if (_mpCloseupCamera != null && _world != null && _world.Display != null && !_world.Display.IsDisposed) _world.Display.TryUnregisterCamera(_mpCloseupCamera);
            }

            private void Place(Vector3 at, Vector3 facing)
            {
                facing.y = 0f;
                if (facing.sqrMagnitude < 1e-6f) facing = Vector3.forward;
                facing.Normalize();
                _mpCloseupCamera.transform.position = at + facing * 3f + Vector3.up * 1.2f;
                _mpCloseupCamera.transform.LookAt(at + Vector3.up * 0.9f);
            }

            private void Render(string name)
            {
                _mpCloseupCamera.Render();
                RenderTexture.active = _mpCloseupTarget;
                var picture = new Texture2D(_mpCloseupTarget.width, _mpCloseupTarget.height, TextureFormat.RGB24, false);
                picture.ReadPixels(new Rect(0, 0, _mpCloseupTarget.width, _mpCloseupTarget.height), 0, 0);
                picture.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes(Path.Combine(directory, "closeups", name + ".png"), picture.EncodeToPNG());
                UnityEngine.Object.Destroy(picture);
            }
        }
    }
}
