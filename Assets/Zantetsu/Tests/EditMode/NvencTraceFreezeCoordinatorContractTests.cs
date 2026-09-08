using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the NVENC-only Trace freeze boundary and its proof:
    /// the narrow freeze coordinator, the exact-correlated freeze receipt, and
    /// the append-only evidence disposition. Shape and non-contact are fixed by
    /// reflection and source scanning; no real GPU, NVENC, filesystem, sleep,
    /// or short negative wait is used.
    /// </summary>
    public class NvencTraceFreezeCoordinatorContractTests
    {
        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        [Test]
        public void Disposition_AppendOnlyValues_Fixed()
        {
            Assert.That((int)NvencRunEvidenceDisposition.None, Is.EqualTo(0));
            Assert.That((int)NvencRunEvidenceDisposition.Finalized, Is.EqualTo(1));
            Assert.That((int)NvencRunEvidenceDisposition.Incomplete, Is.EqualTo(2));
            Assert.That((int)NvencRunEvidenceDisposition.Committed, Is.EqualTo(3));
            Assert.That((int)NvencRunEvidenceDisposition.CommitOutcomeUnknown, Is.EqualTo(4));
        }

        [Test]
        public void TraceFreezeCoordinator_SealedNotDisposable_FieldShapeClean()
        {
            Type type = typeof(NvencTraceFreezeCoordinator);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            Type[] forbiddenFieldTypes =
            {
                typeof(Thread), typeof(System.Threading.Timer), typeof(Stream), typeof(byte[]),
            };

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(8));
            foreach (FieldInfo field in fields)
            {
                Assert.That(Array.IndexOf(forbiddenFieldTypes, field.FieldType), Is.LessThan(0),
                    field.Name + " must not hold a forbidden type.");
                // Every collaborator reference is readonly; only the issued
                // receipt, seal receipt, and terminal buffer latches are
                // mutable references.
                Assert.That(field.IsInitOnly
                    || field.FieldType == typeof(NvencTraceFreezeReceipt)
                    || field.FieldType == typeof(TraceRunSealReceipt)
                    || field.FieldType == typeof(FreezeTerminalTraceBuffer), Is.True,
                    field.Name + " must be readonly or an issued-proof latch.");
            }
        }

        [Test]
        public void TraceFreezeCoordinator_NoThreadTaskPollFilesystemOrPublicationContact()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencTraceFreezeCoordinator.cs"));

            string[] forbidden =
            {
                "Thread.Sleep", "new Thread", "ThreadPool", "Task", "SpinWait", "WaitHandle",
                "ManualResetEvent", "AutoResetEvent", "Timer", "Monitor", "File.", "Directory.",
                "FileStream", "NvEnc", "UnityEngine", "DllImport", "new []", "new List",
                "new Dictionary", "new Queue", "Guid.NewGuid", "Enumerable",
                "Plan", "Publication", "CaptureComplete", "CaptureIndex", "Registry",
                "cleanup", "Committed", "Disposition", "OS lock", "lock (",
                ".Select(", ".Where(", ".ToList(", ".ToArray(",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "trace freeze coordinator source must not contain: " + word);
            }
        }

        [Test]
        public void TraceFreezeReceipt_TypeShape_ExactIssuerCorrelationOnly()
        {
            Type type = typeof(NvencTraceFreezeReceipt);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(5));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(NvencTraceFreezeCoordinator)));
            Assert.That(fields[1].FieldType, Is.EqualTo(typeof(NvencRunChunkContext)));
            Assert.That(fields[2].FieldType, Is.EqualTo(typeof(CaptureRunInitializationSessionIssue)));
            Assert.That(fields[3].FieldType, Is.EqualTo(typeof(TraceRunSealReceipt)));
            Assert.That(fields[4].FieldType, Is.EqualTo(typeof(FreezeTerminalTraceBuffer)));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencTraceFreezeReceipt.cs"));
            string[] forbidden =
            {
                "IntPtr", "DllImport", "byte[", "Texture2D", "RenderTexture",
                "Token", "Guid.NewGuid", "Registry", "OwnershipBundle",
            };
            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "freeze receipt source must not contain: " + word);
            }
        }
    }
}
