using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Submit-to-Output exclusive record
    /// boundary. No real OS, GPU, NVENC, worker, or native resource is used.
    /// </summary>
    public class NvencSubmitToOutputRecordContractTests
    {
        [Test]
        public void DefaultRecord_IsInvalid()
        {
            NvencSubmitToOutputRecord record = default;

            Assert.That(record.IsValid, Is.False);
            Assert.That(record.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.None));
            Assert.That(record.Reason, Is.EqualTo(NvencFailedBeforeSubmitReason.None));
        }

        [Test]
        public void Submitted_ValidAndForwardsAll()
        {
            RentLeases(
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease);
            CaptureFrameWorkToken token = MakeToken(3, 7);

            NvencSubmitToOutputRecord record =
                NvencSubmitToOutputRecord.CreateSubmitted(token, workLease, sampleLease);

            Assert.That(record.IsValid, Is.True);
            Assert.That(record.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.Submitted));
            Assert.That(record.Reason, Is.EqualTo(NvencFailedBeforeSubmitReason.None));
            Assert.That(record.WorkToken.IdenticalTo(token), Is.True);
            Assert.That(record.WorkToken.CaptureFrameId, Is.EqualTo(7));
            Assert.That(record.WorkSlot.SlotIndex, Is.EqualTo(workLease.SlotIndex));
            Assert.That(record.WorkSlot.Generation, Is.EqualTo(workLease.Generation));
            Assert.That(record.SampleSlot.SlotIndex, Is.EqualTo(sampleLease.SlotIndex));
            Assert.That(record.SampleSlot.Generation, Is.EqualTo(sampleLease.Generation));
            Assert.That(record.IsValidFor(workPool, samplePool), Is.True);
        }

        [Test]
        public void FailedBeforeSubmit_EachDefinedReason()
        {
            RentLeases(
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease);
            CaptureFrameWorkToken token = MakeToken(0, 1);

            NvencFailedBeforeSubmitReason[] reasons =
            {
                NvencFailedBeforeSubmitReason.GpuConversionFailed,
                NvencFailedBeforeSubmitReason.NvencSubmitFailed,
                NvencFailedBeforeSubmitReason.CancelledBeforeSubmit,
                NvencFailedBeforeSubmitReason.CancelledAfterRunAbandoned,
                NvencFailedBeforeSubmitReason.DrainedBeforeSubmit,
            };

            foreach (NvencFailedBeforeSubmitReason reason in reasons)
            {
                NvencSubmitToOutputRecord record =
                    NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(token, workLease, sampleLease, reason);

                Assert.That(record.IsValid, Is.True);
                Assert.That(record.Kind, Is.EqualTo(NvencSubmitToOutputRecordKind.FailedBeforeSubmit));
                Assert.That(record.Reason, Is.EqualTo(reason));
                Assert.That(record.WorkToken.IdenticalTo(token), Is.True);
            }
        }

        [Test]
        public void Submitted_CannotCarryNonNoneReason()
        {
            MethodInfo createSubmitted = typeof(NvencSubmitToOutputRecord).GetMethod(
                "CreateSubmitted", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(createSubmitted, Is.Not.Null);
            Assert.That(createSubmitted.GetParameters().Length, Is.EqualTo(3));

            foreach (ConstructorInfo constructor in typeof(NvencSubmitToOutputRecord).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(constructor.IsPrivate, Is.True, "The record must not expose a public or internal constructor.");
            }
        }

        [Test]
        public void FailedBeforeSubmit_RejectsNoneAndUndefinedReason()
        {
            RentLeases(
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease);
            CaptureFrameWorkToken token = MakeToken(0, 1);

            Assert.Throws<ArgumentException>(() =>
                NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, workLease, sampleLease, NvencFailedBeforeSubmitReason.None));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, workLease, sampleLease, (NvencFailedBeforeSubmitReason)999));
        }

        [Test]
        public void Factories_RejectInvalidTokenAndLeases()
        {
            RentLeases(
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease);
            CaptureFrameWorkToken token = MakeToken(0, 1);

            Assert.Throws<ArgumentException>(() =>
                NvencSubmitToOutputRecord.CreateSubmitted(default, workLease, sampleLease));
            Assert.Throws<ArgumentException>(() =>
                NvencSubmitToOutputRecord.CreateSubmitted(token, default, sampleLease));
            Assert.Throws<ArgumentException>(() =>
                NvencSubmitToOutputRecord.CreateSubmitted(token, workLease, default));

            Assert.Throws<ArgumentException>(() =>
                NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    default, workLease, sampleLease, NvencFailedBeforeSubmitReason.GpuConversionFailed));
            Assert.Throws<ArgumentException>(() =>
                NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, default, sampleLease, NvencFailedBeforeSubmitReason.GpuConversionFailed));
            Assert.Throws<ArgumentException>(() =>
                NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, workLease, default, NvencFailedBeforeSubmitReason.GpuConversionFailed));
        }

        [Test]
        public void IsValidFor_ExactPools_True()
        {
            RentLeases(
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease);
            NvencSubmitToOutputRecord record =
                NvencSubmitToOutputRecord.CreateSubmitted(MakeToken(0, 1), workLease, sampleLease);

            Assert.That(record.IsValidFor(workPool, samplePool), Is.True);
        }

        [Test]
        public void IsValidFor_ForeignPool_False()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workA = new NvencCaptureWorkSlotPool(state);
            NvencCaptureWorkSlotPool workB = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool sampleA = new NvencEncodeSampleSlotPool(state);
            NvencEncodeSampleSlotPool sampleB = new NvencEncodeSampleSlotPool(state);

            Assert.That(workA.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
            Assert.That(sampleA.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);
            NvencSubmitToOutputRecord record =
                NvencSubmitToOutputRecord.CreateSubmitted(MakeToken(0, 1), workLease, sampleLease);

            Assert.That(record.IsValidFor(workA, sampleA), Is.True);
            Assert.That(record.IsValidFor(workB, sampleA), Is.False);
            Assert.That(record.IsValidFor(workA, sampleB), Is.False);
            Assert.That(record.IsValidFor(workB, sampleB), Is.False);
        }

        [Test]
        public void IsValidFor_AfterReturn_False()
        {
            RentLeases(
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease);
            NvencSubmitToOutputRecord record =
                NvencSubmitToOutputRecord.CreateSubmitted(MakeToken(0, 1), workLease, sampleLease);

            Assert.That(workPool.TryReturn(workLease), Is.True);
            Assert.That(record.IsValidFor(workPool, samplePool), Is.False);
        }

        [Test]
        public void IsValidFor_AfterSlotRerent_Stale_False()
        {
            RentLeases(
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease);
            NvencSubmitToOutputRecord record =
                NvencSubmitToOutputRecord.CreateSubmitted(MakeToken(0, 1), workLease, sampleLease);

            Assert.That(workPool.TryReturn(workLease), Is.True);
            Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease rerented), Is.True);
            Assert.That(rerented.SlotIndex, Is.EqualTo(workLease.SlotIndex));

            Assert.That(record.IsValidFor(workPool, samplePool), Is.False);
        }

        [Test]
        public void VariantMismatchAndUndefinedValues_AreInvalid()
        {
            RentLeases(
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease);
            CaptureFrameWorkToken token = MakeToken(0, 1);

            NvencSubmitToOutputRecord submitted =
                NvencSubmitToOutputRecord.CreateSubmitted(token, workLease, sampleLease);
            Assert.That(submitted.IsValid, Is.True);
            Assert.That(
                WithField(submitted, "_reason", NvencFailedBeforeSubmitReason.GpuConversionFailed).IsValid,
                Is.False);

            NvencSubmitToOutputRecord failed = NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                token, workLease, sampleLease, NvencFailedBeforeSubmitReason.GpuConversionFailed);
            Assert.That(failed.IsValid, Is.True);
            Assert.That(WithField(failed, "_reason", NvencFailedBeforeSubmitReason.None).IsValid, Is.False);
            Assert.That(WithField(failed, "_kind", NvencSubmitToOutputRecordKind.None).IsValid, Is.False);
            Assert.That(WithField(failed, "_kind", (NvencSubmitToOutputRecordKind)999).IsValid, Is.False);
            Assert.That(WithField(failed, "_reason", (NvencFailedBeforeSubmitReason)999).IsValid, Is.False);
        }

        [Test]
        public void MixedRecords_EnqueueAndDequeue_FifoOrder()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);

            NvencSubmitToOutputRecord[] records = new NvencSubmitToOutputRecord[8];
            for (int i = 0; i < 8; i++)
            {
                Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
                Assert.That(samplePool.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);
                CaptureFrameWorkToken token = MakeToken(i, i + 1);

                if (i % 2 == 0)
                {
                    records[i] = NvencSubmitToOutputRecord.CreateSubmitted(token, workLease, sampleLease);
                }
                else
                {
                    records[i] = NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                        token, workLease, sampleLease, (NvencFailedBeforeSubmitReason)((i % 5) + 1));
                }
            }

            NvencFixedSpscQueue<NvencSubmitToOutputRecord> queue =
                new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryEnqueue(records[i]), Is.True);
            }

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryDequeue(out NvencSubmitToOutputRecord record), Is.True);
                Assert.That(record.Kind, Is.EqualTo(records[i].Kind));
                Assert.That(record.Reason, Is.EqualTo(records[i].Reason));
                Assert.That(record.WorkToken.IdenticalTo(records[i].WorkToken), Is.True);
            }
        }

        [Test]
        public void Record_IsReadonlyValueType_NotDisposable()
        {
            Type type = typeof(NvencSubmitToOutputRecord);

            Assert.That(type.IsValueType, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Record_HoldsOnlyValueTypeFields_NoCollectionsHandlesOrThreading()
        {
            Type type = typeof(NvencSubmitToOutputRecord);
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(5));

            Type[] expected =
            {
                typeof(CaptureFrameWorkToken),
                typeof(NvencCaptureWorkSlotLease),
                typeof(NvencEncodeSampleSlotLease),
                typeof(NvencSubmitToOutputRecordKind),
                typeof(NvencFailedBeforeSubmitReason),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.FieldType.IsValueType, Is.True, field.Name + " must be a value type.");
                Assert.That(expected, Does.Contain(field.FieldType));
            }

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencSubmitToOutputRecord.cs"));
            Assert.That(source, Does.Not.Contain("IntPtr"));
            Assert.That(source, Does.Not.Contain("SafeHandle"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("NvEnc"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("new Thread"));
            Assert.That(source, Does.Not.Contain("ThreadPool"));
            Assert.That(source, Does.Not.Contain("Thread.Sleep"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("lock ("));
            Assert.That(source, Does.Not.Contain("Monitor"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("new []"));
            Assert.That(source, Does.Not.Contain("new List"));
            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain("new byte["));
        }

        [Test]
        public void Record_HasNoSortingApiOrSequenceTimestamp()
        {
            Type type = typeof(NvencSubmitToOutputRecord);

            Assert.That(typeof(IComparable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(IComparable<NvencSubmitToOutputRecord>).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.Name, Does.Not.Contain("Sequence"));
                Assert.That(field.Name, Does.Not.Contain("Timestamp"));
                Assert.That(field.Name, Does.Not.Contain("Sort"));
            }

            string[] forbiddenNames = { "CompareTo", "Sort", "OrderBy", "ThenBy" };
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(forbiddenNames, Does.Not.Contain(method.Name), "The record must not expose " + method.Name + ".");
            }
        }

        private static CaptureFrameWorkToken MakeToken(int slotIndex, long captureFrameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), slotIndex, 1, 1, captureFrameId);
        }

        private static void RentLeases(
            out NvencCaptureWorkSlotPool workPool,
            out NvencEncodeSampleSlotPool samplePool,
            out NvencCaptureWorkSlotLease workLease,
            out NvencEncodeSampleSlotLease sampleLease)
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            workPool = new NvencCaptureWorkSlotPool(state);
            samplePool = new NvencEncodeSampleSlotPool(state);
            Assert.That(workPool.TryRent(out workLease), Is.True);
            Assert.That(samplePool.TryRent(out sampleLease), Is.True);
        }

        private static NvencSubmitToOutputRecord WithField(
            NvencSubmitToOutputRecord record, string fieldName, object value)
        {
            object boxed = record;
            FieldInfo field = typeof(NvencSubmitToOutputRecord).GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(boxed, value);
            return (NvencSubmitToOutputRecord)boxed;
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }
    }
}
