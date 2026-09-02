using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationBootstrapSessionTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                testRunId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            private readonly List<string> _disposeLog;

            public FakeHandle(string lockPath, bool isCreated = true, List<string> disposeLog = null)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
                _disposeLog = disposeLog;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public string Tag { get; set; }

            public int DisposeCount { get; private set; }

            public bool ThrowOnDispose { get; set; }

            public void Dispose()
            {
                DisposeCount++;
                _disposeLog?.Add(LockPath);
                if (ThrowOnDispose)
                {
                    throw new InvalidOperationException("Fake handle dispose failure" + (Tag == null ? string.Empty : ": " + Tag) + ".");
                }
            }
        }

        private sealed class FakeBackend : ICaptureRunLockBackend
        {
            private readonly List<string> _log;
            private readonly List<string> _disposeLog;
            private int _createdCount;

            public FakeBackend(List<string> log, List<string> disposeLog)
            {
                _log = log;
                _disposeLog = disposeLog;
            }

            public Func<string, string> Label { get; set; }

            public Func<string, bool> OnAcquire { get; set; }

            public Exception ThrowOnAcquire { get; set; }

            public bool ThrowOnDisposeSecond { get; set; }

            public int AcquireCount { get; private set; }

            public bool TryAcquire(string absoluteLockPath, out ICaptureRunLockHandle handle)
            {
                AcquireCount++;
                if (Label != null)
                {
                    _log.Add(Label(absoluteLockPath));
                }

                if (ThrowOnAcquire != null)
                {
                    handle = null;
                    throw ThrowOnAcquire;
                }

                bool success = OnAcquire == null || OnAcquire(absoluteLockPath);
                if (success)
                {
                    _createdCount++;
                    FakeHandle created = new FakeHandle(absoluteLockPath, true, _disposeLog);
                    if (_createdCount == 2 && ThrowOnDisposeSecond)
                    {
                        created.ThrowOnDispose = true;
                    }

                    handle = created;
                    return true;
                }

                handle = null;
                return false;
            }
        }

        private sealed class FakeIdSource : ICaptureRunInitializationIdSource
        {
            private readonly List<string> _log;

            public FakeIdSource(List<string> log)
            {
                _log = log;
            }

            public string NextId = InitId;

            public Exception Throw { get; set; }

            public int CallCount { get; private set; }

            public string Create()
            {
                CallCount++;
                _log.Add("Id:Create");
                if (Throw != null)
                {
                    throw Throw;
                }

                return NextId;
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            private readonly List<string> _log;
            private readonly Dictionary<int, Exception> _exceptions = new Dictionary<int, Exception>();
            private int _callCount;

            public FakeProvisioner(List<string> log)
            {
                _log = log;
            }

            public int CallCount => _callCount;

            public void ThrowOnCall(int callNumber, Exception exception)
            {
                _exceptions[callNumber] = exception;
            }

            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                _callCount++;
                _log.Add("Provision:" + operation.RootRole);

                if (_exceptions.TryGetValue(_callCount, out Exception exception))
                {
                    throw exception;
                }

                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            private readonly List<string> _log;
            private readonly Dictionary<int, Exception> _exceptions = new Dictionary<int, Exception>();
            private int _callCount;

            public FakeWriter(List<string> log)
            {
                _log = log;
            }

            public int CallCount => _callCount;

            public void ThrowOnCall(int callNumber, Exception exception)
            {
                _exceptions[callNumber] = exception;
            }

            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                _callCount++;
                _log.Add("Write:" + operation.RootRole + ":" + operation.MarkerKind);

                if (_exceptions.TryGetValue(_callCount, out Exception exception))
                {
                    throw exception;
                }

                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private static CaptureRunInitializationBootstrapCoordinator MakeBootstrap(
            FakeBackend backend,
            FakeIdSource idSource,
            FakeProvisioner provisioner,
            FakeWriter writer)
        {
            CaptureRunLockAcquisitionCoordinator lockCoordinator = new CaptureRunLockAcquisitionCoordinator(backend);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(provisioner, writer);
            return new CaptureRunInitializationBootstrapCoordinator(lockCoordinator, idSource, executionCoordinator);
        }

        private static FakeBackend MakeBackend(List<string> log, List<string> disposeLog, CaptureRunLockPathSet pathSet, Func<string, bool> onAcquire = null)
        {
            return new FakeBackend(log, disposeLog)
            {
                Label = p => p == pathSet.FirstLockPath ? "Lock:first" : "Lock:second",
                OnAcquire = onAcquire ?? (_ => true)
            };
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(new List<string>()),
                new FakeWriter(new List<string>()));
            return executionCoordinator.Execute(batch);
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog, out FakeHandle first, out FakeHandle second)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog) { Tag = "first" };
            second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog) { Tag = "second" };
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private static CaptureRunInitializationSessionOwnershipLease MakeOwnershipLease(CaptureRunRootLayout layout, List<string> disposeLog, out FakeHandle first, out FakeHandle second)
        {
            CaptureRunLockLease lease = MakeLease(layout, disposeLog, out first, out second);
            return CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
        }

        private static CaptureRunLockIdentityEvidence MakeIdentityEvidence(CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            return CaptureRunLockIdentityEvidence.Create(ownershipLease, ownershipLease.LockPathSet);
        }

        private static CaptureRunInitializationSession MakeSession(CaptureRunRootLayout layout)
        {
            return MakeSession(layout, MakeExecutionReceipt(layout));
        }

        private static CaptureRunInitializationSession MakeSession(
            CaptureRunRootLayout layout,
            CaptureRunInitializationExecutionReceipt receipt)
        {
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence).Session;
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationBootstrapSessionTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            Assert.Fail("Source file not found: " + relativePath);
            return null;
        }

        // ---- ID source ----

        [Test]
        public void CryptographicAdapter_Create_ReturnsValidId()
        {
            CryptographicCaptureRunInitializationIdSource source = new CryptographicCaptureRunInitializationIdSource();

            string id = source.Create();

            Assert.That(id, Is.Not.Null);
            Assert.That(id.Length, Is.EqualTo(32));
            Assert.That(id, Does.Match("^[0-9a-f]{32}$"));
        }

        [Test]
        public void CryptographicAdapter_DelegatesToGenerator_Source()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CryptographicCaptureRunInitializationIdSource.cs"));

            Assert.That(source, Does.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(source, Does.Not.Contain("RandomNumberGenerator"));
            Assert.That(source, Does.Not.Contain("GetBytes"));
            Assert.That(source, Does.Not.Contain("ToLowerHex"));
            Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
        }

        // ---- Session construction ----

        [Test]
        public void OwnershipLease_NullLease_Rejected()
        {
            CaptureRunLockLease lease = null;

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationSessionOwnershipLease.Create(ref lease));

            Assert.That(ex.ParamName, Is.EqualTo("lockLease"));
        }

        [Test]
        public void SessionIssue_NullEvidence_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, null));

            Assert.That(ex.ParamName, Is.EqualTo("evidence"));
        }

        [Test]
        public void OwnershipLease_DisposedLease_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockLease lease = MakeLease(layout, null, out _, out _);
            lease.Dispose();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationSessionOwnershipLease.Create(ref lease));

            Assert.That(ex.ParamName, Is.EqualTo("lockLease"));
        }

        [Test]
        public void SessionIssue_InvalidEvidence_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);

            CaptureRunInitializationReadyEvidence invalid = (CaptureRunInitializationReadyEvidence)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationReadyEvidence));

            Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, invalid));
        }

        [Test]
        public void Session_NoStandaloneMintPath()
        {
            Type type = typeof(CaptureRunInitializationSession);

            foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(ctor.IsPrivate, Is.True, ctor + " must be private.");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(typeof(CaptureRunInitializationSession).IsAssignableFrom(method.ReturnType), Is.False, method.Name);
            }

            Type proofType = typeof(CaptureRunInitializationSession.IssuanceProof);
            foreach (ConstructorInfo ctor in proofType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(ctor.IsPrivate, Is.True, ctor + " must be private.");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(typeof(CaptureRunInitializationSession.IssuanceProof).IsAssignableFrom(method.ReturnType), Is.False, method.Name);
            }

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(typeof(CaptureRunInitializationSession.IssuanceProof).IsAssignableFrom(prop.PropertyType), Is.False, prop.Name);
            }
        }

        [Test]
        public void SessionIssue_CrossIssueProof_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationSessionOwnershipLease ownerA = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identityA = MakeIdentityEvidence(ownerA);
            CaptureRunInitializationReadyEvidence evidenceA = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            CaptureRunInitializationSessionOwnershipLease ownerB = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identityB = MakeIdentityEvidence(ownerB);
            CaptureRunInitializationReadyEvidence evidenceB = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            CaptureRunInitializationSessionIssue issueA = CaptureRunInitializationSession.IssuanceProof.Mint(ownerA, identityA, evidenceA);
            CaptureRunInitializationSessionIssue issueB = CaptureRunInitializationSession.IssuanceProof.Mint(ownerB, identityB, evidenceB);

            Assert.That(issueA.IsValid, Is.True);
            Assert.That(issueB.IsValid, Is.True);

            object proofA = GetField(issueA, "_proof");
            object proofB = GetField(issueB, "_proof");
            SetField(issueA, "_proof", proofB);
            SetField(issueB, "_proof", proofA);

            Assert.That(issueA.IsValid, Is.False);
            Assert.That(issueB.IsValid, Is.False);
        }

        [Test]
        public void SessionIssue_CrossIssueFieldSwap_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationSessionOwnershipLease ownerA = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identityA = MakeIdentityEvidence(ownerA);
            CaptureRunInitializationReadyEvidence evidenceA = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            CaptureRunInitializationSessionOwnershipLease ownerB = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identityB = MakeIdentityEvidence(ownerB);
            CaptureRunInitializationReadyEvidence evidenceB = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            foreach (string fieldName in new[] { "_nonce", "_session", "_ownershipLease", "_lockIdentityEvidence" })
            {
                CaptureRunInitializationSessionIssue issueA = CaptureRunInitializationSession.IssuanceProof.Mint(ownerA, identityA, evidenceA);
                CaptureRunInitializationSessionIssue issueB = CaptureRunInitializationSession.IssuanceProof.Mint(ownerB, identityB, evidenceB);

                Assert.That(issueA.IsValid, Is.True);
                Assert.That(issueB.IsValid, Is.True);

                object fieldA = GetField(issueA, fieldName);
                object fieldB = GetField(issueB, fieldName);
                SetField(issueA, fieldName, fieldB);
                SetField(issueB, fieldName, fieldA);

                Assert.That(issueA.IsValid, Is.False, fieldName + " swap must invalidate A.");
                Assert.That(issueB.IsValid, Is.False, fieldName + " swap must invalidate B.");
            }
        }

        [Test]
        public void SessionIssue_FieldsPrivate_NoProofExposure()
        {
            Type type = typeof(CaptureRunInitializationSessionIssue);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsPrivate, Is.True, field.Name + " must be private.");
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Type proofType = typeof(CaptureRunInitializationSession.IssuanceProof);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(proofType.IsAssignableFrom(prop.PropertyType), Is.False, prop.Name + " must not expose the proof.");
                Assert.That(prop.PropertyType == typeof(object), Is.False, prop.Name + " must not expose the nonce.");
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Assert.That(proofType.IsAssignableFrom(method.ReturnType), Is.False, method.Name + " must not return the proof.");
                Assert.That(method.ReturnType == typeof(object), Is.False, method.Name + " must not return the nonce.");
            }
        }

        [Test]
        public void SessionIssue_NullOrForeignProof_Invalid()
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationSessionOwnershipLease ownerA = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identityA = MakeIdentityEvidence(ownerA);
            CaptureRunInitializationReadyEvidence evidenceA = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            CaptureRunInitializationSessionOwnershipLease ownerB = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence identityB = MakeIdentityEvidence(ownerB);
            CaptureRunInitializationReadyEvidence evidenceB = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            CaptureRunInitializationSessionIssue issueA = CaptureRunInitializationSession.IssuanceProof.Mint(ownerA, identityA, evidenceA);
            CaptureRunInitializationSessionIssue issueB = CaptureRunInitializationSession.IssuanceProof.Mint(ownerB, identityB, evidenceB);

            CaptureRunInitializationSessionIssue nullProof = new CaptureRunInitializationSessionIssue(
                issueA.Session, issueA.OwnershipLease, issueA.LockIdentityEvidence, null, new object());
            Assert.That(nullProof.IsValid, Is.False);

            object proofB = GetField(issueB, "_proof");
            CaptureRunInitializationSessionIssue foreignProof = new CaptureRunInitializationSessionIssue(
                issueA.Session, issueA.OwnershipLease, issueA.LockIdentityEvidence,
                (CaptureRunInitializationSession.IssuanceProof)proofB, GetField(issueA, "_nonce"));
            Assert.That(foreignProof.IsValid, Is.False);
        }

        [Test]
        public void SessionFactory_ForeignOwnershipLease_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease ownerA = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunInitializationSessionOwnershipLease ownerB = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence evidenceA = MakeIdentityEvidence(ownerA);

            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => CaptureRunInitializationSessionFactory.Create(ownerB, evidenceA, evidence));

            Assert.That(ex.ParamName, Is.EqualTo("lockIdentityEvidence"));
        }

        [Test]
        public void Session_Forwards_ReadyEvidence()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);

            CaptureRunInitializationSession session = MakeSession(layout, receipt);

            Assert.That(session.ReadyEvidence, Is.Not.Null);
            Assert.That(session.ExecutionReceipt, Is.SameAs(receipt));
            Assert.That(session.RootLayout, Is.SameAs(receipt.RootLayout));
            Assert.That(session.TestRunId, Is.EqualTo(receipt.TestRunId));
            Assert.That(session.RunInitializationId, Is.EqualTo(receipt.RunInitializationId));
        }

        // ---- Session disposal ----

        [Test]
        public void OwnershipLease_IsCreated_BeforeAndAfterDispose()
        {
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(MakeLayout(), null, out _, out _);

            Assert.That(owner.IsCreated, Is.True);
            owner.Dispose();
            Assert.That(owner.IsCreated, Is.False);
        }

        [Test]
        public void OwnershipLease_Dispose_ReleasesSecondThenFirst()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(MakeLayout(), disposeLog, out _, out _);

            owner.Dispose();

            Assert.That(disposeLog, Is.EqualTo(new[] { owner.LockPathSet.SecondLockPath, owner.LockPathSet.FirstLockPath }));
        }

        [Test]
        public void OwnershipLease_Dispose_Idempotent()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(MakeLayout(), disposeLog, out FakeHandle first, out FakeHandle second);

            owner.Dispose();
            owner.Dispose();

            Assert.That(first.DisposeCount, Is.EqualTo(1));
            Assert.That(second.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void OwnershipLease_Dispose_RetryAfterFailure()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(MakeLayout(), disposeLog, out FakeHandle first, out FakeHandle second);

            second.ThrowOnDispose = true;
            Assert.Throws<AggregateException>(() => owner.Dispose());
            Assert.That(owner.IsCreated, Is.False);

            second.ThrowOnDispose = false;
            owner.Dispose();

            Assert.That(owner.IsCreated, Is.False);
            Assert.That(second.DisposeCount, Is.EqualTo(2));
            Assert.That(first.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void OwnershipLease_Dispose_DoesNotTouchEvidence()
        {
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunInitializationSession session = MakeSession(layout, receipt);
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, disposeLog, out _, out _);
            string initIdBefore = receipt.RunInitializationId;

            owner.Dispose();

            Assert.That(session.ExecutionReceipt, Is.SameAs(receipt));
            Assert.That(receipt.RunInitializationId, Is.EqualTo(initIdBefore));
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void IdentityEvidence_IsIssuedFor_ForeignOwner_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationSessionOwnershipLease ownerA = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunInitializationSessionOwnershipLease ownerB = MakeOwnershipLease(layout, null, out _, out _);
            CaptureRunLockIdentityEvidence evidenceA = MakeIdentityEvidence(ownerA);

            Assert.That(evidenceA.IsIssuedFor(ownerA), Is.True);
            Assert.That(evidenceA.IsIssuedFor(ownerB), Is.False);
            Assert.That(evidenceA.IsIssuedFor(null), Is.False);
        }

        [Test]
        public void Session_NoHandleExposure()
        {
            Type type = typeof(CaptureRunInitializationSession);

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(typeof(ICaptureRunLockHandle).IsAssignableFrom(prop.PropertyType), Is.False, prop.Name);
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(typeof(ICaptureRunLockHandle).IsAssignableFrom(field.FieldType), Is.False, field.Name);
            }
        }

        [Test]
        public void Session_Fields_SingleReadonlyEvidence()
        {
            Type type = typeof(CaptureRunInitializationSession);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(CaptureRunInitializationReadyEvidence)));
            Assert.That(fields[0].IsInitOnly, Is.True);
        }

        [Test]
        public void SessionIsNotDisposable_OwnershipLeaseIs()
        {
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureRunInitializationSession)), Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureRunInitializationSessionOwnershipLease)), Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CaptureRunInitializationBootstrapCoordinator)), Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(typeof(CryptographicCaptureRunInitializationIdSource)), Is.False);
        }

        // ---- Bootstrap shape ----

        [Test]
        public void Bootstrap_Fields_AreThreeReadonlyDependencies()
        {
            Type type = typeof(CaptureRunInitializationBootstrapCoordinator);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(3));

            int lockFields = 0;
            int idFields = 0;
            int executionFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunLockAcquisitionCoordinator))
                {
                    lockFields++;
                }
                else if (field.FieldType == typeof(ICaptureRunInitializationIdSource))
                {
                    idFields++;
                }
                else if (field.FieldType == typeof(CaptureRunInitializationExecutionCoordinator))
                {
                    executionFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(lockFields, Is.EqualTo(1));
            Assert.That(idFields, Is.EqualTo(1));
            Assert.That(executionFields, Is.EqualTo(1));
        }

        [Test]
        public void Bootstrap_NullConstructorArgs_Rejected()
        {
            FakeBackend backend = new FakeBackend(new List<string>(), new List<string>());
            CaptureRunLockAcquisitionCoordinator lockCoordinator = new CaptureRunLockAcquisitionCoordinator(backend);
            FakeIdSource idSource = new FakeIdSource(new List<string>());
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(new List<string>()), new FakeWriter(new List<string>()));

            ArgumentNullException ex1 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationBootstrapCoordinator(null, idSource, executionCoordinator));
            Assert.That(ex1.ParamName, Is.EqualTo("lockCoordinator"));

            ArgumentNullException ex2 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationBootstrapCoordinator(lockCoordinator, null, executionCoordinator));
            Assert.That(ex2.ParamName, Is.EqualTo("initializationIdSource"));

            ArgumentNullException ex3 = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationBootstrapCoordinator(lockCoordinator, idSource, null));
            Assert.That(ex3.ParamName, Is.EqualTo("executionCoordinator"));
        }

        [Test]
        public void NoPublicConstructorOrSetter_Sealed()
        {
            foreach (Type type in new[]
            {
                typeof(CaptureRunInitializationBootstrapCoordinator),
                typeof(CaptureRunInitializationSession)
            })
            {
                Assert.That(type.IsPublic, Is.False, type.Name);
                Assert.That(type.IsSealed, Is.True, type.Name);
                Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, type.Name);

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
                }
            }
        }

        [Test]
        public void CryptographicAdapter_Shape()
        {
            Type type = typeof(CryptographicCaptureRunInitializationIdSource);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty, "No fields.");
            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public properties.");

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        // ---- Bootstrap normal order ----

        [Test]
        public void Bootstrap_NormalOrder_And_SessionDisposeReleasesSecondThenFirst()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);

            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);

            CaptureRunInitializationBootstrapCoordinator coordinator = MakeBootstrap(backend, idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue;
            bool success = coordinator.TryInitialize(layout, out issue);

            Assert.That(success, Is.True);
            Assert.That(issue, Is.Not.Null);
            Assert.That(log, Is.EqualTo(new[]
            {
                "Lock:first",
                "Lock:second",
                "Id:Create",
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization",
                "Write:Staging:Ready",
                "Write:Final:Ready"
            }));
            Assert.That(idSource.CallCount, Is.EqualTo(1));
            Assert.That(provisioner.CallCount, Is.EqualTo(2));
            Assert.That(writer.CallCount, Is.EqualTo(4));
            Assert.That(disposeLog, Is.Empty, "Handles must stay held until the session is disposed.");

            issue.OwnershipLease.Dispose();
            Assert.That(disposeLog, Is.EqualTo(new[] { pathSet.SecondLockPath, pathSet.FirstLockPath }));
        }

        // ---- Lock contention ----

        [Test]
        public void Bootstrap_FirstLockContention_False_NoIdNoExecution()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);

            FakeBackend backend = MakeBackend(log, disposeLog, pathSet, _ => false);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);

            CaptureRunInitializationBootstrapCoordinator coordinator = MakeBootstrap(backend, idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue;
            bool success = coordinator.TryInitialize(layout, out issue);

            Assert.That(success, Is.False);
            Assert.That(issue, Is.Null);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(log, Is.EqualTo(new[] { "Lock:first" }));
        }

        [Test]
        public void Bootstrap_SecondLockContention_False_FirstReleased_NoIdNoExecution()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);

            FakeBackend backend = MakeBackend(log, disposeLog, pathSet, p => p == pathSet.FirstLockPath);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);

            CaptureRunInitializationBootstrapCoordinator coordinator = MakeBootstrap(backend, idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue;
            bool success = coordinator.TryInitialize(layout, out issue);

            Assert.That(success, Is.False);
            Assert.That(issue, Is.Null);
            Assert.That(idSource.CallCount, Is.EqualTo(0));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(log, Is.EqualTo(new[] { "Lock:first", "Lock:second" }));
            Assert.That(disposeLog, Is.EqualTo(new[] { pathSet.FirstLockPath }));
        }

        // ---- Invalid IDs ----

        [Test]
        public void Bootstrap_InvalidIds_NoExecution_LeaseReleased_SessionNull()
        {
            AssertInvalidIdRejected(null, typeof(ArgumentNullException));
            AssertInvalidIdRejected(new string('a', 31), typeof(ArgumentException));
            AssertInvalidIdRejected(new string('a', 33), typeof(ArgumentException));
            AssertInvalidIdRejected("0123456789ABCDEF0123456789ABCDEF", typeof(ArgumentException));
            AssertInvalidIdRejected("0123456789abcdef0123456789abcdeg", typeof(ArgumentException));
        }

        private static void AssertInvalidIdRejected(string badId, Type expectedExceptionType)
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);

            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log) { NextId = badId };
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);

            CaptureRunInitializationBootstrapCoordinator coordinator = MakeBootstrap(backend, idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue = null;
            Assert.Throws(expectedExceptionType, () => coordinator.TryInitialize(layout, out issue));

            Assert.That(idSource.CallCount, Is.EqualTo(1));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(disposeLog, Is.EqualTo(new[] { pathSet.SecondLockPath, pathSet.FirstLockPath }));
        }

        // ---- Failure boundaries ----

        private static void AssertBootstrapFailure(
            FakeBackend backend,
            FakeIdSource idSource,
            FakeProvisioner provisioner,
            FakeWriter writer,
            CaptureRunRootLayout layout,
            CaptureRunLockPathSet pathSet,
            List<string> log,
            List<string> disposeLog,
            IOException injected,
            string[] expectedLog,
            int expectedProvisionCalls,
            int expectedWriteCalls)
        {
            CaptureRunInitializationBootstrapCoordinator coordinator = MakeBootstrap(backend, idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue = null;
            IOException ex = Assert.Throws<IOException>(() => coordinator.TryInitialize(layout, out issue));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(issue, Is.Null);
            Assert.That(provisioner.CallCount, Is.EqualTo(expectedProvisionCalls));
            Assert.That(writer.CallCount, Is.EqualTo(expectedWriteCalls));
            Assert.That(log, Is.EqualTo(expectedLog));
            Assert.That(disposeLog, Is.EqualTo(new[] { pathSet.SecondLockPath, pathSet.FirstLockPath }));
        }

        [Test]
        public void Bootstrap_Failure_IdSource_CleansUpLeaseAndRethrows()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("id boom");
            idSource.Throw = injected;

            AssertBootstrapFailure(backend, idSource, provisioner, writer, layout, pathSet, log, disposeLog, injected,
                new[] { "Lock:first", "Lock:second", "Id:Create" }, 0, 0);
        }

        [Test]
        public void Bootstrap_Failure_StagingProvision_CleansUpLeaseAndRethrows()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("provision boom");
            provisioner.ThrowOnCall(1, injected);

            AssertBootstrapFailure(backend, idSource, provisioner, writer, layout, pathSet, log, disposeLog, injected,
                new[] { "Lock:first", "Lock:second", "Id:Create", "Provision:Staging" }, 1, 0);
        }

        [Test]
        public void Bootstrap_Failure_StagingInitWrite_CleansUpLeaseAndRethrows()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("write boom");
            writer.ThrowOnCall(1, injected);

            AssertBootstrapFailure(backend, idSource, provisioner, writer, layout, pathSet, log, disposeLog, injected,
                new[] { "Lock:first", "Lock:second", "Id:Create", "Provision:Staging", "Write:Staging:Initialization" }, 1, 1);
        }

        [Test]
        public void Bootstrap_Failure_FinalProvision_CleansUpLeaseAndRethrows()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("provision boom");
            provisioner.ThrowOnCall(2, injected);

            AssertBootstrapFailure(backend, idSource, provisioner, writer, layout, pathSet, log, disposeLog, injected,
                new[] { "Lock:first", "Lock:second", "Id:Create", "Provision:Staging", "Write:Staging:Initialization", "Provision:Final" }, 2, 1);
        }

        [Test]
        public void Bootstrap_Failure_FinalInitWrite_CleansUpLeaseAndRethrows()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("write boom");
            writer.ThrowOnCall(2, injected);

            AssertBootstrapFailure(backend, idSource, provisioner, writer, layout, pathSet, log, disposeLog, injected,
                new[]
                {
                    "Lock:first", "Lock:second", "Id:Create",
                    "Provision:Staging", "Write:Staging:Initialization",
                    "Provision:Final", "Write:Final:Initialization"
                }, 2, 2);
        }

        [Test]
        public void Bootstrap_Failure_StagingReadyWrite_CleansUpLeaseAndRethrows()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("write boom");
            writer.ThrowOnCall(3, injected);

            AssertBootstrapFailure(backend, idSource, provisioner, writer, layout, pathSet, log, disposeLog, injected,
                new[]
                {
                    "Lock:first", "Lock:second", "Id:Create",
                    "Provision:Staging", "Write:Staging:Initialization",
                    "Provision:Final", "Write:Final:Initialization",
                    "Write:Staging:Ready"
                }, 2, 3);
        }

        [Test]
        public void Bootstrap_Failure_FinalReadyWrite_CleansUpLeaseAndRethrows()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            FakeIdSource idSource = new FakeIdSource(log);
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("write boom");
            writer.ThrowOnCall(4, injected);

            AssertBootstrapFailure(backend, idSource, provisioner, writer, layout, pathSet, log, disposeLog, injected,
                new[]
                {
                    "Lock:first", "Lock:second", "Id:Create",
                    "Provision:Staging", "Write:Staging:Initialization",
                    "Provision:Final", "Write:Final:Initialization",
                    "Write:Staging:Ready", "Write:Final:Ready"
                }, 2, 4);
        }

        [Test]
        public void Bootstrap_Failure_CleanupAlsoFails_AggregateOrder()
        {
            List<string> log = new List<string>();
            List<string> disposeLog = new List<string>();
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);

            FakeBackend backend = MakeBackend(log, disposeLog, pathSet);
            backend.ThrowOnDisposeSecond = true;
            FakeIdSource idSource = new FakeIdSource(log);
            IOException injected = new IOException("id boom");
            idSource.Throw = injected;
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);

            CaptureRunInitializationBootstrapCoordinator coordinator = MakeBootstrap(backend, idSource, provisioner, writer);

            CaptureRunInitializationSessionIssue issue = null;
            AggregateException ex = Assert.Throws<AggregateException>(() => coordinator.TryInitialize(layout, out issue));

            Assert.That(issue, Is.Null);
            Assert.That(ex.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(ex.InnerExceptions[0], Is.SameAs(injected));
            Assert.That(ex.InnerExceptions[1], Is.InstanceOf<AggregateException>());
        }

        // ---- Source inspection ----

        [Test]
        public void Bootstrap_NoForbiddenDependencies_Source()
        {
            string bootstrap = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationBootstrapCoordinator.cs"));
            string session = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationSession.cs"));
            string idInterface = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunInitializationIdSource.cs"));

            foreach (string source in new[] { bootstrap, session, idInterface })
            {
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("Stream"));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("Trace"));
                Assert.That(source, Does.Not.Contain("System.Threading"));
                Assert.That(source, Does.Not.Contain("Task"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
            }

            Assert.That(bootstrap, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(bootstrap, Does.Not.Contain("CaptureRunMarkerBindingFactory"));
            Assert.That(bootstrap, Does.Not.Contain("RandomNumberGenerator"));
        }

        [Test]
        public void Bootstrap_Source_ExclusiveRawOrOwnerCleanup()
        {
            string bootstrap = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationBootstrapCoordinator.cs"));

            // The catch must release exactly one held lock: the ownership lease
            // once the raw lease has been transferred, or the raw lease itself
            // when ownership transfer has not yet completed.
            Assert.That(bootstrap, Does.Contain("if (ownershipLease != null)"));
            Assert.That(bootstrap, Does.Contain("else if (lease != null)"));
            Assert.That(bootstrap, Does.Contain("ownershipLease.Dispose();"));
            Assert.That(bootstrap, Does.Contain("lease.Dispose();"));
        }
    }
}
