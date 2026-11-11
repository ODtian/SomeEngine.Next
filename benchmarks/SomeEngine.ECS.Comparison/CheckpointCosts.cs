using System.Buffers;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

// Resource-only holder observations extend the existing public same-instance lifecycle.
// Their allocations/retained deltas are separate; this instrumented run is not timing acceptance.
internal static class CheckpointCosts
{
    private readonly record struct Memory(long RetainedBytes, long PrivateBytes, long WorkingSetBytes, long PeakWorkingSetBytes);
    private readonly record struct Measurement(double Nanoseconds, double AllocatedBytes, int Operations);
    private readonly record struct Stage(string Name, Memory Memory, Measurement? Action = null);
    private static readonly BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;


    private sealed class HolderObservation
    {
        public required string Name { get; init; }
        public required Dictionary<string, long> Counts { get; init; }
        public long ObserverAllocatedBytes { get; set; }
        public long ObserverRetainedDeltaBytes { get; set; }
        public double ObserverNanoseconds { get; set; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ObserveHolders(string name, WorldCheckpoint? checkpoint, SourceFullArchiveWorld? full,
        Memory before, List<HolderObservation> observations)
    {
        long allocated = GC.GetTotalAllocatedBytes(true), started = Stopwatch.GetTimestamp();
        var observation = new HolderObservation
        {
            Name = name, Counts = CheckpointHolderCounts.ReadCurrent(checkpoint, full),
        };
        observations.Add(observation);
        Memory after = ReadMemory();
        observation.ObserverAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        observation.ObserverRetainedDeltaBytes = after.RetainedBytes - before.RetainedBytes;
        observation.ObserverNanoseconds = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
    }

    internal static int Run(string[] args)
    {
        int count = int.Parse(args[1]);
        bool preserve = args[2] == "preserve";
        string output = Path.GetFullPath(args[3]);
        var stages = new List<Stage>(10);
        var holders = new List<HolderObservation>(8);
        ReadMemory();
        stages.Add(new("Baseline", ReadMemory()));
        object result = RunWorld(count, preserve, stages, holders);
        stages.Add(new("ReleasedAll", ReadMemory()));
        ObserveHolders("ReleasedAll", null, null, stages[^1].Memory, holders);
        using Process process = Process.GetCurrentProcess();
        var report = new
        {
            Measurement = "SameInstanceCheckpoint", Entities = count,
            ResourceObservationOnly = true, HolderSchemaVersion = 1,
            Host = Environment.MachineName, Framework = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            CpuModel = OperatingSystem.IsLinux()
                ? File.ReadLines("/proc/cpuinfo").First(line => line.StartsWith("model name")).Split(':', 2)[1].Trim()
                : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            ProcessorMask = OperatingSystem.IsWindows() ? process.ProcessorAffinity.ToInt64() : (long?)null,
            TimingProcessor = Thread.GetCurrentProcessorId(), ServerGC = GCSettings.IsServerGC, TieredCompilation = false,
            Progress = preserve ? "preserve" : "rebuild", Encoding = "raw",
            Scope = "One full archive World; N ordinary entities, N edges, two roots. Same checkpoint and systems array for every Capture. Constructor and Grow are separate stages.",
            MemoryScope = "Whole-process retained heap after two blocking compacting collections with finalizers between them. Process peak is cumulative through each stage. It includes runtime caches, validation and retained numeric observer reports; each observer allocation/GC-retained delta is separately recorded.",
            TimingScope = "First and GrowthCapture each time one Capture. Hot samples time a fixed batch of complete same-instance Capture calls. Release/clone work inside Capture is included. Save and Restore are separate actions.",
            CopyScope = "Identity copy counts below are source-derived read/write payload bytes. They are separate from measured heap bytes and omit hardware cache traffic. Typed values, buffers and topology still copy their complete active contents.",
            Result = result, Stages = stages, HolderStages = holders,
            HolderScope = "Explicit known fields only, not a heap walk. Object identity sets die in the counting helper; reports contain numeric counts only. Global array totals deduplicate aliases; per-path capacities count references. Borrowed metadata and BCL shared pools are not exclusive checkpoint heap. ReleasedAll reads only already registered static type-cache counts, never retains World/checkpoint/serializer. Counts do not claim all runtime caches or a complete 19-group resource verdict.",
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json);
        Console.WriteLine(json);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object RunWorld(int count, bool preserve, List<Stage> stages, List<HolderObservation> holders)
    {
        using var full = new SourceFullArchiveWorld(count, true);
        stages.Add(new("WorldCreated", ReadMemory()));
        var checkpoint = new WorldCheckpoint();
        ISystemVersions[] systems = [full.Systems];
        CheckpointProgress progress = preserve ? CheckpointProgress.Preserve : CheckpointProgress.Rebuild;
        Action capture = () => checkpoint.Capture(full.World, progress, systems);
        stages.Add(new("EmptyCheckpoint", ReadMemory()));
        ObserveHolders("EmptyCheckpoint", checkpoint, full, stages[^1].Memory, holders);
        Measurement first = Measure(capture);
        stages.Add(new("FirstCapture", ReadMemory(), first));
        ObserveHolders("FirstCapture", checkpoint, full, stages[^1].Memory, holders);

        int calibrationOperations = 0;
        long calibration = Stopwatch.GetTimestamp();
        do { capture(); calibrationOperations++; }
        while (Stopwatch.GetElapsedTime(calibration).TotalMilliseconds < 300);
        int repetitions = Math.Max(1, (int)Math.Ceiling(calibrationOperations * 100.0 / Stopwatch.GetElapsedTime(calibration).TotalMilliseconds));
        var samples = new Measurement[10];
        for (int i = 0; i < samples.Length; i++) samples[i] = Measure(capture, repetitions);
        stages.Add(new("HotCaptures", ReadMemory()));
        ObserveHolders("HotCaptures", checkpoint, full, stages[^1].Memory, holders);

        using var bytes = new CompactArchiveOutput();
        Measurement save = Measure(() => { bytes.Clear(); full.Save(checkpoint, bytes); bytes.Complete(); });
        long outputBytes = bytes.WrittenCount, outputCapacity = bytes.Capacity;
        CompactArchiveOutput.Costs outputCosts = bytes.Statistics;
        stages.Add(new("Encoded", ReadMemory(), save));
        ObserveHolders("Encoded", checkpoint, full, stages[^1].Memory, holders);
        Measurement restore = Measure(() => checkpoint.Restore(full.World, systems));
        full.Check(preserve);
        full.Clear(); full.Load(bytes.WrittenSpan); full.Check(preserve);

        Measurement grow = Measure(full.Grow);
        stages.Add(new("GrowthWorld", ReadMemory(), grow));
        Measurement growth = Measure(capture);
        stages.Add(new("GrowthCapture", ReadMemory(), growth));
        ObserveHolders("GrowthCapture", checkpoint, full, stages[^1].Memory, holders);
        var layout = Describe(checkpoint);
        bytes.Clear(); full.Save(checkpoint, bytes); bytes.Complete();
        long growthOutputBytes = bytes.WrittenCount, growthOutputCapacity = bytes.Capacity;
        full.Clear(); full.Load(bytes.WrittenSpan); full.Check(preserve);
        checkpoint.Restore(full.World, systems); full.Check(preserve);

        bytes.Dispose(); checkpoint.Dispose();
        stages.Add(new("CheckpointReleasedSerializerAlive", ReadMemory()));
        ObserveHolders("CheckpointReleasedSerializerAlive", checkpoint, full, stages[^1].Memory, holders);
        GC.KeepAlive(full);
        return new
        {
            LiveEntities = full.World.EntityCount,
            First = first, Hot = new
            {
                MedianNanoseconds = Median(samples.Select(sample => sample.Nanoseconds)),
                AllocatedBytes = Median(samples.Select(sample => sample.AllocatedBytes)), Samples = samples,
            },
            GrowthCapture = growth, GrowthWorld = grow, Save = save, Restore = restore,
            OutputBytes = outputBytes, OutputCapacityBytes = outputCapacity,
            OutputUnusedBytes = outputCapacity - outputBytes, CompactOutput = outputCosts,
            GrowthOutputBytes = growthOutputBytes, GrowthOutputCapacityBytes = growthOutputCapacity,
            GrowthOutputUnusedBytes = growthOutputCapacity - growthOutputBytes, Layout = layout,
            Behavior = "Values, identities, owned buffers/old values, sparse/shared/indexes, resources, deferred hierarchy/relation order, destruction facts and system progress checked after Restore and encoded Load, including normal 1000-entity growth.",
        };
    }

    private static Measurement Measure(Action action, int operations = 1)
    {
        long allocated = GC.GetTotalAllocatedBytes(true), started = Stopwatch.GetTimestamp();
        for (int i = 0; i < operations; i++) action();
        long finished = Stopwatch.GetTimestamp(), ended = GC.GetTotalAllocatedBytes(true);
        return new(Stopwatch.GetElapsedTime(started, finished).TotalNanoseconds / operations,
            (double)(ended - allocated) / operations, operations);
    }

    private static Memory ReadMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        long retained = GC.GetTotalMemory(false);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        return new(retained, process.PrivateMemorySize64, process.WorkingSet64, process.PeakWorkingSet64);
    }

    private static object Describe(WorldCheckpoint checkpoint)
    {
        object? Field(object value, string name) => value.GetType().GetField(name, Fields)?.GetValue(value);
        var records = Field(checkpoint, "Records") as Array;
        var generations = Field(checkpoint, "Generations") as Array;
        var free = Field(checkpoint, "FreeIds") as Array;
        int used = (int?)Field(checkpoint, "Used") ?? generations!.Length - 1;
        int freeCount = (int?)Field(checkpoint, "FreeCount") ?? free!.Length;
        Array chunks = (Array)Field(checkpoint, "Chunks")!;
        int chunkCount = (int?)Field(checkpoint, "ChunkCount") ?? chunks.Length;
        long rowCapacity = 0, rows = 0, columnArrayPayload = 0, columnArrays = 0;
        for (int i = 0; i < chunkCount; i++)
        {
            object chunk = chunks.GetValue(i)!;
            Array entities = (Array)Field(chunk, "Entities")!;
            rowCapacity += entities.Length;
            rows += (int?)Field(chunk, "Count") ?? entities.Length;
            foreach (object column in (Array)Field(chunk, "Columns")!)
                if (Field(column, "Values") is Array values)
                {
                    int size = (int)typeof(CheckpointCosts).GetMethod(nameof(ElementSize), BindingFlags.Static | BindingFlags.NonPublic)!
                        .MakeGenericMethod(values.GetType().GetElementType()!).Invoke(null, null)!;
                    columnArrayPayload += values.LongLength * size; columnArrays++;
                }
        }
        long identityBytes = records is not null ? records.LongLength * 16 : generations!.LongLength * 4 + free!.LongLength * 4;
        return new
        {
            Used = used, FreeCount = freeCount, IdentityCapacity = records?.Length ?? generations!.Length,
            IdentityRecordBytes = records is not null ? 16 : 4,
            IdentityArrayPayloadBytes = identityBytes,
            IdentityArrayCount = records is not null ? 1 : 2,
            IdentityCaptureReadBytes = records is not null ? 8L * used : 4L * used,
            IdentityCaptureWriteBytes = records is not null ? 8L * used : 4L * (used + freeCount),
            FreeChainReadBytes = 4L * freeCount,
            FreeListMaterialization = records is null ? "List growth and ToArray copy are additional to these payload counts." : "Head/tail/count only.",
            IdentityZeroingOnGrowthBytes = records is not null ? identityBytes : 4L * generations!.Length,
            FreeChainTraversalSteps = freeCount,
            ChunkCount = chunkCount, ChunkDirectoryCapacity = chunks.Length,
            EntityRows = rows, EntityRowCapacity = rowCapacity, EntityArrayPayloadBytes = rowCapacity * 8,
            ColumnArrayPayloadBytes = columnArrayPayload, ColumnArrayCount = columnArrays,
            ArrayAccounting = "Payload from actual array lengths. Array headers/alignment, owned nested elements, masks, changes, service arrays, values World/pools and serializer bindings are included by the whole-process retained metric.",
        };
    }

    private static int ElementSize<T>() => Unsafe.SizeOf<T>();
    private static double Median(IEnumerable<double> values)
    { double[] sorted = values.Order().ToArray(); return (sorted[4] + sorted[5]) / 2; }
}

internal sealed partial class SourceFullArchiveWorld
{
    internal void Save(WorldCheckpoint checkpoint, IBufferWriter<byte> output) => _serializer.Save(checkpoint, output, _contract);
}
