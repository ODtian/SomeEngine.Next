using System.Numerics;
using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;
using SomeEngine.ECS.Systems;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Fact]
    public void DeviceContactSupportsKeepTheOffsetStackLevelAndTransferItsLoadToTheFloor()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 4);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        Entity floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        Entity lower = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        Entity upper = CreateBody(world, bodies, equations, 2, new(0, 3, 0), BodyMotion.Dynamic);
        Entity falling = CreateBody(world, bodies, equations, 3, new(5, 10, 0), BodyMotion.Dynamic);
        foreach (Entity body in new[] { lower, upper })
        {
            world.Set(body, new BodyMass
            {
                Motion = BodyMotion.Dynamic,
                InverseMass = 1,
                CenterOfMass = new(.25f, 0, .2f),
                InverseInertia = Matrix4x4.Identity
            });
            world.Add(body, new LongRangeBodySupport());
        }
        using var source = new ContactManifolds<DeviceAvbdContactState>(jobs, default);
        source.Resize(2, 8, 16);
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device,
            new(2, 8, 16) { SupportStates = true }, default);
        Query supportBodies = world.Query(new QueryDescription().With<LongRangeBodySupport>());
        var graphCapacity = new DeviceGraphCapacity(12, 2);
        using var supports = new DeviceContactSupports(backend, device, world, supportBodies, bodies, contacts,
            new(Vector3.UnitY, 1e-5f, 1e-4f, 1e-4f, 1e-4f, .99f, .99f, 1e-6f), graphCapacity);
        world.Add(lower, new DeviceSupportBodyBinding(supports)); world.Add(upper, new DeviceSupportBodyBinding(supports));
        int row = 0;
        for (int layer = 0; layer < 2; layer++)
        {
            Entity firstBody = layer == 0 ? floor : lower, secondBody = layer == 0 ? lower : upper;
            source.Columns.Pairs[layer] = new(firstBody, secondBody, 0, 0, layer * 4, 4);
            foreach (Vector2 corner in new[] { new Vector2(-1, -1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(1, 1) })
            {
                ContactPoint point = new() { PairIndex = layer, Normal = Vector3.UnitY, FirstBodyCount = 1, WorldPoint = new(corner.X, layer * 2, corner.Y) };
                source.Columns.SetPoint(row, in point); source.Columns.BodyStarts[row] = row * 2; source.Columns.BodyCounts[row] = 2;
                source.Columns.BodyLinks[row * 2] = new(firstBody, new(corner.X, layer == 0 ? 0 : 1, corner.Y), 1);
                source.Columns.BodyLinks[row * 2 + 1] = new(secondBody, new(corner.X, -1, corner.Y), 1);
                source.Columns.Materials[row] = new() { NormalStiffness = float.PositiveInfinity, StaticFriction = 1, DynamicFriction = .5f };
                source.States[row] = new() { NormalPenalty = 100, TangentPenalty = 100, FrictionRegularizationSpeed = .01f };
                row++;
            }
        }
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        using var input = new DeviceContactInputs<DeviceAvbdContactState>(backend, device, world, source, contacts);
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings); supports.AddBindings(bindings);
        JobResourceAccess[] reads = supports.GetReadAccesses(jobs);
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileSupportGraph(), graphCapacity, bindings, reads, 64,
            new IDeviceConstraintSelection[] { supports }, contacts: contacts);
        using var solver = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileSupportAvbd(), new(1, 1, 0, 0), bindings,
            supports.GetWriteAccesses(jobs), 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64,
            Array.Empty<IDeviceConstraintSelection>());
        var step = input.Upload().Then(supports.InitializeBuffers()).Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs())
            .Then(dynamics.Predict(new(0, -10, 0))).Then(response.Initialize()).Then(solver.Solve()).Then(dynamics.UpdateVelocities())
            .Then(results.Readback(64)).Then(contactResults.Readback(contacts));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), step, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(MathF.Abs(world.ReadComponentData<BodyPose>(lower).Position.Y - 1), 0, 1e-4f);
        Assert.InRange(MathF.Abs(world.ReadComponentData<BodyPose>(upper).Position.Y - 3), 0, 1e-4f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(lower).Angular.Length(), 0, 1e-4f);
        Assert.InRange(world.ReadComponentData<BodyVelocity>(upper).Angular.Length(), 0, 1e-4f);
        float load = 0;
        foreach (ContactResponse value in contactResults.Contacts.OpenView().Responses[..4]) load += value.ForceOnSecond.Y;
        Assert.InRange(load, 19.99f, 20.01f);
        Assert.InRange(world.ReadComponentData<BodyPose>(falling).Position.Y, 9.8999f, 9.9001f);
    }
    private DeviceGraphKernels CompileSupportGraph()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_support_graph", """
            #include "physics_contact_data.slang"
            #include "physics_contact_support_bodies.slang"
            [shader("compute")][numthreads(64,1,1)]
            void WriteRows(uint3 id : SV_DispatchThreadID)
            { DeviceConstraintBodyGroups<DeviceContactBodies,DeviceSupportBodies> rows; writeDeviceConstraintGraphRows(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void BuildGraph(uint3 id : SV_DispatchThreadID) { buildDeviceConstraintGraph(); }
            """, [new("WriteRows", SlangStage.Compute), new("BuildGraph", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1));
    }
    private AvbdGraphKernels CompileSupportAvbd()
    {
        D3D12TestShader shader = shaderCompiler.CompileWithSearchPaths("contact_support_avbd", """
            #include "physics_avbd_contact_support.slang"
            [shader("compute")][numthreads(64,1,1)]
            void Initialize(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceSupportedContactAvbdConstraints,DeviceContactSupportAvbdConstraints> rows; initializeDeviceAvbd(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateBody(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceSupportedContactAvbdConstraints,DeviceContactSupportAvbdConstraints> rows; updateDeviceAvbdBody(id.x,rows); }
            [shader("compute")][numthreads(64,1,1)]
            void UpdateDuals(uint3 id : SV_DispatchThreadID)
            { DeviceAvbdConstraintGroups<DeviceSupportedContactAvbdConstraints,DeviceContactSupportAvbdConstraints> rows; updateDeviceAvbdDuals(id.x,rows); }
            [shader("compute")][numthreads(1,1,1)]
            void InitializeGroups(uint3 id : SV_DispatchThreadID) { initializeDeviceContactSupports(AvbdStep); }
            [shader("compute")][numthreads(1,1,1)]
            void UpdateGroups(uint3 id : SV_DispatchThreadID) { updateDeviceContactSupports(AvbdStep); }
            """, [new("Initialize", SlangStage.Compute), new("UpdateBody", SlangStage.Compute), new("UpdateDuals", SlangStage.Compute),
                new("InitializeGroups", SlangStage.Compute), new("UpdateGroups", SlangStage.Compute)], "sm_6_0", ShaderDirectory());
        return new(Entry(shader, 0), Entry(shader, 1), Entry(shader, 2), Entry(shader, 3), Entry(shader, 4));
    }
}
