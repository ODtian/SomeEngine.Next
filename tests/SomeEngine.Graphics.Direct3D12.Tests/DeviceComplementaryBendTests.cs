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
    public void DeviceContactAndInternalBendShareTheirBodyForceAndCompletedStep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new() { WorkerCount = 2 });
        using World world = new(execution:new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 3);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity firstAnchor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = CreateBody(world, bodies, equations, 1, Vector3.UnitX, BodyMotion.Dynamic);
        Entity lastAnchor = CreateBody(world, bodies, equations, 2, new(1, 1, 0), BodyMotion.Static);
        Vector3 normal = Vector3.Normalize(new Vector3(1, -1, 0));
        using var source=CreateAvbdContactValues(jobs,firstAnchor,moving,new() { Normal=normal,Separation=-.1f,BodyAnchorDelta=Vector3.UnitX,FirstBodyCount=1 },new() { NormalStiffness=10 },
            [new(firstAnchor,Vector3.Zero,1),new(moving,Vector3.Zero,1)],new() { NormalPenalty=90 });
        using var contacts=new DeviceContactManifolds<DeviceAvbdContactState>(backend,device,new(1,1,2),default);
        using var writeContacts=new DeviceContactInputs<DeviceAvbdContactState>(backend,device,world,source,contacts);
        using var bends = new DeviceConstraintData<DeviceBendConstraint>(backend, device, 1, "DeviceBends");
        using var multipliers = new DeviceConstraintState<float>(backend, device, 1, "DeviceBendMultipliers");
        Entity bend = ConstraintBodies.Create(world, new XpbdBend { MinimumCosine = .5f, Compliance = .1f },
            new[] { firstAnchor, moving, lastAnchor });
        world.Add(bend, new DeviceConstraintBinding<DeviceBendConstraint>(bends, 0));
        world.Add(bend, new DeviceXpbdState<float>(multipliers));
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().With<DeviceAvbdEquationBinding>());
        Query bendRows = world.Query(new QueryDescription().WithStored<XpbdBend>()
            .WithStored<DeviceConstraintBinding<DeviceBendConstraint>>().With<DeviceXpbdState<float>>());
        using var writeBends = bends.WriteInputs<XpbdBend, DeviceBendSource>(world, bendRows, default, 64);
        using var selectedBends = new DeviceConstraintSelection<XpbdBend, DeviceBendConstraint, DeviceXpbdStateRows<float>>(
            backend, device, world, bendRows, bends, new(multipliers), "DeviceBendsSelection");
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        contacts.Current.AddBindings(bindings);
        bends.AddBindings(bindings); multipliers.AddBindings(bindings);
        JobResourceAccess[] reads = [.. bends.GetReadAccesses(jobs)];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactBendGraph(), new(2, 3),
            bindings, reads, 64, new IDeviceConstraintSelection[] { selectedBends },contacts:contacts);
        JobResourceAccess[] solveAccesses = [.. reads, multipliers.GetReadAccess(jobs)];
        using var avbd = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactBendAvbd(), new(1, 1, 0, 0),
            bindings, solveAccesses, 64,
            Array.Empty<IDeviceConstraintSelection>());
        using var xpbd = new DeviceXpbdProjection<DeviceBendConstraint, float>(backend, device, world, bendRows,
            bodies, dynamics, bends, multipliers, CompileContactBendProjection(), 64);
        using var actions = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64, Array.Empty<IDeviceConstraintSelection>());
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend,device,jobs);
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs())
            .Then(writeContacts.Upload())
            .Then(writeBends).Then(bends.UploadInputs())
            .Then(dynamics.Predict(Vector3.Zero)).Then(actions.Initialize()).Then(avbd.Initialize()).Then(xpbd.Initialize())
            .Then(new AlternateContactAndBend(avbd.Iterate(1), xpbd.Iterate(1), 64)).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(contactResults.Readback(contacts));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(7, 1, .1).Complete();

        // For p=(1-t,t), D=1-2t+2t²: 110t=10(.5-2t(1-t)/D)(1-2t)/D²-1/sqrt(2).
        // Its root .035129714 balances the inertial step, 10 N/m contact and .1 bend compliance.
        Assert.InRange(Vector3.Distance(new(.9648703f, .0351297f, 0), world.ReadComponentData<BodyPose>(moving).Position), 0, .0001f);
        Assert.InRange(Vector3.Distance(new(-.3512971f, .3512971f, 0), world.ReadComponentData<BodyVelocity>(moving).Linear), 0, .001f);
        Assert.InRange(Vector3.Distance(new(1.058404f, -1.058404f, 0), contactResults.Contacts.OpenView().Responses[0].ForceOnSecond), 0, .001f);
        Assert.Equal(7, world.ReadComponentData<BodyStepState>(moving).Number); Assert.Equal(1.1, world.ReadComponentData<BodyStepState>(moving).Time, 8);
        Assert.Equal(Vector3.Zero, world.ReadComponentData<BodyPose>(firstAnchor).Position); Assert.Equal(new(1, 1, 0), world.ReadComponentData<BodyPose>(lastAnchor).Position);
        }

    private DeviceGraphKernels CompileContactBendGraph() => DeviceGraphTestKernels.CompileParallel(shaderCompiler, "contact_bend_graph", """
        #include "physics_contact_data.slang"
        #include "physics_bend_graph.slang"
        [shader("compute")][numthreads(64,1,1)]
        void WriteRows(uint3 id:SV_DispatchThreadID)
        { DeviceConstraintBodyGroups<DeviceContactBodies,DeviceBendBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
        """, ShaderDirectory());

    private AvbdGraphKernels CompileContactBendAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_bend_avbd", """
            #include "physics_avbd_contact.slang"
            #include "physics_xpbd_bend_forces.slang"
            typedef DeviceAvbdConstraintGroups<DeviceContactAvbdConstraints,DeviceXpbdBendForces> Rows;
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID) { Rows rows; initializeDeviceAvbd(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id:SV_DispatchThreadID) { Rows rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id:SV_DispatchThreadID) { Rows rows; updateDeviceAvbdDuals(id.x,rows); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2));
    }
    private XpbdGraphKernels CompileContactBendProjection()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_bend_projection", """
            #include "physics_xpbd_bend.slang"
            #include "physics_xpbd.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id:SV_DispatchThreadID) { initializeDeviceXpbd<DeviceXpbdBend>(id.x); }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 id:SV_DispatchThreadID) { projectDeviceXpbd<DeviceXpbdBend>(id.x); }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private readonly struct AlternateContactAndBend(IterateAvbdGraph contacts, IterateDeviceXpbd<DeviceBendConstraint, float> bends, int iterations) : IPhysicsStage
    {
        public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency)
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            { dependency = contacts.Schedule(in context, dependency); dependency = bends.Schedule(in context, dependency); }
            return dependency;
        }
    }
}
