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

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void DeviceXpbdRestitutionUsesTheCompletedContactForceAndSelectedBodyVelocity()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Dynamic);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Add(moving, new SelectedXpbdBody());
        world.Set(moving, new BodyVelocity { Linear = new(0, -2, 0) });
        using var source = CreateContactValues<XpbdContactState>(jobs, floor, moving,
            new() { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 },
            new() { NormalStiffness = 100, Restitution = .5f, RestitutionThreshold = .1f },
            [new(floor, Vector3.Zero, 1), new(moving, Vector3.Zero, 1)]);
        using var contacts = new DeviceContactManifolds<XpbdContactState>(backend, device, new(1, 1, 2), default);
        using var write = new DeviceContactInputs<XpbdContactState>(backend, device, world, source, contacts);
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>().With<SelectedXpbdBody>());
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        var bindings = new Dictionary<string, ResourceBinding[]>();
        contacts.Current.AddBindings(bindings);
        JobResourceAccess[] inputs = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileXpbdContactGraph(),
            new(1, 2), bindings, inputs, 64, contacts: contacts);
        using var solve = new XpbdGraphCommands(backend, device, bodies, graph, CompileXpbdContact(),
            new(Iterations: 2, RowsPerBlock: 4), bindings, inputs, Array.Empty<SomeEngine.Graphics.Buffer>(), 64);
        using var actions = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileXpbdContactActions(), bindings, inputs, 64, Array.Empty<IDeviceConstraintSelection>());
        var stages = write.Upload()
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(Vector3.Zero))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities()).Then(actions.ApplyRestitution(1))
            .Then(results.Readback(64)).Then(contactResults.Readback(contacts));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(3, 1, .1).Complete();

        Near(new(0, .9f, 0), world.ReadComponentData<BodyPose>(moving).Position);
        Near(Vector3.UnitY, world.ReadComponentData<BodyVelocity>(moving).Linear);
        Near(Vector3.Zero, world.ReadComponentData<BodyPose>(floor).Position);
        Near(Vector3.Zero, world.ReadComponentData<BodyVelocity>(floor).Linear);
        Near(new(0, 10, 0), contactResults.Contacts.OpenView().Responses[0].ForceOnSecond);
        Assert.InRange(contactResults.Contacts.OpenView().Responses[0].RestitutionImpulse, 1.99999f, 2.00001f);
        Assert.Equal(1.1, contactResults.Contacts.OpenView().Responses[0].EndTime);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(.2f, 0, 0, -2)]
    [InlineData(1, .075f, .75f, -2.5f)]
    public void DeviceXpbdContactCombinesRepeatedBodyAnchorsAndReturnsThePhysicalForce(
        float tangentialSpeed, float expectedX, float expectedVelocity, float expectedForce)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 2 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        Entity floor = AddBody(world, bodies, 0, Vector3.Zero, BodyMotion.Static);
        Entity moving = AddBody(world, bodies, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Set(moving, new BodyVelocity { Linear = new(tangentialSpeed, 0, 0) });
        using var source = CreateContactValues<XpbdContactState>(jobs, floor, moving,
            new() { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 },
            new() { NormalStiffness = 100, StaticFriction = 1, DynamicFriction = .5f },
            [new(floor, Vector3.Zero, 1), new(moving, -Vector3.UnitX, .5f), new(moving, Vector3.UnitX, .5f)]);
        using var contacts = new DeviceContactManifolds<XpbdContactState>(backend, device, new(1, 1, 3), default);
        using var write = new DeviceContactInputs<XpbdContactState>(backend, device, world, source, contacts);
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileBodyDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings);
        JobResourceAccess[] inputs = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileXpbdContactGraph(),
            new(1, 3), bindings, inputs, 64, contacts: contacts);
        using var solve = new XpbdGraphCommands(backend, device, bodies, graph, CompileXpbdContact(),
            new(Iterations: 2, RowsPerBlock: 4), bindings, inputs, Array.Empty<SomeEngine.Graphics.Buffer>(), 64);
        using var actions = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileXpbdContactActions(), bindings, inputs, 64, Array.Empty<IDeviceConstraintSelection>());
        var stages = write.Upload()
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(new(0, -10, 0)))
            .Then(actions.Initialize()).Then(solve.Solve()).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(contactResults.Readback(contacts));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), stages, 1).Schedule(3, 1, .1).Complete();

        Near(new(expectedX, .95f, 0), world.ReadComponentData<BodyPose>(moving).Position);
        Near(new(expectedVelocity, -.5f, 0), world.ReadComponentData<BodyVelocity>(moving).Linear);
        Near(Vector3.Zero, world.ReadComponentData<BodyVelocity>(moving).Angular);
        Near(new(expectedForce, 5, 0), contactResults.Contacts.OpenView().Responses[0].ForceOnSecond);
        Assert.Equal(1.1, contactResults.Contacts.OpenView().Responses[0].EndTime);
    }

    private static ContactManifolds<TState> CreateContactValues<TState>(Scheduler jobs, Entity first, Entity second,
        ContactPoint point, ContactMaterial material, ReadOnlySpan<ContactBodyLink> links, TState defaults = default) where TState : unmanaged
    {
        var contacts = new ContactManifolds<TState>(jobs, defaults);
        contacts.Resize(1, 1, links.Length);
        contacts.Columns.Pairs[0] = new(first, second, 0, 0, 0, 1);
        contacts.Columns.SetPoint(0, in point); contacts.Columns.Materials[0] = material;
        contacts.Columns.BodyStarts[0] = 0; contacts.Columns.BodyCounts[0] = links.Length;
        links.CopyTo(contacts.Columns.BodyLinks); contacts.States[0] = defaults;
        return contacts;
    }

    private DeviceGraphKernels CompileXpbdContactGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_contact_graph", """
            #include "physics_contact_data.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID) { DeviceContactBodies rows; writeDeviceConstraintGraphRows(id.x, rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private XpbdGraphKernels CompileXpbdContact()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_contact", """
            #include "physics_xpbd_graph.slang"
            #include "physics_xpbd_contact.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            {
                DeviceXpbdConstraintRows<DeviceContactBodies, DeviceXpbdContactProjection<DeviceGraphBodies>> rows;
                initializeDeviceXpbdGraph(group.x, thread.x, rows);
            }
            [shader("compute")][numthreads(64,1,1)]
            void Project(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
            {
                DeviceXpbdConstraintRows<DeviceContactBodies, DeviceXpbdContactProjection<DeviceGraphBodies>> rows;
                projectDeviceXpbdGraph(group.x, thread.x, rows);
            }
            """, [new("Initialize", SlangStage.Compute), new("Project", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
    private DeviceContactKernels CompileXpbdContactActions()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("xpbd_contact_actions", """
            #include "physics_contact_actions.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID) { DeviceContactResponseActions<DeviceGraphBodies> actions; initializeDeviceContactResponses(id.x, actions); }
            [shader("compute")][numthreads(64,1,1)]
            void Restore(uint3 id : SV_DispatchThreadID) { DeviceContactResponseActions<DeviceGraphBodies> actions; applyDeviceContactRestitution(id.x, actions); }
            """, [new("Initialize", SlangStage.Compute), new("Restore", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        ComputePipelineDesc Entry(int index) => new(shader.SlangCode, shader.Reflection, shader.GetEntryPoint(index), shader.GetEntryCode(index));
        return new(Entry(0), Entry(1));
    }
}
