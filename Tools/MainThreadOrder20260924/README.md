# Main cutting diagnostics, 2026-09-24

These are diagnostic templates, not production Assets. The hard guard requires the linked worktree `zantetsuken-vr-main-thread-order-20260924`; Unity is fixed to 6000.3.22f1 and the baseline to b19ce67a. Use a fresh linked worktree with these changes, and adjust the guard deliberately for another directory. Never run the installer alongside an Editor on that same worktree.

The main report is [here](../../docs/diagnostics/phase41-main-thread-order-2026-09-24.md). Committed evidence is [here](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/). Licensed m_8 intake files remain external under `C:\log\zantetsuken-vr\Phase41ActPlayer\intake\m8`; `ORDER_INTAKE` overrides that directory. Expected input hashes are in provenance.json.

From the dedicated worktree, one build and four sequential measurements:

```powershell
python Tools/MainThreadOrder20260924/run_diagnostics.py install
python Tools/MainThreadOrder20260924/run_diagnostics.py build --output il2cpp-build2
$orderPlayer = 'Logs/MainThreadOrder20260924/il2cpp-build2/player/PlayerWithTests/PlayerWithTests.exe'
python Tools/MainThreadOrder20260924/run_diagnostics.py player --output player00 --reverse 0 --character-first 0 --player $orderPlayer
python Tools/MainThreadOrder20260924/run_diagnostics.py player --output player11 --reverse 1 --character-first 1 --player $orderPlayer
python Tools/MainThreadOrder20260924/run_diagnostics.py player --output player10 --reverse 1 --character-first 0 --player $orderPlayer
python Tools/MainThreadOrder20260924/run_diagnostics.py player --output player01 --reverse 0 --character-first 1 --player $orderPlayer
python Tools/MainThreadOrder20260924/run_diagnostics.py uninstall
python Tools/MainThreadOrder20260924/run_diagnostics.py regression --output regression2
```

Output directories must be new. On failure, inspect logs and preserve partial measurements. `uninstall` restores the exact saved Build/Publication/ProjectSettings bytes and removes only the owned temporary Assets. Runtime optimization files are never restored by that command. Unity may also regenerate URP prefilter metadata / SceneTemplateSettings; inspect and restore only those generated changes in the experimental worktree before committing.

`StandaloneResults.cs` writes NUnit results and exits only when `ORDER_STANDALONE=1`, set by the runner for independent Player launches. Normal Editor-connected test launches use Unity's own result collector. `FinalHandoffPlayModeTests` exercises actual cut and bake jobs; its duration is not included in benchmark results.

To reproduce summaries from the committed raw records:

```powershell
$orderData = 'docs/diagnostics/phase41-main-thread-order-data-2026-09-24/raw'
python Tools/MainThreadOrder20260924/summarize_order.py "$orderData/player00" "$orderData/player11" "$orderData/player10" "$orderData/player01" --output Logs/order-recomputed
python Tools/MainThreadOrder20260924/summarize_micro.py "$orderData/player00" "$orderData/player11" "$orderData/player10" "$orderData/player01" --output Logs/micro-recomputed
```

Do not pool processes or include build auto-launch/early smoke runs in the final comparison. Warmup is separate in order.csv; microbenchmark warmup batches were not recorded. Empty calibration is retained without subtraction. Order sum statistics add spans within each sample before aggregation. Thread cycles are not nanoseconds or CPU time.

`package_evidence.py` is the archival script for this session's exact run names, including earlier smoke runs and original-repository snapshots. It refuses to overwrite the evidence directory, verifies source/binary provenance and original tracked/nonignored file hashes, and copies raw bytes without transforming line endings. It is not required to recompute the summaries above.
