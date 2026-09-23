using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The fixtures' own clean-up, on made-up lists: what a failure to destroy one entry does to that entry, to the
    /// ones after it in the same list, and to every list after it. Nothing of the product is involved; the failure
    /// is an entry that is not a Unity object at all, which cannot be destroyed.
    /// </summary>
    public class CutFixtureTeardownTests
    {
        private readonly List<GameObject> _survivors = new List<GameObject>();

        [TearDown]
        public void DestroyWhatTheTestKeptAlive()
        {
            foreach (GameObject survivor in _survivors)
            {
                if (survivor != null)
                {
                    Object.DestroyImmediate(survivor);
                }
            }

            _survivors.Clear();
        }

        private GameObject Kept(string name)
        {
            var made = new GameObject(name);
            _survivors.Add(made);
            return made;
        }

        [Test]
        public void AfterTheFirstFailure_NoLaterListIsTouched_AndWhatWasNotProcessedStays()
        {
            GameObject firstOnly = Kept("first-0");
            GameObject secondBefore = Kept("second-0");
            GameObject secondAfter = Kept("second-2");
            GameObject thirdOnly = Kept("third-0");
            var first = new List<object> { firstOnly };
            var second = new List<object> { secondBefore, "not a Unity object", secondAfter };
            var third = new List<object> { thirdOnly };
            var failures = new List<string>();

            bool all = CutFixtureTeardown.DestroyInOrder(
                failures,
                new KeyValuePair<string, IList>("first", first),
                new KeyValuePair<string, IList>("second", second),
                new KeyValuePair<string, IList>("third", third));

            Assert.That(all, Is.False, "the second list had an entry that could not be destroyed");

            // Before the failure: destroyed, and gone from the list.
            Assert.That(first, Is.Empty, "the first list was emptied");
            Assert.That(firstOnly == null, Is.True, "and its object was destroyed");
            Assert.That(secondBefore == null, Is.True, "the entry before the failure in the second list was destroyed");

            // The failure and after it, in the same list: kept, untouched.
            Assert.That(second.Count, Is.EqualTo(2), "the entry that threw and the one after it stay");
            Assert.That(second[0], Is.EqualTo("not a Unity object"), "the one that threw is first");
            Assert.That(ReferenceEquals(second[1], secondAfter), Is.True, "and the one after it is still listed");
            Assert.That(secondAfter != null, Is.True, "and was not destroyed");

            // Every later list: not touched at all.
            Assert.That(third.Count, Is.EqualTo(1), "the third list was not touched");
            Assert.That(ReferenceEquals(third[0], thirdOnly), Is.True);
            Assert.That(thirdOnly != null, Is.True, "and its object is alive");

            Assert.That(failures.Count, Is.EqualTo(2), "one line for the entry, one for the lists after it");
            Assert.That(failures[0], Does.StartWith("second: destroying entry 1 threw"));
            Assert.That(failures[1], Does.Contain("'second'").And.Contain("third"));
        }

        [Test]
        public void WithNoFailure_EveryListIsEmptiedInOrder_AndNothingIsReported()
        {
            GameObject a = Kept("a");
            GameObject b = Kept("b");
            GameObject c = Kept("c");
            var first = new List<object> { a, null };
            var second = new List<object> { b, c };
            var failures = new List<string>();

            bool all = CutFixtureTeardown.DestroyInOrder(
                failures,
                new KeyValuePair<string, IList>("first", first),
                new KeyValuePair<string, IList>("second", second));

            Assert.That(all, Is.True);
            Assert.That(first, Is.Empty, "a null entry is skipped and removed like the rest");
            Assert.That(second, Is.Empty);
            Assert.That(a == null && b == null && c == null, Is.True, "every object was destroyed");
            Assert.That(failures, Is.Empty);
        }
    }
}
