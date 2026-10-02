using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevelPhysics;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // The cooked-shape audit of every check (2026-09-30): the PhysX cooking errors of the run must all be cut failures the
    // cook counted and the consuming path recovered, and no enabled convex collider in the scene may be without a cooked
    // shape. A counted and recovered cut failure is allowed, as every other counted cut failure is.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private readonly CookingErrorAudit _cookingAudit = new CookingErrorAudit();
            private PhysicsCutCook _auditCook;   // kept past the world's release for the final reconciliation

            private void CookingAuditBegin()
            {
                _cookingAudit.Begin();
            }

            /// <summary>
            /// The audit at the summary, the world still standing: every enabled convex collider's shape, the refusals'
            /// attribution, and the cooking errors as they stand -- those of cuts still being checked counted apart, not as
            /// accounted for. Their final reconciliation comes after the world's reclaim (<see cref="CookingAuditFinal"/>).
            /// </summary>
            private void CookingAuditSummary()
            {
                PhysicsCutCook cook = _world != null ? _world.Cook : null;
                _auditCook = cook;
                CookingErrorAudit.Reconciliation now = _cookingAudit.Reconcile(cook != null ? (System.Func<string, bool>)cook.WasRejected : null,
                    cook != null ? (System.Func<string, bool>)cook.WasAbandonedUnchecked : null, cook != null ? (System.Func<string, bool>)cook.IsCheckPending : null);
                int checkedColliders = 0, shapeless = 0;
                var shapelessNames = new List<string>();
                foreach (MeshCollider c in Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (!c.enabled || !c.convex || c.sharedMesh == null || !c.gameObject.activeInHierarchy)
                    {
                        continue;
                    }

                    checkedColliders++;
                    if (c.GeometryHolder.Type == GeometryType.Invalid)
                    {
                        shapeless++;
                        if (shapelessNames.Count < 8) shapelessNames.Add(c.transform.root.name + "/" + c.name + " mesh '" + c.sharedMesh.name + "'");
                    }
                }

                Log("cooking audit: PhysX cooking errors seen " + _cookingAudit.FailedMeshes.Count + " failed-cooking, " + _cookingAudit.CleanupErrors + " 'less than four valid vertices'; "
                    + (cook != null ? cook.HullSummary() + "; cuts still being checked " + cook.CutsChecking : "no cook") + "; enabled convex colliders checked " + checkedColliders + ", without a cooked shape " + shapeless
                    + (shapelessNames.Count > 0 ? " [" + string.Join(", ", shapelessNames) + "]" : ""));
                if (cook != null)
                {
                    foreach (PhysicsCutHullRejection r in cook.HullRejections) Log("cooking audit: refused " + r);
                }

                Log("cooking audit at the summary (the world standing): " + now + "; cuts still being checked " + (cook != null ? cook.CutsChecking : 0) + " (reconciled again after the world's reclaim)");
                if (_world != null && _world.Ledger != null)
                {
                    Log("ledger: incomplete cut operations now " + _world.Ledger.Budget.IncompleteCutOperationCount + ", most at once " + _world.Ledger.Budget.PeakIncompleteCutOperationCount + " of " + _world.Ledger.Budget.MaxIncompleteCutOperationCount);
                }
                Expect(shapeless == 0, "[cooking] no enabled convex collider is without a cooked shape (" + checkedColliders + " checked)");
                Expect(cook == null || cook.HullRejectedCuts == cook.AttributedHullRejections,
                    "[cooking] every cut PhysX refused was recovered by its path, once (" + (cook != null ? cook.HullRejectedCuts + " refused, " + cook.AttributedHullRejections + " attributed" : "no cook") + ")");
            }

            /// <summary>
            /// The final reconciliation, after the world's reclaim (a closing step of the common ending): every cooking
            /// error is either a refusal the cook counted and its path recovered, or a mesh of a cut abandoned before its
            /// check -- never published, its meshes gone. Anything pending or unresolved, or a refusal left unattributed,
            /// fails the check.
            /// </summary>
            private void CookingAuditFinal()
            {
                PhysicsCutCook cook = _auditCook ?? (_world != null ? _world.Cook : null);
                if (cook == null)
                {
                    Log("cooking audit after the reclaim: no cook to reconcile with (the world was never made)");
                    Expect(_cookingAudit.FailedMeshes.Count == 0 && _cookingAudit.CleanupErrors == 0, "[cooking] no PhysX cooking error without a world");
                    return;
                }

                CookingErrorAudit.Reconciliation last = _cookingAudit.Reconcile(cook.WasRejected, cook.WasAbandonedUnchecked, cook.IsCheckPending);
                int liveAbandoned = 0;
                foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>()) if (m != null && cook.WasAbandonedUnchecked(m.name)) liveAbandoned++;
                bool reclaimed = _world == null || _world.IsReleased;
                Log("cooking audit after the reclaim (world released " + reclaimed + "): " + last + "; cuts abandoned before their check " + cook.AbandonedUncheckedCuts + " (meshes " + cook.AbandonedUncheckedMeshes
                    + ", still alive " + liveAbandoned + "); refused " + cook.HullRejectedCuts + " (attributed " + cook.AttributedHullRejections + "); " + cook.HullSummary());
                Expect(last.Resolved, "[cooking] after the reclaim every PhysX cooking error is resolved: refused and recovered, or abandoned before its check (" + last + ")");
                Expect(liveAbandoned == 0 && cook.CutsChecking == 0, "[cooking] the cuts abandoned before their check left nothing behind (" + liveAbandoned + " meshes alive, " + cook.CutsChecking + " still being checked)");
                Expect(cook.HullRejectedCuts == cook.AttributedHullRejections, "[cooking] every refusal is attributed after the reclaim (" + cook.HullRejectedCuts + " refused, " + cook.AttributedHullRejections + " attributed)");
            }
        }
    }
}
