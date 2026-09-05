using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 fixed-capacity, single-producer /
    /// single-consumer queue primitive. No real OS, GPU, NVENC, worker, or
    /// publication is used; the payload is a small local value type.
    /// </summary>
    public class NvencFixedSpscQueueContractTests
    {
        private const int WatchdogTimeoutMs = 5000;
        private const int BoundedAttempts = 1_000_000;
        private const int ConcurrentItemCount = 32;

        private readonly struct QueuePayload
        {
            internal readonly int Seq;
            internal readonly int A;
            internal readonly int B;

            internal QueuePayload(int seq)
            {
                Seq = seq;
                A = seq * 2 + 1;
                B = 0x13579BDF + seq;
            }
        }

        [Test]
        public void NewQueue_IsEmpty_CapacityEight()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            Assert.That(queue.Capacity, Is.EqualTo(8));
            Assert.That(queue.Count, Is.EqualTo(0));
            Assert.That(queue.TryDequeue(out QueuePayload item), Is.False);
            Assert.That(item.Seq, Is.EqualTo(0));
        }

        [Test]
        public void ThreeQueueCapacityConstants_AreAllEight()
        {
            Assert.That(NvencBringUpProfileV1.SubmissionQueueCapacity, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.SubmitToOutputQueueCapacity, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.FrameCompletionQueueCapacity, Is.EqualTo(8));
            Assert.That(new NvencFixedSpscQueue<QueuePayload>().Capacity, Is.EqualTo(NvencBringUpProfileV1.SubmissionQueueCapacity));
        }

        [Test]
        public void EnqueueEight_ThenNinthFails()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryEnqueue(new QueuePayload(i)), Is.True);
            }

            Assert.That(queue.Count, Is.EqualTo(8));
            Assert.That(queue.TryEnqueue(new QueuePayload(8)), Is.False);
            Assert.That(queue.Count, Is.EqualTo(8));
        }

        [Test]
        public void DequeueEight_FifoOrder()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryEnqueue(new QueuePayload(100 + i)), Is.True);
            }

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryDequeue(out QueuePayload item), Is.True);
                Assert.That(item.Seq, Is.EqualTo(100 + i));
                Assert.That(item.A, Is.EqualTo((100 + i) * 2 + 1));
                Assert.That(item.B, Is.EqualTo(0x13579BDF + 100 + i));
            }

            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void EmptyDequeue_FailsWithDefault()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            Assert.That(queue.TryDequeue(out QueuePayload item), Is.False);
            Assert.That(item.Seq, Is.EqualTo(0));
            Assert.That(item.A, Is.EqualTo(0));
            Assert.That(item.B, Is.EqualTo(0));
        }

        [Test]
        public void DequeuedSlot_ReturnsToDefault()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            Assert.That(queue.TryEnqueue(new QueuePayload(42)), Is.True);
            Assert.That(queue.TryDequeue(out QueuePayload item), Is.True);
            Assert.That(item.Seq, Is.EqualTo(42));

            QueuePayload[] items = ReadItems(queue);
            Assert.That(items[0].Seq, Is.EqualTo(0));
            Assert.That(items[0].A, Is.EqualTo(0));
            Assert.That(items[0].B, Is.EqualTo(0));
        }

        [Test]
        public void RingIndex_WrapsMultipleTimes_OrderMaintained()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();
            int next = 0;

            for (int cycle = 0; cycle < 10; cycle++)
            {
                for (int i = 0; i < 8; i++)
                {
                    Assert.That(queue.TryEnqueue(new QueuePayload(next)), Is.True);
                    next++;
                }

                for (int i = 0; i < 8; i++)
                {
                    Assert.That(queue.TryDequeue(out QueuePayload item), Is.True);
                    Assert.That(item.Seq, Is.EqualTo(next - 8 + i));
                }

                Assert.That(queue.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public void AfterFull_OneDequeue_AllowsExactlyOneEnqueue()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryEnqueue(new QueuePayload(i)), Is.True);
            }

            Assert.That(queue.TryEnqueue(new QueuePayload(8)), Is.False);

            Assert.That(queue.TryDequeue(out QueuePayload first), Is.True);
            Assert.That(first.Seq, Is.EqualTo(0));

            Assert.That(queue.TryEnqueue(new QueuePayload(8)), Is.True);
            Assert.That(queue.TryEnqueue(new QueuePayload(9)), Is.False);
            Assert.That(queue.Count, Is.EqualTo(8));
        }

        [Test]
        public void FailedEnqueue_DoesNotChangePayloadOrPositions()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryEnqueue(new QueuePayload(100 + i)), Is.True);
            }

            long writeBefore = ReadPosition(queue, "_writePosition");
            long readBefore = ReadPosition(queue, "_readPosition");

            Assert.That(queue.TryEnqueue(new QueuePayload(999)), Is.False);

            Assert.That(ReadPosition(queue, "_writePosition"), Is.EqualTo(writeBefore));
            Assert.That(ReadPosition(queue, "_readPosition"), Is.EqualTo(readBefore));

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryDequeue(out QueuePayload item), Is.True);
                Assert.That(item.Seq, Is.EqualTo(100 + i));
            }
        }

        [Test]
        public void FailedDequeue_DoesNotChangePosition()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            long writeBefore = ReadPosition(queue, "_writePosition");
            long readBefore = ReadPosition(queue, "_readPosition");

            Assert.That(queue.TryDequeue(out QueuePayload item), Is.False);

            Assert.That(ReadPosition(queue, "_writePosition"), Is.EqualTo(writeBefore));
            Assert.That(ReadPosition(queue, "_readPosition"), Is.EqualTo(readBefore));
        }

        [Test]
        public void PositionLimit_FailsClosed_NoWrap()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();

            SetPosition(queue, "_writePosition", long.MaxValue);
            SetPosition(queue, "_readPosition", long.MaxValue - 5);

            Assert.That(queue.TryEnqueue(new QueuePayload(99)), Is.False);
            Assert.That(ReadPosition(queue, "_writePosition"), Is.EqualTo(long.MaxValue));
            Assert.That(ReadPosition(queue, "_readPosition"), Is.EqualTo(long.MaxValue - 5));

            Assert.That(queue.TryDequeue(out QueuePayload item), Is.True);
            Assert.That(ReadPosition(queue, "_readPosition"), Is.EqualTo(long.MaxValue - 4));
        }

        [Test]
        public void ConcurrentSingleProducerSingleConsumer_NoLossNoDuplicateNoReorder()
        {
            NvencFixedSpscQueue<QueuePayload> queue = new NvencFixedSpscQueue<QueuePayload>();
            QueuePayload[] received = new QueuePayload[ConcurrentItemCount];
            int producerDropped = 0;

            using (Barrier barrier = new Barrier(2))
            {
                Thread producer = new Thread(() =>
                {
                    barrier.SignalAndWait(WatchdogTimeoutMs);
                    for (int i = 0; i < ConcurrentItemCount; i++)
                    {
                        bool enqueued = false;
                        for (int attempt = 0; attempt < BoundedAttempts && !enqueued; attempt++)
                        {
                            enqueued = queue.TryEnqueue(new QueuePayload(i));
                            if (!enqueued)
                            {
                                Thread.Yield();
                            }
                        }

                        if (!enqueued)
                        {
                            producerDropped++;
                        }
                    }
                });
                producer.IsBackground = true;

                Thread consumer = new Thread(() =>
                {
                    barrier.SignalAndWait(WatchdogTimeoutMs);
                    for (int i = 0; i < ConcurrentItemCount; i++)
                    {
                        bool dequeued = false;
                        for (int attempt = 0; attempt < BoundedAttempts && !dequeued; attempt++)
                        {
                            dequeued = queue.TryDequeue(out QueuePayload item);
                            if (dequeued)
                            {
                                received[i] = item;
                            }
                            else
                            {
                                Thread.Yield();
                            }
                        }
                    }
                });
                consumer.IsBackground = true;

                producer.Start();
                consumer.Start();

                Assert.That(producer.Join(WatchdogTimeoutMs), Is.True, "Producer did not finish within the watchdog timeout.");
                Assert.That(consumer.Join(WatchdogTimeoutMs), Is.True, "Consumer did not finish within the watchdog timeout.");
            }

            Assert.That(producerDropped, Is.EqualTo(0), "The producer dropped at least one item.");
            for (int i = 0; i < ConcurrentItemCount; i++)
            {
                Assert.That(received[i].Seq, Is.EqualTo(i), "Item " + i + " was lost, duplicated, or reordered.");
                Assert.That(received[i].A, Is.EqualTo(i * 2 + 1));
                Assert.That(received[i].B, Is.EqualTo(0x13579BDF + i));
            }

            Assert.That(queue.Count, Is.EqualTo(0));
        }

        [Test]
        public void EnqueueDequeuePaths_DoNotAllocateArraysCollectionsOrDelegates()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencFixedSpscQueue.cs"));

            string[] bodies =
            {
                ExtractMethodBody(source, "TryEnqueue"),
                ExtractMethodBody(source, "TryDequeue"),
            };

            string[] forbidden =
            {
                "new long[", "new bool[", "new int[", "new byte[", "new []",
                "new List", "new Dictionary", "new Stack", "new Queue", "new HashSet",
                "Array.", "ArrayPool", ".ToArray(", ".ToList(", "Enumerable.", "Allocate",
                "delegate", "Action", "Func",
            };

            foreach (string body in bodies)
            {
                foreach (string word in forbidden)
                {
                    Assert.That(body, Does.Not.Contain(word), "Enqueue/dequeue path must not use: " + word);
                }
            }
        }

        [Test]
        public void ProductionSource_NoLockNoInterlockedNoWaitNoSleepNoTaskNoIoNoUnityNoNative()
        {
            string text = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencFixedSpscQueue.cs"));

            Assert.That(text, Does.Not.Contain("lock ("));
            Assert.That(text, Does.Not.Contain("Monitor"));
            Assert.That(text, Does.Not.Contain("Interlocked"));
            Assert.That(text, Does.Not.Contain("SpinWait"));
            Assert.That(text, Does.Not.Contain("Thread.Sleep"));
            Assert.That(text, Does.Not.Contain("ManualResetEvent"));
            Assert.That(text, Does.Not.Contain("AutoResetEvent"));
            Assert.That(text, Does.Not.Contain("WaitHandle"));
            Assert.That(text, Does.Not.Contain("new Thread"));
            Assert.That(text, Does.Not.Contain("ThreadPool"));
            Assert.That(text, Does.Not.Contain("Task"));
            Assert.That(text, Does.Not.Contain("File."));
            Assert.That(text, Does.Not.Contain("Directory."));
            Assert.That(text, Does.Not.Contain("FileStream"));
            Assert.That(text, Does.Not.Contain("DllImport"));
            Assert.That(text, Does.Not.Contain("UnityEngine"));
            Assert.That(text, Does.Not.Contain("Application."));
            Assert.That(text, Does.Not.Contain("SystemInfo"));
            Assert.That(text, Does.Not.Contain("GraphicsDevice"));
        }

        [Test]
        public void Queue_NoStaticMutableState_NoForbiddenApi_NotDisposable()
        {
            Type type = typeof(NvencFixedSpscQueue<>);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(IEnumerable).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, type.Name + "." + field.Name + " must be static readonly.");
            }

            Assert.That(type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty);

            string[] forbiddenMethods =
            {
                "Peek", "Remove", "Clear", "Resize", "Drain", "Join", "Reset", "GetEnumerator", "Dispose",
            };

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(forbiddenMethods, Does.Not.Contain(method.Name), type.Name + " must not expose " + method.Name + ".");
            }
        }

        private static QueuePayload[] ReadItems(NvencFixedSpscQueue<QueuePayload> queue)
        {
            FieldInfo field = typeof(NvencFixedSpscQueue<QueuePayload>).GetField("_items", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing field _items.");
            return (QueuePayload[])field.GetValue(queue);
        }

        private static long ReadPosition(NvencFixedSpscQueue<QueuePayload> queue, string fieldName)
        {
            FieldInfo field = typeof(NvencFixedSpscQueue<QueuePayload>).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing field " + fieldName + ".");
            return (long)field.GetValue(queue);
        }

        private static void SetPosition(NvencFixedSpscQueue<QueuePayload> queue, string fieldName, long value)
        {
            FieldInfo field = typeof(NvencFixedSpscQueue<QueuePayload>).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing field " + fieldName + ".");
            field.SetValue(queue, value);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), "Method signature not found: " + methodName);

            int open = source.IndexOf('{', signature);
            Assert.That(open, Is.GreaterThanOrEqualTo(0), "Method body not found: " + methodName);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                char c = source[i];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open + 1, i - open - 1);
                    }
                }
            }

            Assert.Fail("Unbalanced braces after " + methodName + ".");
            return string.Empty;
        }
    }
}
