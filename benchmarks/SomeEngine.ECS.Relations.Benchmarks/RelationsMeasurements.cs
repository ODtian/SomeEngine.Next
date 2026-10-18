using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Benchmarks;

public static class RelationsMeasurements
{
    public static int Main(string[] args)
    {
        long mainStarted = Stopwatch.GetTimestamp();
        DateTime mainUtc = DateTime.UtcNow;
        string Option(string name, string fallback = "")
        {
            int index = Array.IndexOf(args, name);
            return index < 0 ? fallback : args[index + 1];
        }
        string mode = Option("--mode", "measure");
        string compiledCommit = typeof(RelationsMeasurements).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(value => value.Key == "MeasurementSourceCommit").Value!;
        if (Option("--source") is { Length: > 0 } expectedCommit && expectedCommit != compiledCommit)
            throw new InvalidOperationException("The requested source commit must match the compiled measurement assembly.");
        if (mode == "list")
        {
            Console.WriteLine(JsonSerializer.Serialize(MeasurementCases.All, MeasurementJson.Default.MeasurementCaseArray));
            return 0;
        }
        MeasurementCase input = MeasurementCases.All.Single(item => item.Class == Option("--case") &&
            item.Size == Option("--size") && item.Variant == Option("--variant"));
        if (mode == "first-result")
        {
            bool relation = Option("--side", "relation") == "relation";
            using MeasurementSide first = MeasurementSides.Create(input, relation);
            long queryStarted = Stopwatch.GetTimestamp();
            first.Query();
            long queried = Stopwatch.GetTimestamp();
            first.Consume();
            Console.WriteLine($"FIRST_RESULT utc={DateTime.UtcNow:O} side={(relation ? "relation" : "handwritten")} rows={first.Count} mainTicks={Stopwatch.GetTimestamp() - mainStarted} firstQueryTicks={queried - queryStarted} mainUtc={mainUtc:O}");
            return 0;
        }

        DateTime started = DateTime.UtcNow;
        using MeasurementSide extension = MeasurementSides.Create(input, true);
        using MeasurementSide handwritten = MeasurementSides.Create(input, false);
        EquivalenceData equivalence = Verify(input, extension, handwritten);
        Console.WriteLine($"EQUIVALENT {input.Id}: frames={equivalence.VerifiedFrames}; pairs={equivalence.FinalPairCount}");
        if (mode == "verify")
            return 0;
        var cold = new List<ColdSample>();
        for (int pair = 0; pair < (mode == "phases" ? 0 : input.ColdPairs); pair++)
        {
            bool relationFirst = pair % 2 == 0;
            foreach (bool relation in relationFirst ? new[] { true, false } : new[] { false, true })
            {
                long allocated = GC.GetTotalAllocatedBytes(true);
                long before = Stopwatch.GetTimestamp();
                using MeasurementSide side = MeasurementSides.Create(input, relation);
                long query = Stopwatch.GetTimestamp();
                side.Query();
                long consume = Stopwatch.GetTimestamp();
                side.Consume();
                long end = Stopwatch.GetTimestamp();
                cold.Add(new(pair, relationFirst ? "E/H" : "H/E", relation ? "relation" : "handwritten",
                    side.SetupTicks, side.IndexBuildTicks, consume - query, end - consume,
                    end - before, side.Count, GC.GetTotalAllocatedBytes(true) - allocated));
            }
        }

        for (int frame = 0; frame < input.WarmupFrames; frame++)
        {
            Run(input, extension, frame, input.PrimaryBoundary);
            Run(input, handwritten, frame, input.PrimaryBoundary);
        }
        var raw = new List<FrameSample>();
        string[] sampleBoundaries = mode == "phases" ? ["IndependentStages"] :
            input.ChangesEveryFrame ? ["MaintenanceAndQuery", "FrameTotal"] : [input.PrimaryBoundary];
        foreach (string boundary in sampleBoundaries)
        {
            for (int round = 0; round < input.Rounds; round++)
            {
                bool relationFirst = round % 2 == 0;
                foreach (bool relation in relationFirst ? new[] { true, false } : new[] { false, true })
                {
                    MeasurementSide side = relation ? extension : handwritten;
                    for (int frame = 0; frame < input.FramesPerRound; frame++)
                    {
                        FrameSample sample = Run(input, side, frame, boundary);
                        raw.Add(sample with { Round = round, SideOrder = relationFirst ? "E/H" : "H/E",
                            Side = relation ? "relation" : "handwritten" });
                    }
                }
            }
        }
        var comparisons = Compare(input, raw);
        var device = new DeviceData(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "",
            Environment.ProcessorCount, RuntimeInformation.OSDescription, Environment.Version.ToString(),
            GCSettings.IsServerGC, Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "default",
            Environment.GetEnvironmentVariable("DOTNET_ReadyToRun") ?? "default", Stopwatch.Frequency,
            Environment.CommandLine, compiledCommit, Option("--backend", "NativeAOT"));
        bool? passed = comparisons.Any(value => value.Required)
            ? comparisons.Where(value => value.Required).All(value => value.Passed) : null;
        var report = new MeasurementReport(mode, input, device, started, DateTime.UtcNow,
            ["Query: complete public call including query preparation, traversal, filters and real pair writes.",
             "FrameTotal: actual writes and scheduled maintenance/query/consumption to final completion, or direct query plus consumption.",
             "MaintenanceAndQuery: writes excluded; schedule maintenance and query, complete filled output; consumption follows outside this boundary.",
             "ScheduledPairOutput: outer submit through completed count/prefix/fill; all child scheduling and joins included.",
             "PairsAndConsume: direct public query and same actual geometric consumption.",
             "Independent stages: diagnostic samples complete actual maintenance, query and consumption separately; each submit-to-completion timer follows its completed prerequisites.",
             "Cold total: World/query/output setup, actual index initialization, first public query and first consumption."],
            ["PayloadBytes counts actual value-array elements at the target backend's sizeof<T>.",
             "Live counts root topology; Addressed counts the high-water node address range including reusable entries; Capacity counts backing arrays.",
             "LeafMapKeyValues counts live Entity/int payload; entry capacity is separate; dictionary management and object headers enter actual allocation and process memory.",
             "Partition payload includes node pairs, counts, offsets and nonempty partition indices; access-data arrays are separate.",
             "FrameAllocatedBytes includes the whole actual query/consumption frame around the selected primary boundary, across all workers.",
             "World, Scheduler, query/observer data, object headers and pooled child handles enter measured allocations/Managed/WorkingSet."],
            equivalence, cold.ToArray(), raw.ToArray(), comparisons, extension.Memory(), handwritten.Memory(), passed);
        string output = Option("--output", Path.Combine("artifacts", "ecs-relations", "measurements", input.Id + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(report, MeasurementJson.Default.MeasurementReport));
        foreach (MetricComparison value in comparisons)
            Console.WriteLine($"{input.Id} {value.Boundary}: median={value.MedianRatio:R} p95={value.P95Ratio:R} required={value.Required} pass={value.Passed}");
        Console.WriteLine($"REPORT {Path.GetFullPath(output)}; acceptance={passed}");
        return 0;
    }

    private static EquivalenceData Verify(MeasurementCase input, MeasurementSide relation, MeasurementSide handwritten)
    {
        int frames = input.ChangesEveryFrame ? 8 : 1;
        (Entity, Entity)[] result = [];
        for (int frame = 0; frame < frames; frame++)
        {
            if (input.ChangesEveryFrame)
            {
                relation.Data.WriteFrame(frame);
                handwritten.Data.WriteFrame(frame);
                relation.ScheduleMaintenance().Complete();
                handwritten.ScheduleMaintenance().Complete();
            }
            relation.Query();
            handwritten.Query();
            relation.Consume();
            handwritten.Consume();
            result = relation.Pairs.ToArray();
            var expected = handwritten.Pairs.ToArray();
            Array.Sort(result, PairComparer.Instance);
            Array.Sort(expected, PairComparer.Instance);
            if (!result.AsSpan().SequenceEqual(expected) ||
                !relation.ContactCounts.SequenceEqual(handwritten.ContactCounts) ||
                !DepthsEqual(relation.Depths, handwritten.Depths))
                throw new InvalidOperationException($"Public rows or consumption differ for {input.Id}, frame {frame}: relation={result.Length}, handwritten={expected.Length}");
        }
        return new(true, frames, result.Length, result.Take(16).Select(pair => new PairRow(pair.Item1.Index,
            pair.Item1.Generation, pair.Item2.Index, pair.Item2.Generation)).ToArray(),
            relation.ContactCounts.ToArray(), relation.Depths.ToArray());
    }

    private static bool DepthsEqual(ReadOnlySpan<float> first, ReadOnlySpan<float> second)
    {
        if (first.Length != second.Length)
            return false;
        for (int index = 0; index < first.Length; index++)
            if (MathF.Abs(first[index] - second[index]) > 0.0001f)
                return false;
        return true;
    }

    private static FrameSample Run(MeasurementCase input, MeasurementSide side, int frame, string boundary)
    {
        long allocated = GC.GetTotalAllocatedBytes(true);
        long threadAllocated = GC.GetAllocatedBytesForCurrentThread();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long managed = GC.GetTotalMemory(false), working = Environment.WorkingSet;
        long start = Stopwatch.GetTimestamp();
        long written = start, queried, consumed, queryTicks = 0, consumeTicks = 0;
        long maintenanceTicks = 0, maintenanceBytes = 0, queryBytes = 0, consumeBytes = 0;
        if (input.ChangesEveryFrame && boundary == "IndependentStages")
        {
            side.Data.WriteFrame(frame);
            written = Stopwatch.GetTimestamp();
            long before = GC.GetTotalAllocatedBytes(true);
            long maintenanceStarted = Stopwatch.GetTimestamp();
            side.ScheduleMaintenance().Complete();
            maintenanceTicks = Stopwatch.GetTimestamp() - maintenanceStarted;
            maintenanceBytes = GC.GetTotalAllocatedBytes(true) - before;
            before = GC.GetTotalAllocatedBytes(true);
            long queryStarted = Stopwatch.GetTimestamp();
            side.ScheduleQuery().Complete();
            queried = Stopwatch.GetTimestamp();
            queryTicks = queried - queryStarted;
            queryBytes = GC.GetTotalAllocatedBytes(true) - before;
            before = GC.GetTotalAllocatedBytes(true);
            long consumeStarted = Stopwatch.GetTimestamp();
            side.ScheduleConsume().Complete();
            consumed = Stopwatch.GetTimestamp();
            consumeTicks = consumed - consumeStarted;
            consumeBytes = GC.GetTotalAllocatedBytes(true) - before;
        }
        else if (input.ChangesEveryFrame)
        {
            side.Data.WriteFrame(frame);
            written = Stopwatch.GetTimestamp();
            JobHandle updated = side.ScheduleMaintenance();
            JobHandle filled = side.ScheduleQuery(updated);
            if (boundary == "FrameTotal")
            {
                side.ScheduleConsume(filled).Complete();
                queried = consumed = Stopwatch.GetTimestamp();
            }
            else
            {
                filled.Complete();
                queried = Stopwatch.GetTimestamp();
                side.ScheduleConsume().Complete();
                consumed = Stopwatch.GetTimestamp();
                consumeTicks = consumed - queried;
            }
        }
        else if (input.Class == "threads")
        {
            side.ScheduleQuery().Complete();
            queried = Stopwatch.GetTimestamp();
            side.ScheduleConsume().Complete();
            consumed = Stopwatch.GetTimestamp();
            queryTicks = queried - start;
            consumeTicks = consumed - queried;
        }
        else
        {
            side.Query();
            queried = Stopwatch.GetTimestamp();
            side.Consume();
            consumed = Stopwatch.GetTimestamp();
            queryTicks = queried - start;
            consumeTicks = consumed - queried;
        }
        long end = consumed;
        long elapsed = boundary switch
        {
            "MaintenanceAndQuery" => queried - written,
            "FrameTotal" or "PairsAndConsume" => consumed - start,
            _ => queried - start
        };
        long bytes = GC.GetTotalAllocatedBytes(true) - allocated;
        long threadBytes = GC.GetAllocatedBytesForCurrentThread() - threadAllocated;
        long incidences = 0;
        double depths = 0;
        foreach (int count in side.ContactCounts)
            incidences += count;
        foreach (float depth in side.Depths)
            depths += depth;
        return new(0, "", "", frame, boundary, elapsed, queryTicks, consumeTicks, end - start, written - start,
            maintenanceTicks, maintenanceBytes, queryBytes, consumeBytes,
            side.Count, incidences, depths, bytes, threadBytes, GC.CollectionCount(0) - gen0,
            GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2, managed, GC.GetTotalMemory(false), working, Environment.WorkingSet);
    }

    private static MetricComparison[] Compare(MeasurementCase input, List<FrameSample> samples)
    {
        var comparisons = new List<MetricComparison>();
        foreach (string boundary in samples.Select(value => value.Boundary).Distinct())
        {
            FrameSample[] frames = samples.Where(value => value.Boundary == boundary).ToArray();
            if (boundary == "IndependentStages")
            {
                Add("IndependentMaintenance", false, frames, value => value.MaintenanceTicks);
                Add("IndependentQuery", false, frames, value => value.QueryTicks);
                Add("IndependentConsume", false, frames, value => value.ConsumeTicks);
                continue;
            }
            Add(boundary, true, frames, value => value.ElapsedTicks);
            if (input.Class == "complete")
                Add("FrameTotal", true, frames, value => value.FrameTicks);
            if (!input.ChangesEveryFrame)
                Add("Consume", false, frames, value => value.ConsumeTicks);
        }
        return comparisons.ToArray();

        void Add(string name, bool required, FrameSample[] frames, Func<FrameSample, long> ticks)
        {
            Distribution extension = Statistics(frames.Where(value => value.Side == "relation").ToArray(), ticks);
            Distribution handwritten = Statistics(frames.Where(value => value.Side == "handwritten").ToArray(), ticks);
            double median = extension.MedianTicks / handwritten.MedianTicks;
            double p95 = extension.P95Ticks / handwritten.P95Ticks;
            comparisons.Add(new(name, required, extension, handwritten, median, p95, median <= 1 && p95 <= 1));
        }
    }

    private static Distribution Statistics(FrameSample[] samples, Func<FrameSample, long> ticks)
    {
        double[] sorted = samples.Select(value => (double)ticks(value)).Order().ToArray();
        double mean = sorted.Average();
        double variance = sorted.Sum(value => (value - mean) * (value - mean)) / (sorted.Length - 1);
        return new(Percentile(sorted, 0.5), Percentile(sorted, 0.95), Percentile(sorted, 0.75) - Percentile(sorted, 0.25),
            Math.Sqrt(variance), sorted[0], sorted[^1], mean,
            samples.Average(value => value.OutputPairCount) * Stopwatch.Frequency / mean, samples.Sum(value => value.FrameAllocatedBytes));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        double index = (sorted.Length - 1) * percentile;
        int lower = (int)index;
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (index - lower);
    }

    private sealed class PairComparer : IComparer<(Entity, Entity)>
    {
        public static readonly PairComparer Instance = new();
        public int Compare((Entity, Entity) first, (Entity, Entity) second)
        {
            int comparison = EntityOrder.Compare(first.Item1, second.Item1);
            return comparison == 0 ? EntityOrder.Compare(first.Item2, second.Item2) : comparison;
        }
    }
}
