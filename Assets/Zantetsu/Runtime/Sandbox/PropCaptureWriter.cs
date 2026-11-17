#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// PROP-OBSTACLE-1: writes a capture session (Zantetsu.Core.MobPlan.PropCapture) on its own thread. Whoever hands a record
    /// never waits: a record that does not fit the queue's limit is not written and is counted, by stream. A Development Player
    /// and the Editor only (the launch argument is not read otherwise).
    /// </summary>
    public sealed class PropCaptureWriter
    {
        /// <summary>Followed by the folder the session's folder is made in.</summary>
        public const string Argument = "-zantetsuPropCapture";
        /// <summary>Followed by words about the code the Player was built from (written to session.json).</summary>
        public const string CodeArgument = "-zantetsuPropCaptureCode";
        public const int Shapes = 0, Builds = 1, Cycles = 2, Player = 3;
        private const long MaxQueuedBytes = 64L << 20;
        private static readonly string[] Files = { PropCapture.ShapesFile, PropCapture.BuildsFile, PropCapture.CyclesFile, PropCapture.PlayerFile };

        private struct Item { public int stream; public byte[] payload; public int length; }

        private readonly ConcurrentQueue<Item> queue = new ConcurrentQueue<Item>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread thread;
        private readonly FileStream[] files = new FileStream[4];
        private readonly long[] written = new long[4], writtenBytes = new long[4], dropped = new long[4];
        private long queuedBytes, mostQueuedBytes;
        private int queuedItems, mostQueuedItems;
        private volatile bool closing;
        private volatile string failure;
        private double writeSeconds;

        // the build stream's state: touched by the one build work at a time (PropObstacleSource's worker) only
        private readonly Dictionary<long, float[]> lastMatrices = new Dictionary<long, float[]>();
        private readonly List<PropShape> shapesNotWritten = new List<PropShape>();
        private bool forceAllMatrices = true;

        public string Folder { get; }
        public string Failure => failure;
        public long Dropped(int stream) => Interlocked.Read(ref dropped[stream]);
        public long Written(int stream) => Interlocked.Read(ref written[stream]);
        public long DroppedInAll => Dropped(0) + Dropped(1) + Dropped(2) + Dropped(3);

        /// <summary>The folder a launch asks a capture into (null: none, or not a build that captures).</summary>
        public static string Asked(string[] arguments)
        {
#if ZANTETSU_OBSERVE
            if (arguments == null) return null;
            int at = Array.IndexOf(arguments, Argument);
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
#else
            return null;
#endif
        }

        public PropCaptureWriter(string parent, string sessionJson)
        {
            Folder = Path.Combine(parent, "prop-capture-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            if (Directory.Exists(Folder)) throw new IOException("the capture's folder exists: " + Folder);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, PropCapture.SessionFile), sessionJson, new UTF8Encoding(false));
            for (int i = 0; i < 4; i++) files[i] = new FileStream(Path.Combine(Folder, Files[i]), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
            thread = new Thread(Run) { IsBackground = true, Name = "Zantetsu prop capture", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        /// <summary>Any thread. False: not taken (the queue is at its limit, or the capture is closing or failed) -- counted.</summary>
        public bool Hand(int stream, byte[] payload, int length = -1)
        {
            if (length < 0) length = payload.Length;
            if (closing || failure != null || Interlocked.Read(ref queuedBytes) + length > MaxQueuedBytes)
            {
                Interlocked.Increment(ref dropped[stream]);
                return false;
            }

            long bytes = Interlocked.Add(ref queuedBytes, length);
            int items = Interlocked.Increment(ref queuedItems);
            if (bytes > Interlocked.Read(ref mostQueuedBytes)) Interlocked.Exchange(ref mostQueuedBytes, bytes);
            if (items > mostQueuedItems) mostQueuedItems = items;
            queue.Enqueue(new Item { stream = stream, payload = payload, length = length });
            wake.Set();
            return true;
        }

        /// <summary>The build work's thread: the shapes copied for this build (and any not written before), then the build's record.</summary>
        public void HandBuild(PropObstacleInput input, IReadOnlyList<PropShape> copied, in PropObstacleStats stats, in PropCapture.BuildTimings timings)
        {
            for (int i = 0; i < copied.Count; i++) shapesNotWritten.Add(copied[i]);
            int done = 0;
            while (done < shapesNotWritten.Count && Hand(Shapes, PropCapture.EncodeShape(shapesNotWritten[done]))) done++;
            shapesNotWritten.RemoveRange(0, done);
            // a transform is written against the last one written: after a record that was not taken, all of them again
            bool taken = Hand(Builds, PropCapture.EncodeBuild(input, stats, timings, lastMatrices, forceAllMatrices));
            forceAllMatrices = !taken;
            if (!taken) lastMatrices.Clear();
        }

        private void Run()
        {
            var prefix = new byte[4];
            var clock = new System.Diagnostics.Stopwatch();
            DateTime flushed = DateTime.UtcNow;
            try
            {
                while (true)
                {
                    bool any = false;
                    while (queue.TryDequeue(out Item item))
                    {
                        any = true;
                        clock.Restart();
                        FileStream file = files[item.stream];
                        if (item.stream != Player)
                        {
                            prefix[0] = (byte)item.length; prefix[1] = (byte)(item.length >> 8); prefix[2] = (byte)(item.length >> 16); prefix[3] = (byte)(item.length >> 24);
                            file.Write(prefix, 0, 4);
                        }

                        file.Write(item.payload, 0, item.length);
                        Interlocked.Add(ref queuedBytes, -item.length);
                        Interlocked.Decrement(ref queuedItems);
                        Interlocked.Increment(ref written[item.stream]);
                        Interlocked.Add(ref writtenBytes[item.stream], item.length);
                        writeSeconds += clock.Elapsed.TotalSeconds;
                    }

                    if ((DateTime.UtcNow - flushed).TotalSeconds >= 1.0 || (closing && !any))
                    {
                        foreach (FileStream file in files) file.Flush();
                        flushed = DateTime.UtcNow;
                    }

                    if (closing && queue.IsEmpty) break;
                    wake.WaitOne(250);
                }
            }
            catch (Exception e)
            {
                failure = e.GetType().Name + ": " + e.Message;
            }
            finally
            {
                foreach (FileStream file in files)
                {
                    try { file?.Dispose(); } catch (Exception) { }
                }
            }
        }

        /// <summary>Main, at the end: what is queued is written (waiting at most `seconds`), then the summary. Returns the words of what was written.</summary>
        public string Close(double seconds, string notes)
        {
            closing = true;
            wake.Set();
            bool ended = thread.Join(TimeSpan.FromSeconds(seconds));
            string words = Describe() + (ended ? "" : "; THE WRITER DID NOT END IN TIME: the files may lack their last records");
            try
            {
                var json = new StringBuilder();
                json.Append("{\n  \"formatVersion\": ").Append(PropCapture.Format).Append(",\n  \"writerEnded\": ").Append(ended ? "true" : "false")
                    .Append(",\n  \"failure\": ").Append(failure == null ? "null" : "\"" + Escape(failure) + "\"");
                for (int i = 0; i < 4; i++)
                {
                    json.Append(",\n  \"").Append(Files[i]).Append("\": { \"records\": ").Append(Written(i)).Append(", \"bytes\": ").Append(Interlocked.Read(ref writtenBytes[i])).Append(", \"notWritten\": ").Append(Dropped(i)).Append(" }");
                }

                json.Append(",\n  \"mostQueuedBytes\": ").Append(Interlocked.Read(ref mostQueuedBytes)).Append(",\n  \"mostQueuedItems\": ").Append(mostQueuedItems)
                    .Append(",\n  \"queuedAtEnd\": ").Append(queuedItems).Append(",\n  \"shapesNotWritten\": ").Append(shapesNotWritten.Count)
                    .Append(",\n  \"writeMilliseconds\": ").Append((writeSeconds * 1000.0).ToString("F1", CultureInfo.InvariantCulture))
                    .Append(",\n  \"notes\": \"").Append(Escape(notes ?? string.Empty)).Append("\"\n}\n");
                File.WriteAllText(Path.Combine(Folder, PropCapture.SummaryFile), json.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                words += "; the summary was not written: " + e.Message;
            }

            return words;
        }

        public string Describe()
        {
            return FormattableString.Invariant($"prop capture {Folder}: shapes {Written(Shapes)} written / {Dropped(Shapes)} not, builds {Written(Builds)} / {Dropped(Builds)}, cycle records {Written(Cycles)} / {Dropped(Cycles)}, player blocks {Written(Player)} / {Dropped(Player)}; ")
                + FormattableString.Invariant($"most queued {Interlocked.Read(ref mostQueuedBytes)} B in {mostQueuedItems} items, queued now {queuedItems}; shapes still to write {shapesNotWritten.Count}; writer ms {writeSeconds * 1000.0:F1}")
                + (failure != null ? "; FAILED: " + failure : "");
        }

        public static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }
}
