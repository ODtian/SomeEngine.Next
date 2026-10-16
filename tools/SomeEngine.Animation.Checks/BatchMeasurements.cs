using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Animation;
using SomeEngine.Animation.Consumers;

namespace SomeEngine.Animation.Checks;

internal static class BatchMeasurements
{
    public static void Run()
    {
        Console.WriteLine($"framework={RuntimeInformation.FrameworkDescription}; architecture={RuntimeInformation.ProcessArchitecture}; nativeAot={!RuntimeFeature.IsDynamicCodeSupported}; executableBytes={new FileInfo(Environment.ProcessPath!).Length}; warmup=64; samples=96");
        Console.WriteLine("motion,count,path,median_ns_per_track,p95_ns_per_track,p99_ns_per_track,allocated_bytes_per_batch,instance_bytes,first_allocation_bytes,retained_array_bytes");
        foreach (int count in new[] { 1_000, 10_000, 100_000 })
        {
            Measure<TweenParameters, TweenState, Tween<float, FloatOps, Linear>>("tween", count, new TweenParameters(10_000));
            Measure<SpringParameters, SpringState<float>, Spring<float, FloatOps>>("spring", count, new SpringParameters(180, 20));
            Measure<SpringParameters, SpringState<float>, ConsumerSpring>("consumer-spring", count, new SpringParameters(180, 20));
            Measure<HistoryParameters, HistoryState, HistoryMotion>("history", count, new HistoryParameters(10_000, 0.75f, "shared-path"));
            Measure<HistoryParameters, HistoryState, HistoryBatchMotion>("history-batch", count, new HistoryParameters(10_000, 0.75f, "shared-path"));
        }
    }

    private static void Measure<TParameters, TState, TMotion>(string name, int count, TParameters parameters)
        where TParameters : struct
        where TState : struct
        where TMotion : struct, IMotion<float, FloatOps, TParameters, TState>
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var data = new MotionData<float, TParameters, TState>[count];
        for (int index = 0; index < count; index++)
        {
            ref var element = ref data[index];
            element.Active = true;
            element.Parameters = parameters;
            element.Values = new(0, 1, 0);
            element.Time = new(0.25f + (index % 16) / 64f, 1f / 120);
            TMotion.Initialize(in parameters, in element.Values, ref element.State);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var reference = data.ToArray();
        Record<TParameters, TState>(name, count, "batch", data, allocated, () => Evaluate<TParameters, TState, TMotion>(data));
        Record<TParameters, TState>(name, count, "direct", reference, allocated, () => Direct<TParameters, TState, TMotion>(reference));
    }

    private static void Record<TParameters, TState>(string name, int count, string path,
        MotionData<float, TParameters, TState>[] data, long firstAllocation, Action sample)
        where TParameters : struct
        where TState : struct
    {
        for (int warmup = 0; warmup < 64; warmup++)
            sample();
        var times = new double[96];
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < times.Length; index++)
        {
            long start = Stopwatch.GetTimestamp();
            sample();
            times[index] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / count;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Array.Sort(times);
        int size = Unsafe.SizeOf<MotionData<float, TParameters, TState>>();
        Console.WriteLine(FormattableString.Invariant($"{name},{count},{path},{times[48]:F3},{times[91]:F3},{times[95]:F3},{allocated / 96d:F3},{size},{firstAllocation},{(long)size * count}"));
        GC.KeepAlive(data);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Evaluate<TParameters, TState, TMotion>(MotionData<float, TParameters, TState>[] data)
        where TParameters : struct
        where TState : struct
        where TMotion : struct, IMotion<float, FloatOps, TParameters, TState>
    {
        var batch = new MotionBatch<float, TParameters, TState>(data);
        MotionDispatcher<float, FloatOps, TParameters, TState, TMotion>.EvaluateBatch(ref batch);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Direct<TParameters, TState, TMotion>(MotionData<float, TParameters, TState>[] data)
        where TParameters : struct
        where TState : struct
        where TMotion : struct, IMotion<float, FloatOps, TParameters, TState>
    {
        for (int index = 0; index < data.Length; index++)
        {
            ref var element = ref data[index];
            element.Sample = TMotion.Evaluate(in element.Parameters, in element.Time, in element.Values, ref element.State);
        }
    }
}
