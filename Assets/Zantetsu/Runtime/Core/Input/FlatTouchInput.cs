using UnityEngine;

namespace Zantetsu.Core.Input
{
    public enum FlatTouchPhase { Began, Moved, Ended, Cancelled }
    public readonly struct FlatTouchResult
    {
        public FlatTouchResult(bool flick, Vector2 position, Vector2 direction, bool uiTap = false)
        { Flick = flick; Position = position; Direction = direction; UiTap = uiTap; }
        public bool Flick { get; }
        public Vector2 Position { get; }
        public Vector2 Direction { get; }
        public bool UiTap { get; }
    }

    /// <summary>A finger starts on the stick, UI or play area and keeps that role until released.</summary>
    public sealed class FlatTouchInput
    {
        private enum Owner { None, Stick, Flick, Ui }
        private struct Contact
        {
            public bool active, fired;
            public int id;
            public Owner owner;
            public Vector2 start;
            public double began;
        }
        private readonly Contact[] contacts = new Contact[16];
        private int stickId = int.MinValue;
        private bool awaitingRelease;
        public Vector2 Stick { get; private set; }
        public Vector2 StickCentre { get; set; }
        public float StickRadius { get; set; } = 100f;
        public float DeadZone { get; set; } = 0.15f;
        public float MinimumFlickPixels { get; set; } = 20f;
        public float MinimumFlickPixelsPerSecond { get; set; } = 800f;
        public double MaximumFlickSeconds { get; set; } = 0.35;
        public bool AwaitingRelease => awaitingRelease;

        public void Cancel()
        {
            System.Array.Clear(contacts, 0, contacts.Length);
            stickId = int.MinValue;
            Stick = Vector2.zero;
            awaitingRelease = true;
        }
        public void EndFrame(bool anyContactHeld)
        {
            if (anyContactHeld) return;
            // A removed touch device may give no final Ended event.
            System.Array.Clear(contacts, 0, contacts.Length);
            stickId = int.MinValue;
            Stick = Vector2.zero;
            awaitingRelease = false;
        }

        public FlatTouchResult Feed(int id, FlatTouchPhase phase, Vector2 position, double seconds, bool startsOnUi = false)
        {
            if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !double.IsFinite(seconds)) return default;
            if (awaitingRelease) return default;
            int index = -1;
            for (int i = 0; i < contacts.Length; i++)
                if (contacts[i].active && contacts[i].id == id) { index = i; break; }
            if (phase == FlatTouchPhase.Began)
            {
                if (index >= 0) return default;
                for (int i = 0; i < contacts.Length; i++)
                    if (!contacts[i].active) { index = i; break; }
                if (index < 0) return default;
                Owner owner = startsOnUi ? Owner.Ui
                    : (position - StickCentre).sqrMagnitude <= StickRadius * StickRadius ? Owner.Stick : Owner.Flick;
                if (owner == Owner.Stick && stickId != int.MinValue) owner = Owner.None;
                contacts[index] = new Contact { active = true, id = id, start = position, began = seconds, owner = owner };
                if (owner == Owner.Stick) stickId = id;
            }
            if (index < 0) return default;
            ref Contact contact = ref contacts[index];
            bool ended = phase == FlatTouchPhase.Ended || phase == FlatTouchPhase.Cancelled;
            FlatTouchResult result = default;
            if (contact.owner == Owner.Stick)
            {
                Vector2 raw = Vector2.ClampMagnitude((position - StickCentre) / Mathf.Max(1f, StickRadius), 1f);
                float length = raw.magnitude;
                Stick = ended || length <= DeadZone ? Vector2.zero
                    : raw.normalized * Mathf.InverseLerp(DeadZone, 1f, length);
            }
            else if (contact.owner == Owner.Flick && !contact.fired && phase != FlatTouchPhase.Cancelled)
            {
                Vector2 delta = position - contact.start;
                double elapsed = seconds - contact.began;
                if (elapsed > 0.0 && elapsed <= MaximumFlickSeconds
                    && delta.sqrMagnitude >= MinimumFlickPixels * MinimumFlickPixels
                    && delta.magnitude / elapsed >= MinimumFlickPixelsPerSecond)
                {
                    contact.fired = true;
                    result = new FlatTouchResult(true, (contact.start + position) * 0.5f, delta.normalized);
                }
            }
            else if (contact.owner == Owner.Ui && phase == FlatTouchPhase.Ended
                && (position - contact.start).sqrMagnitude <= MinimumFlickPixels * MinimumFlickPixels * 4f)
                result = new FlatTouchResult(false, contact.start, Vector2.zero, true);
            if (ended)
            {
                if (stickId == id) stickId = int.MinValue;
                contact = default;
            }
            return result;
        }
    }
}
