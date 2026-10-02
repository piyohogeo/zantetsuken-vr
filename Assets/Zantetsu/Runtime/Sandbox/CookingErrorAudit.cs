using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The check's watch on PhysX cooking errors (2026-09-30): every "Failed to create Convex Mesh ... Source mesh name: X"
    /// and every "ConvexHullLib::cleanupVertices" error the run logs, from any thread, kept by mesh name. The check does not
    /// decide the product's success from these lines -- the cook reads the shape from a collider -- it only closes the gap
    /// that let a refused convex go by: a cooking error whose mesh is not a cut failure the cook counted and the consuming
    /// path recovered is a failure of the check.
    /// </summary>
    internal sealed class CookingErrorAudit
    {
        private const string FailedCreate = "Failed to create Convex Mesh";
        private const string MeshNameMarker = "Source mesh name: ";
        private const string CleanupVertices = "ConvexHullLib::cleanupVertices";

        private readonly object _gate = new object();
        private readonly List<string> _failedMeshes = new List<string>();
        private int _cleanupErrors;
        private bool _watching;

        /// <summary>The failed-cooking errors seen, by the mesh name each one gave (in order, repeats kept).</summary>
        public IReadOnlyList<string> FailedMeshes { get { lock (_gate) return _failedMeshes.ToArray(); } }

        /// <summary>The "less than four valid vertices" errors seen (they name no mesh; each comes with a failed-cooking error).</summary>
        public int CleanupErrors { get { lock (_gate) return _cleanupErrors; } }

        public void Begin()
        {
            if (_watching) return;
            _watching = true;
            Application.logMessageReceivedThreaded += Observe;
        }

        public void End()
        {
            if (!_watching) return;
            _watching = false;
            Application.logMessageReceivedThreaded -= Observe;
        }

        /// <summary>Takes one logged message (any thread): counts it when it is a PhysX cooking error, and keeps the mesh it names.</summary>
        internal void Observe(string message, string stack, LogType type)
        {
            if (message == null || (type != LogType.Error && type != LogType.Exception && type != LogType.Warning && type != LogType.Assert))
            {
                return;
            }

            if (message.Contains(CleanupVertices))
            {
                lock (_gate) _cleanupErrors++;
                return;
            }

            if (!message.Contains(FailedCreate))
            {
                return;
            }

            int at = message.IndexOf(MeshNameMarker, System.StringComparison.Ordinal);
            string name = at >= 0 ? message.Substring(at + MeshNameMarker.Length).Trim() : "";
            lock (_gate) _failedMeshes.Add(name);
        }

        /// <summary>What became of every cooking error at a reconciliation, by the mesh it named.</summary>
        internal struct Reconciliation
        {
            public int errors;   // the failed-cooking errors seen
            public int refused;   // their mesh was refused by the cook's check (a counted cut failure)
            public int abandonedUnchecked;   // their mesh belonged to a cut abandoned before its check (never published, given back)
            public int pending;   // their mesh belongs to a cut still being checked
            public int unresolved;   // none of these: a cooking error nothing accounts for
            public int loneCleanup;   // "less than four valid vertices" beyond the failed-cooking errors they come with
            public List<string> unresolvedNames;

            public bool Resolved => pending == 0 && unresolved == 0 && loneCleanup == 0;

            public override string ToString()
            {
                return "errors " + errors + ": refused " + refused + ", abandoned before their check " + abandonedUnchecked + ", pending " + pending + ", unresolved " + unresolved
                    + (loneCleanup > 0 ? ", lone 'less than four' " + loneCleanup : "") + (unresolvedNames != null && unresolvedNames.Count > 0 ? " [" + string.Join(", ", unresolvedNames) + "]" : "");
            }
        }

        /// <summary>Every failed-cooking error sorted by what became of its mesh, in the order refused, abandoned unchecked, pending, unresolved.</summary>
        internal Reconciliation Reconcile(System.Func<string, bool> refused, System.Func<string, bool> abandonedUnchecked, System.Func<string, bool> pending)
        {
            string[] failed;
            int cleanup;
            lock (_gate)
            {
                failed = _failedMeshes.ToArray();
                cleanup = _cleanupErrors;
            }

            var r = new Reconciliation { errors = failed.Length, unresolvedNames = new List<string>() };
            foreach (string name in failed)
            {
                if (refused != null && refused(name)) r.refused++;
                else if (abandonedUnchecked != null && abandonedUnchecked(name)) r.abandonedUnchecked++;
                else if (pending != null && pending(name)) r.pending++;
                else { r.unresolved++; if (r.unresolvedNames.Count < 8) r.unresolvedNames.Add(name); }
            }

            r.loneCleanup = cleanup > failed.Length ? cleanup - failed.Length : 0;
            return r;
        }

        /// <summary>
        /// The errors no counted cut failure accounts for: a failed-cooking error whose mesh the cook did not refuse, and
        /// the "less than four valid vertices" errors beyond the failed-cooking ones they come with.
        /// </summary>
        public int Unaccounted(System.Func<string, bool> wasRejected, List<string> which)
        {
            int unaccounted = 0;
            string[] failed;
            int cleanup;
            lock (_gate)
            {
                failed = _failedMeshes.ToArray();
                cleanup = _cleanupErrors;
            }

            foreach (string name in failed)
            {
                if (wasRejected != null && wasRejected(name)) continue;
                unaccounted++;
                which?.Add(name);
            }

            if (cleanup > failed.Length)
            {
                unaccounted += cleanup - failed.Length;
                which?.Add((cleanup - failed.Length) + " 'less than four valid vertices' without a failed-cooking error");
            }

            return unaccounted;
        }
    }
}
