using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
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
        var accesses = new List<JobResourceAccess>(_participants.GetReadAccesses());
        var resources = new JobResourceAccessBuilder(scheduler!, accesses);
        resources.Write(_grid.Upload, 0, (long)_grid.Capacity.Bodies * DeviceCollisionGrid.InputStride);
        JobHandle begun = scheduler!.Schedule(new BeginDeviceInputWrite(_grid, _participants, context), CollectionsMarshal.AsSpan(accesses), dependency);
        return new WriteDeviceCollisionInputsJob<TComponent, TReader>(_grid, _participants, _reader)
            .ScheduleParallel(context.World, dependency: begun);
    }
}

internal readonly struct BeginDeviceInputWrite(DeviceCollisionGrid grid, DeviceCollisionParticipants? participants = null, PhysicsStepContext context = default) : IJob
{
    public void Execute() { if (participants is { } selected) grid.CaptureMemberVersions(selected, in context); grid.BeginInputWrite(); }
}

internal readonly partial struct WriteDeviceCollisionInputsJob<TComponent, TReader>(DeviceCollisionGrid grid,
    DeviceCollisionParticipants participants, TReader reader) : IJobEntity
    where TComponent : unmanaged, IComponent
    where TReader : struct, IDeviceCollisionReader<TComponent>
{
    public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
    { foreach (JobResourceAccess access in participants.GetReadAccesses()) resources.Add(access); }
    public void Execute(Entity entity, [JobResources<DeviceCollisionUploadResources>] in DeviceCollisionBody body,
        in TComponent component)
    {
        if (!ReferenceEquals(body.Grid, grid))
            return;
        DeviceCollisionInput input = reader.Read(entity, in component);
        input.EntityIndex = (uint)entity.Id;
        input.EntityGeneration = (uint)entity.Generation;
        input.Members = participants.ReadMembers(entity, grid.FirstMemberVersion, grid.SecondMemberVersion);
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

internal readonly record struct DeviceCollisionParticipants(World World, Query First, Query Second,
    DevicePairing Pairing)
{
    internal JobResourceAccess[] GetReadAccesses()
    {
        var jobs = (WorldJobs)World.Execution!;
        return [.. jobs.CollectAccess<MembershipAccess>(First), .. jobs.CollectAccess<MembershipAccess>(Second)];
    }
    private readonly struct MembershipAccess : IChunkAction
    {
        public static ReadOnlySpan<ChunkAccess> Components => [];
        public void Execute(SystemChunk chunk) { }
    }
    internal uint ReadMembers(Entity entity, ulong firstVersion, ulong secondVersion)
    {
        using QueryScope first = First.Since(firstVersion).OpenRead();
        uint members = first.Contains(entity) ? (uint)DevicePairMembership.First : 0;
        if (Pairing != DevicePairing.Within)
        {
            using QueryScope second = Second.Since(secondVersion).OpenRead();
            if (second.Contains(entity)) members |= (uint)DevicePairMembership.Second;
        }
        return members;
    }
}
