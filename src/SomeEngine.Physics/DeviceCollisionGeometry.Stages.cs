using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Starts one device candidate input generation before its concrete geometry writers.</summary>
public readonly struct BeginDeviceCollisionInputs(DeviceCollisionGrid grid) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        WorldJobs.TryGetScheduler(context.World, out Scheduler? scheduler);
        return scheduler!.Schedule(new BeginDeviceInputWrite(grid),
            JobResourceAccess.Write(scheduler.GetResourceToken(grid.Upload), 0, (long)grid.Upload.Info.Size), dependency);
    }
}

public readonly struct WriteDeviceGeometryInputs<TGeometry, TReader> : IPhysicsStage
    where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
    where TReader : struct, IDeviceGeometryReader<TGeometry>
{
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceCollisionParticipants _participants;
    private readonly TReader _reader;
    private readonly uint _geometryType;
    private readonly int _rowsPerPacket;

    internal WriteDeviceGeometryInputs(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
        TReader reader, uint geometryType, int rowsPerPacket) =>
        (_grid, _participants, _reader, _geometryType, _rowsPerPacket) =
        (grid, participants, reader, geometryType, rowsPerPacket);

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        dependency = _reader.Prepare(in context, _rowsPerPacket, dependency);
        return new WriteDeviceGeometryInputsJob<TGeometry, TReader>(_grid, _participants, _reader, _geometryType)
            .ScheduleParallel(context.World, _participants.GetReadAccesses(),
                new JobEntityScheduleOptions(rowsPerPacket: _rowsPerPacket, filter: context.Bodies), dependency);
    }
}

internal readonly partial struct WriteDeviceGeometryInputsJob<TGeometry, TReader>(DeviceCollisionGrid grid,
    DeviceCollisionParticipants participants, TReader reader, uint geometryType) : IJobEntity
    where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
    where TReader : struct, IDeviceGeometryReader<TGeometry>
{
    public void Execute(Entity entity, [JobResources<DeviceCollisionUploadResources>] in DeviceCollisionBody body,
        [JobResources<DeviceGeometryUploadResources>] in DeviceGeometryBinding binding,
        [JobResources(nameof(TReader))] in TGeometry geometry, in BodyPose pose)
    {
        if (!ReferenceEquals(body.Grid, grid)) return;
        DeviceGeometryInput value = reader.Read(in geometry, in pose, binding.Geometry);
        value.GeometryIndex = (uint)binding.Index;
        value.GeometryType = geometryType;
        binding.Geometry.WriteInput(binding.Index, in value);
        CollisionBounds bounds = reader.GetBounds(in geometry, in pose);
        var input = new DeviceCollisionInput
        {
            Min = bounds.Min, Max = bounds.Max, EntityIndex = (uint)entity.Index,
            EntityGeneration = (uint)entity.Generation, GeometryType = geometryType,
            GeometryIndex = (uint)binding.Index, Members = participants.ReadMembers(entity),
        };
        grid.WriteInput(body.Index, in input);
    }
}

/// <summary>Builds candidates from already converted component inputs using the selected transfer.</summary>
public readonly struct BuildDeviceCollisionGrid : IPhysicsStage
{
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceCollisionParticipants _participants;
    private readonly DevicePairRules _rules;
    private readonly DeviceInputTransfer _transfer;
    private readonly DevicePairReadback _readback;

    internal BuildDeviceCollisionGrid(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
        DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback) =>
        (_grid, _participants, _rules, _transfer, _readback) = (grid, participants, rules, transfer, readback);

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        WorldJobs.TryGetScheduler(context.World, out Scheduler? scheduler);
        return _grid.Schedule(scheduler!, in context, _participants.Pairing, _rules, _transfer, _readback, dependency);
    }
}

public sealed partial class DeviceCollisionGrid
{
    public BeginDeviceCollisionInputs BeginInputs() => new(this);

    public WriteDeviceGeometryInputs<TGeometry, TReader> WriteGeometryInputs<TGeometry, TReader>(
        RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs, TReader reader, uint geometryType, int rowsPerPacket)
        where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
        where TReader : struct, IDeviceGeometryReader<TGeometry> => new(this,
        new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(0), DevicePairing.Within),
        reader, geometryType, rowsPerPacket);

    public BuildDeviceCollisionGrid Build(RelationQuery<EntityPairsWithin, (Entity, Entity)> pairs,
        DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback) => new(this,
        new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(0), DevicePairing.Within),
        rules, transfer, readback);

    public WriteDeviceGeometryInputs<TGeometry, TReader> WriteGeometryInputs<TGeometry, TReader>(
        RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs, TReader reader, uint geometryType, int rowsPerPacket)
        where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
        where TReader : struct, IDeviceGeometryReader<TGeometry> => new(this,
        new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Between),
        reader, geometryType, rowsPerPacket);

    public WriteDeviceGeometryInputs<TGeometry, TReader> WriteGeometryInputs<TGeometry, TReader>(
        RelationQuery<EntityProduct2, (Entity, Entity)> pairs, TReader reader, uint geometryType, int rowsPerPacket)
        where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry>
        where TReader : struct, IDeviceGeometryReader<TGeometry> => new(this,
        new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Product),
        reader, geometryType, rowsPerPacket);

    public BuildDeviceCollisionGrid Build(RelationQuery<EntityPairsBetween, (Entity, Entity)> pairs,
        DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback) => new(this,
        new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Between),
        rules, transfer, readback);

    public BuildDeviceCollisionGrid Build(RelationQuery<EntityProduct2, (Entity, Entity)> pairs,
        DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback) => new(this,
        new(pairs.Enumerable.World, pairs.Enumerable.GetQuery(0), pairs.Enumerable.GetQuery(1), DevicePairing.Product),
        rules, transfer, readback);
}
