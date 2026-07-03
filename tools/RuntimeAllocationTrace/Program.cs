using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using SomeEngine.Runtime;

const string ProviderName = "SomeEngine-RuntimeAllocationProbe";

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

if (string.Equals(args[0], "probe", StringComparison.OrdinalIgnoreCase))
{
    int frames = args.Length > 1 && int.TryParse(args[1], out int parsed) ? parsed : 120;
    RunProbe(frames);
    return 0;
}

if (string.Equals(args[0], "report", StringComparison.OrdinalIgnoreCase))
{
    if (args.Length < 3)
    {
        PrintUsage();
        return 2;
    }

    WriteReport(args[1], args[2]);
    return 0;
}

PrintUsage();
return 2;

static void PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  RuntimeAllocationTrace probe [frames]");
    Console.Error.WriteLine("  RuntimeAllocationTrace report <input.nettrace> <output.json>");
}

static void RunProbe(int frames)
{
    if (frames <= 0)
    {
        throw new ArgumentOutOfRangeException(nameof(frames), "Frame count must be positive.");
    }

    var options = RuntimeStartupOptions.Parse([
        "--frames", frames.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--no-vsync",
        "--present-interval", "0",
        "--pipeline-budget", "8",
        "--wait-pipelines",
        "--skip-present",
        "--no-profile"]);

    // Warm the accepted Runtime option consumer path before frame measurement.
    long accumulator = 0;
    for (int i = 0; i < 64; i++)
    {
        accumulator += Consume(options, i);
    }

    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

    Console.WriteLine($"ProbeEventSourceEnabled={RuntimeAllocationProbeEventSource.Log.IsEnabled()}");
    RuntimeAllocationProbeEventSource.Log.ProbeStart(frames);
    for (int frame = 0; frame < frames; frame++)
    {
        RuntimeAllocationProbeEventSource.Log.FrameStart(frame);
        accumulator += Consume(options, frame);
        Thread.Sleep(1);
        RuntimeAllocationProbeEventSource.Log.FrameEnd(frame);
    }

    RuntimeAllocationProbeEventSource.Log.ProbeStop(frames, accumulator);
}

static long Consume(RuntimeStartupOptions options, int frame)
{
    long value = options.FrameLimit;
    value += options.WindowVSync ? 1 : 0;
    value += options.PresentSyncInterval;
    value += (long)options.UpdatesPerSecond;
    value += (long)options.FramesPerSecond;
    value += options.PipelineWarmupBudget;
    value += options.WaitForPipelineWarmup ? 3 : 0;
    value += options.SkipSwapchainPresent ? 5 : 0;
    value += options.VerifyFrameOutput ? 7 : 0;
    value += options.DeviceValidation ? 11 : 0;
    value += options.DynamicScene ? 13 : 0;
    value += options.AsyncCompute ? 17 : 0;
    value += options.ClusterDebug.HasValue ? (int)options.ClusterDebug.Value : 0;
    value += options.Profiler.EnableTracy ? 19 : 0;
    value += frame;
    return value;
}

static void WriteReport(string inputTracePath, string outputJsonPath)
{
    inputTracePath = Path.GetFullPath(inputTracePath);
    outputJsonPath = Path.GetFullPath(outputJsonPath);

    var frames = new List<FrameInterval>();
    var openFrames = new Dictionary<int, double>();
    var allocations = new List<AllocationSample>();
    var gen0Collections = new List<double>();
    var seenEvents = new Dictionary<string, int>(StringComparer.Ordinal);

    using var source = new EventPipeEventSource(inputTracePath);
    Guid probeProviderGuid = EventSource.GetGuid(typeof(RuntimeAllocationProbeEventSource));

    source.AllEvents += data =>
    {
        string seenKey = data.ProviderName + "/" + data.EventName;
        seenEvents[seenKey] = seenEvents.TryGetValue(seenKey, out int seenCount) ? seenCount + 1 : 1;

        if (data.ProviderGuid != probeProviderGuid
            && !string.Equals(data.ProviderName, ProviderName, StringComparison.Ordinal))
        {
            return;
        }

        if ((int)data.ID == 2)
        {
            int frame = Convert.ToInt32(data.PayloadValue(0), System.Globalization.CultureInfo.InvariantCulture);
            openFrames[frame] = data.TimeStampRelativeMSec;
            return;
        }

        if ((int)data.ID == 3)
        {
            int frame = Convert.ToInt32(data.PayloadValue(0), System.Globalization.CultureInfo.InvariantCulture);
            if (openFrames.Remove(frame, out double start))
            {
                frames.Add(new FrameInterval(frame, start, data.TimeStampRelativeMSec));
            }
        }
    };

    source.Clr.GCAllocationTick += data =>
    {
        long amount = data.AllocationAmount64;
        if (amount <= 0)
        {
            amount = data.AllocationAmount;
        }

        if (amount > 0)
        {
            allocations.Add(new AllocationSample(data.TimeStampRelativeMSec, amount));
        }
    };

    source.Clr.GCStart += data =>
    {
        if (data.Depth == 0)
        {
            gen0Collections.Add(data.TimeStampRelativeMSec);
        }
    };

    source.Process();

    if (frames.Count == 0)
    {
        var seen = string.Join("\n", seenEvents.OrderBy(pair => pair.Key, StringComparer.Ordinal).Take(80).Select(pair => pair.Key + " = " + pair.Value));
        Console.Error.WriteLine("Seen events before marker failure:\n" + seen);
        Environment.Exit(1);
    }

    frames.Sort((left, right) => left.Index.CompareTo(right.Index));
    long maxAllocatedBytes = 0;
    int maxGen0 = 0;
    foreach (var frame in frames)
    {
        long allocated = allocations
            .Where(sample => sample.TimeStampRelativeMSec >= frame.StartMSec && sample.TimeStampRelativeMSec <= frame.EndMSec)
            .Sum(sample => sample.Bytes);
        int gen0 = gen0Collections.Count(time => time >= frame.StartMSec && time <= frame.EndMSec);
        maxAllocatedBytes = Math.Max(maxAllocatedBytes, allocated);
        maxGen0 = Math.Max(maxGen0, gen0);
    }

    var report = new RuntimeAllocationTrace(
        Scenario: "RuntimeStartupOptions accepted-runtime steady frame consumer",
        Source: "dotnet-trace",
        Frames: frames.Count,
        GcGen0PerFrame: maxGen0,
        AllocBytesPerFrame: maxAllocatedBytes,
        TraceTool: "dotnet-trace gc-verbose + SomeEngine-RuntimeAllocationProbe frame markers",
        TraceFileName: Path.GetFileName(inputTracePath));

    Directory.CreateDirectory(Path.GetDirectoryName(outputJsonPath)!);
    var options = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(outputJsonPath, JsonSerializer.Serialize(report, options));
}

[EventSource(Name = "SomeEngine-RuntimeAllocationProbe")]
internal sealed class RuntimeAllocationProbeEventSource : EventSource
{
    public static readonly RuntimeAllocationProbeEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void ProbeStart(int frames) => WriteEvent(1, frames);

    [Event(2, Level = EventLevel.Informational)]
    public void FrameStart(int frameIndex) => WriteEvent(2, frameIndex);

    [Event(3, Level = EventLevel.Informational)]
    public void FrameEnd(int frameIndex) => WriteEvent(3, frameIndex);

    [Event(4, Level = EventLevel.Informational)]
    public void ProbeStop(int frames, long accumulator) => WriteEvent(4, frames, accumulator);
}

internal readonly record struct FrameInterval(int Index, double StartMSec, double EndMSec);

internal readonly record struct AllocationSample(double TimeStampRelativeMSec, long Bytes);

internal sealed record RuntimeAllocationTrace(
    string Scenario,
    string Source,
    int Frames,
    double GcGen0PerFrame,
    double AllocBytesPerFrame,
    string TraceTool,
    string TraceFileName);



