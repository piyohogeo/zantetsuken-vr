using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunMarkerBindingTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetInitMarkerType() => GetTypeFromAssembly("CaptureRunInitializationMarker");

        private static Type GetReadyMarkerType() => GetTypeFromAssembly("CaptureRunReadyMarker");

        private static Type GetBindingType() => GetTypeFromAssembly("CaptureRunMarkerBinding");

        private static Type GetRoleType() => GetTypeFromAssembly("CaptureRunRootRole");

        private static Type GetInitCodecType() => GetTypeFromAssembly("CaptureRunInitializationMarkerCodec");

        private static object Role(string name) => Enum.Parse(GetRoleType(), name);

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        private static object MakeInitMarker(long testRunId, string initId, object rootRole, string stagingHash, string finalHash)
        {
            ConstructorInfo ctor = GetInitMarkerType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(string), GetRoleType(), typeof(string), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { testRunId, initId, rootRole, stagingHash, finalHash });
        }

        private static object MakeReadyMarker(long testRunId, string initId, string stagingInitSha256, string finalInitSha256)
        {
            ConstructorInfo ctor = GetReadyMarkerType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(string), typeof(string), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { testRunId, initId, stagingInitSha256, finalInitSha256 });
        }

        private static string ComputeContentSha256(object initMarker)
        {
            MethodInfo method = GetInitCodecType().GetMethod("ComputeContentSha256", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (string)method.Invoke(null, new object[] { initMarker });
        }

        private static object MakeBinding(object stagingInit, object finalInit, object stagingReady, object finalReady)
        {
            ConstructorInfo ctor = GetBindingType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { GetInitMarkerType(), GetInitMarkerType(), GetReadyMarkerType(), GetReadyMarkerType() },
                null);
            Assert.That(ctor, Is.Not.Null);
            return ctor.Invoke(new object[] { stagingInit, finalInit, stagingReady, finalReady });
        }

        private static Exception MakeBindingException(object stagingInit, object finalInit, object stagingReady, object finalReady)
        {
            try
            {
                MakeBinding(stagingInit, finalInit, stagingReady, finalReady);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static void MakeValidFixture(out object stagingInit, out object finalInit, out object stagingReady, out object finalReady)
        {
            stagingInit = MakeInitMarker(1, InitId, Role("Staging"), StagingHash, FinalHash);
            finalInit = MakeInitMarker(1, InitId, Role("Final"), StagingHash, FinalHash);

            string stagingInitSha = ComputeContentSha256(stagingInit);
            string finalInitSha = ComputeContentSha256(finalInit);

            stagingReady = MakeReadyMarker(1, InitId, stagingInitSha, finalInitSha);
            finalReady = MakeReadyMarker(1, InitId, stagingInitSha, finalInitSha);
        }

        // ---- Construction ----

        [Test]
        public void Constructor_AllNull_RejectedWithParamName()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            Exception e1 = MakeBindingException(null, finalInit, stagingReady, finalReady);
            Assert.That(e1, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)e1).ParamName, Is.EqualTo("stagingInitialization"));

            Exception e2 = MakeBindingException(stagingInit, null, stagingReady, finalReady);
            Assert.That(e2, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)e2).ParamName, Is.EqualTo("finalInitialization"));

            Exception e3 = MakeBindingException(stagingInit, finalInit, null, finalReady);
            Assert.That(e3, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)e3).ParamName, Is.EqualTo("stagingReady"));

            Exception e4 = MakeBindingException(stagingInit, finalInit, stagingReady, null);
            Assert.That(e4, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)e4).ParamName, Is.EqualTo("finalReady"));
        }

        [Test]
        public void ValidFixture_Constructs()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object binding = MakeBinding(stagingInit, finalInit, stagingReady, finalReady);
            Assert.That(binding, Is.Not.Null);
        }

        [Test]
        public void HoldsMarkers_ByReference()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object binding = MakeBinding(stagingInit, finalInit, stagingReady, finalReady);

            Assert.That(GetProperty(binding, "StagingInitialization"), Is.SameAs(stagingInit));
            Assert.That(GetProperty(binding, "FinalInitialization"), Is.SameAs(finalInit));
            Assert.That(GetProperty(binding, "StagingReady"), Is.SameAs(stagingReady));
            Assert.That(GetProperty(binding, "FinalReady"), Is.SameAs(finalReady));
        }

        [Test]
        public void ForwardsValuesFromStagingInitialization()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object binding = MakeBinding(stagingInit, finalInit, stagingReady, finalReady);

            Assert.That((long)GetProperty(binding, "TestRunId"), Is.EqualTo((long)GetProperty(stagingInit, "TestRunId")));
            Assert.That((string)GetProperty(binding, "RunInitializationId"), Is.EqualTo((string)GetProperty(stagingInit, "RunInitializationId")));
            Assert.That((string)GetProperty(binding, "StagingRunRootSha256"), Is.EqualTo((string)GetProperty(stagingInit, "StagingRunRootSha256")));
            Assert.That((string)GetProperty(binding, "FinalRunRootSha256"), Is.EqualTo((string)GetProperty(stagingInit, "FinalRunRootSha256")));
        }

        // ---- Role validation ----

        [Test]
        public void StagingRoleFinal_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object badStaging = MakeInitMarker(1, InitId, Role("Final"), StagingHash, FinalHash);

            Exception ex = MakeBindingException(badStaging, finalInit, stagingReady, finalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingInitialization"));
        }

        [Test]
        public void FinalRoleStaging_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object badFinal = MakeInitMarker(1, InitId, Role("Staging"), StagingHash, FinalHash);

            Exception ex = MakeBindingException(stagingInit, badFinal, stagingReady, finalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalInitialization"));
        }

        // ---- Init marker agreement ----

        [Test]
        public void TestRunIdMismatch_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object badFinal = MakeInitMarker(2, InitId, Role("Final"), StagingHash, FinalHash);

            Exception ex = MakeBindingException(stagingInit, badFinal, stagingReady, finalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalInitialization"));
        }

        [Test]
        public void InitializationIdMismatch_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object badFinal = MakeInitMarker(1, "11111111111111111111111111111111", Role("Final"), StagingHash, FinalHash);

            Exception ex = MakeBindingException(stagingInit, badFinal, stagingReady, finalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalInitialization"));
        }

        [Test]
        public void StagingRootHashMismatch_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object badFinal = MakeInitMarker(1, InitId, Role("Final"), new string('0', 64), FinalHash);

            Exception ex = MakeBindingException(stagingInit, badFinal, stagingReady, finalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalInitialization"));
        }

        [Test]
        public void FinalRootHashMismatch_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object badFinal = MakeInitMarker(1, InitId, Role("Final"), StagingHash, new string('0', 64));

            Exception ex = MakeBindingException(stagingInit, badFinal, stagingReady, finalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalInitialization"));
        }

        // ---- Ready copy agreement ----

        [Test]
        public void ReadyCopyPropertyDifferences_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            string stagingInitSha = ComputeContentSha256(stagingInit);
            string finalInitSha = ComputeContentSha256(finalInit);

            // TestRunId difference in staging ready.
            Exception e1 = MakeBindingException(
                stagingInit, finalInit,
                MakeReadyMarker(2, InitId, stagingInitSha, finalInitSha),
                finalReady);
            Assert.That(e1, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)e1).ParamName, Is.EqualTo("finalReady"));

            // RunInitializationId difference in staging ready.
            Exception e2 = MakeBindingException(
                stagingInit, finalInit,
                MakeReadyMarker(1, "11111111111111111111111111111111", stagingInitSha, finalInitSha),
                finalReady);
            Assert.That(e2, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)e2).ParamName, Is.EqualTo("finalReady"));

            // StagingInitSha256 difference in staging ready.
            Exception e3 = MakeBindingException(
                stagingInit, finalInit,
                MakeReadyMarker(1, InitId, new string('0', 64), finalInitSha),
                finalReady);
            Assert.That(e3, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)e3).ParamName, Is.EqualTo("finalReady"));

            // FinalInitSha256 difference in staging ready.
            Exception e4 = MakeBindingException(
                stagingInit, finalInit,
                MakeReadyMarker(1, InitId, stagingInitSha, new string('1', 64)),
                finalReady);
            Assert.That(e4, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)e4).ParamName, Is.EqualTo("finalReady"));
        }

        // ---- Ready vs init agreement ----

        [Test]
        public void ReadyTestRunIdMismatchWithInit_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            string stagingInitSha = ComputeContentSha256(stagingInit);
            string finalInitSha = ComputeContentSha256(finalInit);

            object badStagingReady = MakeReadyMarker(2, InitId, stagingInitSha, finalInitSha);
            object badFinalReady = MakeReadyMarker(2, InitId, stagingInitSha, finalInitSha);

            Exception ex = MakeBindingException(stagingInit, finalInit, badStagingReady, badFinalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingReady"));
        }

        [Test]
        public void ReadyInitializationIdMismatchWithInit_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            string stagingInitSha = ComputeContentSha256(stagingInit);
            string finalInitSha = ComputeContentSha256(finalInit);

            object badStagingReady = MakeReadyMarker(1, "11111111111111111111111111111111", stagingInitSha, finalInitSha);
            object badFinalReady = MakeReadyMarker(1, "11111111111111111111111111111111", stagingInitSha, finalInitSha);

            Exception ex = MakeBindingException(stagingInit, finalInit, badStagingReady, badFinalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingReady"));
        }

        [Test]
        public void StagingInitHashMismatch_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            string finalInitSha = ComputeContentSha256(finalInit);
            object badStagingReady = MakeReadyMarker(1, InitId, new string('0', 64), finalInitSha);
            object badFinalReady = MakeReadyMarker(1, InitId, new string('0', 64), finalInitSha);

            Exception ex = MakeBindingException(stagingInit, finalInit, badStagingReady, badFinalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingReady"));
        }

        [Test]
        public void FinalInitHashMismatch_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            string stagingInitSha = ComputeContentSha256(stagingInit);
            object badStagingReady = MakeReadyMarker(1, InitId, stagingInitSha, new string('1', 64));
            object badFinalReady = MakeReadyMarker(1, InitId, stagingInitSha, new string('1', 64));

            Exception ex = MakeBindingException(stagingInit, finalInit, badStagingReady, badFinalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("finalReady"));
        }

        [Test]
        public void SwappedReadyHashes_Rejected()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            string stagingInitSha = ComputeContentSha256(stagingInit);
            string finalInitSha = ComputeContentSha256(finalInit);

            object swappedStagingReady = MakeReadyMarker(1, InitId, finalInitSha, stagingInitSha);
            object swappedFinalReady = MakeReadyMarker(1, InitId, finalInitSha, stagingInitSha);

            Exception ex = MakeBindingException(stagingInit, finalInit, swappedStagingReady, swappedFinalReady);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("stagingReady"));
        }

        [Test]
        public void CorrectHashesFromCodec_Succeed()
        {
            // The fixture derives both init hashes from the existing codec, so
            // a successful construction proves the hash check accepts
            // codec-generated values.
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            object binding = MakeBinding(stagingInit, finalInit, stagingReady, finalReady);
            Assert.That(binding, Is.Not.Null);
        }

        // ---- Ownership / shape ----

        [Test]
        public void DoesNotModifyInputs()
        {
            object stagingInit, finalInit, stagingReady, finalReady;
            MakeValidFixture(out stagingInit, out finalInit, out stagingReady, out finalReady);

            MakeBinding(stagingInit, finalInit, stagingReady, finalReady);

            Assert.That((long)GetProperty(stagingInit, "TestRunId"), Is.EqualTo(1));
            Assert.That((string)GetProperty(stagingInit, "RunInitializationId"), Is.EqualTo(InitId));
            Assert.That((string)GetProperty(stagingInit, "StagingRunRootSha256"), Is.EqualTo(StagingHash));
            Assert.That((string)GetProperty(stagingInit, "FinalRunRootSha256"), Is.EqualTo(FinalHash));
            Assert.That((string)GetProperty(stagingReady, "RunInitializationId"), Is.EqualTo(InitId));
        }

        [Test]
        public void NoPublicApi_Sealed_NotDisposable_NotUnityObject()
        {
            Type type = GetBindingType();

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, "No public constructor.");
            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public properties.");
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public methods.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void NoMutableStateBeyondMarkerReferences()
        {
            Type type = GetBindingType();

            FieldInfo[] instanceFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceFields.Length, Is.EqualTo(4), "Binding must hold exactly four marker references.");

            int initFields = 0;
            int readyFields = 0;
            foreach (FieldInfo field in instanceFields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == GetInitMarkerType())
                {
                    initFields++;
                }
                else if (field.FieldType == GetReadyMarkerType())
                {
                    readyFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(initFields, Is.EqualTo(2), "Binding must hold exactly two initialization markers.");
            Assert.That(readyFields, Is.EqualTo(2), "Binding must hold exactly two ready markers.");

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        [Test]
        public void Source_DoesNotReimplementHashOrTouchIoUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerBinding.cs"));

            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Debug."));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunMarkerBindingTests).Assembly.Location);
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
    }
}
