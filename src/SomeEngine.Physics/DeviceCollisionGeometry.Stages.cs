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
        dependency = _reader.Prepare(in context, context.Bodies, _rowsPerPacket, dependency);
        Scheduler scheduler = WorldJobs.GetScheduler(context.World);
        JobResourceAccess[] membership = [.. _participants.GetReadAccesses(),
            JobResourceAccess.Write(scheduler.GetResourceToken(_grid.Upload), 0, (long)_grid.Upload.Info.Size)];
        dependency = scheduler.Schedule(new CaptureDeviceMemberVersions(_grid, _participants, context), membership, dependency);
        return new SystemRunner(context.World, _rowsPerPacket == 0 ? 1024 : _rowsPerPacket).Schedule(
            new WriteDeviceGeometryInputsJob<TGeometry, TReader>(_grid, _participants, _reader, _geometryType), dependency);
    }
}

internal readonly struct WriteDeviceGeometryInputsJob<TGeometry, TReader>(DeviceCollisionGrid grid,
    DeviceCollisionParticipants participants, TReader reader, uint geometryType) : IChunkAction
    where TGeometry : struct, IComponent, ICollisionGeometry<TGeometry> where TReader : struct, IDeviceGeometryReader<TGeometry>
{
    public static ReadOnlySpan<ChunkAccess> Components => [ChunkAccess.Read<DeviceCollisionBody>(true),
        ChunkAccess.Read<DeviceGeometryBinding>(true),ChunkAccess.Read<TGeometry>(true),ChunkAccess.Read<BodyPose>(),
        ChunkAccess.ReadOptional<ContactMaterial>()];
    public static void CollectResources(SystemChunk rows, ref JobResourceAccessBuilder resources)
    {
        ComponentResources.CollectRange<DeviceCollisionBody, DeviceCollisionUploadResources>(rows.Read<DeviceCollisionBody>(), ref resources);
        ComponentResources.CollectRange<DeviceGeometryBinding, DeviceGeometryUploadResources>(rows.Read<DeviceGeometryBinding>(), ref resources);
        ComponentResources.CollectRange<TGeometry, TReader>(rows.Read<TGeometry>(), ref resources);
    }
    public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
    { foreach (JobResourceAccess access in participants.GetReadAccesses()) resources.Add(access); }
    public void Execute(SystemChunk rows)
    {
        var bodies = rows.Read<DeviceCollisionBody>(); var bindings = rows.Read<DeviceGeometryBinding>();
        ReadOnlySpan<TGeometry> geometries = rows.Read<TGeometry>(); var poses = rows.Read<BodyPose>();
        rows.TryRead<ContactMaterial>(out var materials);
        using QueryScope first = participants.First.Since(grid.FirstMemberVersion).OpenRead();
        using QueryScope second = participants.Second.Since(grid.SecondMemberVersion).OpenRead();
        for (int row = 0; row < rows.Count; row++)
        {
            if (!ReferenceEquals(bodies[row].Grid, grid)) continue;
            DeviceGeometryBinding binding = bindings[row];
            DeviceGeometryInput value = reader.Read(in geometries[row], in poses[row], binding.Geometry);
            value.GeometryIndex = (uint)binding.Index; value.GeometryType = geometryType;
            binding.Geometry.WriteInput(binding.Index, in value);
            if (rows.Has<ContactMaterial>(row)) binding.Geometry.WriteMaterial(binding.Index, in materials[row]);
            CollisionBounds bounds = reader.GetBounds(in geometries[row], in poses[row]);
            uint members = first.Contains(rows.Entities[row]) ? (uint)DevicePairMembership.First : 0;
            if (participants.Pairing != DevicePairing.Within && second.Contains(rows.Entities[row])) members |= (uint)DevicePairMembership.Second;
            var input = new DeviceCollisionInput
            {
                Min = bounds.Min,
                Max = bounds.Max,
                EntityIndex = (uint)rows.Entities[row].Id,
                EntityGeneration = (uint)rows.Entities[row].Generation,
                GeometryType = geometryType,
                GeometryIndex = (uint)binding.Index,
                Members = members
            };
            grid.WriteInput(bodies[row].Index, in input);
        }
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

internal readonly struct CaptureDeviceMemberVersions(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
    PhysicsStepContext context) : IJob
{
    public void Execute() => grid.CaptureMemberVersions(participants, in context);
}
