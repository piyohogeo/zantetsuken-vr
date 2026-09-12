using System;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the history pages one Run keeps and for the profile
    /// that settles them: what is counted before anything is taken, what is
    /// actually held, and what is refused.
    /// </summary>
    /// <remarks>
    /// Nothing is written into a page here - there is nothing yet that writes
    /// one. These cases are about the sizes, the counts, and the two regions
    /// being each other's business rather than one release taking both.
    /// </remarks>
    public unsafe class TracePagedHistoryContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;

        private const int MaxPayloadLength = 8;
        private const int NormalDrainMaxRecordCount = 4;
        private const int PageSize = 64;
        private const int PageCount = 3;

        // -------------------------------------------------------------------
        // The framing the pages have to hold
        // -------------------------------------------------------------------

        [Test]
        public void TheHeaderIsExactlyTheTwoFieldsItSaysItIs()
        {
            Assert.That(
                UnsafeUtility.SizeOf<TraceRecordHeader>(), Is.EqualTo(TraceRecordHeader.Bytes),
                "the header size the profile counts with must be the header's real size");
        }

        // -------------------------------------------------------------------
        // What the profile counts
        // -------------------------------------------------------------------

        [Test]
        public void TheLanesAndThePagesTogetherAreTheProfilesTotal()
        {
            TraceLaneSetProfile profile = Profile(PageSize, PageCount);

            Assert.That(profile.LaneStorageBytes, Is.GreaterThan(0L));
            Assert.That(profile.HistoryStorageBytes, Is.GreaterThan(0L));
            Assert.That(
                profile.TotalStorageBytes,
                Is.EqualTo(profile.LaneStorageBytes + profile.HistoryStorageBytes),
                "the total is the lanes and the pages and nothing else");
        }

        [Test]
        public void WhatTheLanesAndThePagesHold_IsWhatTheProfileCounted()
        {
            TraceLaneSetProfile profile = Profile(PageSize, PageCount);

            TraceLaneSet lanes = new TraceLaneSet(profile);
            TracePagedHistory history = new TracePagedHistory(profile);
            try
            {
                Assert.That(
                    lanes.AllocatedBytes, Is.EqualTo(profile.LaneStorageBytes),
                    "the lane set holds the lanes' share");
                Assert.That(
                    history.AllocatedBytes, Is.EqualTo(profile.HistoryStorageBytes),
                    "the history holds the pages' share");
                Assert.That(
                    lanes.AllocatedBytes + history.AllocatedBytes,
                    Is.EqualTo(profile.TotalStorageBytes));

                Assert.That(history.PageSize, Is.EqualTo(PageSize));
                Assert.That(history.PageCount, Is.EqualTo(PageCount));
            }
            finally
            {
                history.Dispose();
                lanes.Dispose();
            }
        }

        [Test]
        public void APageJustBigEnoughForTheLongestRecordAndItsHeaderIsEnough()
        {
            int exactly = TraceRecordHeader.Bytes + MaxPayloadLength;

            TraceLaneSetProfile profile = Profile(exactly, 2);
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                Assert.That(history.PageSize, Is.EqualTo(exactly));
                Assert.That(history.AllocatedBytes, Is.EqualTo(profile.HistoryStorageBytes));
            }
        }

        [Test]
        public void APageOneByteShortIsRefusedBeforeAnythingIsTaken()
        {
            int oneShort = TraceRecordHeader.Bytes + MaxPayloadLength - 1;

            Assert.Throws<ArgumentOutOfRangeException>(
                () => Profile(oneShort, 2),
                "a page that cannot hold the longest record and its header is not a page");
        }

        [Test]
        public void APageCountOrSizeOfNothing_AndABadLane_AreAllRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Profile(PageSize, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Profile(0, PageCount));

            // The lane rules from before still hold, and are still checked
            // before anything is allocated.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength,
                    NormalDrainMaxRecordCount,
                    PageSize,
                    PageCount,
                    new[] { new TraceLaneSettings(TraceLaneEventMask.None.With(KindA), 0, 4) }));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    MaxPayloadLength,
                    NormalDrainMaxRecordCount,
                    PageSize,
                    PageCount,
                    new[] { new TraceLaneSettings(TraceLaneEventMask.None.With(KindA), 64, 0) }));
        }

        [Test]
        public void TheBiggestPagesThatCouldBeAskedFor_AreStillCountedExactly()
        {
            // Two int sizes multiplied together still fit in the 64 bits the
            // counts are kept in, so nothing can wrap: the largest page
            // geometry that describes itself consistently is counted exactly,
            // and one whose longest record would not fit a page is refused
            // before anything is taken - which is what stops an unholdable
            // figure being acted on.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new TraceLaneSetProfile(
                    int.MaxValue,
                    NormalDrainMaxRecordCount,
                    int.MaxValue,
                    int.MaxValue,
                    new[]
                    {
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(KindA), int.MaxValue, 4),
                    }),
                "the header pushes the longest record past the end of a page");

            TraceLaneSetProfile profile = new TraceLaneSetProfile(
                MaxPayloadLength,
                NormalDrainMaxRecordCount,
                int.MaxValue,
                int.MaxValue,
                new[] { new TraceLaneSettings(TraceLaneEventMask.None.With(KindA), 64, 4) });

            long expectedPages = (long)int.MaxValue * int.MaxValue;
            Assert.That(
                profile.HistoryStorageBytes - expectedPages, Is.GreaterThan(0L),
                "the pages and their counts are both in the figure");
            Assert.That(
                profile.HistoryStorageBytes, Is.GreaterThan(0L),
                "a figure this big must not have turned over");
            Assert.That(
                profile.TotalStorageBytes,
                Is.EqualTo(profile.LaneStorageBytes + profile.HistoryStorageBytes));
        }

        // -------------------------------------------------------------------
        // What the history starts as, and what releasing it does
        // -------------------------------------------------------------------

        [Test]
        public void ANewHistoryHasCommittedNothing()
        {
            TraceLaneSetProfile profile = Profile(PageSize, PageCount);
            using (TracePagedHistory history = new TracePagedHistory(profile))
            {
                Assert.That(history.CommittedRecordCount, Is.EqualTo(0L));
                for (int page = 0; page < PageCount; page++)
                {
                    Assert.That(
                        history.CommittedByteCountOf(page), Is.EqualTo(0L),
                        "page " + page + " should start empty");
                }

                Assert.Throws<ArgumentOutOfRangeException>(
                    () => history.CommittedByteCountOf(PageCount));
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => history.CommittedByteCountOf(-1));
            }
        }

        [Test]
        public void TheHistoryGivesItsPagesBackOnce_AndASecondCallIsHarmless()
        {
            TraceLaneSetProfile profile = Profile(PageSize, PageCount);
            TracePagedHistory history = new TracePagedHistory(profile);
            try
            {
                Assert.That(history.AllocatedBytes, Is.EqualTo(profile.HistoryStorageBytes));
            }
            finally
            {
                history.Dispose();
            }

            Assert.That(history.AllocatedBytes, Is.EqualTo(0L));
            Assert.Throws<ObjectDisposedException>(() => { long _ = history.CommittedRecordCount; });
            Assert.Throws<ObjectDisposedException>(() => history.CommittedByteCountOf(0));

            history.Dispose();
            Assert.That(history.AllocatedBytes, Is.EqualTo(0L));
        }

        [Test]
        public void EachOfTheTwoRegionsIsReleasedOnItsOwn()
        {
            TraceLaneSetProfile profile = Profile(PageSize, PageCount);
            TraceLaneSet lanes = new TraceLaneSet(profile);
            TracePagedHistory history = new TracePagedHistory(profile);
            try
            {
                history.Dispose();
                Assert.That(history.AllocatedBytes, Is.EqualTo(0L));
                Assert.That(
                    lanes.AllocatedBytes, Is.EqualTo(profile.LaneStorageBytes),
                    "releasing the history must not release the lanes");
                Assert.That(Write(lanes.CreateWriter(0), KindA, 1, 2), Is.True);
            }
            finally
            {
                lanes.Dispose();
                history.Dispose();
            }

            Assert.That(lanes.AllocatedBytes, Is.EqualTo(0L));

            // And the other way round: a released lane set leaves a history
            // holding its own pages.
            TraceLaneSet otherLanes = new TraceLaneSet(profile);
            TracePagedHistory otherHistory = new TracePagedHistory(profile);
            try
            {
                otherLanes.Dispose();
                Assert.That(otherLanes.AllocatedBytes, Is.EqualTo(0L));
                Assert.That(
                    otherHistory.AllocatedBytes, Is.EqualTo(profile.HistoryStorageBytes),
                    "releasing the lanes must not release the history");
                Assert.That(otherHistory.CommittedRecordCount, Is.EqualTo(0L));
            }
            finally
            {
                otherHistory.Dispose();
                otherLanes.Dispose();
            }
        }

        [Test]
        public void AHistoryNeedsAProfileToBeBuiltFrom()
        {
            Assert.Throws<ArgumentNullException>(() => new TracePagedHistory(null));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static TraceLaneSetProfile Profile(int pageSize, int pageCount)
        {
            return new TraceLaneSetProfile(
                MaxPayloadLength,
                NormalDrainMaxRecordCount,
                pageSize,
                pageCount,
                new[]
                {
                    new TraceLaneSettings(TraceLaneEventMask.None.With(KindA), 64, 4),
                    new TraceLaneSettings(TraceLaneEventMask.None.With(KindA), 32, 2),
                });
        }

        private static bool Write(TraceLaneWriter writer, TraceEventType kind, params byte[] bytes)
        {
            fixed (byte* pinned = bytes)
            {
                return writer.TryWrite(kind, pinned, bytes.Length);
            }
        }
    }
}
