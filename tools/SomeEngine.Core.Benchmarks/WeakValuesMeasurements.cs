using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Core.Collections;

namespace SomeEngine.Tools;

internal static class WeakValuesMeasurements
{
    private const int HitCount = 4096;
    private const int Reads = 1_048_576;
    private const int Samples = 9;
    private const int CleanupCount = 65_536;

    public static void Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine($"dotnet={Environment.Version} architecture={RuntimeInformation.ProcessArchitecture} tiered={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")} samples={Samples}");
        MeasureNativeHandles();
        MeasureHits();
        MeasureCleanup("live", CleanupCount);
        MeasureCleanup("half-collected", CleanupCount / 2);
        MeasureCleanup("collected", 0);
    }

    private static void MeasureHits()
    {
        Value[] held = Enumerable.Range(0, HitCount).Select(index => new Value(index)).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var strong = new Dictionary<int, Value>(HitCount);
        for (int index = 0; index < HitCount; index++)
            strong.Add(index, held[index]);
        Console.WriteLine($"strong-construction-managed={GC.GetAllocatedBytesForCurrentThread() - before}B");
        before = GC.GetAllocatedBytesForCurrentThread();
        var references = new Dictionary<int, WeakReference<Value>>(HitCount);
        for (int index = 0; index < HitCount; index++)
            references.Add(index, new WeakReference<Value>(held[index]));
        Console.WriteLine($"weak-reference-construction-managed={GC.GetAllocatedBytesForCurrentThread() - before}B");
        before = GC.GetAllocatedBytesForCurrentThread();
        var handles = new Dictionary<int, WeakGCHandle<Value>>(HitCount);
        for (int index = 0; index < HitCount; index++)
            handles.Add(index, new WeakGCHandle<Value>(held[index], trackResurrection: false));
        Console.WriteLine($"direct-handle-construction-managed={GC.GetAllocatedBytesForCurrentThread() - before}B");
        long managedBefore = GC.GetTotalMemory(forceFullCollection: true);
        before = GC.GetAllocatedBytesForCurrentThread();
        var values = new WeakValues<int, Value>();
        for (int index = 0; index < HitCount; index++)
            Add(values, index, held[index]);
        long fillAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long retained = GC.GetTotalMemory(forceFullCollection: true) - managedBefore;
        Console.WriteLine($"weak-values-construction-managed={fillAllocated}B retained-managed-change={retained}B records={HitCount}");

        try
        {
            ReportHits("strong-dictionary", new StrongReads(strong));
            ReportHits("weak-reference-dictionary", new ReferenceReads(references));
            ReportHits("direct-handle-dictionary", new HandleReads(handles));
            ReportHits("weak-values", new IndexReads(values));
        }
        finally
        {
            foreach (WeakGCHandle<Value> handle in handles.Values)
                handle.Dispose();
            GC.KeepAlive(held);
            GC.KeepAlive(values);
        }
    }

    private static void ReportHits<TReads>(string name, TReads values) where TReads : struct, IReadValues
    {
        _ = Read(values);
        var times = new double[Samples];
        long allocated = 0;
        long total = 0;
        for (int sample = 0; sample < Samples; sample++)
        {
            var result = Read(values);
            times[sample] = result.Nanoseconds;
            allocated += result.Allocated;
            total = result.Total;
        }
        Array.Sort(times);
        Console.WriteLine($"hits name={name} items={HitCount} reads={Reads} p50={times[Samples / 2]:F2}ns min={times[0]:F2}ns max={times[^1]:F2}ns allocated={allocated}B value-total={total}");
    }

    private static (double Nanoseconds, long Allocated, long Total) Read<TReads>(TReads values)
        where TReads : struct, IReadValues
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        uint random = 42;
        long total = 0;
        for (int read = 0; read < Reads; read++)
        {
            random = random * 1_664_525 + 1_013_904_223;
            total += values.Get((int)(random & (HitCount - 1))).Number;
        }
        long elapsed = Stopwatch.GetTimestamp() - started;
        return (elapsed * 1_000_000_000.0 / Stopwatch.Frequency / Reads,
            GC.GetAllocatedBytesForCurrentThread() - before, total);
    }

    private static void MeasureNativeHandles()
    {
        var value = new Value(1);
        var handles = new WeakGCHandle<Value>[CleanupCount];
        using var process = Process.GetCurrentProcess();
        _ = process.PrivateMemorySize64;
        Collect();
        process.Refresh();
        long beforePrivate = process.PrivateMemorySize64;
        long beforeManaged = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < handles.Length; index++)
            handles[index] = new WeakGCHandle<Value>(value, trackResurrection: false);
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeManaged;
        process.Refresh();
        long privateChange = process.PrivateMemorySize64 - beforePrivate;
        started = Stopwatch.GetTimestamp();
        for (int index = 0; index < handles.Length; index++)
            handles[index].Dispose();
        long releaseElapsed = Stopwatch.GetTimestamp() - started;
        Console.WriteLine($"native-handles count={CleanupCount} handle-size={Unsafe.SizeOf<WeakGCHandle<Value>>()}B managed={allocated}B process-private-change={privateChange}B create={Milliseconds(elapsed):F3}ms release={Milliseconds(releaseElapsed):F3}ms");
        GC.KeepAlive(value);
    }

    private static void MeasureCleanup(string name, int liveCount)
    {
        var batches = new double[Samples];
        var growth = new double[Samples];
        var sweeps = new double[Samples];
        long batchAllocated = 0;
        long growthAllocated = 0;
        for (int sample = 0; sample < Samples; sample++)
        {
            var values = new WeakValues<int, Value>();
            Value[] held = FillForCollection(values, liveCount);
            var inserted = new Value(100);
            Collect();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            _ = values.GetOrBegin(CleanupCount, out var completion);
            batches[sample] = Microseconds(Stopwatch.GetTimestamp() - started);
            batchAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;

            allocated = GC.GetAllocatedBytesForCurrentThread();
            started = Stopwatch.GetTimestamp();
            _ = values.CompleteAsync(CleanupCount, completion!, new ValueTask<Value>(inserted)).Result;
            growth[sample] = Microseconds(Stopwatch.GetTimestamp() - started);
            growthAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;

            started = Stopwatch.GetTimestamp();
            for (int index = 1; index < CleanupCount / 2; index++)
                Add(values, CleanupCount + index, inserted);
            sweeps[sample] = Milliseconds(Stopwatch.GetTimestamp() - started);
            for (int index = 0; index < held.Length; index++)
                _ = values.GetOrBegin(index, out _).Result;
            GC.KeepAlive(held);
            GC.KeepAlive(inserted);
            GC.KeepAlive(values);
        }
        Array.Sort(batches);
        Array.Sort(growth);
        Array.Sort(sweeps);
        Console.WriteLine($"cleanup state={name} records={CleanupCount} live={liveCount} begin-p50={batches[Samples / 2]:F3}us begin-max={batches[^1]:F3}us begin-managed-mean={batchAllocated / Samples}B publish-p50={growth[Samples / 2]:F3}us publish-max={growth[^1]:F3}us publish-managed-mean={growthAllocated / Samples}B following-batches={CleanupCount / 2 - 1} sweep-p50={sweeps[Samples / 2]:F3}ms");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Value[] FillForCollection(WeakValues<int, Value> values, int liveCount)
    {
        var all = new Value[CleanupCount];
        for (int index = 0; index < all.Length; index++)
        {
            all[index] = new Value(index);
            Add(values, index, all[index]);
        }
        Value[] held = all[..liveCount];
        GC.KeepAlive(all);
        return held;
    }

    private static void Add(WeakValues<int, Value> values, int key, Value value)
    {
        _ = values.GetOrBegin(key, out var completion);
        _ = values.CompleteAsync(key, completion!, new ValueTask<Value>(value)).Result;
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static double Microseconds(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;
    private static double Milliseconds(long ticks) => ticks * 1_000.0 / Stopwatch.Frequency;

    private sealed record Value(int Number);

    private interface IReadValues
    {
        Value Get(int key);
    }

    private readonly struct StrongReads(Dictionary<int, Value> values) : IReadValues
    {
        private readonly object _sync = new();
        public Value Get(int key)
        {
            lock (_sync)
                return values[key];
        }
    }

    private readonly struct ReferenceReads(Dictionary<int, WeakReference<Value>> values) : IReadValues
    {
        private readonly object _sync = new();
        public Value Get(int key)
        {
            lock (_sync)
            {
                values[key].TryGetTarget(out Value? value);
                return value!;
            }
        }
    }

    private readonly struct HandleReads(Dictionary<int, WeakGCHandle<Value>> values) : IReadValues
    {
        private readonly object _sync = new();
        public Value Get(int key)
        {
            lock (_sync)
            {
                values[key].TryGetTarget(out Value? value);
                return value!;
            }
        }
    }

    private readonly struct IndexReads(WeakValues<int, Value> values) : IReadValues
    {
        public Value Get(int key) => values.GetOrBegin(key, out _).Result;
    }
}
