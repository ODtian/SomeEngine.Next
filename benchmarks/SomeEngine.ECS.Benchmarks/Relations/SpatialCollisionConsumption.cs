using SomeEngine.ECS;
using SomeEngine.ECS.Spatial;
using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public sealed class SpatialContactBuffer : IDisposable
{
    private readonly (Entity, Entity)[] _pairs;
    private readonly int[] _pairCount = new int[1];
    private readonly JobResourceAccess[] _writes;
    private readonly Scheduler _scheduler;
    private readonly JobResource _resource;

    public SpatialContactBuffer(Scheduler scheduler, int pairCapacity, int entityCapacity)
    {
        _scheduler = scheduler;
        _pairs = new (Entity, Entity)[pairCapacity];
        ContactCounts = new int[entityCapacity];
        PenetrationDepths = new float[entityCapacity];
        _resource = scheduler.CreateResource("Spatial contact output and consumption");
        _writes = [JobResourceAccess.Write(_resource)];
    }

    public int PairCount => _pairCount[0];
    public ReadOnlySpan<(Entity, Entity)> Pairs => _pairs.AsSpan(0, PairCount);
    public int[] ContactCounts { get; }
    public float[] PenetrationDepths { get; }
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    internal Span<(Entity, Entity)> PairStorage => _pairs;
    internal ReadOnlySpan<JobResourceAccess> Writes => _writes;

    internal void Consume(World world, Query bodies, int pairCount)
    {
        _pairCount[0] = pairCount;
        Array.Clear(ContactCounts);
        Array.Clear(PenetrationDepths);
        SpatialCollisionConsumption.Measure(world, bodies, Pairs, ContactCounts, PenetrationDepths);
    }

    public void Dispose() => _scheduler.ReleaseResource(_resource);
}

public ref struct SpatialPairOutput(Span<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
{
    private readonly Span<(Entity, Entity)> _pairs = pairs;
    public int Count { get; private set; }

    public bool Add(in (Entity, Entity) pair)
    {
        _pairs[Count++] = pair;
        return true;
    }
}

/// <summary>A business example of consuming broadphase output with current component geometry.</summary>
public static class SpatialCollisionConsumption
{
    public static void Measure(World world, Query bodies, ReadOnlySpan<(Entity, Entity)> pairs,
        Span<int> contactCounts, Span<float> penetrationDepths)
    {
        using QueryScope scope = bodies.WithOptionalRead<Body>().OpenRead();
        using ReadLookup<Body> values = world.OpenRead<Body>();
        foreach (var pair in pairs)
        {
            ref readonly Body first = ref values.ReadKnown(pair.Item1);
            ref readonly Body second = ref values.ReadKnown(pair.Item2);
            Vector3 overlap = Vector3.Min(first.Bounds.Max, second.Bounds.Max)
                - Vector3.Max(first.Bounds.Min, second.Bounds.Min);
            if (overlap.X < 0 || overlap.Y < 0 || overlap.Z < 0) continue;
            float depth = MathF.Min(overlap.X, MathF.Min(overlap.Y, overlap.Z));
            contactCounts[pair.Item1.Id]++;
            contactCounts[pair.Item2.Id]++;
            penetrationDepths[pair.Item1.Id] += depth;
            penetrationDepths[pair.Item2.Id] += depth;
        }
    }
}
