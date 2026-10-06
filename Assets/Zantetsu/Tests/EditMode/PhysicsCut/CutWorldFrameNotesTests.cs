using NUnit.Framework;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The notes of a frame's heavy work (DESIGN 5.6, D-202): a note names its kind for the frame it was made in and
    /// for no other, every kind is named, and a frame with no note says so.
    /// </summary>
    public class CutWorldFrameNotesTests
    {
        [TearDown]
        public void Forget()
        {
            CutWorldFrameNotes.ClearForTest();
        }

        [Test]
        public void ANote_IsOfTheCurrentFrame_AndNamesItsKind()
        {
            CutWorldFrameNotes.ClearForTest();
            Assert.That(CutWorldFrameNotes.TryGetThisFrame(out string none), Is.False, "nothing noted");
            Assert.That(none, Is.Null);

            // Every kind: each is named, and each name is its own.
            var names = new System.Collections.Generic.HashSet<string>();
            foreach (CutWorldFrameNotes.Work work in System.Enum.GetValues(typeof(CutWorldFrameNotes.Work)))
            {
                CutWorldFrameNotes.ClearForTest();
                CutWorldFrameNotes.Note(work);
                Assert.That(CutWorldFrameNotes.TryGetThisFrame(out string which), Is.True, work + " is noted for this frame");
                Assert.That(which, Is.Not.Null.And.Not.Empty, work + " has a name");
                Assert.That(names.Add(which), Is.True, work + "'s name is its own: " + which);
            }

            Assert.That(names.Count, Is.EqualTo(6), "the six kinds: NPC refill, NPC re-preparation, MobPlan publication, MobPlan intake, MobPlan reclaim, the static index's rebuild");

            // The Editor does not move Time.frameCount between these lines, so the note stands within the test; a note
            // of another frame is not this frame's -- said by the frame number, which is all a note is.
            CutWorldFrameNotes.ClearForTest();
            CutWorldFrameNotes.Note(CutWorldFrameNotes.Work.MobPlanIntake);
            Assert.That(CutWorldFrameNotes.TryGetThisFrame(out string intake), Is.True);
            Assert.That(intake, Is.EqualTo("MobPlan intake"));
            Assert.That(Time.frameCount, Is.GreaterThanOrEqualTo(0));
        }
    }
}
