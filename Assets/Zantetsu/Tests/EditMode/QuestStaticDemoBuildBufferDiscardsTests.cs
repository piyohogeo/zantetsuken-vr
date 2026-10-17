using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.EditorTools.Quest;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The Meta Quest feature's optimizeBufferDiscards (unit 22, 2026-10-17): the tree has the product's ON; the OFF
    /// experiment turns it off for one build and puts it back whatever the build did; the normal build touches nothing.
    /// The tree's real feature asset is used; no build is run.
    /// </summary>
    public sealed class QuestStaticDemoBuildBufferDiscardsTests
    {
        private static bool TreeValue() => QuestStaticDemoBuild.MetaQuestBufferDiscardsInTree();

        [Test]
        public void TheTree_HasTheProductsOn()
        {
            Assert.That(TreeValue(), Is.True, "optimizeBufferDiscards ON: off, the GPU memory grew ~631 MB an XR session restart (SLEEP-KILL-1 22d-22j)");
        }

        [Test]
        public void TheOffExperiment_TurnsItOffForTheBody_AndPutsItBack_EvenWhenTheBodyFails()
        {
            Assume.That(TreeValue(), Is.True);
            bool seenOff = false;
            LogAssert.Expect(LogType.Warning, new Regex("optimizeBufferDiscards=False for this build"));
            LogAssert.Expect(LogType.Log, new Regex("put back to True"));
            QuestStaticDemoBuild.WithMetaQuestBufferDiscards(false, () => { seenOff = !TreeValue(); });
            Assert.That(seenOff, Is.True, "the body ran with it off");
            Assert.That(TreeValue(), Is.True, "put back");

            LogAssert.Expect(LogType.Warning, new Regex("optimizeBufferDiscards=False for this build"));
            LogAssert.Expect(LogType.Log, new Regex("put back to True"));
            Assert.Throws<InvalidOperationException>(() => QuestStaticDemoBuild.WithMetaQuestBufferDiscards(false, () => throw new InvalidOperationException("the build failed")));
            Assert.That(TreeValue(), Is.True, "put back although the body failed");
        }

        [Test]
        public void TheNormalBuild_TouchesNothing()
        {
            Assume.That(TreeValue(), Is.True);
            bool ran = false;
            LogAssert.Expect(LogType.Log, new Regex("optimizeBufferDiscards=True \\(as the tree has it\\)"));
            QuestStaticDemoBuild.WithMetaQuestBufferDiscards(true, () => { ran = TreeValue(); });
            Assert.That(ran, Is.True);
            Assert.That(TreeValue(), Is.True);
        }
    }
}
