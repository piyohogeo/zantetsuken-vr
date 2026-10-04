using NUnit.Framework;

namespace Zantetsu.Core.Tests
{
    public class ManagedHeapPacingTests
    {
        [Test]
        public void AutomaticSlice_DefaultsToOneMillisecond_EngineLeavesItsSettingUnchanged()
        {
            Assert.That(ManagedHeapPacing.WayFor(null), Is.EqualTo(ManagedHeapPacing.Way.Automatic));
            Assert.That(ManagedHeapPacing.WayFor(new string[0]), Is.EqualTo(ManagedHeapPacing.Way.Automatic));
            Assert.That(ManagedHeapPacing.WayFor(new[] { "player.exe", "-zantetsuCityWalk", "-zantetsuXrAudioGuidReuse" }), Is.EqualTo(ManagedHeapPacing.Way.Automatic), "a Player's ordinary arguments");
            Assert.That(ManagedHeapPacing.AdoptedSliceNanoseconds, Is.EqualTo(1000000UL), "1 ms");
            Assert.That(ManagedHeapPacing.SliceToSetFor(null), Is.EqualTo(1000000UL), "what a Player sets incrementalTimeSliceNanoseconds to with no argument");
            Assert.That(ManagedHeapPacing.SliceToSetFor(new string[0]), Is.EqualTo(1000000UL));
            Assert.That(ManagedHeapPacing.SliceToSetFor(new[] { "player.exe", "-zantetsuCityWalk", "-zantetsuXrAudioGuidReuse" }), Is.EqualTo(1000000UL));
            Assert.That(ManagedHeapPacing.SliceToSetFor(new[] { ManagedHeapPacing.WayArgument, "auto", ManagedHeapPacing.SliceArgument, "3" }), Is.EqualTo(3000000UL), "another slice only when said");
            Assert.That(ManagedHeapPacing.SliceToSetFor(new[] { ManagedHeapPacing.SliceArgument, "0.2" }), Is.EqualTo(200000UL), "the slice argument alone is the way auto's");
            Assert.That(ManagedHeapPacing.SliceToSetFor(new[] { ManagedHeapPacing.WayArgument, "engine" }), Is.Null, "the engine's own: nothing is set");
            Assert.That(ManagedHeapPacing.SliceToSetFor(new[] { ManagedHeapPacing.WayArgument, "engine", ManagedHeapPacing.SliceArgument, "1" }), Is.Null);
            Assert.That(ManagedHeapPacing.WayFor(new[] { "x", ManagedHeapPacing.WayArgument, "engine" }), Is.EqualTo(ManagedHeapPacing.Way.Engine), "the engine's own only when named");
            Assert.That(ManagedHeapPacing.WayFor(new[] { ManagedHeapPacing.WayArgument, "auto" }), Is.EqualTo(ManagedHeapPacing.Way.Automatic));
            Assert.That(ManagedHeapPacing.WayFor(new[] { ManagedHeapPacing.WayArgument, "other" }), Is.EqualTo(ManagedHeapPacing.Way.Automatic), "a name that is unrecognized");
            Assert.That(ManagedHeapPacing.WayFor(new[] { ManagedHeapPacing.WayArgument }), Is.EqualTo(ManagedHeapPacing.Way.Automatic), "no value");
            Assert.That(ManagedHeapPacing.SliceNanosecondsFor(null), Is.EqualTo(1000000UL));
            Assert.That(ManagedHeapPacing.SliceNanosecondsFor(new string[0]), Is.EqualTo(1000000UL));
            Assert.That(ManagedHeapPacing.SliceNanosecondsFor(new[] { ManagedHeapPacing.SliceArgument, "1" }), Is.EqualTo(1000000UL), "the way auto's slice, 1 ms");
            Assert.That(ManagedHeapPacing.SliceNanosecondsFor(new[] { ManagedHeapPacing.SliceArgument, "0.2" }), Is.EqualTo(200000UL));
            Assert.That(ManagedHeapPacing.SliceNanosecondsFor(new[] { ManagedHeapPacing.SliceArgument, "3" }), Is.EqualTo(3000000UL));
            Assert.That(ManagedHeapPacing.SliceNanosecondsFor(new[] { ManagedHeapPacing.SliceArgument, "0" }), Is.EqualTo(1000000UL), "out of range: 1 ms");
            Assert.That(ManagedHeapPacing.SliceNanosecondsFor(new[] { ManagedHeapPacing.SliceArgument, "20" }), Is.EqualTo(1000000UL));
            Assert.That(ManagedHeapPacing.SliceNanosecondsFor(new[] { ManagedHeapPacing.SliceArgument, "x" }), Is.EqualTo(1000000UL));
        }
    }
}
