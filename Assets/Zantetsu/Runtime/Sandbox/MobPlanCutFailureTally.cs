using System;
using System.Collections.Generic;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The MobPlan check's reading of the cut failures (measurement; the world's own end line is the driver's
    /// <see cref="ProvisionalCutDriver.FailureSummary"/>): the failures the driver reports, each once, by operation, and
    /// their attribution to the cuts the check accepted -- by model and root / child. The per-model counts exist only
    /// where the check runs (it is the check that knows which operation was which model's cut).
    /// </summary>
    public sealed class MobPlanCutFailureTally
    {
        public sealed class Counts
        {
            public int rootAccepted, rootFailed, childAccepted, childFailed;
            public readonly Dictionary<string, int> kinds = new Dictionary<string, int>();
        }

        private readonly Dictionary<CutOperationId, ProvisionalCutDriver.CutFailure> _failures = new Dictionary<CutOperationId, ProvisionalCutDriver.CutFailure>();
        private ProvisionalCutDriver _driver;

        /// <summary>How many failures the driver has reported to this tally (each operation once).</summary>
        public int Reported { get; private set; }

        public void Attach(ProvisionalCutDriver driver)
        {
            Detach();
            _driver = driver;
            _driver.CutFailed += OnFailed;
        }

        public void Detach()
        {
            if (_driver != null) _driver.CutFailed -= OnFailed;
            _driver = null;
        }

        private void OnFailed(ProvisionalCutDriver.CutFailure failure)
        {
            Reported++;
            _failures[failure.operation] = failure;
        }

        public int Operations => _failures.Count;

        public bool TryGet(CutOperationId operation, out ProvisionalCutDriver.CutFailure failure) => _failures.TryGetValue(operation, out failure);

        /// <summary>Whether this operation's cut failed and was aborted before the given frame.</summary>
        public bool FailedBefore(CutOperationId operation, int frame) => _failures.TryGetValue(operation, out ProvisionalCutDriver.CutFailure f) && f.frame < frame;

        public static string Kind(ProvisionalCutDriver.CutFailure f) =>
            f.outcome == PhysicsCutOutcomeKind.KernelFailed ? "KernelFailed/" + (CutStatus)f.cutStatus : f.outcome.ToString();

        /// <summary>
        /// The accepted cuts (operation, child or root, model), each counted once, against the failures: per model, root
        /// and child attempts and failures and the failures' kinds. <paramref name="unattributed"/> is how many failures
        /// match no accepted cut given.
        /// </summary>
        public Dictionary<string, Counts> Tally(IEnumerable<(CutOperationId operation, bool child, string model)> accepted, out int unattributed)
        {
            var byModel = new Dictionary<string, Counts>();
            var seen = new HashSet<CutOperationId>();
            foreach (var (operation, child, model) in accepted)
            {
                if (!seen.Add(operation)) continue;
                if (!byModel.TryGetValue(model, out Counts c)) byModel[model] = c = new Counts();
                bool failed = _failures.TryGetValue(operation, out ProvisionalCutDriver.CutFailure f);
                if (child) { c.childAccepted++; if (failed) c.childFailed++; }
                else { c.rootAccepted++; if (failed) c.rootFailed++; }
                if (failed)
                {
                    string kind = Kind(f);
                    c.kinds[kind] = c.kinds.TryGetValue(kind, out int k) ? k + 1 : 1;
                }
            }

            unattributed = 0;
            foreach (CutOperationId operation in _failures.Keys) if (!seen.Contains(operation)) unattributed++;
            return byModel;
        }
    }
}
