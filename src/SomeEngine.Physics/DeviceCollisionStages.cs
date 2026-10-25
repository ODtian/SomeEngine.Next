using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Converts the selected original components directly into their mapped device-upload ranges.</summary>
public readonly struct WriteDeviceCollisionInputs<TComponent, TReader> : IPhysicsStage
    where TComponent : unmanaged, IComponent
    where TReader : struct, IDeviceCollisionReader<TComponent>
{
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceCollisionParticipants _participants;
    private readonly TReader _reader;
    private readonly int _rowsPerPacket;

    internal WriteDeviceCollisionInputs(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
        TReader reader, int rowsPerPacket) => (_grid, _participants, _reader, _rowsPerPacket) =
        (grid, participants, reader, rowsPerPacket);

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        WorldJobs.TryGetScheduler(context.World, out Scheduler? scheduler);
        var accesses = new List<JobResourceAccess>();
        var resources = new JobResourceAccessBuilder(scheduler!, accesses);
        resources.Write(_grid.Upload, 0, (long)_grid.Capacity.Bodies * DeviceCollisionGrid.InputStride);
        JobHandle begun = scheduler!.Schedule(new BeginDeviceInputWrite(_grid), CollectionsMarshal.AsSpan(accesses), dependency);
        var options = new JobEntityScheduleOptions(rowsPerPacket: _rowsPerPacket);
        return new WriteDeviceCollisionInputsJob<TComponent, TReader>(_grid, _participants, _reader)
            .ScheduleParallel(context.World, _participants.GetReadAccesses(), options, begun);
    }
}

internal readonly struct BeginDeviceInputWrite(DeviceCollisionGrid grid) : IJob
{
    public void Execute() => grid.BeginInputWrite();
}

internal readonly partial struct WriteDeviceCollisionInputsJob<TComponent, TReader>(DeviceCollisionGrid grid,
    DeviceCollisionParticipants participants, TReader reader) : IJobEntity
    where TComponent : unmanaged, IComponent
    where TReader : struct, IDeviceCollisionReader<TComponent>
{
    public void Execute(Entity entity, [JobResources<DeviceCollisionUploadResources>] in DeviceCollisionBody body,
        in TComponent component)
    {
        if (!ReferenceEquals(body.Grid, grid))
            return;
        DeviceCollisionInput input = reader.Read(entity, in component);
        input.EntityIndex = (uint)entity.Index;
        input.EntityGeneration = (uint)entity.Generation;
        input.Members = participants.ReadMembers(entity);
        body.Grid.WriteInput(body.Index, in input);
    }
}

/// <summary>Runs the selected device grid and direct type-bin output as part of the common physics step.</summary>
public readonly struct DeviceCollisionCandidates<TComponent, TReader> : IPhysicsStage
    where TComponent : unmanaged, IComponent
    where TReader : struct, IDeviceCollisionReader<TComponent>
{
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceCollisionParticipants _participants;
    private readonly TReader _reader;
    private readonly int _rowsPerPacket;
    private readonly DevicePairRules _rules;
    private readonly DeviceInputTransfer _transfer;
    private readonly DevicePairReadback _readback;

    internal DeviceCollisionCandidates(DeviceCollisionGrid grid, DeviceCollisionParticipants participants,
        TReader reader, int rowsPerPacket, DevicePairRules rules, DeviceInputTransfer transfer, DevicePairReadback readback)
    {
        _grid = grid;
        _participants = participants;
        _reader = reader;
        _rowsPerPacket = rowsPerPacket;
        _rules = rules;
        _transfer = transfer;
        _readback = readback;
    }

    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (_transfer == DeviceInputTransfer.Upload)
            dependency = new WriteDeviceCollisionInputs<TComponent, TReader>(_grid, _participants, _reader,
                _rowsPerPacket).Schedule(in context, dependency);
        WorldJobs.TryGetScheduler(context.World, out Scheduler? scheduler);
        return _grid.Schedule(scheduler!, in context, _participants.Pairing, _rules, _transfer, _readback, dependency);
    }
}

internal readonly record struct DeviceCollisionParticipants(World World, QueryHandle First, QueryHandle Second,
    DevicePairing Pairing)
{
    internal JobResourceAccess[] GetReadAccesses()
    {
        QueryDefinition first = World.GetQueryDefinition(First);
        QueryDefinition second = World.GetQueryDefinition(Second);
        var accesses = new JobResourceAccess[QueryJobAccess.Count(first) + QueryJobAccess.Count(second)];
        int count = QueryJobAccess.CopyAccesses(World, first, accesses);
        QueryJobAccess.CopyAccesses(World, second, accesses.AsSpan(count));
        return accesses;
    }

    internal uint ReadMembers(Entity entity)
    {
        var first = new FindDeviceMember(entity);
        World.ExecuteQuery(First, ref first);
        uint members = first.Found ? (uint)DevicePairMembership.First : 0;
        if (Pairing != DevicePairing.Within)
        {
            var second = new FindDeviceMember(entity);
            World.ExecuteQuery(Second, ref second);
            if (second.Found)
                members |= (uint)DevicePairMembership.Second;
        }
        return members;
    }

    private struct FindDeviceMember(Entity entity) : IQueryExecution
    {
        internal bool Found;
        public void Execute(scoped QueryCursor query) => Found = query.Contains(entity);
    }
}
