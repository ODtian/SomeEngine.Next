using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal static class DeviceCrowdConstraintInputs
{
    internal static DeviceCrowdConstraintInputs<TInputs> Create<TInputs>(World world, QueryHandle bodies,
        QueryHandle lengths, QueryHandle bends, QueryHandle joints, TInputs inputs) where TInputs : struct, IPhysicsStage =>
        new(world, bodies, lengths, bends, joints, inputs);
}

internal sealed class DeviceCrowdConstraintInputs<TInputs> : IDisposable where TInputs : struct, IPhysicsStage
{
    private readonly World _world;
    private readonly TInputs _inputs;
    private readonly QueryHandle _bodies, _lengths, _bends, _joints, _participants;
    private readonly JobResourceAccess[] _accesses;
    private readonly uint[] _memberships = new uint[5];
    private uint _lastSystemVersion;

    internal DeviceCrowdConstraintInputs(World world, QueryHandle bodies, QueryHandle lengths, QueryHandle bends,
        QueryHandle joints, TInputs inputs)
    {
        _world = world; _inputs = inputs;
        QueryDefinition bodyReads = world.GetQueryDefinition(bodies).AsFilter()
            .WithAccess(new QueryDefinitionBuilder().Read<DeviceBodyBinding>().Build());
        QueryDefinition lengthReads = world.GetQueryDefinition(lengths).AsReadOnly();
        QueryDefinition bendReads = world.GetQueryDefinition(bends).AsReadOnly();
        QueryDefinition jointReads = world.GetQueryDefinition(joints).AsReadOnly();
        QueryDefinition participantReads = new QueryDefinitionBuilder().Read<DirectedRelationEndpoints<ConstraintParticipant>>().Build();
        _bodies = world.Query(bodyReads); _lengths = world.Query(lengthReads); _bends = world.Query(bendReads);
        _joints = world.Query(jointReads);
        _participants = world.Query(participantReads);
        var resources = new QueryReadResources();
        resources.Add<DeviceBodyBinding, DeviceBodyReferenceResources>();
        resources.Add<DeviceConstraintBinding<DeviceLengthConstraint>, DeviceConstraintReferenceResources<DeviceLengthConstraint>>();
        resources.Add<DeviceConstraintBinding<DeviceBendConstraint>, DeviceConstraintReferenceResources<DeviceBendConstraint>>();
        resources.Add<DeviceConstraintBinding<DeviceJointConstraint>, DeviceConstraintReferenceResources<DeviceJointConstraint>>();
        resources.Add<DeviceAvbdState<DeviceAvbdJointState>, DeviceAvbdStateResources<DeviceAvbdJointState>>();
        var accesses = new List<JobResourceAccess>();
        foreach (QueryDefinition definition in new[] { bodyReads, lengthReads, bendReads, jointReads, participantReads })
        {
            var queryAccesses = new JobResourceAccess[QueryJobAccess.Count(definition)];
            QueryJobAccess.CopyAccesses(world, definition, queryAccesses, resources);
            accesses.AddRange(queryAccesses);
        }
        accesses.Add(JobResourceAccess.Write(WorldJobs.GetScheduler(world).GetResourceToken(this)));
        _accesses = accesses.ToArray();
    }

    internal UploadCrowdConstraintInputs<TInputs> Upload() => new(this);
    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) =>
        WorldJobs.GetScheduler(_world).Schedule(new ReadChanges(this, context), _accesses, dependency);

    private void Update(in PhysicsStepContext context)
    {
        var changes = new ChangedInputs();
        ReadOnlySpan<QueryHandle> queries = [_bodies, _lengths, _bends, _joints, _participants];
        for (int index = 0; index < queries.Length; index++)
        {
            uint membership = _world.MembershipVersion(queries[index]);
            changes.Changed |= membership != _memberships[index];
            _memberships[index] = membership;
        }
        _world.ExecuteReadQuery(_bodies, _lastSystemVersion, ref changes,
            static (scoped QueryCursor rows, ref ChangedInputs changes) =>
            {
                changes.Version = rows.CurrentSystemVersion;
                if (changes.Changed) return;
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (chunk.HasChangedSinceLastSystemVersion<DeviceBodyBinding>())
                    { changes.Changed = true; break; }
            });
        _world.ExecuteReadQuery(_lengths, _lastSystemVersion, ref changes,
            static (scoped QueryCursor rows, ref ChangedInputs changes) =>
            {
                changes.Version = rows.CurrentSystemVersion;
                if (changes.Changed) return;
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (chunk.HasChangedSinceLastSystemVersion<XpbdLength>()
                        || chunk.HasChangedSinceLastSystemVersion<UndirectedRelationEndpoints<XpbdLength>>()
                        || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<DeviceLengthConstraint>>())
                    { changes.Changed = true; break; }
            });
        _world.ExecuteReadQuery(_bends, _lastSystemVersion, ref changes,
            static (scoped QueryCursor rows, ref ChangedInputs changes) =>
            {
                changes.Version = rows.CurrentSystemVersion;
                if (changes.Changed) return;
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (chunk.HasChangedSinceLastSystemVersion<XpbdBend>()
                        || chunk.HasBufferChangedSinceLastSystemVersion<ConstraintBodyLink>()
                        || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<DeviceBendConstraint>>())
                    { changes.Changed = true; break; }
            });
        _world.ExecuteReadQuery(_joints, _lastSystemVersion, ref changes,
            static (scoped QueryCursor rows, ref ChangedInputs changes) =>
            {
                changes.Version = rows.CurrentSystemVersion;
                if (changes.Changed) return;
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (chunk.HasChangedSinceLastSystemVersion<JointConstraint>()
                        || chunk.HasChangedSinceLastSystemVersion<DirectedRelationEndpoints<JointConstraint>>()
                        || chunk.HasChangedSinceLastSystemVersion<DeviceConstraintBinding<DeviceJointConstraint>>()
                        || chunk.HasChangedSinceLastSystemVersion<AvbdJointState>()
                        || chunk.HasChangedSinceLastSystemVersion<DeviceAvbdState<DeviceAvbdJointState>>())
                    { changes.Changed = true; break; }
            });
        _world.ExecuteReadQuery(_participants, _lastSystemVersion, ref changes,
            static (scoped QueryCursor rows, ref ChangedInputs changes) =>
            {
                changes.Version = rows.CurrentSystemVersion;
                if (changes.Changed) return;
                foreach (QueryChunkView chunk in rows.Chunks)
                    if (chunk.HasChangedSinceLastSystemVersion<DirectedRelationEndpoints<ConstraintParticipant>>())
                    { changes.Changed = true; break; }
            });
        _lastSystemVersion = changes.Version;
        if (changes.Changed) _inputs.Schedule(in context, default);
    }

    private struct ChangedInputs
    {
        internal bool Changed;
        internal uint Version;
    }
    private readonly struct ReadChanges(DeviceCrowdConstraintInputs<TInputs> inputs, PhysicsStepContext context) : IJob
    {
        public void Execute() => inputs.Update(in context);
    }
    public void Dispose()
    {
        _world.ReleaseQuery(_participants); _world.ReleaseQuery(_joints); _world.ReleaseQuery(_bends);
        _world.ReleaseQuery(_lengths); _world.ReleaseQuery(_bodies);
    }
}

internal readonly struct UploadCrowdConstraintInputs<TInputs>(DeviceCrowdConstraintInputs<TInputs> inputs) : IPhysicsStage
    where TInputs : struct, IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => inputs.Schedule(in context, dependency);
}
