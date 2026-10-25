using System.Collections.Concurrent;
using System.Diagnostics;
using SomeEngine.Job;

int samples = ReadIntArgument(args, "--samples", 12);
int warmups = ReadIntArgument(args, "--warmups", 3);
int workerCount = ReadNonNegativeArgument(
    args,
    "--workers",
    Math.Max(1, Environment.ProcessorCount - 1));
int parallelLength = ReadIntArgument(args, "--length", 1_000_000);
int spinCount = ReadNonNegativeArgument(args, "--spin", 128);
int busySpinCount = ReadNonNegativeArgument(args, "--busy-spin", Math.Max(spinCount, 2_048));
bool automatic = args.Contains("--auto", StringComparer.OrdinalIgnoreCase);
bool counters = args.Contains("--counters", StringComparer.OrdinalIgnoreCase);
bool synchronous = args.Contains("--synchronous", StringComparer.OrdinalIgnoreCase);
bool dependencies = args.Contains("--dependencies", StringComparer.OrdinalIgnoreCase);
bool categories = args.Contains("--categories", StringComparer.OrdinalIgnoreCase);
bool storage = args.Contains("--storage", StringComparer.OrdinalIgnoreCase);

JobSystem.Initialize(new JobRuntimeConfig
{
    WorkerCount = workerCount,
    EnableCounters = counters,
    WorkerSpinCount = spinCount,
    BusyWorkerSpinCount = busySpinCount
});

try
{
    Console.WriteLine(
        $"SomeEngine.Job benchmark | workers={workerCount} | samples={samples} | warmups={warmups} | " +
        (dependencies ? "predecessors=1,16,256,4096,16384 | input-copies=1,2 | "
            : $"length={parallelLength} | batch={(automatic ? "auto" : "explicit")} | ") +
        $"spin={spinCount}/{busySpinCount} | counters={counters}");

    if (storage)
    {
        RunStorage(JobSystem.Default, parallelLength, workerCount, warmups, samples,
            ReadIntArgument(args, "--calls", 128));
        return;
    }

    if (dependencies)
    {
        foreach (int count in new[] { 1, 16, 256, 4_096, 16_384 })
        {
            RunDependencies(JobSystem.Default, count, copies: 1, warmups, samples);
            RunDependencies(JobSystem.Default, count, copies: 2, warmups, samples);
        }
        return;
    }

    if (categories)
    {
        CategoryBenchmarks.Run(JobSystem.Default, workerCount, warmups, samples,
            ReadIntArgument(args, "--calls", 16));
        return;
    }

    if (synchronous)
    {
        int calls = ReadIntArgument(args, "--calls", 128);
        int[] values = new int[parallelLength];
        var job = new LightParallelJob(values);
        int size = automatic ? JobScheduleOptions.AutomaticBatchSize
            : Math.Max(1, parallelLength / Math.Max(1, workerCount * 4));
        RunCalls("schedule-and-complete", warmups, samples, calls,
            () => JobSystem.Default.ScheduleParallel(job, values.Length, size).Complete());
        RunCalls("execute-parallel", warmups, samples, calls,
            () => JobSystem.Default.ExecuteParallel(job, values.Length, size));
        return;
    }

    Run(
        "single-job x10k",
        warmups,
        samples,
        static () =>
        {
            const int count = 10_000;
            var handles = new JobHandle[count];
            for (int i = 0; i < handles.Length; i++)
                handles[i] = JobSystem.Schedule(new EmptyJob());
            JobSystem.CombineDependencies(handles).Complete();
        });

    Run(
        "four-producer x25k",
        warmups,
        samples,
        static () =>
        {
            const int producerCount = 4;
            const int jobsPerProducer = 25_000;
            var groups = new JobHandle[producerCount];
            Parallel.For(0, producerCount, producer =>
            {
                var handles = new JobHandle[jobsPerProducer];
                for (int i = 0; i < handles.Length; i++)
                    handles[i] = JobSystem.Schedule(new EmptyJob());
                groups[producer] = JobSystem.CombineDependencies(handles);
            });
            JobSystem.CombineDependencies(groups).Complete();
        });

    var lightValues = new int[parallelLength];
    int batchSize = automatic
        ? -1 // JobScheduleOptions.AutomaticBatchSize; literal keeps the benchmark source baseline-compatible.
        : Math.Max(1, parallelLength / Math.Max(1, workerCount * 4));
    Run(
        "parallel-light",
        warmups,
        samples,
        () => JobSystem.ScheduleParallel(
            new LightParallelJob(lightValues),
            lightValues.Length,
            batchSize).Complete());

    var variableValues = new long[parallelLength];
    Run(
        "parallel-variable",
        warmups,
        samples,
        () => JobSystem.ScheduleParallel(
            new VariableParallelJob(variableValues),
            variableValues.Length,
            batchSize).Complete());
}
finally
{
    JobSystem.Shutdown();
}

static int ReadIntArgument(string[] arguments, string name, int fallback)
{
    int index = Array.FindIndex(arguments, value =>
        string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length &&
           int.TryParse(arguments[index + 1], out int parsed) && parsed > 0
        ? parsed
        : fallback;
}

static int ReadNonNegativeArgument(string[] arguments, string name, int fallback)
{
    int index = Array.FindIndex(arguments, value =>
        string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length &&
           int.TryParse(arguments[index + 1], out int parsed) && parsed >= 0
        ? parsed
        : fallback;
}

static void Run(string name, int warmups, int samples, Action action)
{
    for (int i = 0; i < warmups; i++)
        action();

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    var timings = new double[samples];
    for (int i = 0; i < timings.Length; i++)
    {
        long started = Stopwatch.GetTimestamp();
        action();
        timings[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    Array.Sort(timings);
    Console.WriteLine(
        $"{name,-24} p50={Percentile(timings, 0.50),9:F3} ms " +
        $"p95={Percentile(timings, 0.95),9:F3} ms " +
        $"p99={Percentile(timings, 0.99),9:F3} ms");
}

static double Percentile(double[] sorted, double percentile)
{
    int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

static void RunCalls(string name, int warmups, int samples, int calls, Action action)
{
    for (int warmup = 0; warmup < warmups; warmup++)
        for (int call = 0; call < calls; call++)
            action();

    var microseconds = new double[samples];
    var allocatedBytes = new double[samples];
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    for (int sample = 0; sample < samples; sample++)
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        for (int call = 0; call < calls; call++)
            action();
        microseconds[sample] = Stopwatch.GetElapsedTime(started).TotalMicroseconds / calls;
        allocatedBytes[sample] = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)calls;
    }
    Array.Sort(microseconds);
    Array.Sort(allocatedBytes);
    Console.WriteLine($"{name,-24} calls={calls} p50={Percentile(microseconds, 0.50):F3} us/call " +
        $"p95={Percentile(microseconds, 0.95):F3} us/call allocated-p50={Percentile(allocatedBytes, 0.50):F2} B/call");
}

static void RunStorage(Scheduler scheduler, int length, int workers, int warmups, int samples, int calls)
{
    int[] values = new int[length];
    var job = new LightParallelJob(values);
    int batchSize = Math.Max(1, length / Math.Max(1, workers * 4));
    RunCalls("empty-complete", warmups, samples, calls,
        () => scheduler.Schedule(new EmptyJob()).Complete());
    RunCalls("parallel-complete", warmups, samples, calls,
        () => scheduler.ScheduleParallel(job, length, batchSize).Complete());
    using var started = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var blocker = new WaitForRelease(started, release);
    foreach (int count in new[] { 1, 4, 16, 64 })
    {
        var accesses = new JobResourceAccess[count];
        for (int index = 0; index < count; index++)
        {
            int start = index * length / count;
            int end = (index + 1) * length / count;
            accesses[index] = JobResourceAccess.Write(scheduler, values, start, end - start);
        }
        RunCalls($"ready-accesses-{count}", warmups, samples, calls,
            () => scheduler.ScheduleParallel(job, length, batchSize, accesses).Complete());
        RunCalls($"waiting-accesses-{count}", warmups, samples, calls, () =>
        {
            started.Reset();
            release.Reset();
            JobHandle dependency = scheduler.Schedule(blocker);
            started.Wait();
            JobHandle handle = scheduler.ScheduleParallel(job, length, batchSize, accesses, dependency);
            release.Set();
            handle.Complete();
            dependency.Complete();
        });
    }
    int expected = (warmups + samples) * calls * 9;
    if (values.Any(value => value != expected))
        throw new InvalidOperationException("The storage workloads must complete every array update.");
}

static void RunDependencies(Scheduler scheduler, int count, int copies, int warmups, int samples)
{
    var inputs = new JobHandle[count * copies];
    (double Microseconds, double AllocatedBytes) Measure()
    {
        for (int index = 0; index < count; index++)
        {
            JobHandle handle = scheduler.Schedule(new EmptyJob());
            for (int copy = 0; copy < copies; copy++)
                inputs[copy * count + count - index - 1] = handle;
        }
        long before = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp();
        scheduler.CombineDependencies(inputs).Complete();
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        return (elapsed, GC.GetTotalAllocatedBytes(precise: true) - before);
    }

    for (int warmup = 0; warmup < warmups; warmup++)
        Measure();
    var microseconds = new double[samples];
    var allocatedBytes = new double[samples];
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    for (int sample = 0; sample < samples; sample++)
        (microseconds[sample], allocatedBytes[sample]) = Measure();
    Array.Sort(microseconds);
    Array.Sort(allocatedBytes);
    Console.WriteLine($"combine-complete unique={count,5} inputs={inputs.Length,5} " +
        $"p50={Percentile(microseconds, 0.50):F3} us p95={Percentile(microseconds, 0.95):F3} us " +
        $"allocated-p50={Percentile(allocatedBytes, 0.50):F0} B");
}

readonly struct EmptyJob : IJob
{
    public void Execute()
    {
    }
}

readonly struct WaitForRelease(ManualResetEventSlim started, ManualResetEventSlim release) : IJob
{
    public void Execute()
    {
        started.Set();
        release.Wait();
    }
}

readonly struct LightParallelJob(int[] values) : IJobParallelFor
{
    public void Execute(int index)
    {
        values[index]++;
    }
}

readonly struct VariableParallelJob(long[] values) : IJobParallelFor
{
    public void Execute(int index)
    {
        long value = index + 1;
        int iterations = (index & 31) + 1;
        for (int i = 0; i < iterations; i++)
            value = unchecked((value * 1_664_525L) + 1_013_904_223L);
        values[index] = value;
    }
}
