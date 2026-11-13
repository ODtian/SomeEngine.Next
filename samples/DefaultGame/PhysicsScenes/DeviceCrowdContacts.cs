using SomeEngine.ECS;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
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
    private readonly DeviceCollisionGrid _grid;
    private readonly DeviceScenePairExclusions? _pairExclusions;
    private readonly DeviceCollisionGeometry _geometry;
    private readonly RelationQuery<EntityPairsWithin, (Entity, Entity)> _pairs;
    private readonly WriteDeviceGeometryReferences<BoxGeometry, DeviceComponentSource<BoxGeometry, DeviceBoxReader>> _boxes;
    private readonly WriteDeviceGeometryReferences<DynamicCapsule, DeviceDynamicCapsuleSource> _capsules;
    private readonly WriteDeviceGeometryReferences<DynamicFace, DeviceDynamicFaceSource> _faces;
    private readonly UpdateDeviceGeometry _boxGeometry, _residentBoxes, _capsuleGeometry, _faceGeometry;
    private readonly DeviceCollisionDetection _detection;
    private readonly DeviceContactMatches<DeviceAvbdContactState> _matching;
    private readonly ContactStateInitialization<DeviceAvbdContactState> _stateInitialization;
    private readonly BufferUav _stateView;
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
                Add(world.Create(new DynamicCapsule(new(arm[segment], default), new(arm[segment + 1], default), .05f)));
            for (int column = 0; column < 3; column++)
                for (int row = 0; row < 3; row++)
                {
                    Entity a = cloth[character * 16 + column * 4 + row], b = cloth[character * 16 + (column + 1) * 4 + row];
                    Entity c = cloth[character * 16 + column * 4 + row + 1], d = cloth[character * 16 + (column + 1) * 4 + row + 1];
                    Add(world.Create(new DynamicFace(new(a, default), new(b, default), new(c, default), .01f)));
                    Add(world.Create(new DynamicFace(new(b, default), new(d, default), new(c, default), .01f)));
                }
        }
        foreach (Entity box in boxes) Add(box);
        Query shapes = world.Query(new QueryDescription().With<CrowdCollisionGeometry>());
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
            new(pairCapacity, 4, Math.Min(pairCapacity, 4096)) { Contacts = contactCapacity, Distances = false }, new(1e-5f, 32, 64), .02f,
            DeviceInputTransfer.Resident, DeviceCollisionReadback.None));
        Manifolds = Keep(new DeviceContactManifolds<DeviceAvbdContactState>(backend, device,
            new(pairCapacity, contactCapacity, contactCapacity * 6), new()
            { NormalPenalty = 1000, TangentPenalty = 1000, FrictionRegularizationSpeed = .01f }));
        _matching = Keep(new DeviceContactMatches<DeviceAvbdContactState>(backend, device, Manifolds, _detection, bodies,
            shaders.Matching(), new(.02f, .99f, true), new() { NormalStiffness = float.PositiveInfinity, StaticFriction = .3f, DynamicFriction = .2f },
            Math.Max(257, bodies.Capacity * 4 + 1)));
        _stateView = Keep(backend.CreateBufferUav(device, new(Manifolds.Current.States, BufferRange.Whole, StructureStride: 64)));
        _stateInitialization = Keep(new ContactStateInitialization<DeviceAvbdContactState>(backend, device, _matching, shaders.ContactStateInitialization()));

        void Add(Entity entity)
        {
            world.Add(entity, new CrowdCollisionGeometry());
            world.Add(entity, new DeviceCollisionBody(_grid, geometryIndex));
            world.Add(entity, new DeviceGeometryBinding(_geometry, geometryIndex++));
            world.Add(entity, new ContactMaterial { NormalStiffness = float.PositiveInfinity, StaticFriction = .3f, DynamicFriction = .2f });
        }
    }

    internal DeviceContactManifolds<DeviceAvbdContactState> Manifolds { get; }
    internal PrepareCrowdContacts Detect() => new(this);
    internal JobResourceAccess[] GetReadAccesses(Scheduler scheduler) => Manifolds.Current.GetReadAccesses(scheduler);
    internal void AddBindings(Dictionary<string, ResourceBinding[]> bindings)
    {
        Manifolds.Current.AddBindings(bindings);
        bindings["DeviceAvbdContactStates"] = [ResourceBinding.WritableBuffer(_stateView)];
    }
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
        dependency = _detection.Detect().Schedule(in context, dependency);
        dependency = _matching.Match().Schedule(in context, dependency);
        return _stateInitialization.Initialize().Schedule(in context, dependency);
    }
    private T Keep<T>(T resource) where T : IDisposable { _resources.Add(resource); return resource; }
    public void Dispose()
    {
        for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose();
    }
}
internal readonly struct PrepareCrowdContacts(DeviceCrowdContacts contacts) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => contacts.Schedule(in context, dependency);
}
internal readonly struct CrowdCollisionGeometry : IComponent;
