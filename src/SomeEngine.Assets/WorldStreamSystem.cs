using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Assets;

/// <summary>Collects absolute streaming sources and advances its WorldStream in the Game system group.</summary>
public sealed class WorldStreamSystem : ISystem<ImmediateSystemContext>
{
    private readonly World _world;
    private Query _sources;
    private Query _origins;
    private WorldStreamSource[] _values = [];

    public WorldStreamSystem(World world, AssetLoader loader, AssetId<WorldAsset> source,
        WorldStreamOptions options, SerializationReadOptions readOptions = default)
    {
        _world = world;
        Stream = new(world, loader, source, options, readOptions);
    }

    public WorldStream Stream { get; }

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _sources = _world.Query(new QueryDescription().With<StreamingSource>().With<WorldTransform>());
        _origins = _world.Query<WorldOrigin>();
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        int count = _sources.Count;
        if (_values.Length < count) Array.Resize(ref _values, Math.Max(count, Math.Max(4, _values.Length * 2)));
        var work = new CollectSources(_sources, _origins, _values);
        if (_world.Execution is { Scheduler: { } scheduler } execution)
        {
            JobResourceAccess[] sources = execution.CollectReadAccess(_sources);
            JobResourceAccess[] origins = execution.CollectReadAccess(_origins);
            var accesses = new List<JobResourceAccess>(sources.Length + origins.Length + 1);
            accesses.AddRange(sources);
            accesses.AddRange(origins);
            accesses.Add(JobResourceAccess.Write(scheduler, _values));
            JobHandle collected = scheduler.Schedule(work, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(accesses));
            Stream.Update(_values.AsMemory(0, count), collected);
        }
        else
        {
            work.Execute();
            Stream.Update(_values.AsMemory(0, count));
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) => Stream.Dispose();

    private readonly struct CollectSources(Query sources, Query origins, WorldStreamSource[] values) : IJob
    {
        public void Execute()
        {
            WorldOrigin origin = default;
            int originCount = 0;
            using (QueryScope scope = origins.OpenRead())
                foreach (QueryChunk chunk in scope)
                    foreach (WorldOrigin value in chunk.Read<WorldOrigin>())
                    {
                        originCount++;
                        DevelopmentCheck.Require(originCount == 1, "A Game world has one coordinate origin.");
                        origin = value;
                    }
            int index = 0;
            using QueryScope sourceScope = sources.OpenRead();
            foreach (QueryChunk chunk in sourceScope)
            {
                ReadOnlySpan<StreamingSource> inputs = chunk.Read<StreamingSource>();
                ReadOnlySpan<WorldTransform> transforms = chunk.Read<WorldTransform>();
                for (int row = 0; row < chunk.Count; row++)
                {
                    Vector3 position = transforms[row].Qvvs.Position;
                    values[index++] = new(origin.X + position.X, origin.Y + position.Y, origin.Z + position.Z,
                        inputs[row].Radius, inputs[row].Priority);
                }
            }
        }
    }
}
