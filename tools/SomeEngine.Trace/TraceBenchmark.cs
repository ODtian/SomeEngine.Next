using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SomeEngine.Diagnostics;
using TraceOptions = SomeEngine.Diagnostics.TraceOptions;

namespace SomeEngine.Trace;

public sealed record TraceBenchmarkResult(string Mode, int Repeat, int Scopes, double NanosecondsPerScope,
    double RecordedNanosecondsPerScope, long AllocatedBytes, long Events, long DroppedEvents, bool Complete);

public static class TraceBenchmark
{
    private static readonly TraceSite Site = new("benchmark", "Scope");
    private static int s_result;

    public static void Execute(string directory)
    {
        Directory.CreateDirectory(directory);
        const int iterations = 250_000;
        List<TraceBenchmarkResult> results = [];
        Loop(iterations, false); Loop(iterations, true);
        for (int repeat = 0; repeat < 5; repeat++)
        {
            foreach (string mode in new[] { "baseline", "disabled", "enabled", "allocations" })
            {
                string path = Path.Combine(directory, $"{mode}-{repeat}.setrace");
                TraceCapture? capture = mode is "enabled" or "allocations"
                    ? EngineTrace.Start(path, new TraceOptions { Allocations = mode == "allocations" }) : null;
                Loop(4096, mode != "baseline");
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                Loop(iterations, mode != "baseline");
                double nanoseconds = Stopwatch.GetElapsedTime(started).TotalNanoseconds / iterations;
                bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                capture?.Dispose();
                TraceData? data = capture is null ? null : TraceFile.Read(path);
                TraceAnalysis? analysis = data is null ? null : TraceAnalyzer.Analyze(data);
                double recorded = analysis is null ? 0 : analysis.Spans.OrderBy(s => s.Start).Skip(4096)
                    .Average(s => (s.End - s.Start) * 1e9 / data!.Frequency);
                var result = new TraceBenchmarkResult(mode, repeat, iterations, nanoseconds, recorded, bytes,
                    data?.Events.LongLength ?? 0, analysis?.DroppedEvents ?? 0, analysis?.Complete ?? true);
                results.Add(result);
                Console.WriteLine($"{mode,-12} repeat {repeat}: {nanoseconds:F2} ns/scope; interval {recorded:F2} ns; {bytes} B; complete={result.Complete}");
            }
        }
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Loop(int count, bool instrument)
    {
        int result = 0;
        if (instrument)
            for (int i = 0; i < count; i++) { using var trace = EngineTrace.Begin(Site); result = Step(result, i); }
        else
            for (int i = 0; i < count; i++) result = Step(result, i);
        Volatile.Write(ref s_result, result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Step(int value, int index) => unchecked(value * 33 + index);
}
