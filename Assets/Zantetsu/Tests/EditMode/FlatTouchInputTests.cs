using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Tests
{
    public class FlatTouchInputTests
    {
        private static FlatTouchInput Make() => new FlatTouchInput
        {
            StickCentre = new Vector2(100, 100), StickRadius = 60,
            MinimumFlickPixels = 20, MinimumFlickPixelsPerSecond = 500
        };
        [Test]
        public void StickAndFlickKeepIndependentFingerOwnership()
        {
            var input = Make();
            input.Feed(1, FlatTouchPhase.Began, new Vector2(100, 100), 0);
            input.Feed(1, FlatTouchPhase.Moved, new Vector2(100, 160), .05);
            input.Feed(2, FlatTouchPhase.Began, new Vector2(500, 500), .05);
            var flick = input.Feed(2, FlatTouchPhase.Moved, new Vector2(550, 550), .10);
            Assert.That(flick.Flick, Is.True);
            Assert.That(Vector2.Distance(flick.Direction, new Vector2(1, 1).normalized), Is.LessThan(1e-5));
            Assert.That(input.Stick, Is.EqualTo(Vector2.up));
            Assert.That(input.Feed(2, FlatTouchPhase.Moved, new Vector2(650, 650), .15).Flick, Is.False);
            Assert.That(input.Feed(2, FlatTouchPhase.Ended, new Vector2(650, 650), .16).Flick, Is.False);
            input.Feed(1, FlatTouchPhase.Ended, new Vector2(100, 160), .17);
            Assert.That(input.Stick, Is.EqualTo(Vector2.zero));
        }
        [Test]
        public void UiAndStickTouchesCannotBecomeSlashAfterCrossingIntoPlayArea()
        {
            var input = Make();
            input.Feed(1, FlatTouchPhase.Began, new Vector2(500, 500), 0, true);
            Assert.That(input.Feed(1, FlatTouchPhase.Moved, new Vector2(800, 500), .1).Flick, Is.False);
            input.Feed(2, FlatTouchPhase.Began, new Vector2(100, 100), 0);
            Assert.That(input.Feed(2, FlatTouchPhase.Moved, new Vector2(800, 500), .1).Flick, Is.False);
        }
        [Test]
        public void CancellationRequiresAllContactsReleasedBeforeNewGesture()
        {
            var input = Make();
            input.Feed(1, FlatTouchPhase.Began, new Vector2(500, 500), 0);
            input.Cancel();
            Assert.That(input.Feed(1, FlatTouchPhase.Moved, new Vector2(800, 500), .05).Flick, Is.False);
            input.EndFrame(true);
            Assert.That(input.AwaitingRelease, Is.True);
            input.EndFrame(false);
            input.Feed(2, FlatTouchPhase.Began, new Vector2(500, 500), .1);
            Assert.That(input.Feed(2, FlatTouchPhase.Moved, new Vector2(550, 500), .15).Flick, Is.True);
        }
        [Test]
        public void MissingEndEventStillStopsStickWhenNoContactsRemain()
        {
            var input = Make();
            input.Feed(1, FlatTouchPhase.Began, new Vector2(100, 100), 0);
            input.Feed(1, FlatTouchPhase.Moved, new Vector2(100, 160), .05);
            Assert.That(input.Stick, Is.EqualTo(Vector2.up));
            input.EndFrame(false);
            Assert.That(input.Stick, Is.EqualTo(Vector2.zero));
            input.Feed(1, FlatTouchPhase.Began, new Vector2(500, 500), .1);
            Assert.That(input.Feed(1, FlatTouchPhase.Moved, new Vector2(550, 500), .15).Flick, Is.True);
        }
        [Test]
        public void SlowDragDoesNotFireAndStrokeLengthOnlyChangesRecognition()
        {
            var input = Make();
            input.Feed(1, FlatTouchPhase.Began, new Vector2(500, 500), 0);
            Assert.That(input.Feed(1, FlatTouchPhase.Moved, new Vector2(550, 500), .3).Flick, Is.False);
            Assert.That(input.Feed(1, FlatTouchPhase.Moved, new Vector2(900, 500), .5).Flick, Is.False);
            input.Feed(2, FlatTouchPhase.Began, new Vector2(500, 500), 1);
            var result = input.Feed(2, FlatTouchPhase.Moved, new Vector2(540, 500), 1.05);
            Assert.That(result.Flick, Is.True);
            Assert.That(result.Direction, Is.EqualTo(Vector2.right));
            Assert.That(result.Position, Is.EqualTo(new Vector2(520, 500)));
        }
    }
}
