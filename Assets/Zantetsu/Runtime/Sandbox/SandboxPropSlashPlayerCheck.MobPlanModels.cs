using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Zantetsu.Core.Animation;

namespace Zantetsu.Sandbox
{
    // Measurement (the MobPlanSlash unit, a crowd of several models): what each model of the crowd went through, by the
    // model family of its slots (SandboxNpcCharacter.Family) -- its slots and their failures, the individuals it carried
    // (first or on a reused slot), whether a live individual of it was drawn, posed after its activation and a hit candidate,
    // its direct skin inputs, and the cuts of its lineages: root cuts accepted, published and committed (on a reused slot
    // too), and child cuts committed. One line per model at the end and mobplan-models.csv (written once). Only reads.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private sealed class ModelTally
            {
                public int slots, brokenSlots, individuals, reusedIndividuals, directCreations;
                public bool drawn, posed, hitTarget;
                public int rootAccepted, rootPublished, rootCommitted, reusedRootCommitted, childCommitted;
                public readonly List<string> failures = new List<string>();
                public string size;
            }

            private readonly Dictionary<string, ModelTally> _mpModels = new Dictionary<string, ModelTally>();
            private readonly Dictionary<string, (string family, bool reused)> _mpModelOfName = new Dictionary<string, (string, bool)>();
            // Which model activated or was prepared again in which frame (joined with frames.csv afterwards), and each slot's
            // counts as last seen.
            private readonly List<(int frame, string family, string kind)> _mpModelEvents = new List<(int, string, string)>();
            private readonly Dictionary<SandboxNpcCharacter, (int activations, int reprepared)> _mpSlotCounts = new Dictionary<SandboxNpcCharacter, (int, int)>();

            private ModelTally ModelOf(string family)
            {
                family ??= "?";
                if (!_mpModels.TryGetValue(family, out ModelTally tally)) _mpModels[family] = tally = new ModelTally();
                return tally;
            }

            private void MobPlanModelsAdded(string name, SandboxNpcCharacter c, bool reused)
            {
                _mpModelOfName[name] = (c.Family, reused);
                ModelTally tally = ModelOf(c.Family);
                tally.individuals++;
                if (reused) tally.reusedIndividuals++;
            }

            // Every frame: whether a live individual of each model is drawn, has had its pose applied since its activation (the
            // level of detail updates a far one less often), and is a hit candidate.
            private void MobPlanModelsFrame(int frame)
            {
                foreach (SandboxNpcCharacter c in _crowd.Slots)
                {
                    if (c == null) continue;
                    if (_mpSlotCounts.TryGetValue(c, out (int activations, int reprepared) seen))
                    {
                        if (c.Activations > seen.activations) _mpModelEvents.Add((frame, c.Family, "activate"));
                        if (c.Reprepared > seen.reprepared) _mpModelEvents.Add((frame, c.Family, "reprepare"));
                    }

                    _mpSlotCounts[c] = (c.Activations, c.Reprepared);
                    if (!c.IsTarget || c.Handle == null) continue;
                    if (!_mpActorOf.TryGetValue(c, out (int id, bool replacement, int addedFrame) a) || _mpRetired.ContainsKey(a.id)) continue;
                    ModelTally tally = ModelOf(c.Family);
                    if (!tally.drawn && c.Renderer != null && c.Renderer.enabled && c.Renderer.gameObject.activeInHierarchy) tally.drawn = true;
                    if (!tally.posed)
                    {
                        PoseTablePlayer pose = c.CharacterRoot != null ? c.CharacterRoot.GetComponent<PoseTablePlayer>() : null;
                        if (pose != null && pose.AppliedFrame >= a.addedFrame) tally.posed = true;
                    }

                    if (!tally.hitTarget && _detector.HasCharacter(c.Handle)) tally.hitTarget = true;
                    // Once per model, while drawn: the height it is drawn at (the product's unit-scale renderer, posed) beside its
                    // own display mesh's bind-pose height in world units (mesh bounds times its renderer's scale).
                    if (tally.size == null && tally.drawn && c.Renderer != null && c.Renderer.enabled)
                    {
                        SkinnedMeshRenderer source = c.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                            .FirstOrDefault(s => s != c.Renderer && s.sharedMesh != null && c.Renderer.sharedMesh != null && s.sharedMesh.name.Length > 0
                                                 && c.Renderer.name.StartsWith(s.name));
                        Vector3 drawn = c.Renderer.bounds.size;
                        Vector3 bind = source != null ? Vector3.Scale(source.sharedMesh.bounds.size, source.transform.lossyScale) : Vector3.zero;
                        tally.size = "drawn " + drawn.ToString("F3") + " m; own mesh at bind " + bind.ToString("F3") + " m (renderer scale "
                                     + (source != null ? source.transform.lossyScale.x.ToString("R") : "?") + ", yaw root " + c.CharacterRoot.transform.eulerAngles.y.ToString("F1")
                                     + " renderer " + c.Renderer.transform.eulerAngles.ToString("F1") + ")";
                        Log("mobplan model size " + c.Family + ": " + tally.size);
                    }
                }
            }

            private void MobPlanModelsEnd()
            {
                foreach (SandboxNpcCharacter c in _crowd.Slots)
                {
                    if (c == null) continue;
                    ModelTally tally = ModelOf(c.Family);
                    tally.slots++;
                    tally.directCreations += c.DirectCreations;
                    if (c.Failure != null) { tally.brokenSlots++; tally.failures.Add(c.CharacterRoot != null ? c.CharacterRoot.name + ": " + c.Failure : c.Failure); }
                }

                foreach (Accepted a in _accepted)
                {
                    string lineage = LineageOf(a.fragment);
                    if (lineage == null || !lineage.StartsWith("npc-")) continue;
                    if (!_mpModelOfName.TryGetValue(lineage.Substring(4), out (string family, bool reused) of)) continue;
                    ModelTally tally = ModelOf(of.family);
                    if (a.child)
                    {
                        if (a.committedFrame >= 0) tally.childCommitted++;
                        continue;
                    }

                    tally.rootAccepted++;
                    if (a.publishedFrame >= 0) tally.rootPublished++;
                    if (a.committedFrame >= 0) { tally.rootCommitted++; if (of.reused) tally.reusedRootCommitted++; }
                }

                var csv = new StringBuilder("family,slots,brokenSlots,individuals,reusedIndividuals,drawn,posed,hitTarget,directCreations,rootAccepted,rootPublished,rootCommitted,reusedRootCommitted,childCommitted,failures\n");
                foreach (KeyValuePair<string, ModelTally> m in _mpModels.OrderBy(p => p.Key, System.StringComparer.Ordinal))
                {
                    ModelTally t = m.Value;
                    csv.Append(string.Join(",", m.Key, t.slots, t.brokenSlots, t.individuals, t.reusedIndividuals, t.drawn ? 1 : 0, t.posed ? 1 : 0, t.hitTarget ? 1 : 0,
                        t.directCreations, t.rootAccepted, t.rootPublished, t.rootCommitted, t.reusedRootCommitted, t.childCommitted,
                        "\"" + string.Join("; ", t.failures).Replace("\"", "'") + "\"")).Append('\n');
                    if (t.size != null) Log("mobplan model " + m.Key + " size: " + t.size);
                    Log("mobplan model " + m.Key + ": slots " + t.slots + " (broken " + t.brokenSlots + ") individuals " + t.individuals + " (on a reused slot "
                        + t.reusedIndividuals + ") drawn=" + t.drawn + " posed=" + t.posed + " hitTarget=" + t.hitTarget + " directSkin=" + t.directCreations
                        + " root cuts accepted/published/committed " + t.rootAccepted + "/" + t.rootPublished + "/" + t.rootCommitted
                        + " (reused individual committed " + t.reusedRootCommitted + ") child cuts committed " + t.childCommitted
                        + (t.failures.Count > 0 ? " failures: " + string.Join("; ", t.failures) : ""));
                }

                File.WriteAllText(Path.Combine(directory, "mobplan-models.csv"), csv.ToString());
                File.WriteAllText(Path.Combine(directory, "mobplan-model-events.csv"),
                    "frame,family,kind\n" + string.Concat(_mpModelEvents.Select(e => e.frame + "," + e.family + "," + e.kind + "\n")));
                int models = _mpModels.Count(p => p.Key != "?");
                int activated = _mpModels.Count(p => p.Value.individuals > 0);
                int cut = _mpModels.Count(p => p.Value.rootCommitted > 0);
                string line = "mobplan models: " + models + " models in the slots, " + activated + " carried an individual, "
                              + _mpModels.Count(p => p.Value.drawn && p.Value.posed && p.Value.hitTarget) + " drawn, posed and a hit candidate, "
                              + cut + " cut by a real Slash through to the commit; not cut: ["
                              + string.Join(" ", _mpModels.Where(p => p.Value.rootCommitted == 0).Select(p => p.Key).OrderBy(k => k, System.StringComparer.Ordinal)) + "]";
                Log(line);
                MobPlanRecord(line);
                Expect(_mpModels.Values.All(t => t.individuals == 0 || (t.drawn && t.posed && t.hitTarget)),
                    "[scenario] every model that carried an individual had one drawn, posed after its activation and a hit candidate: "
                    + string.Join(" ", _mpModels.Where(p => p.Value.individuals > 0 && !(p.Value.drawn && p.Value.posed && p.Value.hitTarget)).Select(p => p.Key)));
            }
        }
    }
}
