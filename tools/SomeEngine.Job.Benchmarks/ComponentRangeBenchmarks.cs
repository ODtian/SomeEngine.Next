using System.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Storage;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

internal static class ComponentRangeBenchmarks
{
    internal static void Run(Scheduler scheduler, int count, int warmups, int samples, int chunkCapacity)
    {
        var jobs = new WorldJobs(scheduler);
        using var world = new World(chunkCapacity: chunkCapacity, execution: jobs);
        var values = new int[count];
        var state = new BufferElement(values, 0);
        world.ExecuteBundleSpawnBatch<BufferElement, CreateElement>(
            [ComponentType.Of<BufferElement>().Id], count, state, new CreateElement());
        Query query = world.Query<BufferElement>();
        var captured = new BufferElement[count];
        var timing = new WriteTiming();
        var milliseconds = new double[samples];
        for (int sample = -warmups; sample < samples; sample++)
        {
            int offset = 0, chunks = 0;
            using (QueryScope scope = query.OpenRead())
                foreach (QueryChunk chunk in scope)
                {
                    chunk.Read<BufferElement>().CopyTo(captured.AsSpan(offset));
                    offset += chunk.Count;
                    chunks++;
                }
            long capturedAt = Stopwatch.GetTimestamp();
            JobResourceAccess[] accesses = jobs.CollectReadAccess<BufferElement, BufferWrites>(query);
            scheduler.Schedule(new WriteElements(captured, timing),
                [.. accesses, JobResourceAccess.Read(scheduler.GetResourceToken(captured))]).Complete();
            double elapsed = Stopwatch.GetElapsedTime(capturedAt, timing.StartedAt).TotalMilliseconds;
            if (offset != count) throw new InvalidOperationException("Capture must retain every selected member.");
            if (sample < 0) continue;
            milliseconds[sample] = elapsed;
            Console.WriteLine(FormattableString.Invariant(
                $"component_ranges members={count} chunk_capacity={chunkCapacity} chunks={chunks} sample={sample} capture_to_write_ms={elapsed:F6}"));
            Console.Out.Flush();
        }
        foreach (int value in values)
            if (value != warmups + samples)
                throw new InvalidOperationException("Each captured member must write its selected buffer element.");
        Array.Sort(milliseconds);
        Console.WriteLine(FormattableString.Invariant(
            $"component-ranges members={count} p50={milliseconds[(samples - 1) / 2]:F6} ms p95={milliseconds[(int)Math.Ceiling(samples * .95) - 1]:F6} ms"));
    }

    private readonly record struct BufferElement(int[] Values, int Index) : IComponent;

    private readonly struct CreateElement : IBundleWriter<BufferElement>
    {
        public void Write(BundleWriteView view, in BufferElement state) =>
            view.Write(new BufferElement(state.Values, view.Index));
    }

    private readonly struct BufferWrites : IJobComponentResources<BufferElement>
    {
        public static void Collect(in BufferElement component, ref JobResourceAccessBuilder resources) =>
            resources.Write(component.Values.AsMemory(component.Index, 1));

        public static void CollectRange<TResources>(ReadOnlySpan<BufferElement> components,
            ref JobResourceAccessBuilder resources) where TResources : struct, IJobComponentResources<BufferElement>
        {
            ref readonly BufferElement first = ref components[0];
            resources.Write(first.Values.AsMemory(first.Index, components.Length));
        }
    }

    private sealed class WriteTiming
    {
        internal long StartedAt;
    }

    private readonly struct WriteElements(BufferElement[] elements, WriteTiming timing) : IJob
    {
        public void Execute()
        {
            timing.StartedAt = Stopwatch.GetTimestamp();
            foreach (ref readonly BufferElement element in elements.AsSpan()) element.Values[element.Index]++;
        }
    }
}
