using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>Reusable ordinary spatial index and completed actual-motion samples.</summary>
public sealed class NavigationNeighbors(float cellSize = 2)
{
    private NavigationNeighborAgent[] _agents = [];
    private int[] _next = [];
    private int[][] _neighbors = [];
    private int[] _neighborCounts = [];
    private NavigationTrajectoryPoint[][] _predictions = [];
    private int[] _predictionCounts = [];
    private readonly Dictionary<(int X, int Z, int Layer), int> _heads = [];
    private readonly Dictionary<Entity, int> _indices = [];
    private int _count;

    public ReadOnlyMemory<NavigationNeighborAgent> Agents => _agents.AsMemory(0, _count);
    public ReadOnlyMemory<int> GetNeighbors(Entity entity) => GetNeighbors(_indices[entity]);
    internal ReadOnlyMemory<int> GetNeighbors(int index) => _neighbors[index].AsMemory(0, _neighborCounts[index]);
    internal int Find(Entity entity) => _indices[entity];
    internal void Clear() { _count = 0; _heads.Clear(); _indices.Clear(); }

    internal void Add(NavigationNeighborAgent agent)
    {
        if (_count == _agents.Length)
        {
            int capacity = Math.Max(16, _count * 2);
            Array.Resize(ref _agents, capacity);
            Array.Resize(ref _next, capacity);
            Array.Resize(ref _neighbors, capacity);
            Array.Resize(ref _neighborCounts, capacity);
            Array.Resize(ref _predictions, capacity);
            Array.Resize(ref _predictionCounts, capacity);
        }
        _agents[_count++] = agent;
    }

    internal void BuildIndex()
    {
        for (int index = 0; index < _count; index++)
        {
            NavigationNeighborAgent agent = _agents[index];
            var cell = Cell(agent.Motion);
            _next[index] = _heads.GetValueOrDefault(cell, -1);
            _heads[cell] = index;
            _indices.Add(agent.Entity, index);
            _predictionCounts[index] = 0;
            if (_neighbors[index] is null || _neighbors[index].Length < agent.Avoidance.MaximumNeighbors)
                _neighbors[index] = new int[agent.Avoidance.MaximumNeighbors];
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

    internal void Query(int index)
    {
        NavigationNeighborAgent self = _agents[index];
        var center = Cell(self.Motion);
        int reach = (int)MathF.Ceiling(self.Avoidance.NeighborDistance / cellSize);
        float rangeSquared = self.Avoidance.NeighborDistance * self.Avoidance.NeighborDistance;
        int[] neighbors = _neighbors[index];
        int count = 0;
        for (int z = center.Z - reach; z <= center.Z + reach; z++)
        for (int x = center.X - reach; x <= center.X + reach; x++)
        for (int other = _heads.GetValueOrDefault((x, z, center.Layer), -1); other >= 0; other = _next[other])
        {
            NavigationNeighborAgent sample = _agents[other];
            if (index == other || (self.Avoidance.InteractionMask & sample.Avoidance.InteractionMask) == 0 ||
                self.Motion.Position.Y + self.Profile.Height <= sample.Motion.Position.Y ||
                sample.Motion.Position.Y + sample.Profile.Height <= self.Motion.Position.Y) continue;
            Vector3 offset = sample.Motion.Position - self.Motion.Position;
            float distance = offset.X * offset.X + offset.Z * offset.Z;
            if (distance > rangeSquared || neighbors.Length == 0) continue;
            int insertion = count;
            while (insertion > 0 && DistanceSquared(neighbors[insertion - 1]) > distance) insertion--;
            if (insertion == neighbors.Length) continue;
            count = Math.Min(count + 1, neighbors.Length);
            for (int move = count - 1; move > insertion; move--) neighbors[move] = neighbors[move - 1];
            neighbors[insertion] = other;
        }
        _neighborCounts[index] = count;

        float DistanceSquared(int other)
        {
            Vector3 offset = _agents[other].Motion.Position - self.Motion.Position;
            return offset.X * offset.X + offset.Z * offset.Z;
        }
    }

    private (int X, int Z, int Layer) Cell(NavigationMotion motion) =>
        ((int)MathF.Floor(motion.Position.X / cellSize), (int)MathF.Floor(motion.Position.Z / cellSize), motion.Layer);
}

public sealed class NavigationNeighborSystem<TMotion, TReader>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world;
    private readonly QueryDefinition _actors;
    private JobHandle _completion;
    public NavigationNeighborSystem(World world, Scheduler scheduler, float cellSize = 2)
    {
        _world = world;
        _actors = world.QueryDefinition().All<NavigationAgent>().All<AvoidanceAgent>().All<TMotion>().Build();
        Scheduler = scheduler;
        Data = new(cellSize);
    }

    public Scheduler Scheduler { get; }
    public NavigationNeighbors Data { get; }

    public JobHandle Schedule(JobHandle motionReady = default, JobHandle predictionReady = default)
    {
        JobResourceAccess access = JobResourceAccess.Write(Scheduler.GetResourceToken(Data));
        JobHandle cleared = Scheduler.Schedule(new ClearNeighbors(Data), [access],
            Scheduler.CombineDependencies([_completion, motionReady, predictionReady]));
        JobHandle collected = new CollectNavigationNeighbors<TMotion, TReader>(Data).Schedule(_world, [access], cleared);
        JobHandle built = Scheduler.Schedule(new BuildNeighbors(Data, Scheduler), [access], collected);
        _completion = new CollectNavigationNeighborPredictions(Data).Schedule(_world, [access],
            new JobEntityScheduleOptions(filter: _actors), built);
        return _completion;
    }

    private readonly struct ClearNeighbors(NavigationNeighbors data) : IJob
    {
        public void Execute() => data.Clear();
    }

    private readonly struct BuildNeighbors(NavigationNeighbors data, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            data.BuildIndex();
            scheduler.ScheduleParallel(new QueryNeighbors(data), data.Agents.Length, 16,
                [JobResourceAccess.Write(scheduler.GetResourceToken(data))]);
        }
    }

    private readonly struct QueryNeighbors(NavigationNeighbors data) : IJobParallelFor
    {
        public void Execute(int index) => data.Query(index);
    }
}

internal partial struct CollectNavigationNeighborPredictions(NavigationNeighbors data) : IJobEntity
{
    private readonly NavigationNeighbors _data = data;
    public void Execute(Entity entity, BufferView<NavigationTrajectoryPoint> prediction) =>
        _data.SetPrediction(entity, prediction.AsSpan());
}

internal partial struct CollectNavigationNeighbors<TMotion, TReader>(NavigationNeighbors data) : IJobEntity
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly NavigationNeighbors _data = data;
    public void Execute(Entity entity, in AvoidanceAgent avoidance,
        [JobResources<NavigationAgentResources>] in NavigationAgent agent, in TMotion motion) =>
        _data.Add(new(entity, TReader.Read(in motion), avoidance, agent.Profile));
}
