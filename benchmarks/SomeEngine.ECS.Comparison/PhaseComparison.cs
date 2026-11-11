using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SomeEngine.ECS.Comparison;

internal static class PhaseComparison
{
    internal static int Run(string[] args)
    {
        string phase = args[1];
        string workload = args[2];
        int count = int.Parse(args[3]);
        string output = Path.GetFullPath(args[4]);
        int cycleOperations = args.Length > 5 ? int.Parse(args[5]) : 100;
        var libraries = ComparisonEntry.CreateLibraries(count, workload);
        var samples = libraries.Select(_ => new List<Measurement>()).ToArray();
        var sampleMilliseconds = libraries.Select(_ => new List<double>()).ToArray();
        var sampleRepetitions = libraries.Select(_ => new List<int>()).ToArray();
        var repetitions = new int[libraries.Length];
        for (int library = 0; library < libraries.Length; library++)
        {
            Console.WriteLine($"{libraries[library].Name}: {phase} {workload} N={count}, calibration began, heap={GC.GetTotalMemory(false)} B.");
            using (ComparisonWorld world = libraries[library].Create())
            {
                Action action = world.GetAction(workload);
                if (phase == "growth") world.GrowAndReclaim(action);
                else action();
                world.CheckResult(workload);
            }
            long calibrationStart = Stopwatch.GetTimestamp();
            int repeats = 0;
            do
            {
                Measure(libraries[library].Create, workload, phase, cycleOperations);
                repeats++;
            } while (Stopwatch.GetElapsedTime(calibrationStart).TotalMilliseconds < 300);
            repetitions[library] = Math.Max(1, (int)(repeats * 100.0 / Stopwatch.GetElapsedTime(calibrationStart).TotalMilliseconds));
            Console.WriteLine($"{libraries[library].Name}: {repetitions[library]} repetitions/sample.");
        }
        for (int round = 0; round < 10; round++)
            for (int offset = 0; offset < libraries.Length; offset++)
            {
                int library = (round + offset) % libraries.Length;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                double time = 0, bytes = 0;
                long sampleStart = Stopwatch.GetTimestamp();
                int repeatCount = 0;
                do
                {
                    Measurement measurement = Measure(libraries[library].Create, workload, phase, cycleOperations);
                    time += measurement.Nanoseconds;
                    bytes += measurement.AllocatedBytes;
                    repeatCount++;
                } while (Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds < 100);
                samples[library].Add(new(time / repeatCount, bytes / repeatCount));
                sampleRepetitions[library].Add(repeatCount);
                sampleMilliseconds[library].Add(Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds);
                Console.WriteLine($"{libraries[library].Name}: sample {round + 1}/10 complete, heap={GC.GetTotalMemory(false)} B.");
            }
        var report = new
        {
            Phase = phase, Workload = workload, Entities = count, Samples = 10, TargetSampleMilliseconds = 100,
            ActionThreadLifetime = BufferActionThread.UsesActionThread(workload) ? "One action" : "Measurement",
            CycleOperations = phase == "cycle" ? cycleOperations : 1,
            LayoutChangeBatch = phase == "cycle" ? ComparisonWorld.Changes : 0,
            EndpointCount = workload.StartsWith("relation-") ? count * (workload.StartsWith("relation-undirected-one-to-one") ? 2 : 1) : (int?)null,
            EdgeCount = workload.StartsWith("relation-") ? count : (int?)null,
            Framework = RuntimeInformation.FrameworkDescription, ServerGC = GCSettings.IsServerGC,
            TimingProcessor = Thread.GetCurrentProcessorId(),
            ParticipantProcessors = BenchmarkCpu.ParticipantProcessors,
            Results = libraries.Select((library, i) => new
            {
                Library = library.Name,
                Repetitions = repetitions[i],
                RepetitionsPerSample = sampleRepetitions[i],
                MedianSampleMilliseconds = Median(sampleMilliseconds[i]),
                SampleMilliseconds = sampleMilliseconds[i],
                Storage = ComparisonEntry.QueryStorage(workload, library.Name),
                MedianNanoseconds = Median(samples[i].Select(s => s.Nanoseconds)),
                AllocatedBytes = Median(samples[i].Select(s => s.AllocatedBytes)),
                Measurements = samples[i],
            }),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(output, json);
        Console.WriteLine(json);
        return 0;
    }

    private static Measurement Measure(Func<ComparisonWorld> create, string workload, string phase, int cycleOperations)
    {
        if (phase == "growth")
        {
            using ComparisonWorld ready = create();
            Action observe = ready.GetAction(workload);
            if (!workload.StartsWith("query-")) observe();
            long before = GC.GetTotalAllocatedBytes(true);
            long began = Stopwatch.GetTimestamp();
            ready.GrowAndReclaim(observe);
            return new(Stopwatch.GetElapsedTime(began).TotalNanoseconds,
                GC.GetTotalAllocatedBytes(true) - before);
        }
        long bytes = GC.GetTotalAllocatedBytes(true);
        long start = Stopwatch.GetTimestamp();
        ComparisonWorld world = create();
        Action action = world.GetAction(workload);
        if (phase == "first")
        {
            action();
            Measurement result = new(Stopwatch.GetElapsedTime(start).TotalNanoseconds,
                GC.GetTotalAllocatedBytes(true) - bytes);
            world.Dispose();
            return result;
        }
        if (phase == "cycle")
        {
            for (int batch = 1; batch <= cycleOperations; batch++)
            {
                action();
                if (batch % 10 == 0 || batch == cycleOperations / 2) world.AddRemove();
            }
            world.Dispose();
            return new(Stopwatch.GetElapsedTime(start).TotalNanoseconds,
                GC.GetTotalAllocatedBytes(true) - bytes);
        }
        world.Dispose();
        throw new ArgumentException("Choose first, growth or cycle.");
    }

    private static double Median(IEnumerable<double> sequence)
    {
        double[] values = sequence.Order().ToArray();
        return (values[4] + values[5]) / 2;
    }

    private sealed record Measurement(double Nanoseconds, double AllocatedBytes);
}
