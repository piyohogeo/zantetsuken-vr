using System;
using System.Text;

namespace Zantetsu.Core.Statistics
{
    /// <summary>What one send came to.</summary>
    public struct PlayStatisticsSendResult
    {
        public bool Success;       // the receiver answered 201 with ok:true
        public long StatusCode;    // 0 where no response
        public bool TimedOut;
        public string Error;       // the transport's error text, or null
    }

    /// <summary>A send under way, which can be abandoned.</summary>
    public interface IPlayStatisticsRequest
    {
        void Abort();
    }

    /// <summary>What carries a body to the receiver (UnityWebRequest in the game; a fake in a test). The callback comes once, on the main thread, or never after an Abort.</summary>
    public interface IPlayStatisticsTransport
    {
        IPlayStatisticsRequest Send(byte[] body, Action<PlayStatisticsSendResult> done);
    }

    /// <summary>What an offer of the latest statistics came to.</summary>
    public enum PlayStatisticsOfferKind
    {
        NotAgreed,     // no agreement: nothing sent
        NoTransport,   // no send configuration: nothing sent
        NoPlay,        // nothing of play counted: nothing sent
        Unchanged,     // the latest content was accepted by the receiver already: nothing sent
        Oversized,     // the body is over the receiver's limit: nothing sent
        Pending,       // a send is under way: this snapshot waits for it (or is the very one under way)
        Sending,       // a send of this snapshot began
    }

    /// <summary>An offer's outcome: its kind, the snapshot concerned (null where nothing is to be sent), and the words for the log.</summary>
    public struct PlayStatisticsOffer
    {
        public PlayStatisticsOfferKind Kind;
        public PlayStatisticsSnapshot Snapshot;
        public string Text;

        /// <summary>Whether a send of the snapshot is under way or waiting: something a quit may wait for.</summary>
        public bool InProgress => Kind == PlayStatisticsOfferKind.Pending || Kind == PlayStatisticsOfferKind.Sending;
    }

    /// <summary>One cumulative snapshot: byte-stable, so that a resend carries the same bytes under the same session id and revision.</summary>
    public sealed class PlayStatisticsSnapshot
    {
        public int Revision;
        public long ChangeCount;
        public byte[] Bytes;
    }

    /// <summary>
    /// Sends the cumulative play statistics when they are offered (DESIGN 21.18): at the loss of focus, and at the
    /// ordinary quit of a PC Player (<see cref="PlayStatisticsQuit"/>) -- no timer, no other event. Both come through
    /// <see cref="Offer"/>. At most one send is in flight; while one is, only the newest snapshot is kept pending. A snapshot is taken
    /// only where the statistics changed since the last; the same content is resent under the same session id,
    /// revision and bytes, and a snapshot the receiver accepted is not sent again. A failure stops nothing and retries
    /// nothing at once: the next loss of focus sends the latest cumulative values. Nothing is kept across launches.
    /// </summary>
    public sealed class PlayStatisticsSender
    {
        /// <summary>The receiver's limit on a body; a snapshot over it is not sent and is counted (<see cref="Oversized"/>).</summary>
        public const int MaxBodyBytes = 16 * 1024;

        private readonly PlayStatistics _statistics;
        private readonly PlayStatisticsConsent _consent;
        private readonly IPlayStatisticsTransport _transport;
        private readonly StringBuilder _json = new StringBuilder(4096);

        private PlayStatisticsSnapshot _last;        // the newest snapshot taken
        private PlayStatisticsSnapshot _pending;     // waiting for the one in flight to end
        private PlayStatisticsSnapshot _inFlight;
        private IPlayStatisticsRequest _request;
        private int _nextRevision;
        private int _acceptedRevision = -1;          // the newest revision the receiver accepted

        public PlayStatisticsSender(PlayStatistics statistics, PlayStatisticsConsent consent, IPlayStatisticsTransport transport, string testerRole)
        {
            _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
            _consent = consent ?? throw new ArgumentNullException(nameof(consent));
            _transport = transport;
            TesterRole = string.IsNullOrEmpty(testerRole) ? "external" : testerRole;
            SessionId = Guid.NewGuid().ToString();
        }

        /// <summary>This process's session: new at every launch, never changed while it runs.</summary>
        public string SessionId { get; }

        public string TesterRole { get; }

        public bool Busy => _inFlight != null;
        public bool HasPending => _pending != null;
        public int Started { get; private set; }
        public int Succeeded { get; private set; }
        public int Failed { get; private set; }
        public int Superseded { get; private set; }
        public int Oversized { get; private set; }
        public int Aborted { get; private set; }
        public int SkippedUnchanged { get; private set; }
        public PlayStatisticsSendResult LastResult { get; private set; }
        public int LastRevisionSent { get; private set; } = -1;
        public int LastSnapshotBytes => _last != null ? _last.Bytes.Length : 0;

        /// <summary>After a send ended, with its snapshot and result.</summary>
        public event Action<PlayStatisticsSnapshot, PlayStatisticsSendResult> Completed;

        /// <summary>
        /// The focus was lost: with an agreement, a transport and something of play counted, the latest cumulative
        /// statistics go -- a new snapshot where they changed, the last one again where they did not and the receiver
        /// has not accepted it yet. Returns what happened, for the log.
        /// </summary>
        public string OnFocusLost() => Offer().Text;

        /// <summary>
        /// Offers the latest cumulative statistics to the receiver, as a loss of focus does and as the quit does
        /// (DESIGN 21.18): the one path of every send, so that the session id and the revision keep a snapshot from being
        /// sent twice whichever event comes first. Returns what happened and the snapshot concerned.
        /// </summary>
        public PlayStatisticsOffer Offer()
        {
            if (!_consent.Agreed) return Said(PlayStatisticsOfferKind.NotAgreed, null, "not agreed: nothing sent");
            if (_transport == null) return Said(PlayStatisticsOfferKind.NoTransport, null, "no transport: nothing sent");
            if (!_statistics.HasPlay) return Said(PlayStatisticsOfferKind.NoPlay, null, "nothing of play counted yet: nothing sent");

            PlayStatisticsSnapshot snapshot;
            if (_last != null && _last.ChangeCount == _statistics.ChangeCount)
            {
                if (_acceptedRevision >= _last.Revision)
                {
                    SkippedUnchanged++;
                    return Said(PlayStatisticsOfferKind.Unchanged, null, "unchanged since revision " + _last.Revision + ", which the receiver accepted: nothing sent");
                }

                snapshot = _last;   // the same bytes, the same revision
            }
            else
            {
                PlayStatisticsJson.Write(_json, _statistics, SessionId, _consent.InstallId, _nextRevision, TesterRole);
                byte[] bytes = Encoding.UTF8.GetBytes(_json.ToString());
                if (bytes.Length > MaxBodyBytes)
                {
                    Oversized++;
                    return Said(PlayStatisticsOfferKind.Oversized, null, "snapshot of " + bytes.Length + " bytes is over the receiver's " + MaxBodyBytes + ": nothing sent");
                }

                snapshot = new PlayStatisticsSnapshot { Revision = _nextRevision, ChangeCount = _statistics.ChangeCount, Bytes = bytes };
                _nextRevision++;
                _last = snapshot;
            }

            if (_inFlight != null)
            {
                // (Where the snapshot is the very one under way -- a quit right after a loss of focus -- holding it pending
                // sends nothing again: at its end the pending one is begun only where it is another and not yet accepted.)
                if (_pending != null && !ReferenceEquals(_pending, snapshot)) Superseded++;
                _pending = snapshot;
                return Said(PlayStatisticsOfferKind.Pending, snapshot, "a send is under way: revision " + snapshot.Revision + " held as the one pending");
            }

            Begin(snapshot);
            return Said(PlayStatisticsOfferKind.Sending, snapshot, "sending revision " + snapshot.Revision + " (" + snapshot.Bytes.Length + " bytes)");
        }

        private static PlayStatisticsOffer Said(PlayStatisticsOfferKind kind, PlayStatisticsSnapshot snapshot, string text)
        {
            return new PlayStatisticsOffer { Kind = kind, Snapshot = snapshot, Text = text };
        }

        /// <summary>
        /// Whether nothing more will come of a snapshot by itself: it is neither under way nor waiting. With
        /// <paramref name="accepted"/>: whether the receiver has accepted it (or a later revision).
        /// </summary>
        public bool IsSettled(PlayStatisticsSnapshot snapshot, out bool accepted)
        {
            accepted = snapshot != null && _acceptedRevision >= snapshot.Revision;
            return snapshot == null || (!ReferenceEquals(_inFlight, snapshot) && !ReferenceEquals(_pending, snapshot));
        }

        /// <summary>
        /// The send under way is abandoned (its late word ignored) and the one waiting dropped: the quit's time is up.
        /// The consent and the last snapshot stand; nothing is tried again here.
        /// </summary>
        public void Abandon()
        {
            _pending = null;
            if (_inFlight != null)
            {
                Aborted++;
                _inFlight = null;
                try { _request?.Abort(); } catch (Exception) { }
                _request = null;
            }
        }

        private void Begin(PlayStatisticsSnapshot snapshot)
        {
            _inFlight = snapshot;
            Started++;
            LastRevisionSent = snapshot.Revision;
            _request = _transport.Send(snapshot.Bytes, result => Ended(snapshot, result));
        }

        private void Ended(PlayStatisticsSnapshot snapshot, PlayStatisticsSendResult result)
        {
            if (!ReferenceEquals(_inFlight, snapshot))
            {
                return;   // an abandoned send's late word: nothing of it is taken
            }

            _inFlight = null;
            _request = null;
            LastResult = result;
            if (result.Success)
            {
                Succeeded++;
                if (snapshot.Revision > _acceptedRevision) _acceptedRevision = snapshot.Revision;
            }
            else
            {
                Failed++;
            }

            Completed?.Invoke(snapshot, result);

            // The one pending, if any and not the same accepted content: now. A failure is not retried here.
            PlayStatisticsSnapshot next = _pending;
            _pending = null;
            if (next != null && !ReferenceEquals(next, snapshot) && next.Revision > _acceptedRevision)
            {
                Begin(next);
            }
        }

        /// <summary>
        /// The consent was withdrawn: the pending snapshot is dropped, the send under way abandoned (its late word is
        /// ignored), and nothing is sent until an agreement. The statistics themselves are the caller's to clear.
        /// </summary>
        public void Withdraw()
        {
            _pending = null;
            _last = null;
            if (_inFlight != null)
            {
                Aborted++;
                _inFlight = null;
                try { _request?.Abort(); } catch (Exception) { }
                _request = null;
            }
        }
    }
}
