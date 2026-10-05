using System;
using NUnit.Framework;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The audio device id diagnosis's rules (2026-10-04), without any runtime: what is counted in the observe mode, and
    /// in the reuse mode that only a successful answer is kept, for its instance and its direction, and that nothing is
    /// kept across an instance's destruction or into a new instance, nor for a runtime that is not the Simulator.
    /// </summary>
    public sealed class XrAudioGuidDiagnosisTests
    {
        private static char[] Guid(string text)
        {
            var b = new char[XrAudioGuidDiagnosis.GuidLength];
            text.AsSpan().CopyTo(b);
            return b;
        }

        private static string Text(char[] b)
        {
            int end = Array.IndexOf(b, '\0');
            return new string(b, 0, end < 0 ? b.Length : end);
        }

        [Test]
        public void Arguments_NoneIsOff_ObserveAndReuse_ReuseWins()
        {
            Assert.That(XrAudioGuidDiagnosis.ModeFromArguments(null), Is.EqualTo(XrAudioGuidDiagnosis.DiagnosisMode.Off));
            Assert.That(XrAudioGuidDiagnosis.ModeFromArguments(new[] { "player.exe", "-zantetsuCityWalk" }), Is.EqualTo(XrAudioGuidDiagnosis.DiagnosisMode.Off));
            Assert.That(XrAudioGuidDiagnosis.ModeFromArguments(new[] { "x", XrAudioGuidDiagnosis.ObserveArgument }), Is.EqualTo(XrAudioGuidDiagnosis.DiagnosisMode.Observe));
            Assert.That(XrAudioGuidDiagnosis.ModeFromArguments(new[] { XrAudioGuidDiagnosis.ReuseArgument, XrAudioGuidDiagnosis.ObserveArgument }), Is.EqualTo(XrAudioGuidDiagnosis.DiagnosisMode.Reuse));
            Assert.That(XrAudioGuidDiagnosis.ModeFromArguments(new[] { "-zantetsuxraudioguidobserve" }), Is.EqualTo(XrAudioGuidDiagnosis.DiagnosisMode.Off), "the argument is exact");
        }

        [Test]
        public void SimulatorOnly_ByTheRuntimesName()
        {
            Assert.That(XrAudioGuidDiagnosis.IsSimulatorRuntime("Meta XR Simulator"), Is.True);
            Assert.That(XrAudioGuidDiagnosis.IsSimulatorRuntime("Oculus"), Is.False);
            Assert.That(XrAudioGuidDiagnosis.IsSimulatorRuntime(""), Is.False);
            Assert.That(XrAudioGuidDiagnosis.IsSimulatorRuntime(null), Is.False);
        }

        [Test]
        public void Off_DescribesItself_AndKeepsNothing()
        {
            var d = new XrAudioGuidDiagnosis();
            Assert.That(d.Mode, Is.EqualTo(XrAudioGuidDiagnosis.DiagnosisMode.Off));
            Assert.That(d.Describe(), Does.StartWith("off"));
            var buffer = new char[XrAudioGuidDiagnosis.GuidLength];
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.False);
        }

        [Test]
        public void Observe_CountsCallsTimesResultsAndChanges_AndNeverAnswersItself()
        {
            var d = new XrAudioGuidDiagnosis();
            d.Begin(XrAudioGuidDiagnosis.DiagnosisMode.Observe);
            d.OnInstanceCreate(7, "Meta XR Simulator", true);
            d.NoteLookup(XrAudioGuidDiagnosis.Output);
            d.NoteLookup(XrAudioGuidDiagnosis.Input);
            var buffer = new char[XrAudioGuidDiagnosis.GuidLength];
            for (int i = 0; i < 5; i++)
            {
                Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.False, "observing never answers");
                d.Record(XrAudioGuidDiagnosis.Output, 7, XrAudioGuidDiagnosis.Success, 100 + i, Guid(i < 3 ? "{out-a}" : "{out-b}"));
            }

            d.Record(XrAudioGuidDiagnosis.Input, 7, -12, 50, Guid("garbage of a failed call"));
            d.Record(XrAudioGuidDiagnosis.Input, 7, XrAudioGuidDiagnosis.Success, 60, Guid("{in}"));

            Assert.That(d.OutputLookups, Is.EqualTo(1));
            Assert.That(d.InputLookups, Is.EqualTo(1));
            Assert.That(d.Calls(XrAudioGuidDiagnosis.Output), Is.EqualTo(5));
            Assert.That(d.Forwarded(XrAudioGuidDiagnosis.Output), Is.EqualTo(5));
            Assert.That(d.Reused(XrAudioGuidDiagnosis.Output), Is.EqualTo(0));
            Assert.That(d.ValueChanges(XrAudioGuidDiagnosis.Output), Is.EqualTo(1), "a to b once");
            Assert.That(d.LastValue(XrAudioGuidDiagnosis.Output), Is.EqualTo("{out-b}"));
            Assert.That(d.Failed(XrAudioGuidDiagnosis.Input), Is.EqualTo(1));
            Assert.That(d.LastValue(XrAudioGuidDiagnosis.Input), Is.EqualTo("{in}"), "a failed call's buffer is not taken as a value");
            Assert.That(d.HasKept(XrAudioGuidDiagnosis.Output), Is.False, "nothing is kept when observing");
            string text = d.Describe();
            Assert.That(text, Does.Contain("mode Observe").And.Contain("output: calls 5, passed on 5").And.Contain("-12: 1"));
        }

        [Test]
        public void Reuse_KeepsOnlyASuccess_PerInstanceAndDirection()
        {
            var d = new XrAudioGuidDiagnosis();
            d.Begin(XrAudioGuidDiagnosis.DiagnosisMode.Reuse);
            d.OnInstanceCreate(7, "Meta XR Simulator", true);
            var buffer = new char[XrAudioGuidDiagnosis.GuidLength];

            // a failure first: not kept, the next call is passed on again
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.False);
            d.Record(XrAudioGuidDiagnosis.Output, 7, -1, 10, Guid("{bad}"));
            Assert.That(d.HasKept(XrAudioGuidDiagnosis.Output), Is.False, "a failed answer is not kept");
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.False);

            // a success: kept for the output of instance 7 only
            d.Record(XrAudioGuidDiagnosis.Output, 7, XrAudioGuidDiagnosis.Success, 10, Guid("{out}"));
            Assert.That(d.HasKept(XrAudioGuidDiagnosis.Output), Is.True);
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.True);
            Assert.That(Text(buffer), Is.EqualTo("{out}"));
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Input, 7, buffer), Is.False, "the input has its own");
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 8, buffer), Is.False, "another instance is not answered from this one's");
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 0, buffer), Is.False);

            d.Record(XrAudioGuidDiagnosis.Input, 7, XrAudioGuidDiagnosis.Success, 10, Guid("{in}"));
            Array.Clear(buffer, 0, buffer.Length);
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Input, 7, buffer), Is.True);
            Assert.That(Text(buffer), Is.EqualTo("{in}"));

            // an answer for an instance that is not the current one is not kept
            d.Record(XrAudioGuidDiagnosis.Output, 9, XrAudioGuidDiagnosis.Success, 10, Guid("{other}"));
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 9, buffer), Is.False);

            Assert.That(d.Reused(XrAudioGuidDiagnosis.Output), Is.EqualTo(1));
            Assert.That(d.Forwarded(XrAudioGuidDiagnosis.Output), Is.EqualTo(3));
            Assert.That(d.Calls(XrAudioGuidDiagnosis.Output), Is.EqualTo(4));

            // a buffer too short is never written into
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, new char[8]), Is.False);
        }

        [Test]
        public void Reuse_NothingSurvivesTheInstancesDestruction_NorIsCarriedIntoANewInstance()
        {
            var d = new XrAudioGuidDiagnosis();
            d.Begin(XrAudioGuidDiagnosis.DiagnosisMode.Reuse);
            d.OnInstanceCreate(7, "Meta XR Simulator", true);
            var buffer = new char[XrAudioGuidDiagnosis.GuidLength];
            d.Record(XrAudioGuidDiagnosis.Output, 7, XrAudioGuidDiagnosis.Success, 10, Guid("{out}"));
            d.Record(XrAudioGuidDiagnosis.Input, 7, XrAudioGuidDiagnosis.Success, 10, Guid("{in}"));
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.True);

            d.OnInstanceDestroy(7);
            Assert.That(d.HasKept(XrAudioGuidDiagnosis.Output), Is.False);
            Assert.That(d.HasKept(XrAudioGuidDiagnosis.Input), Is.False);
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.False, "after the destruction, even for the same handle value");

            Assert.That(d.DescribeReuseCounts(), Does.Match(@"^output: real calls \d+ / cache hits \d+; input: real calls \d+ / cache hits \d+$"),
                "the reuse mode's last line: the two counts of each direction and nothing else");
            Assert.That(d.DescribeReuseCounts(), Is.EqualTo("output: real calls " + d.Forwarded(XrAudioGuidDiagnosis.Output) + " / cache hits " + d.Reused(XrAudioGuidDiagnosis.Output)
                + "; input: real calls " + d.Forwarded(XrAudioGuidDiagnosis.Input) + " / cache hits " + d.Reused(XrAudioGuidDiagnosis.Input)));

            // the same handle value again: a new instance starts with nothing kept
            d.OnInstanceCreate(7, "Meta XR Simulator", true);
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.False);
            d.Record(XrAudioGuidDiagnosis.Output, 7, XrAudioGuidDiagnosis.Success, 10, Guid("{out-2}"));
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.True);
            Assert.That(Text(buffer), Is.EqualTo("{out-2}"));

            // an instance created without a destruction in between: still nothing carried over
            d.OnInstanceCreate(8, "Meta XR Simulator", true);
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.False);
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 8, buffer), Is.False);
            Assert.That(d.InstancesCreated, Is.EqualTo(3));
            Assert.That(d.InstancesDestroyed, Is.EqualTo(1));
        }

        [Test]
        public void Reuse_ForARuntimeThatIsNotTheSimulator_OnlyObserves()
        {
            var d = new XrAudioGuidDiagnosis();
            d.Begin(XrAudioGuidDiagnosis.DiagnosisMode.Reuse);
            d.OnInstanceCreate(7, "Oculus", XrAudioGuidDiagnosis.IsSimulatorRuntime("Oculus"));
            var buffer = new char[XrAudioGuidDiagnosis.GuidLength];
            d.Record(XrAudioGuidDiagnosis.Output, 7, XrAudioGuidDiagnosis.Success, 10, Guid("{out}"));
            Assert.That(d.HasKept(XrAudioGuidDiagnosis.Output), Is.False);
            Assert.That(d.TryReuse(XrAudioGuidDiagnosis.Output, 7, buffer), Is.False);
            Assert.That(d.Describe(), Does.Contain("not allowed for this runtime"));
        }
    }
}
