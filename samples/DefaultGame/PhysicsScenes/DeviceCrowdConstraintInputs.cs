using SomeEngine.ECS;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal static class DeviceCrowdConstraintInputs
{
    internal static DeviceCrowdConstraintInputs<TInputs> Create<TInputs>(World world, Query bodies,
        Query lengths, Query bends, Query joints, TInputs inputs) where TInputs : struct, IPhysicsStage =>
        new(world, bodies, lengths, bends, joints, inputs);
}

internal sealed class DeviceCrowdConstraintInputs<TInputs> : IDisposable where TInputs : struct, IPhysicsStage
{
    private readonly World _world;
    private readonly TInputs _inputs;
    private readonly Query _bodies;
    private readonly Query[] _queries;
    private readonly ulong[] _memberships = new ulong[5];
    private readonly ulong[] _versions = new ulong[11];
    private bool _hasInputs;
    internal DeviceCrowdConstraintInputs(World world, Query bodies, Query lengths, Query bends, Query joints, TInputs inputs)
    {
        _world = world; _inputs = inputs; _bodies = bodies;
        _queries = [bodies, lengths, bends, joints,
            world.Query(new QueryDescription().WithStored<RelationEndpoints<ConstraintBodyLink>>())];
        for (int index = 0; index < _queries.Length; index++) _memberships[index] = _queries[index].MembershipVersion;
    }
    internal UploadCrowdConstraintInputs<TInputs> Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        WorldJobs jobs = (WorldJobs)_world.Execution!;
        return jobs.Scheduler.Schedule(new CollectChanges(this, context), jobs.StructureRead, dependency);
    }
    private readonly struct InputReads : IChunkAction
    {
        private static readonly ChunkAccess[] Fields = [ChunkAccess.Read<DeviceBodyBinding>(hasResources: true)];
        public static ReadOnlySpan<ChunkAccess> Components => Fields;
        public static SystemAccesses AdditionalAccesses => new SystemAccesses().ReadRandom<DeviceBodyBinding>()
            .ReadRandom<XpbdLength>().ReadRandom<RelationEndpoints<XpbdLength>>().ReadRandom<DeviceConstraintBinding<DeviceLengthConstraint>>()
            .ReadRandom<XpbdBend>().ReadRandom<DeviceConstraintBinding<DeviceBendConstraint>>()
            .ReadRandom<JointConstraint>().ReadRandom<RelationEndpoints<JointConstraint>>()
            .ReadRandom<DeviceConstraintBinding<DeviceJointConstraint>>().ReadRandom<AvbdJointState>()
            .ReadRandom<DeviceAvbdState<DeviceAvbdJointState>>();
        public void Execute(SystemChunk rows) { }
    }
    private readonly struct CollectChanges(DeviceCrowdConstraintInputs<TInputs> inputs, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            WorldJobs jobs = (WorldJobs)inputs._world.Execution!;
            jobs.Scheduler.Schedule(new ReadChanges(inputs, context),
                [.. jobs.CollectAccess<InputReads>(inputs._bodies), JobResourceAccess.Write(jobs.Scheduler.GetResourceToken(inputs))]);
        }
    }
    private readonly struct ReadChanges(DeviceCrowdConstraintInputs<TInputs> inputs, PhysicsStepContext context) : IJob
    {
        public void Execute()
        {
            World world = inputs._world;
            bool changed = !inputs._hasInputs;
            ReadOnlySpan<ulong> versions = [world.ComponentWriteVersion<DeviceBodyBinding>(),
                world.ComponentWriteVersion<XpbdLength>(), world.ComponentWriteVersion<RelationEndpoints<XpbdLength>>(),
                world.ComponentWriteVersion<DeviceConstraintBinding<DeviceLengthConstraint>>(),
                world.ComponentWriteVersion<XpbdBend>(), world.ComponentWriteVersion<DeviceConstraintBinding<DeviceBendConstraint>>(),
                world.ComponentWriteVersion<JointConstraint>(), world.ComponentWriteVersion<RelationEndpoints<JointConstraint>>(),
                world.ComponentWriteVersion<DeviceConstraintBinding<DeviceJointConstraint>>(), world.ComponentWriteVersion<AvbdJointState>(),
                world.ComponentWriteVersion<DeviceAvbdState<DeviceAvbdJointState>>()];
            for (int index = 0; index < versions.Length; index++)
            {
                changed |= versions[index] != inputs._versions[index]; inputs._versions[index] = versions[index];
            }
            for (int index = 0; index < inputs._queries.Length; index++)
            {
                ulong version = inputs._queries[index].MembershipVersion;
                changed |= version != inputs._memberships[index]; inputs._memberships[index] = version;
            }
            if (changed) inputs._inputs.Schedule(in context, default);
            inputs._hasInputs = true;
        }
    }
    public void Dispose() { }
}

internal readonly struct UploadCrowdConstraintInputs<TInputs>(DeviceCrowdConstraintInputs<TInputs> inputs) : IPhysicsStage
    where TInputs : struct, IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}
