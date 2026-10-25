using System.Diagnostics;
using SomeEngine.Job;

internal static class CategoryBenchmarks
{
    internal static void Run(Scheduler scheduler, int workers, int warmups, int samples, int calls)
    {
        const int groups = 64;
        const int foregroundCount = groups * 4;
        int foregroundLimit = Math.Min(4, workers);
        int backgroundLimit = Math.Min(2, workers);
        JobCategory foreground = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "frame", MaxInFlight = 32, MaxExecuting = foregroundLimit, ServiceShare = 4,
        });
        JobCategory background = scheduler.CreateCategory(new JobCategoryConfig
        {
            Name = "decode", MaxInFlight = 8, MaxExecuting = backgroundLimit, ServiceShare = 1,
        });
        var frontOptions = new JobScheduleOptions { Category = foreground, Priority = JobPriority.High };
        var backOptions = new JobScheduleOptions { Category = background, Priority = JobPriority.Low };
        double[] frontValues = new double[foregroundCount];
        double[] backValues = new double[groups];
        var frontEvents = new WorkEvents();
        var backEvents = new WorkEvents();
        var submitted = new ValueTask<JobHandle>[foregroundCount + groups];
        var handles = new JobHandle[submitted.Length];

        void RunBatch()
        {
            frontEvents.Reset();
            backEvents.Reset();
            int next = 0;
            for (int group = 0; group < groups; group++)
            {
                for (int item = 0; item < 4; item++)
                {
                    int index = group * 4 + item;
                    submitted[next++] = scheduler.ScheduleAsync(
                        new CalculateValue(frontValues, index, 256, frontEvents),
                        JobResourceAccess.Write(scheduler, frontValues, index, 1), frontOptions);
                }
                submitted[next++] = scheduler.ScheduleAsync(
                    new CalculateValue(backValues, group, 4096, backEvents),
                    JobResourceAccess.Write(scheduler, backValues, group, 1), backOptions);
            }
            for (int index = 0; index < submitted.Length; index++)
                handles[index] = submitted[index].AsTask().GetAwaiter().GetResult();
            scheduler.CombineDependencies(handles).Complete();
            if (frontEvents.Completed != foregroundCount || backEvents.Completed != groups ||
                frontEvents.Maximum > foregroundLimit || backEvents.Maximum > backgroundLimit)
                throw new InvalidOperationException("The mixed category workload did not finish within its execution limits.");
        }

        for (int warmup = 0; warmup < warmups; warmup++)
            for (int call = 0; call < calls; call++)
                RunBatch();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var microseconds = new double[samples];
        var allocatedBytes = new double[samples];
        for (int sample = 0; sample < samples; sample++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            long started = Stopwatch.GetTimestamp();
            for (int call = 0; call < calls; call++)
                RunBatch();
            microseconds[sample] = Stopwatch.GetElapsedTime(started).TotalMicroseconds / calls;
            allocatedBytes[sample] = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)calls;
        }
        Array.Sort(microseconds);
        Array.Sort(allocatedBytes);
        int median = (int)Math.Ceiling(samples * 0.5) - 1;
        int tail = (int)Math.Ceiling(samples * 0.95) - 1;
        Console.WriteLine($"mixed-categories calls={calls} p50={microseconds[median]:F3} us/batch " +
            $"p95={microseconds[tail]:F3} us/batch allocated-p50={allocatedBytes[median]:F2} B/batch");
        Console.WriteLine($"frame count={frontEvents.Completed} max-executing={frontEvents.Maximum}/{foregroundLimit} " +
            "max-in-flight=32 share=4 priority=High iterations=256");
        Console.WriteLine($"decode count={backEvents.Completed} max-executing={backEvents.Maximum}/{backgroundLimit} " +
            "max-in-flight=8 share=1 priority=Low iterations=4096");
    }

    private sealed class WorkEvents
    {
        internal int Active;
        internal int Maximum;
        internal int Completed;
        internal void Reset() => (Active, Maximum, Completed) = (0, 0, 0);
        internal void Start()
        {
            int active = Interlocked.Increment(ref Active);
            int maximum = Volatile.Read(ref Maximum);
            while (active > maximum)
            {
                int observed = Interlocked.CompareExchange(ref Maximum, active, maximum);
                if (observed == maximum)
                    break;
                maximum = observed;
            }
        }
        internal void Finish()
        {
            Interlocked.Decrement(ref Active);
            Interlocked.Increment(ref Completed);
        }
    }

    private readonly record struct CalculateValue(double[] Values, int Index, int Iterations, WorkEvents Events) : IJob
    {
        public void Execute()
        {
            Events.Start();
            double value = Index + 1;
            for (int iteration = 0; iteration < Iterations; iteration++)
                value = Math.Sqrt(value + 3);
            Values[Index] = value;
            Events.Finish();
        }
    }
}
