using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

internal sealed class DeviceCrowdContacts : IDisposable
{
    private readonly World _world;
    private readonly List<IDisposable> _resources = [];
    private readonly List<QueryHandle> _queries = [];
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceScenePairExclusions? _pairExclusions;
    private readonly DeviceCollisionGeometry _geometry;
    private readonly RelationQuery<EntityPairsWithin, (Entity, Entity)> _pairs;
    private readonly WriteDeviceGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>> _boxes;
    private readonly WriteDeviceGeometryReferences<DynamicCapsule, DeviceDynamicCapsuleSource> _capsules;
    private readonly WriteDeviceGeometryReferences<DynamicFace, DeviceDynamicFaceSource> _faces;
    private readonly UpdateDeviceGeometry _boxGeometry, _residentBoxes, _capsuleGeometry, _faceGeometry;
    private readonly DeviceCollisionDetection _detection;
    private readonly ContactPointBuffer _detected;
    private readonly DeviceContactBodies _anchors;
    private readonly DeviceContactMatches _matching;
    private readonly DeviceContactGeometry _current;
    private readonly ContactPoints<AverageContactMaterials, DeviceMatchedContactComponents<CrowdContactComponents>> _refresh;
    private readonly DeviceContactInputs _inputs;
    private readonly DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource> _stateInputs;
    private bool _geometryUploaded;

    internal DeviceCrowdContacts(IGraphicsBackend backend, Device device, World world, DeviceBodies bodies,
        IReadOnlyList<ReadOnlyMemory<Entity>> characters, IReadOnlyList<Entity> cloth, IReadOnlyList<Entity> boxes,
        ReadOnlySpan<Entity> bodyEntities, int exclusionCapacity, bool jointNet, PhysicsSceneShaders shaders)
    {
        _world = world;
        int shapeCount = characters.Count * 21 + boxes.Count;
        int pairCapacity = Math.Max(128, characters.Count * 32 + (boxes.Count - 1) * 8);
        int contactCapacity = pairCapacity * 4;
        _geometry = Keep(new DeviceCollisionGeometry(backend, device, new(shapeCount, 0, 0, 0), bodies));
        var gridBindings = new Dictionary<string, ResourceBinding[]>();
        var gridReads = new List<JobResourceAccess>();
        if (jointNet)
        {
            _pairExclusions = Keep(new DeviceScenePairExclusions(backend, device, world, bodyEntities, exclusionCapacity));
            _pairExclusions.AddBindings(gridBindings);
            Scheduler jobs = WorldJobs.GetScheduler(world);
            gridReads.Add(bodies.GetReadAccess(jobs)); gridReads.Add(_pairExclusions.ReadAccess(jobs));
            BufferUav bodyView = Keep(backend.CreateBufferUav(device,
                new(bodies.States, new(0, bodies.States.Info.Size), StructureStride: DeviceBodies.StateStride)));
            gridBindings.Add("DeviceBodyStates", [ResourceBinding.WritableBuffer(bodyView)]);
            var references = _geometry.References!;
            BufferUav view = Keep(backend.CreateBufferUav(device, new(references, new(0, references.Info.Size), StructureStride: 64)));
            gridBindings.Add("DeviceGeometryReferences", [ResourceBinding.WritableBuffer(view)]);
            gridReads.Add(JobResourceAccess.Read(jobs.GetResourceToken(references), 0, (long)references.Info.Size));
            var geometryPoints = _geometry.Points!;
            BufferUav pointsView = Keep(backend.CreateBufferUav(device,
                new(geometryPoints, new(0, geometryPoints.Info.Size), StructureStride: 12)));
            gridBindings.Add("DeviceGeometryPoints", [ResourceBinding.WritableBuffer(pointsView)]);
            gridReads.Add(JobResourceAccess.Read(jobs.GetResourceToken(geometryPoints), 0, (long)geometryPoints.Info.Size));
        }
        _grid = Keep(new DeviceCollisionGrid(backend, device, shaders.CrowdGrid(jointNet), new(.4f, 12),
            new(shapeCount, shapeCount * 8, shapeCount * 16 + 1, pairCapacity, 6), gridBindings, gridReads.ToArray()));
        int geometryIndex = 0;
        for (int character = 0; character < characters.Count; character++)
        {
            ReadOnlySpan<Entity> arm = characters[character].Span;
            for (int segment = 0; segment < 3; segment++)
                Add(world.CreateEntity(new DynamicCapsule(new(arm[segment], default), new(arm[segment + 1], default), .05f)));
            for (int column = 0; column < 3; column++)
            for (int row = 0; row < 3; row++)
            {
                Entity a = cloth[character * 16 + column * 4 + row], b = cloth[character * 16 + (column + 1) * 4 + row];
                Entity c = cloth[character * 16 + column * 4 + row + 1], d = cloth[character * 16 + (column + 1) * 4 + row + 1];
                Add(world.CreateEntity(new DynamicFace(new(a, default), new(b, default), new(c, default), .01f)));
                Add(world.CreateEntity(new DynamicFace(new(b, default), new(d, default), new(c, default), .01f)));
            }
        }
        foreach (Entity box in boxes) Add(box);
        QueryHandle shapes = Query(new QueryDefinitionBuilder().All<CrowdCollisionGeometry>().Build());
        QueryHandle poses = Query(new QueryDefinitionBuilder().Read<BodyPose>().Build());
        QueryHandle points = Query(new QueryDefinitionBuilder().All<CrowdContact>().Read<ContactPoint>().Build());
        QueryHandle states = Query(new QueryDefinitionBuilder().All<CrowdContact>().Read<AvbdContactState>()
            .Read<DeviceConstraintBinding<DeviceContactConstraint>>().Read<DeviceAvbdState<DeviceAvbdContactState>>().Build());
        _pairs = world.Within(shapes);
        _boxes = Keep(_grid.WriteGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>>(_pairs, default, 0, 64));
        _capsules = Keep(_grid.WriteGeometryReferences<DynamicCapsule, DeviceDynamicCapsuleSource>(_pairs, default, 1, 64));
        _faces = Keep(_grid.WriteGeometryReferences<DynamicFace, DeviceDynamicFaceSource>(_pairs, default, 2, 64));
        ComputePipelineDesc boxKernel = shaders.CrowdGeometry("DeviceBoxUpdate");
        _boxGeometry = Keep(new UpdateDeviceGeometry(backend, device, _grid, _geometry, boxKernel, 0, DeviceInputTransfer.Upload));
        _residentBoxes = Keep(new UpdateDeviceGeometry(backend, device, _grid, _geometry, boxKernel, 0, DeviceInputTransfer.Resident));
        _capsuleGeometry = Keep(new UpdateDeviceGeometry(backend, device, _grid, _geometry, shaders.CrowdGeometry("DeviceDynamicCapsuleUpdate"), 1, DeviceInputTransfer.Resident));
        _faceGeometry = Keep(new UpdateDeviceGeometry(backend, device, _grid, _geometry, shaders.CrowdGeometry("DeviceDynamicFaceUpdate"), 2, DeviceInputTransfer.Resident));
        ComputePipelineDesc[] detection = jointNet ? shaders.CrowdDetection() : [shaders.CrowdCapsuleFaces()];
        _detection = Keep(new DeviceCollisionDetection(backend, device, _grid, _geometry, detection, jointNet ? 0 : 4,
            new(pairCapacity, 4, Math.Min(pairCapacity, 4096)), new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.Contacts));
        Scheduler scheduler = WorldJobs.GetScheduler(world);
        _detected = Keep(new ContactPointBuffer(scheduler, contactCapacity, contactCapacity * 5));
        Parameters = Keep(new DeviceConstraintData<DeviceContactConstraint>(backend, device, contactCapacity, "DeviceContacts"));
        _anchors = Keep(new DeviceContactBodies(backend, device, contactCapacity, 5));
        Histories = Keep(new DeviceConstraintState<DeviceAvbdContactState>(backend, device, contactCapacity, "DeviceAvbdContactStates"));
        Responses = Keep(new DeviceConstraintState<DeviceContactResponseState>(backend, device, contactCapacity, "DeviceContactResponses"));
        _matching = Keep(new DeviceContactMatches(backend, device, world, points, Parameters, _detection, bodies, _anchors, shaders.Matching(),
            new(.02f, .99f, true), contactCapacity * 2 + 1, 64));
        _current = Keep(_matching.CreateGeometryUpdate(bodies, _anchors, shaders.ContactGeometry()));
        _refresh = Keep(ContactPoints.Create(world, poses, points, _detected, new AverageContactMaterials(),
            _matching.Components(new CrowdContactComponents(Histories, Responses)), new(.02f, .99f, true)));
        _inputs = Keep(new DeviceContactInputs(world, points, Parameters, _anchors));
        _stateInputs = Keep(new DeviceAvbdStateInputs<AvbdContactState, DeviceContactConstraint, DeviceAvbdContactState, DeviceAvbdContactStateSource>(
            backend, device, world, states, Parameters, Histories, default));
        Selection = Keep(new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint>(backend, device, world, points, Parameters, "DeviceContactsSelection"));
        StateSelection = Keep(new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>>(
            backend, device, world, points, Parameters, new(Histories), "DeviceContactAvbdSelection"));
        ResponseSelection = Keep(new DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows>(
            backend, device, world, points, Parameters, new(Responses), "DeviceContactResponsesSelection"));

        void Add(Entity entity)
        {
            world.Add(entity, new CrowdCollisionGeometry());
            world.Add(entity, new DeviceCollisionBody(_grid, geometryIndex));
            world.Add(entity, new DeviceGeometryBinding(_geometry, geometryIndex++));
            world.Add(entity, new ContactMaterial { NormalStiffness = float.PositiveInfinity, StaticFriction = .3f, DynamicFriction = .2f });
        }
    }

    internal DeviceConstraintData<DeviceContactConstraint> Parameters { get; }
    internal DeviceConstraintState<DeviceAvbdContactState> Histories { get; }
    internal DeviceConstraintState<DeviceContactResponseState> Responses { get; }
    internal DeviceConstraintSelection<ContactPoint, DeviceContactConstraint> Selection { get; }
    internal DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceAvbdStateRows<DeviceAvbdContactState>> StateSelection { get; }
    internal DeviceConstraintSelection<ContactPoint, DeviceContactConstraint, DeviceContactResponseRows> ResponseSelection { get; }
    internal PrepareCrowdContacts Detect() => new(this);
    internal JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => [.. Parameters.GetReadAccesses(scheduler), _anchors.GetReadAccess(scheduler)];
    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    { Parameters.AddBindings(bindings); _anchors.AddBindings(bindings); Histories.AddBindings(bindings); Responses.AddBindings(bindings); }

    internal JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
    {
        if (!_geometryUploaded)
        {
            _geometryUploaded = true;
            dependency = _grid.BeginInputs().Schedule(in context, dependency);
            dependency = _boxes.Schedule(in context, dependency);
            dependency = _capsules.Schedule(in context, dependency);
            dependency = _faces.Schedule(in context, dependency);
            dependency = _boxGeometry.Update().Schedule(in context, dependency);
        }
        else dependency = _residentBoxes.Update().Schedule(in context, dependency);
        dependency = _capsuleGeometry.Update().Schedule(in context, dependency);
        dependency = _faceGeometry.Update().Schedule(in context, dependency);
        if (_pairExclusions is { } exclusions) dependency = exclusions.Upload().Schedule(in context, dependency);
        dependency = _grid.Build(_pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None).Schedule(in context, dependency);
        dependency = WorldJobs.GetScheduler(_world).Schedule(new Clear(_detected), [_detected.WriteAccess], dependency);
        dependency = _detection.Detect().Schedule(in context, dependency);
        dependency = _detection.WriteContacts(new ContactPointOutput(_detected), new[] { _detected.WriteAccess }).Schedule(in context, dependency);
        dependency = _matching.Match().Schedule(in context, dependency);
        dependency = _refresh.Refresh(_current.Results).Schedule(in context, dependency);
        dependency = _inputs.Upload().Schedule(in context, dependency);
        dependency = _stateInputs.Upload().Schedule(in context, dependency);
        return _current.Update().Schedule(in context, dependency);
    }
    private readonly struct Clear(ContactPointBuffer contacts) : IJob { public void Execute() => contacts.Clear(); }
    private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
    private QueryHandle Query(QueryDefinition definition) { QueryHandle query = _world.Query(definition); _queries.Add(query); return query; }
    public void Dispose()
    {
        for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
        foreach (QueryHandle query in _queries) _world.ReleaseQuery(query);
    }
}
internal readonly struct PrepareCrowdContacts(DeviceCrowdContacts contacts) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => contacts.Schedule(in context, dependency);
}
internal readonly struct CrowdCollisionGeometry : IComponent;
internal readonly struct CrowdContact : IComponent;
internal readonly struct CrowdContactComponents(DeviceConstraintState<DeviceAvbdContactState> histories,
    DeviceConstraintState<DeviceContactResponseState> responses) : IContactPointComponents
{
    public void AddReads(World world, List<JobResourceAccess> accesses) { }
    public void Add(ref JobCommandWriter commands, Entity point, in ContactPoint geometry,
        ReadOnlySpan<ContactBody> first, ReadOnlySpan<ContactBody> second, in PhysicsStepContext context)
    {
        commands.Add(point, new CrowdContact());
        new AvbdContactComponents(new AvbdContactState { NormalPenalty = 1000, TangentPenalty = 1000,
            FrictionRegularizationSpeed = .01f }).Add(ref commands, point, in geometry, first, second, in context);
        commands.Add(point, new DeviceAvbdState<DeviceAvbdContactState>(histories));
        commands.Add(point, new DeviceContactResponseBinding(responses));
    }
}
