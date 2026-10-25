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
    private SpatialPartition[] _spatialPartitions = [];
    private readonly Dictionary<Entity, int> _indices = [];
    private int _count;

    public ReadOnlyMemory<NavigationNeighborAgent> Agents => _agents.AsMemory(0, _count);
    public ReadOnlyMemory<int> GetNeighbors(Entity entity) => GetNeighbors(_indices[entity]);
    internal ReadOnlyMemory<int> GetNeighbors(int index) => _neighbors[index].AsMemory(0, _neighborCounts[index]);
    internal int Find(Entity entity) => _indices[entity];
    private sealed class SpatialPartition
    {
        internal readonly List<int> Agents = [];
        internal readonly Dictionary<(int X, int Z, int Layer), int> Heads = [];
    }

    internal void BeginCollection(int count)
    {
        _indices.Clear();
        _count = count;
        if (count > _agents.Length)
        {
            int capacity = Math.Max(count, Math.Max(16, _agents.Length * 2));
            Array.Resize(ref _agents, capacity);
            Array.Resize(ref _next, capacity);
            Array.Resize(ref _neighbors, capacity);
            Array.Resize(ref _neighborCounts, capacity);
            Array.Resize(ref _predictions, capacity);
            Array.Resize(ref _predictionCounts, capacity);
        }
    }

    internal void SetEntity(int index, Entity entity) => _indices.Add(entity, index);
    internal void Set(NavigationNeighborAgent agent) => _agents[_indices[agent.Entity]] = agent;

    internal void BuildIndex(Scheduler scheduler)
    {
        int partitionCount = Math.Min(Math.Max(1, (_count + 63) / 64), Math.Max(1, (scheduler.WorkerCount + 1) * 4));
        if (_spatialPartitions.Length != partitionCount)
        {
            _spatialPartitions = new SpatialPartition[partitionCount];
            for (int index = 0; index < partitionCount; index++) _spatialPartitions[index] = new();
        }
        foreach (SpatialPartition partition in _spatialPartitions)
        {
            partition.Agents.Clear();
            partition.Heads.Clear();
        }
        for (int index = 0; index < _count; index++)
            _spatialPartitions[Partition(Cell(_agents[index].Motion))].Agents.Add(index);
        scheduler.ExecuteParallel(new BuildSpatialPartition(this), partitionCount,
            batchSize: JobScheduleOptions.AutomaticBatchSize);
    }

    private readonly struct BuildSpatialPartition(NavigationNeighbors data) : IJobParallelFor
    {
        public void Execute(int partitionIndex)
        {
            SpatialPartition partition = data._spatialPartitions[partitionIndex];
            foreach (int index in partition.Agents)
            {
                NavigationNeighborAgent agent = data._agents[index];
                var cell = data.Cell(agent.Motion);
                data._next[index] = partition.Heads.GetValueOrDefault(cell, -1);
                partition.Heads[cell] = index;
                data._predictionCounts[index] = 0;
                if (data._neighbors[index] is null || data._neighbors[index].Length < agent.Avoidance.MaximumNeighbors)
                    data._neighbors[index] = new int[agent.Avoidance.MaximumNeighbors];
            }
        }
    }

    private int Partition((int X, int Z, int Layer) cell) =>
        (int)((uint)HashCode.Combine(cell.X, cell.Z, cell.Layer) % (uint)_spatialPartitions.Length);

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
        for (int other = Head((x, z, center.Layer)); other >= 0; other = _next[other])
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

    private int Head((int X, int Z, int Layer) cell) =>
        _spatialPartitions[Partition(cell)].Heads.GetValueOrDefault(cell, -1);
}

public sealed class NavigationNeighborSystem<TMotion, TReader>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world;
    private readonly QueryDefinition _actors;
    private readonly QueryHandle _actorQuery;
    private readonly JobResourceAccess[] _collectionAccesses;
    private JobHandle _completion;
    public NavigationNeighborSystem(World world, Scheduler scheduler, float cellSize = 2)
    {
        _world = world;
        _actors = world.QueryDefinition().All<NavigationAgent>().All<AvoidanceAgent>().All<TMotion>().Build();
        Scheduler = scheduler;
        Data = new(cellSize);
        _actorQuery = world.Query(_actors);
        _collectionAccesses = new JobResourceAccess[QueryJobAccess.Count(_actors) + 1];
        QueryJobAccess.CopyAccesses(world, _actors, _collectionAccesses);
        _collectionAccesses[^1] = JobResourceAccess.Write(scheduler.GetResourceToken(Data));
    }

    public Scheduler Scheduler { get; }
    public NavigationNeighbors Data { get; }

    public JobHandle Schedule(JobHandle motionReady = default, JobHandle predictionReady = default)
    {
        _completion = Scheduler.Schedule(new CollectNeighbors(this), _collectionAccesses,
            Scheduler.CombineDependencies([_completion, motionReady, predictionReady]));
        return _completion;
    }

    private readonly struct CollectNeighbors(NavigationNeighborSystem<TMotion, TReader> system) : IJob
    {
        public void Execute()
        {
            NavigationNeighbors data = system.Data;
            system._world.ExecuteQuery(system._actorQuery, ref data,
                static (scoped QueryCursor query, ref NavigationNeighbors output) =>
                {
                    using ReadOnlyQueryPacketPlan packets = ReadOnlyQueryPacketJobs.CreatePlan(query);
                    output.BeginCollection(packets.RowCount);
                    packets.ExecuteInline(new CollectEntities(output));
                });
            JobResourceAccess access = JobResourceAccess.Write(system.Scheduler.GetResourceToken(data));
            JobHandle collected = new CollectNavigationNeighbors<TMotion, TReader>(data).ScheduleParallel(system._world, [access]);
            JobHandle built = system.Scheduler.Schedule(new BuildNeighbors(data, system.Scheduler), [access], collected);
            new CollectNavigationNeighborPredictions(data).Schedule(system._world, [access],
                new JobEntityScheduleOptions(filter: system._actors), built);
        }
    }

    private readonly struct CollectEntities(NavigationNeighbors data) : IReadOnlyQueryPacketJob
    {
        public void Execute(in ReadOnlyQueryPacketContext context, ReadOnlyQueryPacket packet)
        {
            for (int row = 0; row < packet.Count; row++) data.SetEntity(context.OutputStart + row, packet.Entities[row]);
        }
    }

    private readonly struct BuildNeighbors(NavigationNeighbors data, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            data.BuildIndex(scheduler);
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
        _data.Set(new(entity, TReader.Read(in motion), avoidance, agent.Profile));
}
