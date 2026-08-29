using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CapturePublicationPlanTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        // ---- Reflection helpers ----

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetEntryType() => GetTypeFromAssembly("CapturePublicationPlanEntry");

        private static Type GetPlanType() => GetTypeFromAssembly("CapturePublicationPlan");

        private static Type GetCodecType() => GetTypeFromAssembly("CapturePublicationPlanCodec");

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

        // ---- Factories ----

        private static object MakeEntryRaw(
            long captureFrameId,
            string pngStaging,
            string sidecarStaging,
            string pngFinal,
            string sidecarFinal,
            long pngByteLength,
            long sidecarByteLength,
            string pngContentSha256,
            string sidecarContentSha256)
        {
            ConstructorInfo ctor = GetEntryType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(string), typeof(string), typeof(string), typeof(string), typeof(long), typeof(long), typeof(string), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null, "Entry constructor not found.");
            return ctor.Invoke(new object[] { captureFrameId, pngStaging, sidecarStaging, pngFinal, sidecarFinal, pngByteLength, sidecarByteLength, pngContentSha256, sidecarContentSha256 });
        }

        private static Exception MakeEntryRawException(
            long captureFrameId,
            string pngStaging,
            string sidecarStaging,
            string pngFinal,
            string sidecarFinal,
            long pngByteLength,
            long sidecarByteLength,
            string pngContentSha256,
            string sidecarContentSha256)
        {
            try
            {
                MakeEntryRaw(captureFrameId, pngStaging, sidecarStaging, pngFinal, sidecarFinal, pngByteLength, sidecarByteLength, pngContentSha256, sidecarContentSha256);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object MakeEntry(long captureFrameId, long pngByteLength = 16, long sidecarByteLength = 32)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return MakeEntryRaw(
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                pngByteLength,
                sidecarByteLength,
                Hash64,
                Hash64);
        }

        private static Array MakeEntryArray(params object[] entries)
        {
            Array array = Array.CreateInstance(GetEntryType(), entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                array.SetValue(entries[i], i);
            }

            return array;
        }

        private static Array MakeManyEntries(int count)
        {
            Array array = Array.CreateInstance(GetEntryType(), count);
            for (int i = 0; i < count; i++)
            {
                array.SetValue(MakeEntry(i + 1, 16, 32), i);
            }

            return array;
        }

        private static object MakePlanRaw(long testRunId, string initId, string hash, Array entries)
        {
            ConstructorInfo ctor = GetPlanType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(string), typeof(string), GetEntryType().MakeArrayType() },
                null);
            Assert.That(ctor, Is.Not.Null, "Plan constructor not found.");
            return ctor.Invoke(new object[] { testRunId, initId, hash, entries });
        }

        private static Exception MakePlanRawException(long testRunId, string initId, string hash, Array entries)
        {
            try
            {
                MakePlanRaw(testRunId, initId, hash, entries);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static object MakePlan(long testRunId = 1, string initId = InitId, string hash = Hash64, Array entries = null)
        {
            if (entries == null)
            {
                entries = MakeEntryArray();
            }

            return MakePlanRaw(testRunId, initId, hash, entries);
        }

        private static object MakePlanWithIds(params long[] captureFrameIds)
        {
            Array entries = Array.CreateInstance(GetEntryType(), captureFrameIds.Length);
            for (int i = 0; i < captureFrameIds.Length; i++)
            {
                entries.SetValue(MakeEntry(captureFrameIds[i]), i);
            }

            return MakePlan(entries: entries);
        }

        // ---- Codec helpers ----

        private static byte[] Serialize(object plan)
        {
            MethodInfo method = GetCodecType().GetMethod("SerializeCanonical", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, "SerializeCanonical method not found.");
            return (byte[])method.Invoke(null, new object[] { plan });
        }

        private static Exception SerializeException(object plan)
        {
            try
            {
                Serialize(plan);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static string SerializeString(object plan)
        {
            return Utf8NoBom.GetString(Serialize(plan));
        }

        private static object GetEntry(object plan, int index)
        {
            MethodInfo method = GetPlanType().GetMethod("GetEntry", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(method, Is.Not.Null, "GetEntry method not found.");
            return method.Invoke(plan, new object[] { index });
        }

        private static Exception GetEntryException(object plan, int index)
        {
            try
            {
                GetEntry(plan, index);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        // ---- Entry value contract ----

        [Test]
        public void Entry_CaptureFrameIdZeroAndNegative_Rejected()
        {
            foreach (long id in new[] { 0L, -1L })
            {
                string s = id.ToString(CultureInfo.InvariantCulture);
                Exception ex = MakeEntryRawException(id, "frames/" + s + ".png.stage", "frames/" + s + ".json.stage", "frames/" + s + ".png", "frames/" + s + ".json", 16, 32, Hash64, Hash64);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("captureFrameId"));
            }
        }

        [Test]
        public void Entry_NullPaths_Rejected()
        {
            Assert.That(MakeEntryRawException(10, null, "frames/10.json.stage", "frames/10.png", "frames/10.json", 16, 32, Hash64, Hash64), Is.TypeOf<ArgumentNullException>().And.Property("ParamName").EqualTo("pngStagingRelativePath"));
            Assert.That(MakeEntryRawException(10, "frames/10.png.stage", null, "frames/10.png", "frames/10.json", 16, 32, Hash64, Hash64), Is.TypeOf<ArgumentNullException>().And.Property("ParamName").EqualTo("sidecarStagingRelativePath"));
            Assert.That(MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", null, "frames/10.json", 16, 32, Hash64, Hash64), Is.TypeOf<ArgumentNullException>().And.Property("ParamName").EqualTo("pngFinalRelativePath"));
            Assert.That(MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", null, 16, 32, Hash64, Hash64), Is.TypeOf<ArgumentNullException>().And.Property("ParamName").EqualTo("sidecarFinalRelativePath"));
        }

        [Test]
        public void Entry_PathExactMatch_Rejected()
        {
            // Wrong extension on each of the four paths.
            Exception ex1 = MakeEntryRawException(10, "frames/10.png", "frames/10.json.stage", "frames/10.png", "frames/10.json", 16, 32, Hash64, Hash64);
            Assert.That(ex1, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex1).ParamName, Is.EqualTo("pngStagingRelativePath"));

            Exception ex2 = MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json", "frames/10.png", "frames/10.json", 16, 32, Hash64, Hash64);
            Assert.That(ex2, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex2).ParamName, Is.EqualTo("sidecarStagingRelativePath"));

            Exception ex3 = MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png.stage", "frames/10.json", 16, 32, Hash64, Hash64);
            Assert.That(ex3, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex3).ParamName, Is.EqualTo("pngFinalRelativePath"));

            Exception ex4 = MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json.stage", 16, 32, Hash64, Hash64);
            Assert.That(ex4, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex4).ParamName, Is.EqualTo("sidecarFinalRelativePath"));
        }

        [Test]
        public void Entry_PathVariants_Rejected()
        {
            // Leading zero, backslash, dot-dot, rooted, different ID, case change.
            foreach (string bad in new[]
            {
                "frames/010.png.stage",
                "frames\\10.png.stage",
                "frames/../10.png.stage",
                "/frames/10.png.stage",
                "frames/11.png.stage",
                "frames/10.PNG.stage"
            })
            {
                Exception ex = MakeEntryRawException(10, bad, "frames/10.json.stage", "frames/10.png", "frames/10.json", 16, 32, Hash64, Hash64);
                Assert.That(ex, Is.TypeOf<ArgumentException>(), "Expected rejection for path: " + bad);
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("pngStagingRelativePath"));
            }
        }

        [Test]
        public void Entry_ByteLengthZero_Rejected()
        {
            Exception ex1 = MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json", 0, 32, Hash64, Hash64);
            Assert.That(ex1, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)ex1).ParamName, Is.EqualTo("pngByteLength"));

            Exception ex2 = MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json", 16, 0, Hash64, Hash64);
            Assert.That(ex2, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)ex2).ParamName, Is.EqualTo("sidecarByteLength"));
        }

        [Test]
        public void Entry_NullHashes_Rejected()
        {
            Exception ex1 = MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json", 16, 32, null, Hash64);
            Assert.That(ex1, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)ex1).ParamName, Is.EqualTo("pngContentSha256"));

            Exception ex2 = MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json", 16, 32, Hash64, null);
            Assert.That(ex2, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)ex2).ParamName, Is.EqualTo("sidecarContentSha256"));
        }

        [Test]
        public void Entry_HashLengthUppercaseNonHex_Rejected()
        {
            foreach (string bad in new[] { Hash64.Substring(0, 63), Hash64 + "0", Hash64.ToUpperInvariant(), "g" + Hash64.Substring(1) })
            {
                Exception ex = MakeEntryRawException(10, "frames/10.png.stage", "frames/10.json.stage", "frames/10.png", "frames/10.json", 16, 32, bad, Hash64);
                Assert.That(ex, Is.TypeOf<ArgumentException>(), "Expected rejection for hash: " + bad);
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("pngContentSha256"));
            }
        }

        [Test]
        public void Entry_Valid_ExposesAllValues()
        {
            object entry = MakeEntry(10, 16, 32);

            Assert.That((long)GetProperty(entry, "CaptureFrameId"), Is.EqualTo(10));
            Assert.That((string)GetProperty(entry, "PngStagingRelativePath"), Is.EqualTo("frames/10.png.stage"));
            Assert.That((string)GetProperty(entry, "SidecarStagingRelativePath"), Is.EqualTo("frames/10.json.stage"));
            Assert.That((string)GetProperty(entry, "PngFinalRelativePath"), Is.EqualTo("frames/10.png"));
            Assert.That((string)GetProperty(entry, "SidecarFinalRelativePath"), Is.EqualTo("frames/10.json"));
            Assert.That((long)GetProperty(entry, "PngByteLength"), Is.EqualTo(16));
            Assert.That((long)GetProperty(entry, "SidecarByteLength"), Is.EqualTo(32));
            Assert.That((string)GetProperty(entry, "PngContentSha256"), Is.EqualTo(Hash64));
            Assert.That((string)GetProperty(entry, "SidecarContentSha256"), Is.EqualTo(Hash64));
        }

        // ---- Plan value contract ----

        [Test]
        public void Plan_TestRunIdZeroAndNegative_Rejected()
        {
            foreach (long id in new[] { 0L, -1L })
            {
                Exception ex = MakePlanRawException(id, InitId, Hash64, MakeEntryArray());
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("testRunId"));
            }
        }

        [Test]
        public void Plan_InitializationIdInvalid_Rejected()
        {
            Exception nullEx = MakePlanRawException(1, null, Hash64, MakeEntryArray());
            Assert.That(nullEx, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullEx).ParamName, Is.EqualTo("runInitializationId"));

            foreach (string bad in new[] { InitId.Substring(0, 31), InitId + "0", InitId.ToUpperInvariant(), "g" + InitId.Substring(1) })
            {
                Exception ex = MakePlanRawException(1, bad, Hash64, MakeEntryArray());
                Assert.That(ex, Is.TypeOf<ArgumentException>(), "Expected rejection for init ID: " + bad);
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("runInitializationId"));
            }
        }

        [Test]
        public void Plan_HashInvalid_Rejected()
        {
            Exception nullEx = MakePlanRawException(1, InitId, null, MakeEntryArray());
            Assert.That(nullEx, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullEx).ParamName, Is.EqualTo("runManifestContentSha256"));

            foreach (string bad in new[] { Hash64.Substring(0, 63), Hash64 + "0", Hash64.ToUpperInvariant(), "g" + Hash64.Substring(1) })
            {
                Exception ex = MakePlanRawException(1, InitId, bad, MakeEntryArray());
                Assert.That(ex, Is.TypeOf<ArgumentException>(), "Expected rejection for hash: " + bad);
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("runManifestContentSha256"));
            }
        }

        [Test]
        public void Plan_NullEntries_Rejected()
        {
            Exception ex = MakePlanRawException(1, InitId, Hash64, null);
            Assert.That(ex, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)ex).ParamName, Is.EqualTo("entries"));
        }

        [Test]
        public void Plan_TooManyEntries_Rejected()
        {
            Array entries = Array.CreateInstance(GetEntryType(), 100001);
            entries.SetValue(MakeEntry(1), 0);

            Exception ex = MakePlanRawException(1, InitId, Hash64, entries);
            Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("entries"));
        }

        [Test]
        public void Plan_NullEntryElement_Rejected()
        {
            Array entries = MakeEntryArray(null, null);

            Exception ex = MakePlanRawException(1, InitId, Hash64, entries);
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("entries"));
        }

        [Test]
        public void Plan_DuplicateOrReverseIds_Rejected()
        {
            foreach (long[] ids in new[] { new long[] { 10, 10 }, new long[] { 20, 10 } })
            {
                Array entries = Array.CreateInstance(GetEntryType(), ids.Length);
                for (int i = 0; i < ids.Length; i++)
                {
                    entries.SetValue(MakeEntry(ids[i]), i);
                }

                Exception ex = MakePlanRawException(1, InitId, Hash64, entries);
                Assert.That(ex, Is.TypeOf<ArgumentException>());
                Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo("entries"));
            }
        }

        [Test]
        public void Plan_EmptyEntries_Accepted()
        {
            object plan = MakePlan();

            Assert.That((int)GetProperty(plan, "SchemaVersion"), Is.EqualTo(1));
            Assert.That((long)GetProperty(plan, "TestRunId"), Is.EqualTo(1));
            Assert.That((string)GetProperty(plan, "RunInitializationId"), Is.EqualTo(InitId));
            Assert.That((string)GetProperty(plan, "RunManifestContentSha256"), Is.EqualTo(Hash64));
            Assert.That((int)GetProperty(plan, "EntryCount"), Is.EqualTo(0));
        }

        [Test]
        public void Plan_DefensiveCopy_InputMutationDoesNotAffectPlan()
        {
            object e10 = MakeEntry(10);
            object e20 = MakeEntry(20);
            Array entries = MakeEntryArray(e10, e20);

            object plan = MakePlan(entries: entries);

            // Mutate the caller's array after construction.
            entries.SetValue(null, 0);
            entries.SetValue(null, 1);

            Assert.That(ReferenceEquals(GetEntry(plan, 0), e10), Is.True);
            Assert.That(ReferenceEquals(GetEntry(plan, 1), e20), Is.True);
            Assert.That((int)GetProperty(plan, "EntryCount"), Is.EqualTo(2));
        }

        [Test]
        public void Plan_GetEntryBoundaries_Rejected()
        {
            object plan = MakePlanWithIds(10);

            foreach (int index in new[] { -1, 1, 5 })
            {
                Exception ex = GetEntryException(plan, index);
                Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(((ArgumentOutOfRangeException)ex).ParamName, Is.EqualTo("index"));
            }
        }

        // ---- Canonical bytes ----

        [Test]
        public void Codec_EmptyPlan_GoldenBytes()
        {
            object plan = MakePlan();
            string expected =
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":0,\"Entries\":[]}";

            Assert.That(Serialize(plan), Is.EqualTo(Utf8NoBom.GetBytes(expected)));
        }

        [Test]
        public void Codec_MultiEntryPlan_GoldenBytes()
        {
            object plan = MakePlanWithIds(10, 20);

            string e1 = "{\"CaptureFrameId\":10,\"PngStagingRelativePath\":\"frames/10.png.stage\",\"SidecarStagingRelativePath\":\"frames/10.json.stage\",\"PngFinalRelativePath\":\"frames/10.png\",\"SidecarFinalRelativePath\":\"frames/10.json\",\"PngByteLength\":16,\"SidecarByteLength\":32,\"PngContentSha256\":\"" + Hash64 + "\",\"SidecarContentSha256\":\"" + Hash64 + "\"}";
            string e2 = "{\"CaptureFrameId\":20,\"PngStagingRelativePath\":\"frames/20.png.stage\",\"SidecarStagingRelativePath\":\"frames/20.json.stage\",\"PngFinalRelativePath\":\"frames/20.png\",\"SidecarFinalRelativePath\":\"frames/20.json\",\"PngByteLength\":16,\"SidecarByteLength\":32,\"PngContentSha256\":\"" + Hash64 + "\",\"SidecarContentSha256\":\"" + Hash64 + "\"}";
            string expected =
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"" + InitId +
                "\",\"RunManifestContentSha256\":\"" + Hash64 +
                "\",\"EntryCount\":2,\"Entries\":[" + e1 + "," + e2 + "]}";

            Assert.That(Serialize(plan), Is.EqualTo(Utf8NoBom.GetBytes(expected)));
        }

        [Test]
        public void Codec_PropertyOrderBomNewlineWhitespace()
        {
            object plan = MakePlanWithIds(10);
            byte[] bytes = Serialize(plan);
            string json = Utf8NoBom.GetString(bytes);

            // No BOM.
            Assert.That(bytes.Length, Is.GreaterThanOrEqualTo(3));
            Assert.That(!(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF), Is.True, "BOM must not be present.");

            // No trailing newline.
            Assert.That(bytes[bytes.Length - 1], Is.Not.EqualTo((byte)'\n'));

            // No whitespace of any kind.
            Assert.That(json, Does.Not.Contain(" "));
            Assert.That(json, Does.Not.Contain("\n"));
            Assert.That(json, Does.Not.Contain("\r"));
            Assert.That(json, Does.Not.Contain("\t"));

            // Fixed top-level property order.
            int schema = json.IndexOf("\"SchemaVersion\"", StringComparison.Ordinal);
            int testRun = json.IndexOf("\"TestRunId\"", StringComparison.Ordinal);
            int init = json.IndexOf("\"RunInitializationId\"", StringComparison.Ordinal);
            int hash = json.IndexOf("\"RunManifestContentSha256\"", StringComparison.Ordinal);
            int count = json.IndexOf("\"EntryCount\"", StringComparison.Ordinal);
            int entries = json.IndexOf("\"Entries\"", StringComparison.Ordinal);
            Assert.That(schema, Is.LessThan(testRun));
            Assert.That(testRun, Is.LessThan(init));
            Assert.That(init, Is.LessThan(hash));
            Assert.That(hash, Is.LessThan(count));
            Assert.That(count, Is.LessThan(entries));
        }

        [Test]
        public void Codec_LongMaxValue_NoExponent()
        {
            Array entries = MakeEntryArray(MakeEntry(long.MaxValue, long.MaxValue, long.MaxValue));
            object plan = MakePlan(long.MaxValue, entries: entries);

            string json = SerializeString(plan);

            Assert.That(json, Does.Contain("9223372036854775807"));
            Assert.That(json, Does.Not.Contain("E+"));
            Assert.That(json, Does.Not.Contain("e+"));
        }

        [Test]
        public void Codec_RepeatSerialize_ByteIdentical()
        {
            object plan = MakePlanWithIds(10, 20, 30);

            byte[] first = Serialize(plan);
            byte[] second = Serialize(plan);

            Assert.That(first, Is.EqualTo(second));
        }

        [Test]
        public void Codec_Within16MiB_Accepted()
        {
            object plan = MakePlan(entries: MakeManyEntries(1000));

            byte[] bytes = Serialize(plan);

            Assert.That(bytes.Length, Is.LessThan(16 * 1024 * 1024));
        }

        [Test]
        public void Codec_Exceeds16MiB_Rejected()
        {
            object plan = MakePlan(entries: MakeManyEntries(50000));

            Exception ex = SerializeException(plan);
            Assert.That(ex, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void Codec_DoesNotReorderOrMutateEntries()
        {
            object plan = MakePlanWithIds(10, 20, 30);

            byte[] before = Serialize(plan);

            // Entry order is unchanged and references are stable.
            Assert.That((long)GetProperty(GetEntry(plan, 0), "CaptureFrameId"), Is.EqualTo(10));
            Assert.That((long)GetProperty(GetEntry(plan, 1), "CaptureFrameId"), Is.EqualTo(20));
            Assert.That((long)GetProperty(GetEntry(plan, 2), "CaptureFrameId"), Is.EqualTo(30));

            byte[] after = Serialize(plan);
            Assert.That(after, Is.EqualTo(before));
        }

        // ---- Type contracts ----

        [Test]
        public void Types_InternalSealedNotDisposableNotUnityObject()
        {
            foreach (Type type in new[] { GetEntryType(), GetPlanType() })
            {
                Assert.That(type.IsNotPublic, Is.True, type.Name + " must be internal.");
                Assert.That(type.IsSealed, Is.True, type.Name + " must be sealed.");
                Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False, type.Name + " must not be IDisposable.");
                Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False, type.Name + " must not be a MonoBehaviour.");
                Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False, type.Name + " must not be a ScriptableObject.");
                Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, type.Name + " must have no public constructor.");
            }
        }

        [Test]
        public void Types_HoldOnlyValueFields()
        {
            foreach (FieldInfo field in GetEntryType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType == typeof(long) || field.FieldType == typeof(string),
                    GetEntryType().Name + "." + field.Name + " must be a long or string, not " + field.FieldType.Name);
            }

            foreach (FieldInfo field in GetPlanType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                bool allowed = field.FieldType == typeof(long)
                    || field.FieldType == typeof(string)
                    || field.FieldType == GetEntryType().MakeArrayType();
                Assert.That(allowed, GetPlanType().Name + "." + field.Name + " must be a long, string, or entry array, not " + field.FieldType.Name);
            }
        }

        [Test]
        public void Codec_IsStaticAndHoldsNoState()
        {
            Type type = GetCodecType();
            Assert.That(type.IsAbstract, Is.True, "Codec must be a static class.");
            Assert.That(type.IsSealed, Is.True, "Codec must be a static class.");
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty, "Codec must hold no instance state.");
        }
    }
}
