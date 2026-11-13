using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void DeviceMatchedContactKeepsItsFeaturesAndResidentForceThroughTheNextGeometryRefresh()
    {
        using var scene = new HistoryContactScene(this, false);
        scene.Step(0, true);
        var original = scene.PointKey();
        Assert.InRange(scene.World.ReadComponentData<BodyPose>(scene.Moving).Position.X, 1.66666f, 1.66668f);
        scene.Step(1, true);
        Assert.Equal(original, scene.PointKey());
        Assert.InRange(scene.World.ReadComponentData<BodyVelocity>(scene.Moving).Linear.X, 2.7776f, 2.7780f);
        Assert.InRange(scene.World.ReadComponentData<BodyPose>(scene.Moving).Position.X, 1.94443f, 1.94446f);
        Assert.InRange(scene.ContactForce.X, 11.110f, 11.113f);
        Assert.Equal(1, scene.World.ReadComponentData<BodyStepState>(scene.Moving).Number);
    }
    [Fact]
    public void DeviceMatchedContactKeepsItsResidentGeometryUntilTheBodyConsumerRuns()
    {
        using var scene = new HistoryContactScene(this, true);
        scene.Step(0, false);
        var original = scene.PointKey();
        scene.Step(1, false);
        Assert.Equal(new Vector3(1.5f, 0, 0), scene.World.ReadComponentData<BodyPose>(scene.Moving).Position);
        scene.ReadBodies(1);
        Assert.Equal(original, scene.PointKey());
        Assert.InRange(scene.World.ReadComponentData<BodyPose>(scene.Moving).Position.X, 1.94443f, 1.94446f);
        Assert.InRange(scene.World.ReadComponentData<BodyVelocity>(scene.Moving).Linear.X, 2.7776f, 2.7780f);
        Assert.InRange(scene.ContactForce.X, 11.110f, 11.113f);
        Assert.Equal(1, scene.World.ReadComponentData<BodyStepState>(scene.Moving).Number);
    }
    [Fact]
    public void DeviceGeometryReplacementStartsItsOwnContactForceAtTheReusedBodyIndex()
    {
        using var scene = new HistoryContactScene(this, true);
        scene.Step(0, false);
        var original = scene.PointKey();
        scene.ReplaceMoving();
        scene.Step(1, true);
        Assert.NotEqual(original, scene.PointKey());
        Assert.InRange(scene.World.ReadComponentData<BodyPose>(scene.Moving).Position.X, 1.66665f, 1.66669f);
        Assert.InRange(scene.World.ReadComponentData<BodyVelocity>(scene.Moving).Linear.X, 1.6665f, 1.6669f);
        Assert.InRange(scene.ContactForce.X, 16.665f, 16.668f);
        Assert.Equal(1, scene.World.ReadComponentData<BodyStepState>(scene.Moving).Number);
    }
    [Fact]
    public void DeviceContactSourceEditsKeepItsResidentGeometryAndForceHistory()
    {
        using var scene = new HistoryContactScene(this, false);
        scene.Step(0, false);
        var original = scene.PointKey();
        scene.World.Set(scene.Moving, new ContactMaterial { NormalStiffness = 300 });
        scene.Step(1, true);
        Assert.Equal(original, scene.PointKey());
        Assert.InRange(scene.World.ReadComponentData<BodyPose>(scene.Moving).Position.X, 1.96665f, 1.96669f);
        Assert.InRange(scene.World.ReadComponentData<BodyVelocity>(scene.Moving).Linear.X, 2.9998f, 3.0002f);
        Assert.InRange(scene.ContactForce.X, 13.331f, 13.336f);
        Assert.Equal(1, scene.World.ReadComponentData<BodyStepState>(scene.Moving).Number);
    }

    private sealed class HistoryContactScene : IDisposable
    {
        private readonly List<IDisposable> _resources = [];
        private readonly DeviceBodies _bodies;
        private readonly DeviceAvbdBodyEquations _equations;
        private readonly DeviceCollisionGrid _grid;
        private readonly DeviceCollisionGeometry _geometry;
        private readonly DeviceCollisionDetection _detection;
        private readonly DeviceContactManifolds<DeviceAvbdContactState> _contacts;
        private readonly DeviceContactMatches<DeviceAvbdContactState> _matches;
        private readonly ContactStateInitialization<DeviceAvbdContactState> _state;
        private readonly DeviceBodyInputs _bodyInputs;
        private readonly WriteDeviceGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>> _shapeInputs;
        private readonly UpdateDeviceGeometry _update;
        private readonly BodyDynamicsCommands _dynamics;
        private readonly AvbdGraphCommands _solver;
        private readonly DeviceContactActions _responses;
        private readonly DeviceBodyResults _bodyResults;
        private readonly DeviceContactResults _contactResults;
        private readonly RelationQuery<EntityPairsWithin, (Entity, Entity)> _pairs;
        private readonly Query _members;
        private JobHandle _completed;
        internal World World { get; }
        internal Entity Floor { get; }
        internal Entity Moving { get; private set; }
        internal Vector3 ContactForce => _contactResults.Contacts.OpenView().Responses[0].ForceOnSecond;
        internal HistoryContactScene(DeviceAvbdTests test, bool parallel)
        {
            var backend = Keep(D3D12GraphicsBackend.Create());
            Device device = Keep(CreateDevice(backend));
            var jobs = Keep(new Scheduler(new JobConfiguration { WorkerCount = 0 }));
            World = Keep(new World(execution: new WorldJobs(jobs)));
            _bodies = Keep(new DeviceBodies(backend, device, 2));
            _equations = Keep(new DeviceAvbdBodyEquations(backend, device, _bodies));
            _grid = Keep(new DeviceCollisionGrid(backend, device, test.CompileHistoryGrid(), new(1, 3), new(2, 256, 257, 4, 1)));
            _geometry = Keep(new DeviceCollisionGeometry(backend, device, new(2, 0, 0, 0), _bodies));
            Floor = CreateBody(World, _bodies, _equations, 0, Vector3.Zero, BodyMotion.Static);
            Moving = CreateBody(World, _bodies, _equations, 1, new(1.5f, 0, 0), BodyMotion.Dynamic);
            AddShape(Floor, 0); AddShape(Moving, 1);
            _members = World.Query(new QueryDescription().With<DeviceBodyBinding>());
            Query spheres = World.Query(new QueryDescription().With<SphereGeometry>());
            _pairs = World.Within(spheres);
            _detection = Keep(new DeviceCollisionDetection(backend, device, _grid, _geometry, test.CompileHistoryReferencedDetection(), 0,
                new(4, 4, 1) { Contacts = 4, BodiesPerContact = 2 }, new(1e-5f, 32, 64), 0, DeviceInputTransfer.Resident, DeviceCollisionReadback.None));
            _contacts = Keep(new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(4, 4, 8),
                new() { NormalPenalty = 100, TangentPenalty = 100 }));
            _matches = Keep(new DeviceContactMatches<DeviceAvbdContactState>(backend, device, _contacts, _detection, _bodies,
                test.CompileContactMatching(), new(.25f, .99f, true), new() { NormalStiffness = 100 }, 17));
            _state = Keep(new ContactStateInitialization<DeviceAvbdContactState>(backend, device, _matches, test.CompileHistoryStateInitialization()));
            _bodyInputs = Keep(new DeviceBodyInputs(World, _members, _bodies));
            _shapeInputs = Keep(_grid.WriteGeometryReferences<SphereGeometry, DeviceComponentSource<SphereGeometry, DeviceSphereReader>>(_pairs, default, 0, 64));
            _update = Keep(new UpdateDeviceGeometry(backend, device, _grid, _geometry, test.CompileHistoryGeometryUpdate(), 0, DeviceInputTransfer.Upload));
            _dynamics = Keep(new BodyDynamicsCommands(backend, device, World, _members, _bodies, test.CompileDynamics(), 64));
            var bindings = new Dictionary<string, ResourceBinding[]>(); _contacts.Current.AddBindings(bindings);
            var graph = Keep(new DeviceConstraintGraph(backend, device, _bodies, _dynamics, parallel ? test.CompileHistoryAvbdGraph() : test.CompileContactGraph(),
                new(4, 2), bindings, Array.Empty<JobResourceAccess>(), 64, contacts: _contacts));
            _solver = Keep(new AvbdGraphCommands(backend, device, _bodies, _equations, graph, test.CompileContactAvbd(), new(1, 1, 1, 0), bindings, Array.Empty<JobResourceAccess>(), 64));
            _responses = Keep(new DeviceContactActions(backend, device, _bodies, graph, _contacts, test.CompileContactActions(), bindings, Array.Empty<JobResourceAccess>(), 64, Array.Empty<IDeviceConstraintSelection>()));
            _bodyResults = Keep(new DeviceBodyResults(backend, device, _bodies));
            _contactResults = Keep(new DeviceContactResults(backend, device, jobs));
        }
        private void AddShape(Entity entity, int index)
        {
            using var structure = World.OpenStructureWrite();
            World.Add(entity, new SphereGeometry(1)); World.Add(entity, new ContactMaterial { NormalStiffness = 100 });
            World.Add(entity, new DeviceCollisionBody(_grid, index)); World.Add(entity, new DeviceGeometryBinding(_geometry, index));
        }
        internal void ReplaceMoving()
        {
            using var structure = World.OpenStructureWrite();
            World.Destroy(Moving);
            Moving = CreateBody(World, _bodies, _equations, 1, new(1.5f, 0, 0), BodyMotion.Dynamic); AddShape(Moving, 1);
        }
        internal void Translate(Vector3 translation)
        {
            foreach (Entity body in new[] { Floor, Moving })
            { BodyPose pose = World.ReadComponentData<BodyPose>(body); pose.Position += translation; World.Set(body, pose); }
        }
        internal void Step(long number, bool readBodies)
        {
            var context = new PhysicsStepContext(World, _members, number, 0, 1, number * .1, .1);
            var stages = _bodyInputs.Upload().Then(_grid.BeginInputs()).Then(_shapeInputs).Then(_update.Update())
                .Then(_grid.Build(_pairs, DevicePairRules.None, DeviceInputTransfer.Resident, DevicePairReadback.None))
                .Then(_detection.Detect()).Then(_matches.Match()).Then(_state.Initialize())
                .Then(_dynamics.Predict(Vector3.Zero)).Then(_responses.Initialize()).Then(_solver.Solve()).Then(_dynamics.UpdateVelocities());
            _completed = stages.Schedule(in context, _completed);
            if (readBodies) _completed = _bodyResults.Readback(64).Schedule(in context, _completed);
            _completed = _contactResults.Readback(_contacts).Schedule(in context, _completed);
            _completed.Complete();
        }
        internal void ReadBodies(long number)
        {
            var context = new PhysicsStepContext(World, _members, number, 0, 1, number * .1, .1);
            _completed = _bodyResults.Readback(64).Schedule(in context, _completed); _completed.Complete();
        }
        internal (ContactPair Pair, int FirstFeature, int SecondFeature, int PointIndex) PointKey()
        {
            ContactView view = _contactResults.Contacts.OpenView();
            Assert.Equal(1, view.PointCount);
            ContactPoint point = view.GetPoint(0);
            return (view.Pairs[point.PairIndex], point.FirstFeature, point.SecondFeature, point.PointIndex);
        }
        private T Keep<T>(T value) where T : IDisposable { _resources.Add(value); return value; }
        public void Dispose() { _completed.Complete(); for (int index = _resources.Count - 1; index >= 0; index--) _resources[index].Dispose(); }
    }

    private ComputePipelineDesc CompileHistoryStateInitialization()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_history_initialization", "#include \"physics_contact_state_initialization.slang\"",
            [new("InitializeContactStateRows", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }

    private DeviceGraphKernels CompileHistoryAvbdGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_history_parallel_graph", """
            #include "physics_contact_data.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id:SV_DispatchThreadID)
            { DeviceContactBodies rows; writeDeviceConstraintGraphRows(devicePhysicsThreadIndex(id,64),rows); }
            #include "physics_constraint_graph_parallel.slang"
            #include "physics_avbd_graph_colors.slang"
            """, [new("WriteRows", SlangStage.Compute), new("InitializeDeviceGraph", SlangStage.Compute),
            new("ConnectDeviceGraph", SlangStage.Compute), new("CompressDeviceGraph", SlangStage.Compute),
            new("ScanDeviceGraphDegrees", SlangStage.Compute), new("OffsetDeviceGraphBlocks", SlangStage.Compute),
            new("AddDeviceGraphDegreeOffsets", SlangStage.Compute), new("WriteDeviceGraphAdjacency", SlangStage.Compute),
            new("ResetDeviceGraphColorProgress", SlangStage.Compute), new("ProposeDeviceBodyColors", SlangStage.Compute),
            new("ResolveDeviceBodyColors", SlangStage.Compute), new("ProposeDeviceRowColors", SlangStage.Compute),
            new("ResolveDeviceRowColors", SlangStage.Compute), new("CountDeviceGraphColors", SlangStage.Compute),
            new("OffsetDeviceGraphGroups", SlangStage.Compute), new("CountDeviceGraphRows", SlangStage.Compute),
            new("OffsetDeviceGraphRanges", SlangStage.Compute), new("WriteDeviceGraphWork", SlangStage.Compute),
            new("PrepareAvbdGraphColors", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), new DeviceGraphParallelKernels(Entry(shader, 1), Entry(shader, 2), Entry(shader, 3), Entry(shader, 4),
            Entry(shader, 5), Entry(shader, 6), Entry(shader, 7), Entry(shader, 8), Entry(shader, 9), Entry(shader, 10),
            Entry(shader, 11), Entry(shader, 12), Entry(shader, 13), Entry(shader, 14), Entry(shader, 15), Entry(shader, 16),
            Entry(shader, 17))
        { PrepareColors = Entry(shader, 18) });
    }

    private DeviceGridKernels CompileHistoryGrid()
    {
        D3D12TestEntry[] entries = [new("ClearDeviceGrid", SlangStage.Compute), new("BuildDeviceGrid", SlangStage.Compute),
            new("CountDevicePairs", SlangStage.Compute), new("ScanDevicePairCounts", SlangStage.Compute),
            new("OffsetDevicePairBlocks", SlangStage.Compute), new("OffsetDevicePairBins", SlangStage.Compute), new("WriteDevicePairs", SlangStage.Compute)];
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_history_grid", "#include \"physics_grid.slang\"", entries, "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2), Entry(shader, 3), Entry(shader, 4), Entry(shader, 5), Entry(shader, 6));
    }
    private ComputePipelineDesc CompileHistoryDetection()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_history_detection", """
            #include "physics_collision.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Detect(uint3 id : SV_DispatchThreadID)
            { DeviceConvexCollision<DeviceSphere,DeviceSphere> kernel; detectDeviceCollision(id.x,kernel); }
            """, [new("Detect", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }
    private DeviceContactMatchKernels CompileContactMatching()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_history_matching", "#include \"physics_contact_matching.slang\"",
            [new("ClearDeviceContactMatches", SlangStage.Compute), new("IndexDeviceContactMatches", SlangStage.Compute),
                new("MatchDeviceContacts", SlangStage.Compute), new("ClaimContactInputs", SlangStage.Compute),
                new("CollectNewContactInputs", SlangStage.Compute), new("CollectRemovedContactRows", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        D3D12TestShader arguments = shaderCompiler.CompileWithSearchPaths("contact_dispatch", "#include \"physics_contact_geometry.slang\"",
            [new("WriteDeviceContactDispatchArguments", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2), Entry(shader, 3), Entry(shader, 4), Entry(shader, 5), CompileCurrentContactGeometry(), Entry(arguments, 0));
    }
    private ComputePipelineDesc CompileHistoryReferencedDetection()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("resident_contact_detection", """
            #include "physics_geometry_references.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Detect(uint3 id : SV_DispatchThreadID)
            { DeviceConvexCollision<DeviceSphere,DeviceSphere> kernel; detectDeviceReferencedCollision(id.x,kernel); }
            """, [new("Detect", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }
    private ComputePipelineDesc CompileHistoryGeometryUpdate()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("resident_contact_bounds", """
            #include "physics_geometry_update.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Update(uint3 id : SV_DispatchThreadID) { updateDeviceGeometry<DeviceSphereUpdate>(id.x); }
            """, [new("Update", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }
    private ComputePipelineDesc CompileCurrentContactGeometry()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("current_contact_geometry", "#include \"physics_contact_geometry.slang\"",
            [new("UpdateDeviceContactGeometry", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return Entry(shader, 0);
    }
}
