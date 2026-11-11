using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>Reusable ordinary spatial index and completed actual-motion samples.</summary>
public sealed class NavigationNeighbors(float cellSize = 2)
{
    private NavigationNeighborAgent[] _agents = [];
    private int[][] _neighbors = [];
    private int[] _neighborCounts = [];
    private NavigationTrajectoryPoint[][] _predictions = [];
    private int[] _predictionCounts = [];
    private readonly SpatialGrid<UniformGridSettings> _grid = SpatialQueries.CreateUniformGrid(cellSize);
    private readonly Dictionary<Entity, int> _indices = [];
    private int _count;

    public ReadOnlyMemory<NavigationNeighborAgent> Agents => _agents.AsMemory(0, _count);
    public ReadOnlyMemory<int> GetNeighbors(Entity entity) => GetNeighbors(_indices[entity]);
    internal ReadOnlyMemory<int> GetNeighbors(int index) => _neighbors[index].AsMemory(0, _neighborCounts[index]);
    internal int Find(Entity entity) => _indices[entity];
    internal void BeginCollection() { _indices.Clear(); _count = 0; }

    internal void Add(in NavigationNeighborAgent agent)
    {
        if (_count == _agents.Length)
        {
            int capacity = Math.Max(_count + 1, Math.Max(16, _agents.Length * 2));
            Array.Resize(ref _agents, capacity);
            Array.Resize(ref _neighbors, capacity);
            Array.Resize(ref _neighborCounts, capacity);
            Array.Resize(ref _predictions, capacity);
            Array.Resize(ref _predictionCounts, capacity);
        }
        _indices.Add(agent.Entity, _count);
        _agents[_count] = agent;
        _predictionCounts[_count] = 0;
        _count++;
    }

    internal void BuildIndex(World world, Query actors)
    {
        _grid.BeginUpdate();
        for (int index = 0; index < _count; index++)
        {
            NavigationNeighborAgent agent = _agents[index];
            _neighborCounts[index] = 0;
            if (_neighbors[index] is null || _neighbors[index].Length < agent.Avoidance.MaximumNeighbors)
                _neighbors[index] = new int[agent.Avoidance.MaximumNeighbors];
            Vector3 extent = new(agent.Avoidance.NeighborDistance, 0, agent.Avoidance.NeighborDistance);
            var bounds = new SpatialBounds(agent.Motion.Position - extent,
                agent.Motion.Position + extent + Vector3.UnitY * agent.Profile.Height);
            _grid.Update(agent.Entity, in bounds);
        }
        _grid.EndUpdate();
        var output = new NeighborPairs(this);
        _grid.ReadCandidates(world.Within(actors)).Enumerate(ref output);
    }

    private readonly struct NeighborPairs(NavigationNeighbors data) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            int first = data._indices[pair.Item1], second = data._indices[pair.Item2];
            data.AddNeighbor(first, second);
            data.AddNeighbor(second, first);
            return true;
        }
    }

    private void AddNeighbor(int index, int other)
    {
        NavigationNeighborAgent self = _agents[index], sample = _agents[other];
        if (self.Motion.Layer != sample.Motion.Layer
            || (self.Avoidance.InteractionMask & sample.Avoidance.InteractionMask) == 0
            || self.Motion.Position.Y + self.Profile.Height <= sample.Motion.Position.Y
            || sample.Motion.Position.Y + sample.Profile.Height <= self.Motion.Position.Y) return;
        Vector3 offset = sample.Motion.Position - self.Motion.Position;
        float distance = offset.X * offset.X + offset.Z * offset.Z;
        int limit = self.Avoidance.MaximumNeighbors;
        if (distance > self.Avoidance.NeighborDistance * self.Avoidance.NeighborDistance || limit == 0) return;
        int count = _neighborCounts[index], insertion = count;
        int[] neighbors = _neighbors[index];
        while (insertion > 0 && DistanceSquared(neighbors[insertion - 1]) > distance) insertion--;
        if (insertion == limit) return;
        count = Math.Min(count + 1, limit);
        for (int move = count - 1; move > insertion; move--) neighbors[move] = neighbors[move - 1];
        neighbors[insertion] = other;
        _neighborCounts[index] = count;

        float DistanceSquared(int neighbor)
        {
            Vector3 separation = _agents[neighbor].Motion.Position - self.Motion.Position;
            return separation.X * separation.X + separation.Z * separation.Z;
        }
    }

    internal void SetPrediction(Entity entity, ReadOnlySpan<NavigationTrajectoryPoint> points)
    {
        if (!_indices.TryGetValue(entity, out int index)) return;
        if (_predictions[index] is null || _predictions[index].Length < points.Length)
            _predictions[index] = new NavigationTrajectoryPoint[points.Length];
        points.CopyTo(_predictions[index]);
        _predictionCounts[index] = points.Length;
    }

    /// <summary>Uses the acquired forecast in its time coordinates, aligned to current actual feedback.</summary>
    public NavigationMotion Predict(int index, float time, float actualTime)
    {
        NavigationMotion actual = _agents[index].Motion;
        if (_predictionCounts[index] < 2)
            return actual with { Position = actual.Position + actual.Velocity * (time - actualTime) };
        ReadOnlySpan<NavigationTrajectoryPoint> points = _predictions[index].AsSpan(0, _predictionCounts[index]);
        NavigationTrajectoryPoint baseline = Interpolate(points, actualTime), future = Interpolate(points, time);
        return actual with
        {
            Position = actual.Position + future.Position - baseline.Position,
            Velocity = future.Velocity,
            Heading = actual.Heading + NavigationHeading.Difference(future.Heading, baseline.Heading),
        };
    }

    private static NavigationTrajectoryPoint Interpolate(ReadOnlySpan<NavigationTrajectoryPoint> points, float time)
    {
        for (int index = 1; index < points.Length; index++)
        {
            if (points[index].Time < time) continue;
            NavigationTrajectoryPoint first = points[index - 1], second = points[index];
            float fraction = Math.Clamp((time - first.Time) / (second.Time - first.Time), 0, 1);
            return new(time, Vector3.Lerp(first.Position, second.Position, fraction), Vector3.Lerp(first.Velocity, second.Velocity, fraction),
                first.Heading + NavigationHeading.Difference(second.Heading, first.Heading) * fraction);
        }
        NavigationTrajectoryPoint last = points[^1];
        return last with { Time = time, Position = last.Position + last.Velocity * (time - last.Time) };
    }

}

public sealed class NavigationNeighborSystem<TMotion, TReader> : IDisposable
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world;
    private readonly Query _actors;
    private readonly SystemRunner _runner;
    private readonly JobResourceAccess[] _buildAccesses;
    private readonly JobResourceAccess _dataWrite;
    private JobHandle _completion;
    private bool _disposed;
    public NavigationNeighborSystem(World world, Scheduler scheduler, float cellSize = 2)
    {
        _world = world;
        Scheduler = scheduler;
        Data = new(cellSize);
        _runner = new(world, scheduler);
        _actors = world.Query(new QueryDescription().With<NavigationAgent>().With<AvoidanceAgent>().With<TMotion>()
            .Optional<Buffer<NavigationTrajectoryPoint>>());
        _dataWrite = JobResourceAccess.Write(scheduler.GetResourceToken(Data));
        JobResourceAccess[] values = ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses()
            .ReadRandom<NavigationAgent>().ReadRandom<AvoidanceAgent>().ReadRandom<TMotion>()
            .ReadRandom<Buffer<NavigationTrajectoryPoint>>());
        _buildAccesses = new JobResourceAccess[values.Length + 1];
        values.CopyTo(_buildAccesses, 0);
        _buildAccesses[^1] = _dataWrite;
    }
    public Scheduler Scheduler { get; }
    public NavigationNeighbors Data { get; }
    public JobHandle Schedule(JobHandle motionReady = default, JobHandle predictionReady = default)
    {
        JobHandle reset = Scheduler.Schedule(new ResetNeighbors(Data), _dataWrite,
            Scheduler.CombineDependencies([_completion, motionReady, predictionReady]));
        JobHandle collected = _runner.Schedule(_actors, new CollectNeighbors(Data), reset, parallel: false);
        _completion = Scheduler.Schedule(new BuildNeighbors(_world, _actors, Data), _buildAccesses, collected);
        return _completion;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _completion.Complete();
    }
    private readonly struct ResetNeighbors(NavigationNeighbors data) : IJob
    { public void Execute() => data.BeginCollection(); }

    private readonly struct CollectNeighbors(NavigationNeighbors data) : IChunkAction
    {
        private static readonly ChunkAccess[] Inputs = [ChunkAccess.Read<NavigationAgent>(hasResources: true),
            ChunkAccess.Read<AvoidanceAgent>(), ChunkAccess.Read<TMotion>(),
            ChunkAccess.ReadOptionalBuffer<NavigationTrajectoryPoint>()];
        public static ReadOnlySpan<ChunkAccess> Components => Inputs;
        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources) => resources.Write(data);
        public static void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources) =>
            ComponentResources.CollectRange<NavigationAgent, NavigationAgentResources>(rows.Read<NavigationAgent>(), ref resources);
        public void Execute(SystemChunk rows)
        {
            ReadOnlySpan<TMotion> motions = rows.Read<TMotion>();
            ReadOnlySpan<AvoidanceAgent> avoidances = rows.Read<AvoidanceAgent>();
            ReadOnlySpan<NavigationAgent> agents = rows.Read<NavigationAgent>();
            for (int row = 0; row < rows.Count; row++)
            {
                Entity entity = rows.Entities[row];
                data.Add(new(entity, TReader.Read(in motions[row]), avoidances[row], agents[row].Profile));
                if (rows.Has<Buffer<NavigationTrajectoryPoint>>(row))
                {
                    using ReadOnlyBuffer<NavigationTrajectoryPoint> prediction = rows.ReadBuffer<NavigationTrajectoryPoint>(row);
                    data.SetPrediction(entity, prediction.AsSpan());
                }
            }
        }
    }
    private readonly struct BuildNeighbors(World world, Query actors, NavigationNeighbors data) : IJob
    { public void Execute() => data.BuildIndex(world, actors); }
}
