using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Unity.Profiling;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class ProfilerMarkersTests
    {
        private static readonly string[] ExpectedMarkerNames =
        {
            "Zantetsu.Slash.CandidateSearch",
            "Zantetsu.Slash.FrontAdvance",
            "Zantetsu.Slash.FrontSweep",
            "Zantetsu.Slash.TopologyValidate",
            "Zantetsu.Future.PredictPose",
            "Zantetsu.Physics.Predict",
            "Zantetsu.Mesh.Classify",
            "Zantetsu.Mesh.BuildCap",
            "Zantetsu.Convex.Slice",
            "Zantetsu.Commit.Validate",
            "Zantetsu.Commit.Apply",
            "Zantetsu.Trace.Drain",
            "Zantetsu.Capture.Copy",
            "Zantetsu.Capture.Encode",
        };

        [Test]
        public void MarkerNames_MatchDesignDocExactly()
        {
            FieldInfo[] fields = typeof(ZantetsuProfilerMarkers).GetFields(BindingFlags.Public | BindingFlags.Static);
            HashSet<string> names = new HashSet<string>();
            int stringFieldCount = 0;
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(string))
                {
                    stringFieldCount++;
                    names.Add((string)field.GetValue(null));
                }
            }

            Assert.That(stringFieldCount, Is.EqualTo(14));
            Assert.That(names.Count, Is.EqualTo(14), "Duplicate marker name detected");

            foreach (string expected in ExpectedMarkerNames)
            {
                Assert.That(names.Contains(expected), Is.True, "Missing marker name: " + expected);
            }
        }

        [Test]
        public void MarkerNames_AreNotDynamic()
        {
            FieldInfo[] fields = typeof(ZantetsuProfilerMarkers).GetFields(BindingFlags.Public | BindingFlags.Static);
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(string))
                {
                    string name = (string)field.GetValue(null);
                    Assert.That(name.Contains("{"), Is.False, name + " contains a format placeholder");
                    Assert.That(name.Contains("}"), Is.False, name + " contains a format placeholder");
                }
            }
        }

        [Test]
        public void Markers_AreStaticReadonly()
        {
            FieldInfo[] fields = typeof(ZantetsuProfilerMarkers).GetFields(BindingFlags.Public | BindingFlags.Static);
            int markerCount = 0;
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(ProfilerMarker))
                {
                    markerCount++;
                    Assert.That(field.IsStatic, Is.True, field.Name + " is not static");
                    Assert.That(field.IsInitOnly, Is.True, field.Name + " is not readonly");
                }
            }

            Assert.That(markerCount, Is.EqualTo(14));
        }

        [Test]
        public void TraceLogger_Drain_StillWorksWithSharedMarker()
        {
            using (TraceLogger logger = new TraceLogger(4))
            {
                logger.Enqueue(new TraceEvent { Timestamp = 1 });

                Assert.That(logger.Drain(), Is.EqualTo(1));
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
            }
        }
    }
}
