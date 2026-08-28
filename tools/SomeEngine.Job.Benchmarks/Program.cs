using System.Collections.Concurrent;
using System.Diagnostics;
using SomeEngine.Job;

int samples = ReadIntArgument(args, "--samples", 12);
int warmups = ReadIntArgument(args, "--warmups", 3);
int workerCount = ReadIntArgument(
    args,
    "--workers",
    Math.Max(1, Environment.ProcessorCount - 1));
int parallelLength = ReadIntArgument(args, "--length", 1_000_000);
int spinCount = ReadNonNegativeArgument(args, "--spin", 128);
int busySpinCount = ReadNonNegativeArgument(args, "--busy-spin", Math.Max(spinCount, 2_048));
bool automatic = args.Contains("--auto", StringComparer.OrdinalIgnoreCase);
bool counters = args.Contains("--counters", StringComparer.OrdinalIgnoreCase);

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
        $"SomeEngine.Job benchmark | workers={workerCount} | samples={samples} | " +
        $"length={parallelLength} | batch={(automatic ? "auto" : "explicit")} | " +
        $"spin={spinCount}/{busySpinCount} | counters={counters}");

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

readonly struct EmptyJob : IJob
{
    public void Execute()
    {
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
