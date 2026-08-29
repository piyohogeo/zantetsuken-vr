using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationIdGeneratorTests
    {
        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetGeneratorType() => GetTypeFromAssembly("CaptureRunInitializationIdGenerator");

        private static Type GetFactoryType() => GetTypeFromAssembly("CaptureRunMarkerBindingFactory");

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        private static string Create()
        {
            MethodInfo method = GetGeneratorType().GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (string)method.Invoke(null, null);
        }

        private static string EncodeEntropy(byte[] entropy)
        {
            MethodInfo method = GetGeneratorType().GetMethod("EncodeEntropy", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "EncodeEntropy helper not found.");
            return (string)method.Invoke(null, new object[] { entropy });
        }

        private static Exception EncodeEntropyException(byte[] entropy)
        {
            try
            {
                EncodeEntropy(entropy);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object CreateBinding(long testRunId, string initId, string stagingHash, string finalHash)
        {
            MethodInfo method = GetFactoryType().GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(null, new object[] { testRunId, initId, stagingHash, finalHash });
        }

        // ---- Create ----

        [Test]
        public void Create_Returns32Characters()
        {
            Assert.That(Create().Length, Is.EqualTo(32));
        }

        [Test]
        public void Create_AllLowercaseHex()
        {
            for (int i = 0; i < 8; i++)
            {
                string id = Create();
                Assert.That(id, Does.Match("^[0-9a-f]{32}$"));
            }
        }

        [Test]
        public void Create_NoUppercase()
        {
            for (int i = 0; i < 8; i++)
            {
                Assert.That(Create(), Does.Not.Match("[A-F]"));
            }
        }

        [Test]
        public void Create_ConsecutiveCallsDiffer()
        {
            string first = Create();
            string second = Create();

            Assert.That(second, Is.Not.EqualTo(first));
        }

        [Test]
        public void Create_ManyCalls_NoDuplicates()
        {
            // Regression guard against a fixed value, cache, or buffer reuse;
            // not a cryptographic proof of uniqueness.
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < 100; i++)
            {
                Assert.That(seen.Add(Create()), Is.True, "Duplicate initialization ID generated.");
            }
        }

        // ---- Deterministic encoding helper ----

        [Test]
        public void EncodeEntropy_KnownVector()
        {
            byte[] entropy = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                entropy[i] = (byte)i;
            }

            Assert.That(EncodeEntropy(entropy), Is.EqualTo("000102030405060708090a0b0c0d0e0f"));
        }

        [Test]
        public void EncodeEntropy_AllZero()
        {
            Assert.That(EncodeEntropy(new byte[16]), Is.EqualTo(new string('0', 32)));
        }

        [Test]
        public void EncodeEntropy_AllFf()
        {
            byte[] entropy = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                entropy[i] = 0xFF;
            }

            Assert.That(EncodeEntropy(entropy), Is.EqualTo(new string('f', 32)));
        }

        [Test]
        public void EncodeEntropy_NullAndWrongLength_Rejected()
        {
            Exception nullEx = EncodeEntropyException(null);
            Assert.That(nullEx, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullEx).ParamName, Is.EqualTo("entropy"));

            Exception shortEx = EncodeEntropyException(new byte[15]);
            Assert.That(shortEx, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)shortEx).ParamName, Is.EqualTo("entropy"));

            Exception longEx = EncodeEntropyException(new byte[17]);
            Assert.That(longEx, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)longEx).ParamName, Is.EqualTo("entropy"));
        }

        [Test]
        public void EncodeEntropy_DoesNotMutateInput()
        {
            byte[] entropy = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                entropy[i] = (byte)(i * 7);
            }

            byte[] copy = (byte[])entropy.Clone();
            EncodeEntropy(entropy);

            Assert.That(entropy, Is.EqualTo(copy));
        }

        // ---- Integration with the marker factory ----

        [Test]
        public void GeneratedId_PassesBindingFactory()
        {
            string id = Create();
            object binding = CreateBinding(1, id, StagingHash, FinalHash);

            Assert.That(binding, Is.Not.Null);
        }

        // ---- Shape / responsibilities ----

        [Test]
        public void Class_HasNoFieldsAndNoPublicApi()
        {
            Type type = GetGeneratorType();

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), Is.Empty, "Generator must have no fields.");
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly), Is.Empty, "Generator must expose no public API.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Source_UsesCryptoRngAndDelegatesHexEncoding()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationIdGenerator.cs"));

            Assert.That(source, Does.Contain("RandomNumberGenerator"));
            Assert.That(source, Does.Contain("CaptureRunInitializationMarkerCodec"));
            Assert.That(source, Does.Contain("ToLowerHex"));

            Assert.That(source, Does.Not.Contain("Guid.NewGuid"));
            Assert.That(source, Does.Not.Contain("System.Random"));
            Assert.That(source, Does.Not.Contain("UnityEngine.Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Interlocked"));
            Assert.That(source, Does.Not.Contain("Stopwatch"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Debug."));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationIdGeneratorTests).Assembly.Location);
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
