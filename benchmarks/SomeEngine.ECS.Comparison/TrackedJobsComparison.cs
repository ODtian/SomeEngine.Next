using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal static class TrackedJobsComparison
{
    internal static void Check(int count)
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        foreach (string mode in new[] { "execute", "parallel", "schedule", "dependent" })
        foreach (var library in ArchiveComparison.Libraries(count, true, scheduler))
        {
            using FullArchiveWorld world = library.Create(); using TrackedArchiveJobs jobs = world.ConnectTrackedJobs(scheduler);
            jobs.Run(mode); jobs.Check(); world.Grow(); jobs.RecordGrowth(); jobs.Run(mode); jobs.Check();
            Console.WriteLine(library.Name + ": tracked " + mode + " exact rows, one version, summaries and declared sparse/resource/Parent reads passed before and after growth.");
        }
    }
    internal static int Run(string[] args)
    {
        string mode = args[1], phase = args[2]; int count = int.Parse(args[3]); string path = Path.GetFullPath(args[4]);
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 3, EnableCounters = false });
        var libraries = ArchiveComparison.Libraries(count, true, scheduler); var repetitions = new int[libraries.Length];
        var samples = libraries.Select(_ => new List<Measurement>()).ToArray();
        var sampleMilliseconds = libraries.Select(_ => new List<double>()).ToArray();
        var sampleRepetitions = libraries.Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < libraries.Length; i++)
        {
            using FullArchiveWorld? world = phase == "hot" ? libraries[i].Create() : null;
            using TrackedArchiveJobs? ready = world?.ConnectTrackedJobs(scheduler);
            if (ready is not null)
            {
                ready.Run(mode); ready.Check();
            }
            else Measure(libraries[i], scheduler, mode, phase, true);
            long calibrationStart = Stopwatch.GetTimestamp(); int done = 0;
            do { Sample(i, ready); done++; } while (Stopwatch.GetElapsedTime(calibrationStart).TotalMilliseconds < 300);
            repetitions[i] = Math.Max(1, (int)(done * 100.0 / Stopwatch.GetElapsedTime(calibrationStart).TotalMilliseconds));
        }
        for (int round = 0; round < 10; round++)
            for (int offset = 0; offset < libraries.Length; offset++)
            {
                int i = (round + offset) % libraries.Length;
                using FullArchiveWorld? world = phase == "hot" ? libraries[i].Create() : null;
                using TrackedArchiveJobs? ready = world?.ConnectTrackedJobs(scheduler);
                if (ready is not null) { ready.Run(mode); ready.Check(); ready.Run(mode); }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                double elapsed = 0, bytes = 0;
                long sampleStart = Stopwatch.GetTimestamp();
                int repeatCount = 0;
                do
                {
                    var measured = Sample(i, ready); elapsed += measured.Nanoseconds; bytes += measured.AllocatedBytes;
                    repeatCount++;
                } while (Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds < 100);
                samples[i].Add(new(elapsed / repeatCount, bytes / repeatCount));
                sampleRepetitions[i].Add(repeatCount);
                sampleMilliseconds[i].Add(Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds);
            }
            var report = new
            {
                Workload = "tracked-job-" + mode, Phase = phase, Entities = count, Samples = 10, TargetSampleMilliseconds = 100,
                ReferenceValueAccess = "concrete-nonvirtual",
                CycleOperations = phase == "cycle" ? 4 : 1, GrowthEntities = phase is "growth" or "cycle" ? 1000 : 0,
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
        Measurement Sample(int i, TrackedArchiveJobs? ready)
        {
            if (phase != "hot") return Measure(libraries[i], scheduler, mode, phase);
            long allocated = GC.GetTotalAllocatedBytes(false), began = Stopwatch.GetTimestamp();
            ready!.Run(mode);
            return new(Stopwatch.GetElapsedTime(began).TotalNanoseconds, GC.GetTotalAllocatedBytes(false) - allocated);
        }
    }
    private static Measurement Measure(ArchiveComparison.Library library, Scheduler scheduler, string mode, string phase, bool verify = false)
    {
        long bytes = 0, began = 0;
        if (phase != "growth") { bytes = GC.GetTotalAllocatedBytes(false); began = Stopwatch.GetTimestamp(); }
        FullArchiveWorld world = library.Create(); TrackedArchiveJobs jobs = world.ConnectTrackedJobs(scheduler);
        if (phase == "growth")
        {
            jobs.Run(mode); bytes = GC.GetTotalAllocatedBytes(false); began = Stopwatch.GetTimestamp();
            world.Grow(); jobs.RecordGrowth();
        }
        int operations = phase == "cycle" ? 4 : 1;
        for (int operation = 0; operation < operations; operation++)
        {
            if (phase == "cycle" && operation == 2) { world.Grow(); jobs.RecordGrowth(); }
            jobs.Run(mode);
        }
        long ended = Stopwatch.GetTimestamp(), allocated = GC.GetTotalAllocatedBytes(false);
        if (verify) jobs.Check();
        jobs.Dispose(); world.Dispose();
        if (phase == "cycle") { ended = Stopwatch.GetTimestamp(); allocated = GC.GetTotalAllocatedBytes(false); }
        return new(Stopwatch.GetElapsedTime(began, ended).TotalNanoseconds, allocated - bytes);
    }
    private static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return (sorted[4] + sorted[5]) / 2; }
    private readonly record struct Measurement(double Nanoseconds, double AllocatedBytes);
}
