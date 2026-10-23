using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

public sealed class SpatialContactBuffer : IDisposable
{
    private readonly (Entity, Entity)[] _pairs;
    private readonly int[] _pairCount = new int[1];
    private readonly JobResourceAccess[] _writes;
    private readonly Scheduler _scheduler;
    private readonly JobResourceToken _resource;

    public SpatialContactBuffer(Scheduler scheduler, int pairCapacity, int entityCapacity)
    {
        _scheduler = scheduler;
        _pairs = new (Entity, Entity)[pairCapacity];
        ContactCounts = new int[entityCapacity];
        PenetrationDepths = new float[entityCapacity];
        _resource = scheduler.CreateResourceToken("Spatial contact output and consumption");
        _writes = [JobResourceAccess.Write(_resource)];
    }

    public int PairCount => _pairCount[0];
    public ReadOnlySpan<(Entity, Entity)> Pairs => _pairs.AsSpan(0, PairCount);
    public int[] ContactCounts { get; }
    public float[] PenetrationDepths { get; }
    public JobResourceAccess ReadAccess => JobResourceAccess.Read(_resource);
    internal Span<(Entity, Entity)> PairStorage => _pairs;
    internal ReadOnlySpan<JobResourceAccess> Writes => _writes;

    internal void Consume(World world, QueryHandle bodies, int pairCount)
    {
        _pairCount[0] = pairCount;
        Array.Clear(ContactCounts);
        Array.Clear(PenetrationDepths);
        SpatialCollisionConsumption.Measure(world, bodies, Pairs, ContactCounts, PenetrationDepths);
    }

    public void Dispose() => _scheduler.ReleaseResourceToken(_resource);
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
    public static void Measure(World world, QueryHandle bodies, ReadOnlySpan<(Entity, Entity)> pairs,
        Span<int> contactCounts, Span<float> penetrationDepths)
    {
        QueryDefinition definition = QueryDefinition.Empty.WithAccessFrom(world.GetQueryDefinition(bodies))
            .WithOptionalRead<Body>();
        QueryHandle access = world.Query(definition);
        var execution = new ContactExecution(pairs, contactCounts, penetrationDepths);
        try
        {
            world.ExecuteQuery(access, ref execution);
        }
        finally
        {
            world.ReleaseQuery(access);
        }
    }

    private readonly ref struct ContactExecution(ReadOnlySpan<(Entity, Entity)> pairs, Span<int> contactCounts,
        Span<float> penetrationDepths) : IQueryExecution
    {
        private readonly ReadOnlySpan<(Entity, Entity)> _pairs = pairs;
        private readonly Span<int> _counts = contactCounts;
        private readonly Span<float> _depths = penetrationDepths;

        public void Execute(QueryCursor access)
        {
            foreach (var pair in _pairs)
            {
                access.TryGetRow(pair.Item1, out var first);
                access.TryGetRow(pair.Item2, out var second);
                ref readonly var firstBody = ref first.ReadRef<Body>();
                ref readonly var secondBody = ref second.ReadRef<Body>();
                Vector3 overlap = Vector3.Min(firstBody.Bounds.Max, secondBody.Bounds.Max) -
                    Vector3.Max(firstBody.Bounds.Min, secondBody.Bounds.Min);
                if (overlap.X < 0 || overlap.Y < 0 || overlap.Z < 0)
                    continue;
                float depth = MathF.Min(overlap.X, MathF.Min(overlap.Y, overlap.Z));
                _counts[pair.Item1.Index]++;
                _counts[pair.Item2.Index]++;
                _depths[pair.Item1.Index] += depth;
                _depths[pair.Item2.Index] += depth;
            }
        }
    }
}
