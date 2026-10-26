#if ZANTETSU_ANDROID_COLLECTION_PROBE && UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>Isolated Release probe: reads existing counters; an explicit launch extra permits a budget-only comparison.</summary>
    [DefaultExecutionOrder(32000)]
    public sealed class AndroidFlatCollectionProbe : MonoBehaviour
    {
        private CutWorldRoot world;
        private CutFragmentGroupCollection groups;
        private Func<double> remaining;
        private bool unlimited, launchUnlimited; private int normalThreshold = -1, blockThreshold = -1; private bool safetyEnabled = true;
        private float next;
        private int epoch;
        private int peakLive;
        private long asks, negative;
        private double sum, minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;

        [Serializable]
        private sealed class Sample
        {
            public int epoch, frame, live, peakLive, threshold, known, comingBack, turns, walked, examined, candidates;
            public int groupsRetired, fragmentsRetired, piecesRetired, piecesLookedAt, piecesSeen, piecesNotReady;
            public int freeBlocks, blockCapacity;
            public int[] holds, passes;
            public bool unlimitedBudget, safetyEnabled;
            public int safetyReason, fragmentSafetyThreshold, blockSafetyThreshold, safetyTurns, safetyBlocked, safetyGroupsCompleted, safetyPiecesRetired, maxPieceChecks, safetyEventsDropped;
            public double maxSafetyRetireMs;
            public int[] piecePasses;
            public float seconds;
            public long budgetAsks, negativeBudgetAsks;
            public double minimumRemainingMs, maximumRemainingMs, meanRemainingMs, collectExpectedMs, oneFragmentExpectedMs;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartProbe()
        {
            if (Application.identifier != "com.zantetsuken.collectionprobe") throw new InvalidOperationException("Collection probe package mismatch");
            var probe = new GameObject("Collection measurement only").AddComponent<AndroidFlatCollectionProbe>();
            DontDestroyOnLoad(probe.gameObject);
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
            {
                probe.launchUnlimited = probe.unlimited = intent.Call<bool>("getBooleanExtra", "collectionProbeUnlimitedBudget", false);
                probe.normalThreshold = intent.Call<int>("getIntExtra", "collectionProbeNormalThreshold", -1);
                probe.blockThreshold = intent.Call<int>("getIntExtra", "collectionProbeBlockThreshold", -1);
                probe.safetyEnabled = intent.Call<bool>("getBooleanExtra", "collectionProbeSafetyEnabled", true);
            }
            Debug.Log("ANDROID COLLECTION PROBE start: unlimitedBudget=" + probe.unlimited + "; statelessSafety=" + probe.safetyEnabled
                + "; normalOverride=" + probe.normalThreshold + "; blockOverride=" + probe.blockThreshold
                + "; holds=" + string.Join(",", Enum.GetNames(typeof(CutGroupHold)))
                + "; passes=" + string.Join(",", Enum.GetNames(typeof(CutGroupPass)))
                + "; isolated Release IL2CPP; safety reads current counts only; lifetime checks preserved");
        }

        private double ReadRemaining()
        {
            double value = remaining();
            asks++; if (value < 0) negative++;
            sum += value; minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value);
            return unlimited ? 3600.0 : value;
        }

        private void LateUpdate()
        {
            if (world == null) world = FindFirstObjectByType<CutWorldRoot>();
            if (world == null || world.Groups == null || world.Ledger == null || world.Storage == null) return;
            if (!ReferenceEquals(groups, world.Groups))
            {
                groups = world.Groups; remaining = groups.RemainingMainSeconds; groups.RemainingMainSeconds = ReadRemaining;
                var settings = groups.Settings;
                groups.Settings = new CutFragmentGroupSettings(settings.enabled,
                    normalThreshold >= 0 ? normalThreshold : settings.maxLiveCutFragments,
                    settings.examinePerFrame, settings.reserveSeconds, settings.seedSecondsPerFragment, settings.seedSecondsPerGroup,
                    safetyEnabled, blockThreshold > 0 ? blockThreshold : settings.safetyVertexBlockThreshold);
                epoch++; peakLive = 0; asks = negative = 0; sum = 0;
                minimum = double.PositiveInfinity; maximum = double.NegativeInfinity; next = 0;
            }
            peakLive = Math.Max(peakLive, world.Ledger.LiveCutFragmentCount);
            if (Time.realtimeSinceStartup < next) return;
            next = Time.realtimeSinceStartup + 10f;
            bool nextMode = launchUnlimited || System.IO.File.Exists(System.IO.Path.Combine(Application.persistentDataPath, "collection-probe-unlimited"));
            if (nextMode != unlimited)
            {
                unlimited = nextMode;
                Debug.Log("ANDROID COLLECTION PROBE budget mode changed: unlimitedBudget=" + unlimited);
            }
            Record();
        }

        private void Record()
        {
            if (world == null || groups == null || world.Ledger == null || world.Storage == null) return;
            foreach (var item in groups.SafetyEvents)
                Debug.Log("ANDROID COLLECTION PROBE safety " + JsonUtility.ToJson(item));
            groups.ClearSafetyEvents();
            var piecePasses = new int[(int)CutGroupPass.Count];
            for (int i = 0; i < piecePasses.Length; i++) piecePasses[i] = groups.SafetyPassedAs((CutGroupPass)i);
            var holds = new int[(int)CutGroupHold.Count];
            var passes = new int[(int)CutGroupPass.Count];
            for (int i = 0; i < holds.Length; i++) holds[i] = groups.HeldFor((CutGroupHold)i);
            for (int i = 0; i < passes.Length; i++) passes[i] = groups.PassedAs((CutGroupPass)i);
            var sample = new Sample {
                epoch = epoch, frame = Time.frameCount, seconds = Time.realtimeSinceStartup,
                live = world.Ledger.LiveCutFragmentCount, peakLive = peakLive, threshold = groups.Settings.maxLiveCutFragments,
                known = groups.GroupCount, comingBack = groups.ComingBack, turns = groups.Turns, walked = groups.WalkedTurns,
                examined = groups.Examined, candidates = groups.Candidates,
                groupsRetired = groups.GroupsRetired, fragmentsRetired = groups.FragmentsRetired,
                piecesRetired = groups.PiecesRetired, piecesLookedAt = groups.PiecesLookedAt,
                piecesSeen = groups.PiecesSeen, piecesNotReady = groups.PiecesNotReady,
                freeBlocks = world.Storage.FreeVertexBlockRoom, blockCapacity = world.Storage.VertexBlockCapacity,
                holds = holds, passes = passes, unlimitedBudget = unlimited,
                safetyEnabled = groups.Settings.safetyEnabled, safetyReason = (int)groups.SafetyReason,
                fragmentSafetyThreshold = groups.Settings.SafetyFragmentThreshold, blockSafetyThreshold = groups.Settings.safetyVertexBlockThreshold,
                safetyTurns = groups.SafetyTurns, safetyBlocked = groups.SafetyBlockedTurns,
                safetyGroupsCompleted = groups.SafetyGroupsCompleted, safetyPiecesRetired = groups.SafetyPiecesRetired,
                maxPieceChecks = groups.SafetyMaxPieceChecks, piecePasses = piecePasses,
                safetyEventsDropped = groups.SafetyEventsDropped, maxSafetyRetireMs = groups.SafetyMaxRetireMs,
                budgetAsks = asks, negativeBudgetAsks = negative, minimumRemainingMs = asks == 0 ? 0 : minimum * 1000,
                maximumRemainingMs = asks == 0 ? 0 : maximum * 1000, meanRemainingMs = asks == 0 ? 0 : sum * 1000 / asks,
                collectExpectedMs = groups.ExpectedCollectAfterSeconds * 1000,
                oneFragmentExpectedMs = groups.ExpectedSeconds(1) * 1000
            };
            string json = JsonUtility.ToJson(sample);
            const int chunkSize = 600; // Android's log transport may truncate long messages.
            int count = (json.Length + chunkSize - 1) / chunkSize;
            for (int part = 0; part < count; part++)
            {
                int start = part * chunkSize;
                Debug.Log("ANDROID COLLECTION PROBE sample-chunk " + epoch + ":" + Time.frameCount + ":" + part + "/" + count
                    + " " + json.Substring(start, Math.Min(chunkSize, json.Length - start)));
            }
            Debug.Log("ANDROID COLLECTION PROBE storage " + world.Storage.DescribeRoom());
        }

        private void OnApplicationQuit() { Record(); }
    }
}
#endif
