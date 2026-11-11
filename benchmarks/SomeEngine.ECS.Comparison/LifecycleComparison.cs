using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal static class LifecycleComparison
{
    private const int CycleOperations = 4;
    internal static int Run(string[] args)
    {
        string mode = args[1], phase = args[2]; int count = int.Parse(args[3]); string path = Path.GetFullPath(args[4]);
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        var libraries = ArchiveComparison.Libraries(count, true, scheduler);
        var repetitions = new int[libraries.Length];
        var samples = libraries.Select(_ => new List<Measurement>()).ToArray();
        var sampleMilliseconds = libraries.Select(_ => new List<double>()).ToArray();
        var sampleRepetitions = libraries.Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < libraries.Length; i++)
        {
            Measure(libraries[i].Create, scheduler, mode, phase, true);
            long calibrationStart = Stopwatch.GetTimestamp(); int done = 0;
            do { Measure(libraries[i].Create, scheduler, mode, phase); done++; }
            while (Stopwatch.GetElapsedTime(calibrationStart).TotalMilliseconds < 300);
            repetitions[i] = Math.Max(1, (int)(done * 100.0 / Stopwatch.GetElapsedTime(calibrationStart).TotalMilliseconds));
        }
        for (int round = 0; round < 10; round++)
            for (int offset = 0; offset < libraries.Length; offset++)
            {
                int i = (round + offset) % libraries.Length;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                double elapsed = 0, bytes = 0;
                long sampleStart = Stopwatch.GetTimestamp();
                int repeatCount = 0;
                do
                {
                    var measurement = Measure(libraries[i].Create, scheduler, mode, phase);
                    elapsed += measurement.Nanoseconds; bytes += measurement.AllocatedBytes;
                    repeatCount++;
                } while (Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds < 100);
                samples[i].Add(new(elapsed / repeatCount, bytes / repeatCount));
                sampleRepetitions[i].Add(repeatCount);
                sampleMilliseconds[i].Add(Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds);
            }
        var report = new
        {
            Workload = "lifecycle-" + mode, Phase = phase, Entities = count, Samples = 10, TargetSampleMilliseconds = 100,
            ReferenceValueAccess = "concrete-nonvirtual",
            CycleOperations = phase == "cycle" ? CycleOperations : 1, GrowthEntities = phase is "growth" or "cycle" ? 1000 : 0,
            Framework = RuntimeInformation.FrameworkDescription, ServerGC = GCSettings.IsServerGC, TieredCompilation = false, TimingProcessor = Thread.GetCurrentProcessorId(),
            Results = libraries.Select((library, i) => new
            {
                Library = library.Name, Repetitions = repetitions[i], MedianNanoseconds = Median(samples[i].Select(s => s.Nanoseconds)),
                RepetitionsPerSample = sampleRepetitions[i],
                MedianSampleMilliseconds = Median(sampleMilliseconds[i]),
                SampleMilliseconds = sampleMilliseconds[i],
                AllocatedBytes = Median(samples[i].Select(s => s.AllocatedBytes)), Measurements = samples[i]
            })
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json); Console.WriteLine(json); return 0;
    }

    private static Measurement Measure(Func<FullArchiveWorld> create, Scheduler scheduler, string mode, string phase, bool verify = false)
    {
        if (phase == "cycle")
        {
            long allocated = GC.GetTotalAllocatedBytes(false), began = Stopwatch.GetTimestamp();
            for (int cycle = 0; cycle < CycleOperations; cycle++)
            {
                FullArchiveWorld world = create(); var jobs = world.ConnectJobs(scheduler);
                IDisposable? retained = mode == "retained" ? world.Capture(true) : null;
                long resources = world.ResourceReleases;
                if ((cycle & 1) != 0) { world.Grow(); jobs.RecordGrowth(1000); }
                Close(world, jobs, mode, retained);
                if (mode == "clear") world.Dispose();
                if (verify) Verify(world, jobs, mode, resources, true);
            }
            return new(Stopwatch.GetElapsedTime(began).TotalNanoseconds, GC.GetTotalAllocatedBytes(false) - allocated);
        }

        long bytes = 0, start = 0;
        if (phase == "first") { bytes = GC.GetTotalAllocatedBytes(false); start = Stopwatch.GetTimestamp(); }
        FullArchiveWorld ready = create(); var preparedJobs = ready.ConnectJobs(scheduler);
        long resourceStart = ready.ResourceReleases;
        IDisposable? saved = mode == "retained" ? ready.Capture(true) : null;
        if (phase == "hot" && mode == "retained") { preparedJobs.Schedule(); ready.Dispose(); }
        if (phase != "first") { bytes = GC.GetTotalAllocatedBytes(false); start = Stopwatch.GetTimestamp(); }
        if (phase == "growth") { ready.Grow(); preparedJobs.RecordGrowth(1000); }
        if (phase == "hot" && mode == "retained") saved!.Dispose();
        else Close(ready, preparedJobs, mode, saved);
        var result = new Measurement(Stopwatch.GetElapsedTime(start).TotalNanoseconds, GC.GetTotalAllocatedBytes(false) - bytes);
        if (verify) Verify(ready, preparedJobs, mode, resourceStart, mode != "clear");
        if (mode == "clear") ready.Dispose();
        return result;
    }

    private static void Close(FullArchiveWorld world, MixedLifecycleJobs jobs, string mode, IDisposable? retained)
    {
        jobs.Schedule();
        if (mode == "clear") world.Clear();
        else world.Dispose();
        retained?.Dispose();
    }
    private static void Verify(FullArchiveWorld world, MixedLifecycleJobs jobs, string mode, long resourceStart, bool disposed)
    {
        jobs.CheckCompleted();
        if (disposed) world.CheckDisposed(); else world.CheckCleared();
        if (world.ResourceReleases - resourceStart != (mode == "retained" ? 32 : 16) || !world.InitialResourceReleased)
            throw new InvalidOperationException("Mixed lifecycle retained resources differ.");
    }
    private static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return (sorted[4] + sorted[5]) / 2; }
    private readonly record struct Measurement(double Nanoseconds, double AllocatedBytes);
}
