using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace PmcAnalysis
{
    public static class NativePmcReader
    {
        private sealed class FunctionRange
        {
            public string Name;
            public uint Start;
            public uint End;
            public FunctionRange(string name, uint start, uint end)
            { Name = name; Start = start; End = end; }
        }

        // Frozen 0c Native ranges supplied with this diagnostic. End is exclusive.
        private static readonly FunctionRange[] Functions = {
            new FunctionRange("Run", 0x5ab20, 0x5b8ec),
            new FunctionRange("G_OneWayWorld", 0x228160, 0x228582),
            new FunctionRange("H_OneWayLoop", 0x161660, 0x1616fa),
            new FunctionRange("Point", 0x1dda60, 0x1dde7c),
            new FunctionRange("DictionaryFindValue", 0x185520, 0x185646),
            new FunctionRange("MatchesRow", 0xeec0, 0xefa9),
            new FunctionRange("Materialize", 0x1d3b0, 0x1d44a),
            new FunctionRange("GetRecord", 0x1c790, 0x1c962),
            new FunctionRange("ResolveChunk", 0x1d350, 0x1d3a2),
            new FunctionRange("ResolveArch", 0x1d2f0, 0x1d342),
            new FunctionRange("MatchesExact", 0xefc0, 0xf0af)
        };

        private sealed class PmcSample
        {
            public int Source, Tid, Cpu, Interval;
            public long Qpc;
            public double RelativeMs;
            public ulong LeafIp;
            public string IntervalEvidence = "CaptureConfiguration65536";
        }

        private sealed class StackFragment
        {
            public long EventQpc, SampleQpc;
            public int Tid, Cpu;
            public ulong[] Ips;
        }

        private sealed class SampleGroup
        {
            public long Qpc;
            public int Tid;
            public readonly List<PmcSample> Samples = new List<PmcSample>();
            public readonly List<StackFragment> Fragments = new List<StackFragment>();
        }

        private sealed class IntervalRecord
        {
            public string Event;
            public int Source, NewInterval, OldInterval;
            public long Qpc;
            public double RelativeMs;
        }

        private sealed class ImageRecord
        {
            public string Event, FileName;
            public ulong Base;
            public int Size;
            public long Qpc;
        }

        private sealed class Counts
        {
            public long Samples, PairedStackSamples, UserStackSamples, KernelStackSamples;
            public long LeafInsideImage, IntervalFromEventSamples;
            public double EstimatedEvents;
            public readonly Dictionary<string, long> Inclusive = new Dictionary<string, long>();
            public readonly Dictionary<string, long> Self = new Dictionary<string, long>();
        }

        private sealed class SourceCounts
        {
            public int Source;
            public string Name;
            public readonly Dictionary<int, long> UsedIntervals = new Dictionary<int, long>();
            public readonly Dictionary<string, Dictionary<string, Counts>> Scopes =
                new Dictionary<string, Dictionary<string, Counts>>();
        }

        private static string SourceName(int source)
        {
            switch (source)
            {
                case 26: return "InstructionRetired";
                case 25: return "UnhaltedCoreCycles";
                case 19: return "TotalCycles";
                case 27: return "UnhaltedReferenceCycles";
                case 31: return "BranchMispredictsRetired";
                case 11: return "BranchMispredictions";
                case 29: return "LLCMisses";
                case 10: return "CacheMisses";
                default: return source.ToString();
            }
        }

        private static int FunctionIndex(ulong ip, ulong imageBase, int imageSize)
        {
            if (ip < imageBase || ip - imageBase >= (ulong)imageSize) return -1;
            uint rva = (uint)(ip - imageBase);
            for (int i = 0; i < Functions.Length; i++)
                if (rva >= Functions[i].Start && rva < Functions[i].End) return i;
            return -1;
        }

        private static void Increment<TKey>(Dictionary<TKey, long> counts, TKey key)
        {
            long count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }

        private static Counts GetCounts(SourceCounts source, string scope, string side)
        {
            Dictionary<string, Counts> sides;
            if (!source.Scopes.TryGetValue(scope, out sides))
            {
                sides = new Dictionary<string, Counts>();
                source.Scopes.Add(scope, sides);
            }
            Counts counts;
            if (!sides.TryGetValue(side, out counts))
            {
                counts = new Counts();
                foreach (FunctionRange function in Functions)
                { counts.Inclusive.Add(function.Name, 0); counts.Self.Add(function.Name, 0); }
                sides.Add(side, counts);
            }
            return counts;
        }

        private static double? Ratio(long numerator, long denominator)
        { return denominator == 0 ? (double?)null : (double)numerator / denominator; }

        private static long RunSamples(Dictionary<int, SourceCounts> sources, int source, string side)
        {
            SourceCounts item;
            Dictionary<string, Counts> sides;
            Counts counts;
            return sources.TryGetValue(source, out item)
                && item.Scopes.TryGetValue("Run", out sides)
                && sides.TryGetValue(side, out counts) ? counts.Samples : 0;
        }

        private static void WriteJsonFile(string path, object value, JsonSerializerOptions options)
        {
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(file, value, options);
        }

        // Raw QPC joins PMC samples with their StackWalk records.
#pragma warning disable CS0618
        public static string Read(string etlPath, int targetPid, string imageName, string outputDirectory)
        {
            var groups = new Dictionary<(long Qpc, int Tid), SampleGroup>();
            var intervals = new Dictionary<int, List<IntervalRecord>>();
            var images = new List<ImageRecord>();
            long targetSamples = 0, targetFragments = 0;
            long targetStackKeyReferences = 0;
            int eventsLost;
            DateTime sessionStart, sessionEnd;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();

            SampleGroup getGroup(long qpc, int tid)
            {
                SampleGroup group;
                var key = (qpc, tid);
                if (!groups.TryGetValue(key, out group))
                { group = new SampleGroup { Qpc = qpc, Tid = tid }; groups.Add(key, group); }
                return group;
            }

            void readInterval(SampledProfileIntervalTraceData data, string eventName)
            {
                List<IntervalRecord> records;
                if (!intervals.TryGetValue(data.SampleSource, out records))
                { records = new List<IntervalRecord>(); intervals.Add(data.SampleSource, records); }
                records.Add(new IntervalRecord { Event = eventName, Source = data.SampleSource,
                    NewInterval = data.NewInterval, OldInterval = data.OldInterval,
                    Qpc = data.TimeStampQPC, RelativeMs = data.TimeStampRelativeMSec });
            }

            void readImage(ImageLoadTraceData data, string eventName)
            {
                if (data.ProcessID == targetPid &&
                    string.Equals(Path.GetFileName(data.FileName), imageName, StringComparison.OrdinalIgnoreCase))
                    images.Add(new ImageRecord { Event = eventName, FileName = data.FileName,
                        Base = data.ImageBase, Size = data.ImageSize, Qpc = data.TimeStampQPC });
            }

            using (var source = new ETWTraceEventSource(etlPath))
            {
                var kernel = source.Kernel;
                kernel.ImageLoad += data => readImage(data, "ImageLoad");
                kernel.ImageDCStart += data => readImage(data, "ImageDCStart");
                kernel.ImageDCStop += data => readImage(data, "ImageDCStop");
                kernel.PerfInfoCollectionStart += data => readInterval(data, "CollectionStart");
                kernel.PerfInfoSetInterval += data => readInterval(data, "SetInterval");
                kernel.PerfInfoPMCSample += data =>
                {
                    if (data.ProcessID != targetPid) return;
                    getGroup(data.TimeStampQPC, data.ThreadID).Samples.Add(new PmcSample {
                        Source = data.ProfileSource, Tid = data.ThreadID, Cpu = data.ProcessorNumber,
                        Qpc = data.TimeStampQPC, RelativeMs = data.TimeStampRelativeMSec,
                        LeafIp = data.InstructionPointer, Interval = 65536 });
                    targetSamples++;
                };
                kernel.StackWalkStack += data =>
                {
                    if (data.ProcessID != targetPid) return;
                    var ips = new ulong[data.FrameCount];
                    for (int i = 0; i < ips.Length; i++) ips[i] = data.InstructionPointer(i);
                    getGroup(data.EventTimeStampQPC, data.ThreadID).Fragments.Add(new StackFragment {
                        EventQpc = data.TimeStampQPC, SampleQpc = data.EventTimeStampQPC,
                        Tid = data.ThreadID, Cpu = data.ProcessorNumber, Ips = ips });
                    targetFragments++;
                };
                kernel.StackWalkStackKeyKernel += data =>
                { if (data.ProcessID == targetPid) targetStackKeyReferences++; };
                kernel.StackWalkStackKeyUser += data =>
                { if (data.ProcessID == targetPid) targetStackKeyReferences++; };
                source.Process();
                eventsLost = source.EventsLost;
                sessionStart = source.SessionStartTime.ToUniversalTime();
                sessionEnd = source.SessionEndTime.ToUniversalTime();
            }

            if (images.Count == 0) throw new InvalidDataException("Target native image load is required for RVA mapping.");
            ImageRecord image = images[0];
            foreach (List<IntervalRecord> records in intervals.Values)
                records.Sort((left, right) => left.Qpc.CompareTo(right.Qpc));

            var sources = new Dictionary<int, SourceCounts>();
            long unpairedSamples = 0, noUserStackSamples = 0, unpairedFragments = 0;
            long repeatedKeyEvents = 0, groupsWithMultiplePmc = 0, samplesWithoutSide = 0, samplesWithBothSides = 0;
            var jsonOptions = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
            string rawPath = Path.Combine(outputDirectory, "pmc-target-" + targetPid + ".raw.json.gz");
            string summaryPath = Path.Combine(outputDirectory, "pmc-target-" + targetPid + ".summary.json");
            using (var rawFile = new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write))
            using (var compressed = new GZipStream(rawFile, CompressionLevel.Fastest))
            using (var raw = new Utf8JsonWriter(compressed))
            {
                raw.WriteStartObject();
                raw.WriteNumber("TargetPid", targetPid);
                raw.WriteString("FrozenNative", "0c267");
                raw.WriteString("ImageBase", "0x" + image.Base.ToString("x"));
                raw.WriteNumber("ImageSize", image.Size);
                raw.WritePropertyName("Functions"); JsonSerializer.Serialize(raw, Functions, jsonOptions);
                raw.WritePropertyName("ImageEvents"); JsonSerializer.Serialize(raw, images, jsonOptions);
                raw.WritePropertyName("IntervalEvents"); JsonSerializer.Serialize(raw, intervals, jsonOptions);
                raw.WritePropertyName("Groups"); raw.WriteStartArray();
                foreach (SampleGroup group in groups.Values)
                {
                    int stackMask = 0;
                    bool userStack = false, kernelStack = false;
                    foreach (StackFragment fragment in group.Fragments)
                        foreach (ulong ip in fragment.Ips)
                        {
                            if ((ip & 0x8000000000000000UL) == 0) userStack = true; else kernelStack = true;
                            int index = FunctionIndex(ip, image.Base, image.Size);
                            if (index >= 0) stackMask |= 1 << index;
                        }
                    string scope = (stackMask & 1) != 0 ? "Run" : "OutsideRun";
                    if (group.Samples.Count == 0) unpairedFragments += group.Fragments.Count;
                    if (group.Samples.Count > 1)
                    { groupsWithMultiplePmc++; repeatedKeyEvents += group.Samples.Count - 1; }
                    raw.WriteStartObject();
                    raw.WriteNumber("Qpc", group.Qpc); raw.WriteNumber("Tid", group.Tid);
                    raw.WriteString("Scope", scope);
                    raw.WritePropertyName("Samples"); raw.WriteStartArray();
                    foreach (PmcSample sample in group.Samples)
                    {
                        List<IntervalRecord> records;
                        if (intervals.TryGetValue(sample.Source, out records))
                        {
                            int lo = 0, hi = records.Count;
                            while (lo < hi)
                            { int middle = lo + (hi - lo) / 2; if (records[middle].Qpc <= sample.Qpc) lo = middle + 1; else hi = middle; }
                            if (lo > 0 && records[lo - 1].NewInterval > 0)
                            { sample.Interval = records[lo - 1].NewInterval; sample.IntervalEvidence = records[lo - 1].Event; }
                        }
                        int leaf = FunctionIndex(sample.LeafIp, image.Base, image.Size);
                        int inclusiveMask = leaf >= 0 ? stackMask | (1 << leaf) : stackMask;
                        bool hasG = (inclusiveMask & (1 << 1)) != 0, hasH = (inclusiveMask & (1 << 2)) != 0;
                        string side = hasG ? (hasH ? "Both" : "G") : (hasH ? "H" : "Unassigned");
                        if (side == "Unassigned") samplesWithoutSide++;
                        if (side == "Both") samplesWithBothSides++;
                        SourceCounts sourceCounts;
                        if (!sources.TryGetValue(sample.Source, out sourceCounts))
                        { sourceCounts = new SourceCounts { Source = sample.Source, Name = SourceName(sample.Source) }; sources.Add(sample.Source, sourceCounts); }
                        Increment(sourceCounts.UsedIntervals, sample.Interval);
                        Counts counts = GetCounts(sourceCounts, scope, side);
                        counts.Samples++; counts.EstimatedEvents += sample.Interval;
                        if (group.Fragments.Count > 0) counts.PairedStackSamples++; else unpairedSamples++;
                        if (userStack) counts.UserStackSamples++; else noUserStackSamples++;
                        if (kernelStack) counts.KernelStackSamples++;
                        if (sample.IntervalEvidence != "CaptureConfiguration65536") counts.IntervalFromEventSamples++;
                        if (sample.LeafIp >= image.Base && sample.LeafIp - image.Base < (ulong)image.Size) counts.LeafInsideImage++;
                        for (int i = 0; i < Functions.Length; i++)
                            if ((inclusiveMask & (1 << i)) != 0) Increment(counts.Inclusive, Functions[i].Name);
                        Increment(counts.Self, leaf >= 0 ? Functions[leaf].Name :
                            (sample.LeafIp >= image.Base && sample.LeafIp - image.Base < (ulong)image.Size ? "OtherImageFunction" : "OutsideImage"));
                        raw.WriteStartObject();
                        raw.WriteNumber("Source", sample.Source); raw.WriteNumber("Interval", sample.Interval);
                        raw.WriteString("IntervalEvidence", sample.IntervalEvidence);
                        raw.WriteNumber("Qpc", sample.Qpc); raw.WriteNumber("RelativeMs", sample.RelativeMs);
                        raw.WriteNumber("Tid", sample.Tid); raw.WriteNumber("Cpu", sample.Cpu);
                        raw.WriteString("LeafIp", "0x" + sample.LeafIp.ToString("x"));
                        if (sample.LeafIp >= image.Base && sample.LeafIp - image.Base < (ulong)image.Size)
                            raw.WriteString("LeafRva", "0x" + (sample.LeafIp - image.Base).ToString("x"));
                        raw.WriteString("Side", side); raw.WriteEndObject();
                    }
                    raw.WriteEndArray(); raw.WritePropertyName("Fragments"); raw.WriteStartArray();
                    foreach (StackFragment fragment in group.Fragments)
                    {
                        raw.WriteStartObject();
                        raw.WriteNumber("EventQpc", fragment.EventQpc); raw.WriteNumber("SampleQpc", fragment.SampleQpc);
                        raw.WriteNumber("Tid", fragment.Tid); raw.WriteNumber("Cpu", fragment.Cpu);
                        raw.WritePropertyName("Ips"); raw.WriteStartArray();
                        foreach (ulong ip in fragment.Ips) raw.WriteStringValue("0x" + ip.ToString("x"));
                        raw.WriteEndArray(); raw.WriteEndObject();
                    }
                    raw.WriteEndArray(); raw.WriteEndObject();
                }
                raw.WriteEndArray(); raw.WriteEndObject();
            }

            var sourceRatios = sources.Values.OrderBy(item => item.Source).Select(item => new {
                item.Source, item.Name, G = RunSamples(sources, item.Source, "G"), H = RunSamples(sources, item.Source, "H"),
                GtoH = Ratio(RunSamples(sources, item.Source, "G"), RunSamples(sources, item.Source, "H")) }).ToArray();
            int cycleSource = sources.ContainsKey(25) ? 25 : 19;
            double? gCpi = Ratio(RunSamples(sources, cycleSource, "G"), RunSamples(sources, 26, "G"));
            double? hCpi = Ratio(RunSamples(sources, cycleSource, "H"), RunSamples(sources, 26, "H"));
            elapsed.Stop();
            var summary = new {
                FrozenNative = "0c267", EtlPath = etlPath, TargetPid = targetPid,
                TraceEventVersion = typeof(TraceEvent).Assembly.GetName().Version.ToString(),
                SessionStartUtc = sessionStart, SessionEndUtc = sessionEnd, ElapsedSeconds = elapsed.Elapsed.TotalSeconds,
                ImageBase = "0x" + image.Base.ToString("x"), ImageSize = image.Size, ImageEvents = images,
                FunctionRvaRangesEndExclusive = Functions, IntervalEvents = intervals,
                IntervalFallback = "Capture configuration 65536 until an observed interval event applies by QPC",
                Quality = new { EventsLost = eventsLost, TargetPmcSamples = targetSamples, TargetStackFragments = targetFragments,
                    UnpairedPmcSamples = unpairedSamples, SamplesWithoutUserStack = noUserStackSamples,
                    StackFragmentsWithoutPmc = unpairedFragments, StackKeyReferences = targetStackKeyReferences,
                    GroupsWithMultiplePmc = groupsWithMultiplePmc, AdditionalPmcAtSharedQpcTid = repeatedKeyEvents,
                    SamplesWithoutSide = samplesWithoutSide, SamplesWithBothSides = samplesWithBothSides },
                Sources = sources.Values.OrderBy(item => item.Source).ToArray(), RunSampleRatios = sourceRatios,
                RunRelativeCpiFromSampleCounts = new { CycleSource = cycleSource, InstructionSource = 26, G = gCpi, H = hCpi,
                    GtoH = gCpi.HasValue && hCpi.HasValue && hCpi.Value != 0 ? gCpi / hCpi : null },
                ScopeRule = "Run means an original joined stack contains the supplied Run RVA; all other samples are OutsideRun",
                SideRule = "G/H supplied function ranges in joined stack or sampled leaf; Both and Unassigned remain separate",
                RawSubset = rawPath
            };
            WriteJsonFile(summaryPath, summary, jsonOptions);
            return summaryPath;
        }
#pragma warning restore CS0618
    }
}
