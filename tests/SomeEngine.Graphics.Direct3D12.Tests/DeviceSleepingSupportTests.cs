using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{

    [Fact]
    public void ASleepingDeviceSupportStackKeepsItsLoadInCompletedContactEvents()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new(execution:new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 4);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity lower = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        Entity upper = AddBody(world, bodies, 2, new(0, 3, 0), BodyMotion.Dynamic);
        Entity falling = AddBody(world, bodies, 3, new(5, 10, 0), BodyMotion.Dynamic);
        Entity[] dynamicBodies = [lower, upper, falling];
        for (int index = 0; index < dynamicBodies.Length; index++)
        {
            world.Add(dynamicBodies[index], new DeviceAvbdEquationBinding(equations, index + 1));
            world.Add(dynamicBodies[index], new BodyActivity { Radius = 1 });
        }
        foreach (Entity body in new[] { lower, upper })
        {
            world.Set(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1,
                CenterOfMass = new(.25f, 0, .2f), InverseInertia = Matrix4x4.Identity });
            world.Add(body, new LongRangeBodySupport());
        }
        using var source=new ContactManifolds<DeviceAvbdContactState>(jobs,default);
        source.Resize(2,8,16);
        using var contacts=new DeviceContactManifolds<DeviceAvbdContactState>(backend,device,new(2,8,16) { SupportStates=true },default);
        Query supportBodies=world.Query(new QueryDescription().With<LongRangeBodySupport>());
        var graphCapacity = new DeviceGraphCapacity(12, 2);
        using var supports = new DeviceContactSupports(backend, device, world, supportBodies,bodies,contacts,
            new(Vector3.UnitY, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .5f, .99f, 1e-6f), graphCapacity);
        world.Add(lower, new DeviceSupportBodyBinding(supports)); world.Add(upper, new DeviceSupportBodyBinding(supports));
        world.Add(lower,new ReportContacts()); world.Add(upper,new ReportContacts());
        int row=0;
        for(int layer=0;layer<2;layer++)
        {
            Entity firstBody=layer==0?floor:lower,secondBody=layer==0?lower:upper;
            source.Columns.Pairs[layer]=new(firstBody,secondBody,0,0,layer*4,4);
            foreach(Vector2 corner in new[] {new Vector2(-1,-1),new Vector2(-1,1),new Vector2(1,-1),new Vector2(1,1)})
            {
                ContactPoint point=new() { PairIndex=layer,Normal=Vector3.UnitY,FirstBodyCount=1,WarmStart=true,
                    WorldPoint=new(corner.X,layer*2,corner.Y) };
                source.Columns.SetPoint(row,in point); source.Columns.BodyStarts[row]=row*2; source.Columns.BodyCounts[row]=2;
                source.Columns.BodyLinks[row*2]=new(firstBody,new(corner.X,layer==0?0:1,corner.Y),1);
                source.Columns.BodyLinks[row*2+1]=new(secondBody,new(corner.X,-1,corner.Y),1);
                source.Columns.Materials[row]=new() { NormalStiffness=float.PositiveInfinity,StaticFriction=1,DynamicFriction=.5f };
                source.States[row]=new() { NormalPenalty=100,TangentPenalty=100,FrictionRegularizationSpeed=.01f };
                row++;
            }
        }
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        using var activities = new DeviceBodyActivities(backend, device, world, members, bodies);
        foreach (Entity body in dynamicBodies) world.Add(body, new DeviceBodyActivityBinding(activities));
        var bindings = new Dictionary<string, ResourceBinding[]>(); activities.AddBindings(bindings);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileActiveBodyDynamics(), 64,
            bindings, activities.GetWriteAccesses(jobs));
        using var bodyInputs = new DeviceBodyInputs(world, members, bodies);
        using var input=new DeviceContactInputs<DeviceAvbdContactState>(backend,device,world,source,contacts);
        contacts.Current.AddBindings(bindings); supports.AddBindings(bindings);
        JobResourceAccess[] reads=[..supports.GetReadAccesses(jobs),..activities.GetReadAccesses(jobs)];
        using var graph=new DeviceConstraintGraph(backend,device,bodies,dynamics,CompileSleepingSupportGraph(),graphCapacity,bindings,reads,64,
            new IDeviceConstraintSelection[] {supports},contacts:contacts);
        using var solve=new AvbdGraphCommands(backend,device,bodies,equations,graph,CompileSleepingSupportAvbd(),new(1,1,.5f,0),bindings,
            supports.GetWriteAccesses(jobs),64);
        using var actions=new DeviceContactActions(backend,device,bodies,graph,contacts,CompileSleepingContactActions(),bindings,reads,64,
            Array.Empty<IDeviceConstraintSelection>());
        using var activity = new DevicePhysicsActivity(backend, device, activities, graph, CompileSleepingSupportActivity(),
            new(.05f, .2f, .01f, .01f), bindings, reads, 64);
        using var samples=new DeviceContactSamples(backend,device,bodies,contacts,CompileSleepingContactSamples());
        using var common=new DeviceContactResults(backend,device,jobs);
        using var output=new PhysicsEventBuffer(world,64);
        using var observation=new DeviceContactEvents(world,samples,common.Contacts,output,.001f);
        var received=new List<(long Number,PhysicsEvent Event)>();
        using var consumer=new RecordDeviceActivityEvents(jobs,output.Watch(),received);
        var stages=bodyInputs.Upload().Then(supports.InitializeBuffers()).Then(activity.UploadInputs())
            .Then(dynamics.UpdateSelection()).Then(activity.WakeConnected()).Then(dynamics.Predict(new(0,-10,0)))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(activity.Finish()).Then(activity.Readback(output))
            .Then(samples.Readback()).Then(common.Readback(contacts)).Then(new ObserveSleepingSupport(observation)).Then(output.Emit());
        var context=new PhysicsStepContext(world,members,0,0,1,0,.1);
        input.Upload().Schedule(in context,default).Complete();
        using var frame=PhysicsSimulation.Create(world,members,stages,1).CreateFrame(consumer,.1,0,0,0);

        frame.AdvanceTime(.1); frame.CompleteForRead();
        frame.AdvanceTime(.1); frame.CompleteForRead();
        Assert.True(world.ReadComponentData<BodyActivity>(lower).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(upper).Sleeping);
        frame.AdvanceTime(.1); frame.CompleteForRead();

        Assert.True(world.ReadComponentData<BodyActivity>(lower).Sleeping); Assert.True(world.ReadComponentData<BodyActivity>(upper).Sleeping);
        Assert.False(world.ReadComponentData<BodyActivity>(falling).Sleeping);
        for (long number = 0; number < 3; number++)
        {
            var floorContacts=received.Where(item=>item.Number==number&&item.Event.Kind==PhysicsEventKind.Contact&&item.Event.First==floor&&item.Event.Second==lower).ToArray();
            Assert.Equal(4,floorContacts.Sum(item=>item.Event.Occurrences));
            Assert.InRange(floorContacts.Sum(item => item.Event.Force.Y), 19.98f, 20.02f);
            Assert.All(floorContacts, item => Assert.Equal(number == 0 ? PhysicsEventPhase.Begin : PhysicsEventPhase.Stay, item.Event.Phase));
        }
        }

    private readonly struct ObserveSleepingSupport(DeviceContactEvents events):IPhysicsStage
    { public JobHandle Schedule(in PhysicsStepContext context,JobHandle dependency)=>events.Schedule(in context,dependency); }

    private DeviceGraphKernels CompileSleepingSupportGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "sleeping_support_graph", """
        #include "physics_body_activity_types.slang"
        #define DEVICE_GRAPH_BODY_ACTIVE(body) deviceBodyActivityAwake(body)
        #include "physics_contact_data.slang"
        #include "physics_contact_support_bodies.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceConstraintBodyGroups<DeviceContactBodies,DeviceSupportBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private AvbdGraphKernels CompileSleepingSupportAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_support_avbd", """
            #include "physics_avbd_contact_support.slang"
            #include "physics_awake_avbd.slang"
            typedef DeviceAvbdConstraintGroups<DeviceSupportedContactAvbdConstraints,DeviceContactSupportAvbdConstraints> SupportRows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeAvbdConstraints<SupportRows> rows; DeviceAwakeBodies bodies; initializeDeviceAvbd(id.x,rows,bodies); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id:SV_DispatchThreadID)
            { SupportRows rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void Duals(uint3 id:SV_DispatchThreadID)
            { SupportRows rows; updateDeviceAvbdDuals(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void InitializeGroups(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeBodies bodies; initializeDeviceContactSupports(AvbdStep,bodies); }
            [shader("compute")][numthreads(1,1,1)]
            void UpdateGroups(uint3 id:SV_DispatchThreadID)
            { DeviceAwakeBodies bodies; updateDeviceContactSupports(AvbdStep,bodies); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("Duals", SlangStage.Compute),
                new("InitializeGroups", SlangStage.Compute), new("UpdateGroups", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1), Entry(2), Entry(3), Entry(4));
    }

    private DeviceActivityKernels CompileSleepingSupportActivity()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("sleeping_support_activity", """
            #include "physics_contact_support_activity.slang"
            [shader("compute")][numthreads(64,1,1)]
            void ReadRows(uint3 id:SV_DispatchThreadID)
            { DeviceConstraintActivityGroups<DeviceSupportedContactActivity,DeviceSupportBodyActivity> rows; readDeviceActivityRows(id.x,rows); }
            """, [new("ApplyDeviceActivityInputs", SlangStage.Compute), new("ClearDeviceActivitySignals", SlangStage.Compute),
                new("CollectDeviceActivityBodies", SlangStage.Compute), new("ReadRows", SlangStage.Compute),
                new("WakeDeviceBodies", SlangStage.Compute), new("AdvanceDeviceBodyQuietTime", SlangStage.Compute),
                new("FinishDeviceBodyActivity", SlangStage.Compute),new("ReadRemovedDeviceActivityContacts",SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0),Entry(1),Entry(2),Entry(3),Entry(4),Entry(5),Entry(6),RemovedContacts:Entry(7));
    }
}
