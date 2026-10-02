using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// Layer pairs lent to group cuts for the exclusion of their copies from their crossed members (TL, 2026-09-30:
    /// every copy with every crossed member's collider was 63504 IgnoreCollision pairs, 106 ms, for 252 crossed members).
    /// A pair is two unnamed user layers that collide with every layer exactly as the base layer (Default) does -- and
    /// with the other pairs' layers as the base layer does with itself -- except with each other. A group cut borrows one
    /// pair: its crossed members' collider objects go to the first layer, its copies' to the second; nothing else changes,
    /// and no two cuts share a pair. The layer matrix is the physics' own and shared by every world, so the pairs are one
    /// set for the process: the first user configures them (saving what the matrix said for those layers) and the last
    /// user puts the matrix back. Layers named in the project, 8 (Player), 30 and 31 (diagnostics), the built-in ones, and
    /// any a loaded collider stands on when the set is made are never taken.
    /// </summary>
    public static class ExclusionLayerPairs
    {
        public const int BaseLayer = 0;
        private static readonly int[] s_candidates = { 3, 6, 7, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29 };

        private static readonly List<(int a, int b)> s_pairs = new List<(int, int)>();
        private static object[] s_lentTo = Array.Empty<object>();
        private static readonly HashSet<object> s_users = new HashSet<object>();
        private static readonly Dictionary<int, bool[]> s_saved = new Dictionary<int, bool[]>();
        private static string s_excluded = "";

        /// <summary>The pairs the set holds, those lent now, the most lent at once, and the loans and returns over the process.</summary>
        public static int PairCount => s_pairs.Count;
        public static int LentNow { get { int n = 0; foreach (object o in s_lentTo) if (o != null) n++; return n; } }
        public static int MaxLent { get; private set; }
        public static long Loans { get; private set; }
        public static long Returns { get; private set; }
        public static long Refusals { get; private set; }   // asked with none free

        /// <summary>The layers the set left out and why (named, or a loaded collider stood on them), for a record.</summary>
        public static string Excluded => s_excluded;

        /// <summary>The two layers of a pair.</summary>
        public static (int a, int b) LayersOf(int pair) => s_pairs[pair];

        /// <summary>A user begins: the first one makes the set and configures the matrix for it.</summary>
        public static void Attach(object user)
        {
            if (user == null || !s_users.Add(user) || s_users.Count > 1)
            {
                return;
            }

            s_pairs.Clear();
            var used = new HashSet<int>();
            foreach (Collider c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None)) used.Add(c.gameObject.layer);
            var free = new List<int>();
            var left = new List<string>();
            foreach (int layer in s_candidates)
            {
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(layer))) { left.Add(layer + " (named " + LayerMask.LayerToName(layer) + ")"); continue; }
                if (used.Contains(layer)) { left.Add(layer + " (a loaded collider stands on it)"); continue; }
                free.Add(layer);
            }

            s_excluded = string.Join(", ", left);
            for (int i = 0; i + 1 < free.Count; i += 2) s_pairs.Add((free[i], free[i + 1]));
            s_lentTo = new object[s_pairs.Count];
            s_saved.Clear();
            foreach ((int a, int b) in s_pairs) { Save(a); Save(b); }

            // Every pair's layers as the base layer, against every layer; the pairs' layers against each other as the
            // base against itself; and each pair's two layers apart.
            bool self = Physics.GetIgnoreLayerCollision(BaseLayer, BaseLayer);
            foreach ((int a, int b) in s_pairs)
            {
                for (int layer = 0; layer < 32; layer++)
                {
                    bool ignore = IsPoolLayer(layer) ? self : Physics.GetIgnoreLayerCollision(BaseLayer, layer);
                    Physics.IgnoreLayerCollision(a, layer, ignore);
                    Physics.IgnoreLayerCollision(b, layer, ignore);
                }
            }

            foreach ((int a, int b) in s_pairs) Physics.IgnoreLayerCollision(a, b, true);
        }

        /// <summary>A user ends: its loans are returned, and the last one puts the matrix back as it was for these layers.</summary>
        public static void Detach(object user)
        {
            if (user == null || !s_users.Remove(user))
            {
                return;
            }

            for (int i = 0; i < s_lentTo.Length; i++) if (ReferenceEquals(s_lentTo[i], user)) { s_lentTo[i] = null; Returns++; }
            if (s_users.Count > 0)
            {
                return;
            }

            // Each saved row back as it was (the matrix is symmetric: a row put back puts back its column too).
            foreach (KeyValuePair<int, bool[]> e in s_saved) for (int layer = 0; layer < 32; layer++) Physics.IgnoreLayerCollision(e.Key, layer, e.Value[layer]);
            s_saved.Clear();
            s_pairs.Clear();
            s_lentTo = Array.Empty<object>();
        }

        /// <summary>A free pair lent to a user, or -1 when none is free (the caller waits).</summary>
        public static int TryLend(object user)
        {
            for (int i = 0; i < s_lentTo.Length; i++)
            {
                if (s_lentTo[i] != null) continue;
                s_lentTo[i] = user;
                Loans++;
                MaxLent = Math.Max(MaxLent, LentNow);
                return i;
            }

            Refusals++;
            return -1;
        }

        /// <summary>A pair given back by the user it was lent to.</summary>
        public static void Return(int pair, object user)
        {
            if (pair < 0 || pair >= s_lentTo.Length || !ReferenceEquals(s_lentTo[pair], user))
            {
                throw new InvalidOperationException("layer pair " + pair + " was not lent to this user");
            }

            s_lentTo[pair] = null;
            Returns++;
        }

        /// <summary>Whether a pair is lent to this user now.</summary>
        public static bool IsLentTo(int pair, object user) => pair >= 0 && pair < s_lentTo.Length && ReferenceEquals(s_lentTo[pair], user);

        private static bool IsPoolLayer(int layer)
        {
            foreach ((int a, int b) in s_pairs) if (a == layer || b == layer) return true;
            return false;
        }

        private static void Save(int layer)
        {
            var row = new bool[32];
            for (int l = 0; l < 32; l++) row[l] = Physics.GetIgnoreLayerCollision(layer, l);
            s_saved[layer] = row;
        }
    }
}
