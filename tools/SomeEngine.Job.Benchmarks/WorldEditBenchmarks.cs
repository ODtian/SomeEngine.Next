using System.Diagnostics;
using System.Globalization;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

internal static class WorldEditBenchmarks
{
    internal static void Run(Scheduler scheduler, int count, int warmups, int samples)
    {
        Measure(scheduler, count, warmups, samples, structureRange: false);
        Measure(scheduler, count, warmups, samples, structureRange: true);
    }

    private static void Measure(Scheduler scheduler, int count, int warmups, int samples, bool structureRange)
    {
        using var world = new World(execution: new WorldJobs(scheduler));
        var entities = new Entity[count];
        using (world.OpenStructureWrite())
            for (int i = 0; i < entities.Length; i++) entities[i] = world.Create();

        string scope = structureRange ? "one-range" : "per-entity";
        var milliseconds = new double[samples];
        for (int sample = -warmups; sample < samples; sample++)
        {
            if (sample != -warmups) world.Remove<AddedValue>(entities);
            long started = Stopwatch.GetTimestamp();
            if (structureRange)
            {
                using var edit = world.OpenStructureWrite();
                AddValues(world, entities);
            }
            else AddValues(world, entities);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (sample < 0) continue;
            milliseconds[sample] = elapsed;
            Console.WriteLine(FormattableString.Invariant(
                $"world_add scope={scope} entities={count} sample={sample} batch_ms={elapsed:F6}"));
            Console.Out.Flush();
        }

        if (world.Query<AddedValue>().Count != count)
            throw new InvalidOperationException("World additions must retain every entity and value.");
        for (int i = 0; i < entities.Length; i++)
            if (world.ReadComponentData<AddedValue>(entities[i]).Value != i)
                throw new InvalidOperationException("World additions must retain each entity's assigned value.");
        Array.Sort(milliseconds);
        double median = milliseconds[(samples - 1) / 2];
        double p95 = milliseconds[(int)Math.Ceiling(samples * 0.95) - 1];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"world-add-{scope} entities={count} p50={median:F6} ms p95={p95:F6} ms"));
    }

    private static void AddValues(World world, Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++) world.Add(entities[i], new AddedValue { Value = i });
    }

    private struct AddedValue : IComponent
    {
        public int Value;
    }
}
