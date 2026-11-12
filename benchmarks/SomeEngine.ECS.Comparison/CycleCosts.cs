using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SomeEngine.ECS.Comparison;

internal static class CycleCosts
{
    private const string Workload = "job-parallel";
    private const int Entities = 10_000;
    private const int Operations = 100;
    private const int Rounds = 10;

    internal static int Run(string[] args)
    {
        string output = Path.GetFullPath(args[1]);
        string workload = args.Length > 2 ? args[2] : Workload;
        var libraries = ComparisonEntry.CreateLibraries(Entities, workload);
        var samples = libraries.Select(_ => new List<Measurement>(Rounds)).ToArray();
        for (int round = 0; round < Rounds; round++)
            for (int offset = 0; offset < libraries.Length; offset++)
            {
                int library = (round + offset) % libraries.Length;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                samples[library].Add(Measure(libraries[library].Create, workload, round + 1, offset + 1));
                Console.WriteLine($"{libraries[library].Name}: cycle costs {round + 1}/{Rounds} complete.");
            }

        var report = new
        {
            Measurement = "CycleCosts", Phase = "cycle", Workload = workload, Entities, Samples = Rounds,
            Host = Environment.MachineName,
            CpuModel = OperatingSystem.IsLinux()
                ? File.ReadLines("/proc/cpuinfo").First(line => line.StartsWith("model name")).Split(':', 2)[1].Trim()
                : Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            Framework = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ServerGC = GCSettings.IsServerGC,
            ProcessorCount = Environment.ProcessorCount, TimingProcessor = Thread.GetCurrentProcessorId(),
            ParticipantProcessors = BenchmarkCpu.ParticipantProcessors,
            WorldsPerMeasurement = 1, CyclesPerMeasurement = 1, CycleOperations = Operations,
            LayoutChangeCalls = Operations / 10, LayoutChangeBatch = ComparisonWorld.Changes,
            ActionThreadLifetime = "Measurement", TimeUnit = "nanoseconds", AllocationUnit = "bytes",
            Sampling = "One cycle per library in each rotated round; collection precedes each world.",
            Timing = "ConstructAndGetAction, each action, each AddRemove and Dispose have separate timers. MeasuredTotal sums those intervals.",
            Allocation = "GC.GetTotalAllocatedBytes(true), across all threads, read outside each stage timer.",
            Validation = $"CheckResult({workload}, {Operations}) follows all action and AddRemove snapshots, precedes Dispose and has separate allocation accounting.",
            InputPreparation = "CreateLibraries prepares the existing shared inputs before all measured worlds.",
            Results = libraries.Select((library, index) => new
            {
                Library = library.Name,
                ConstructAndGetAction = Summarize(samples[index].Select(sample => sample.ConstructAndGetAction)),
                Actions = Summarize(samples[index].Select(sample => sample.Actions)),
                AddRemove = Summarize(samples[index].Select(sample => sample.AddRemove)),
                Dispose = Summarize(samples[index].Select(sample => sample.Dispose)),
                MeasuredTotal = Summarize(samples[index].Select(sample => sample.MeasuredTotal)),
                MedianValidationAllocatedBytes = Median(samples[index].Select(sample => (double)sample.ValidationAllocatedBytes)),
                Measurements = samples[index],
            }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json);
        Console.WriteLine(json);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Measurement Measure(Func<ComparisonWorld> create, string workload, int round, int order)
    {
        Cost construction = default, actions = default, changes = default, disposal = default;
        long validation = 0;
        Reading began = Reading.Start();
        ComparisonWorld world = create();
        try
        {
            Action action = world.GetAction(workload);
            construction = began.Finish();
            for (int operation = 1; operation <= Operations; operation++)
            {
                began = Reading.Start();
                action();
                actions += began.Finish();
                if (operation % 10 == 0 || operation == Operations / 2)
                {
                    began = Reading.Start();
                    world.AddRemove();
                    changes += began.Finish();
                }
            }
            long beforeValidation = GC.GetTotalAllocatedBytes(true);
            world.CheckResult(workload, Operations);
            validation = GC.GetTotalAllocatedBytes(true) - beforeValidation;
        }
        finally
        {
            began = Reading.Start();
            world.Dispose();
            disposal = began.Finish();
        }
        return new(round, order, construction, actions, changes, disposal, validation);
    }

    private static CostSummary Summarize(IEnumerable<Cost> sequence)
    {
        Cost[] values = sequence.ToArray();
        return new(Median(values.Select(value => value.Nanoseconds)), Median(values.Select(value => (double)value.AllocatedBytes)));
    }

    private static double Median(IEnumerable<double> sequence)
    {
        double[] values = sequence.Order().ToArray();
        return (values[4] + values[5]) / 2;
    }

    private readonly record struct Reading(long Timestamp, long AllocatedBytes)
    {
        internal static Reading Start()
        {
            long allocated = GC.GetTotalAllocatedBytes(true);
            return new(Stopwatch.GetTimestamp(), allocated);
        }

        internal Cost Finish()
        {
            long ended = Stopwatch.GetTimestamp();
            long allocated = GC.GetTotalAllocatedBytes(true);
            return new(Stopwatch.GetElapsedTime(Timestamp, ended).TotalNanoseconds, allocated - AllocatedBytes);
        }
    }

    private readonly record struct Cost(double Nanoseconds, long AllocatedBytes)
    {
        public static Cost operator +(Cost first, Cost second)
            => new(first.Nanoseconds + second.Nanoseconds, first.AllocatedBytes + second.AllocatedBytes);
    }

    private readonly record struct Measurement(int Round, int Order, Cost ConstructAndGetAction, Cost Actions,
        Cost AddRemove, Cost Dispose, long ValidationAllocatedBytes)
    {
        public Cost MeasuredTotal => ConstructAndGetAction + Actions + AddRemove + Dispose;
    }

    private sealed record CostSummary(double MedianNanoseconds, double MedianAllocatedBytes);
}
