using System;

namespace Zantetsu.Core.Statistics
{
    /// <summary>How the quit's last offer of the statistics ended.</summary>
    public enum PlayQuitOutcome
    {
        None = 0,          // no quit asked yet
        NotAgreed,         // no agreement: nothing to send, no wait
        NoTransport,       // no send configuration: no wait (the local record is the caller's, apart from this)
        NoPlay,            // nothing of play counted: no wait
        AlreadyAccepted,   // the latest content was accepted already: no wait
        Oversized,         // the body is over the receiver's limit: nothing sent, no wait
        Accepted,          // the last snapshot was accepted within the wait
        Failed,            // its send ended without acceptance within the wait: nothing tried again
        TimedOut,          // the wait's time was up: the send under way abandoned
    }

    /// <summary>
    /// The quit of a PC Player and the last statistics (DESIGN 21.18), apart from Unity so that the rule is tested:
    /// at the first request to quit the latest cumulative statistics are offered by the sender's one path (the same
    /// session id and revision as a loss of focus just before would have used: nothing is sent twice), and the quit
    /// waits for that send -- and for one already under way before it -- at most <see cref="WaitSeconds"/> in all,
    /// counted from the request and never lengthened. Accepted, failed or out of time, the quit is then allowed; a
    /// failure is not tried again and a send out of time is abandoned. Where there is nothing to wait for -- no
    /// agreement, no transport, nothing of play, the latest accepted already -- the quit is allowed at once.
    /// <para>
    /// A request made again while waiting starts nothing; once the wait has ended every request is allowed, so the
    /// quit the caller then makes does not come back here. The caller asks <see cref="Tick"/> each frame while
    /// <see cref="Waiting"/>: nothing here blocks a thread.
    /// </para>
    /// </summary>
    public sealed class PlayStatisticsQuit
    {
        /// <summary>The most the quit waits for the send(s), seconds, from the first request.</summary>
        public const double WaitSeconds = 2.0;

        private readonly PlayStatisticsSender _sender;
        private PlayStatisticsSnapshot _final;
        private double _deadline;

        public PlayStatisticsQuit(PlayStatisticsSender sender)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        }

        /// <summary>Whether a quit was asked for at all.</summary>
        public bool Requested { get; private set; }

        /// <summary>Whether the quit is held for the send: <see cref="Tick"/> is to be asked.</summary>
        public bool Waiting { get; private set; }

        /// <summary>Whether the quit is allowed from here on (the wait ended, or there was none).</summary>
        public bool Finished { get; private set; }

        public PlayQuitOutcome Outcome { get; private set; }

        /// <summary>Requests that came while the wait was under way: each started nothing.</summary>
        public int RepeatedRequests { get; private set; }

        /// <summary>When the first request came, and how long the wait lasted (0 where there was none), by the caller's clock.</summary>
        public double RequestedAt { get; private set; }
        public double WaitedSeconds { get; private set; }

        /// <summary>The sender's words for the offer made at the request.</summary>
        public string OfferText { get; private set; } = "";

        /// <summary>The revision the quit waited (or waits) for; -1 where none.</summary>
        public int FinalRevision => _final != null ? _final.Revision : -1;

        /// <summary>
        /// A request to quit at <paramref name="now"/> (seconds of any steady clock). True: the quit may go on now.
        /// False: it is held, and <see cref="Tick"/> says when it may. The caller has stopped the collection before
        /// the first request, so that the statistics offered are final and the wait is no part of them.
        /// </summary>
        public bool Request(double now)
        {
            if (Finished)
            {
                return true;
            }

            if (Waiting)
            {
                RepeatedRequests++;
                return false;
            }

            Requested = true;
            RequestedAt = now;
            PlayStatisticsOffer offer = _sender.Offer();
            OfferText = offer.Text ?? "";
            switch (offer.Kind)
            {
                case PlayStatisticsOfferKind.NotAgreed: return End(PlayQuitOutcome.NotAgreed, now);
                case PlayStatisticsOfferKind.NoTransport: return End(PlayQuitOutcome.NoTransport, now);
                case PlayStatisticsOfferKind.NoPlay: return End(PlayQuitOutcome.NoPlay, now);
                case PlayStatisticsOfferKind.Unchanged: return End(PlayQuitOutcome.AlreadyAccepted, now);
                case PlayStatisticsOfferKind.Oversized: return End(PlayQuitOutcome.Oversized, now);
            }

            _final = offer.Snapshot;
            _deadline = now + WaitSeconds;
            Waiting = true;
            // A transport that answers at once has settled it already.
            return Tick(now);
        }

        /// <summary>
        /// One look at the wait, at <paramref name="now"/>. True when the quit may go on (the wait has just ended, or
        /// had ended): the caller quits. A send that ended is taken before the time is judged up.
        /// </summary>
        public bool Tick(double now)
        {
            if (Finished)
            {
                return true;
            }

            if (!Waiting)
            {
                return false;
            }

            if (_sender.IsSettled(_final, out bool accepted))
            {
                return End(accepted ? PlayQuitOutcome.Accepted : PlayQuitOutcome.Failed, now);
            }

            if (now >= _deadline)
            {
                _sender.Abandon();
                return End(PlayQuitOutcome.TimedOut, now);
            }

            return false;
        }

        private bool End(PlayQuitOutcome outcome, double now)
        {
            Outcome = outcome;
            WaitedSeconds = Waiting ? Math.Max(0.0, now - RequestedAt) : 0.0;
            Waiting = false;
            Finished = true;
            return true;
        }

        /// <summary>One line for the log: the outcome, the revision, the wait.</summary>
        public string Describe()
        {
            if (!Requested) return "no quit asked";
            if (Waiting) return "waiting for revision " + FinalRevision + " (at most " + WaitSeconds.ToString("0.#") + " s from the request; " + OfferText + ")";
            return Outcome + (FinalRevision >= 0 ? ", revision " + FinalRevision : "") + ", waited " + WaitedSeconds.ToString("0.00") + " s (" + OfferText + ")"
                + (RepeatedRequests > 0 ? "; requests repeated while waiting " + RepeatedRequests : "");
        }
    }
}
