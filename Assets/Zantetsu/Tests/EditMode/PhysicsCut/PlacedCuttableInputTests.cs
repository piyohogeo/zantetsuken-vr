using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// A placed input's anchors (TL, 2026-10-03): having none is a normal input whatever the family, so an input written
    /// without the field reads as one with none -- never as a missing value a registration would refuse -- and the anchors
    /// written are read as they are.
    /// </summary>
    public sealed class PlacedCuttableInputTests
    {
        [Test]
        public void AnInputWithoutTheAnchorsField_HasNone()
        {
            var data = JsonUtility.FromJson<PlacedCuttableInput>("{\"name\":\"bench\",\"isBuilding\":false,\"isCuttable\":true}");
            Assert.That(data.anchors, Is.Not.Null);
            Assert.That(data.anchors, Is.Empty);
        }

        [Test]
        public void AnInputWithAnEmptyOrAWrittenAnchorList_HasThoseAnchors()
        {
            Assert.That(JsonUtility.FromJson<PlacedCuttableInput>("{\"anchors\":[]}").anchors, Is.Empty);
            Vector3[] two = JsonUtility.FromJson<PlacedCuttableInput>("{\"anchors\":[{\"x\":1,\"y\":0,\"z\":2},{\"x\":-1,\"y\":0,\"z\":2}]}").anchors;
            Assert.That(two, Is.EqualTo(new[] { new Vector3(1f, 0f, 2f), new Vector3(-1f, 0f, 2f) }));
        }
    }
}
