using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace Zantetsu.Editor.Quest
{
    /// <summary>Exports CPU sample trees from a saved player capture without running the scene.</summary>
    public static class QuestNpcProfileInspect
    {
        public static void Export()
        {
            string source = Arg("-zantetsuProfileInput");
            string output = Arg("-zantetsuProfileOutput");
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(output))
                throw new ArgumentException("-zantetsuProfileInput and -zantetsuProfileOutput are required");
            ProfilerDriver.enabled = false;
            if (!ProfilerDriver.LoadProfile(source, false)) throw new IOException("Cannot load " + source);
            Directory.CreateDirectory(output);
            int first = ProfilerDriver.firstFrameIndex, last = ProfilerDriver.lastFrameIndex;
            using (var frames = new StreamWriter(Path.Combine(output, "raw-threads.csv"), false, new UTF8Encoding(false)))
            using (var samples = new StreamWriter(Path.Combine(output, "raw-samples.csv"), false, new UTF8Encoding(false)))
            {
                frames.WriteLine("frame,threadIndex,threadId,threadName,startMs,durationMs,sampleCount");
                samples.WriteLine("frame,threadIndex,threadName,sample,parent,depth,startMs,inclusiveMs,selfMs,name,path");
                for (int frame = first; frame <= last; ++frame)
                {
                    for (int thread = 0; thread < 256; ++thread)
                    using (var view = ProfilerDriver.GetRawFrameDataView(frame, thread))
                    {
                        if (!view.valid) break;
                        frames.WriteLine(string.Join(",", frame, thread, view.threadId, Csv(view.threadName), N(view.frameStartTimeMs), N(view.frameTimeMs), view.sampleCount));
                        if (view.threadName != "Main Thread" && view.threadName != "Render Thread") continue;
                        var parents = new int[view.sampleCount];
                        var depths = new int[view.sampleCount];
                        var paths = new string[view.sampleCount];
                        var stack = new System.Collections.Generic.Stack<int>();
                        for (int i = 0; i < view.sampleCount; ++i)
                        {
                            while (stack.Count > 0 && i > stack.Peek() + view.GetSampleChildrenCountRecursive(stack.Peek())) stack.Pop();
                            int parent = stack.Count == 0 ? -1 : stack.Peek();
                            parents[i] = parent;
                            depths[i] = stack.Count;
                            string name = view.GetSampleName(i);
                            paths[i] = parent < 0 ? name : paths[parent] + "/" + name;
                            double duration = view.GetSampleTimeMs(i), self = duration;
                            int end = i + view.GetSampleChildrenCountRecursive(i);
                            for (int child = i + 1; child <= end; child += 1 + view.GetSampleChildrenCountRecursive(child))
                                self -= view.GetSampleTimeMs(child);
                            if (duration >= 0.1)
                                samples.WriteLine(string.Join(",", frame, thread, Csv(view.threadName), i, parent, depths[i], N(view.GetSampleStartTimeMs(i)), N(duration), N(self), Csv(name), Csv(paths[i])));
                            if (end > i) stack.Push(i);
                        }
                    }
                }
            }
            File.WriteAllText(Path.Combine(output, "raw-profile-info.txt"), "source=" + source + "\nfirst=" + first + "\nlast=" + last + "\nCPU sample durations in ms; samples >=0.1 ms from Main/Render Thread; inclusive samples overlap\n");
            Debug.Log("QUEST PROFILE: exported " + first + ".." + last + " into " + output);
        }

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == name) return args[i + 1];
            return null;
        }
        private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}
