using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SomeEngine.ECS.Comparison;

internal static class ArchivePhaseComparison
{
    internal static int Run(string[] args)
    {
        string phase = args[1], mode = args[2]; bool preserve = args[3] == "preserve", raw = args[4] == "raw";
        int count = int.Parse(args[5]); string path = Path.GetFullPath(args[6]);
        var libraries = ArchiveComparison.Libraries(count, raw);
        var repetitions = new int[libraries.Length];
        var samples = libraries.Select(_ => new List<Measurement>()).ToArray();
        var sampleMilliseconds = libraries.Select(_ => new List<double>()).ToArray();
        var sampleRepetitions = libraries.Select(_ => new List<int>()).ToArray();
        for (int i = 0; i < libraries.Length; i++)
        {
            Measure(libraries[i], mode, phase, preserve, true);
            long calibrationStart = Stopwatch.GetTimestamp(); int done = 0;
            do { Measure(libraries[i], mode, phase, preserve); done++; }
            while (Stopwatch.GetElapsedTime(calibrationStart).TotalMilliseconds < 300);
            repetitions[i] = Math.Max(1, (int)(done * 100.0 / Stopwatch.GetElapsedTime(calibrationStart).TotalMilliseconds));
        }
        for (int round = 0; round < 10; round++)
            for (int offset = 0; offset < libraries.Length; offset++)
            {
                int i = (round + offset) % libraries.Length;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                double elapsed = 0, bytes = 0; long output = 0;
                long sampleStart = Stopwatch.GetTimestamp();
                int repeatCount = 0;
                do
                {
                    var measurement = Measure(libraries[i], mode, phase, preserve);
                    elapsed += measurement.Nanoseconds; bytes += measurement.AllocatedBytes; output = measurement.OutputBytes;
                    repeatCount++;
                } while (Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds < 100);
                samples[i].Add(new(elapsed / repeatCount, bytes / repeatCount, output));
                sampleRepetitions[i].Add(repeatCount);
                sampleMilliseconds[i].Add(Stopwatch.GetElapsedTime(sampleStart).TotalMilliseconds);
            }
        var report = new
        {
            Workload = "archive-" + mode, Phase = phase, Progress = preserve ? "preserve" : "rebuild", Encoding = raw ? "raw" : "codec",
            ReferenceValueAccess = "concrete-nonvirtual", FileService = mode == "file" ? "DurableSaveStore" : null,
            FileIntegrity = mode == "file" ? "SHA-256" : null, FileEnvelopeVersion = mode == "file" ? 4 : 0,
            FileHeaderBytes = mode == "file" ? 64 : 0, FileGenerationSlots = mode == "file" ? 2 : 0,
            Entities = count, Samples = 10, TargetSampleMilliseconds = 100, CycleOperations = phase == "cycle" ? 4 : 1,
            GrowthEntities = phase is "growth" or "cycle" ? 1000 : 0,
            Framework = RuntimeInformation.FrameworkDescription, ServerGC = GCSettings.IsServerGC, TieredCompilation = false, TimingProcessor = Thread.GetCurrentProcessorId(),
            Results = libraries.Select((library, i) => new
            {
                Library = library.Name, Repetitions = repetitions[i], MedianNanoseconds = Median(samples[i].Select(s => s.Nanoseconds)),
                RepetitionsPerSample = sampleRepetitions[i],
                MedianSampleMilliseconds = Median(sampleMilliseconds[i]),
                SampleMilliseconds = sampleMilliseconds[i],
                AllocatedBytes = Median(samples[i].Select(s => s.AllocatedBytes)), OutputBytes = samples[i][^1].OutputBytes,
                Measurements = samples[i]
            })
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, json); Console.WriteLine(json); return 0;
    }

    private static Measurement Measure(ArchiveComparison.Library library, string mode, string phase, bool preserve, bool verify = false)
    {
        long bytes = 0, began = 0;
        if (phase != "growth") { bytes = GC.GetTotalAllocatedBytes(false); began = Stopwatch.GetTimestamp(); }
        FullArchiveWorld world = library.Create();
        var action = new ArchiveComparison.ArchiveAction(world, mode, preserve, library.Name);
        if (phase == "growth")
        {
            action.Run(); action.Verify();
            bytes = GC.GetTotalAllocatedBytes(false); began = Stopwatch.GetTimestamp();
            world.Grow(); action.PrepareGrowth();
        }
        int operations = phase == "cycle" ? 4 : 1;
        for (int operation = 0; operation < operations; operation++)
        {
            if (phase == "cycle" && operation == 2) { world.Grow(); action.PrepareGrowth(); }
            action.Run();
        }
        long ended = Stopwatch.GetTimestamp(), allocated = GC.GetTotalAllocatedBytes(false);
        long output = action.OutputBytes;
        if (verify) { action.Verify(); world.Check(preserve); }
        action.Dispose(); world.Dispose();
        if (phase == "cycle") { ended = Stopwatch.GetTimestamp(); allocated = GC.GetTotalAllocatedBytes(false); }
        return new(Stopwatch.GetElapsedTime(began, ended).TotalNanoseconds, allocated - bytes, output);
    }
    private static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return (sorted[4] + sorted[5]) / 2; }
    private readonly record struct Measurement(double Nanoseconds, double AllocatedBytes, long OutputBytes);
}
