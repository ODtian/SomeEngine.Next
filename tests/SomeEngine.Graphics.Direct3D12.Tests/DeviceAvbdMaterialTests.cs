using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceAvbdTests
{
    [Theory]
    [InlineData(100, 10, 0, 0, .9666667f, 0, 0)]
    [InlineData(1e6f, 0, .2f, .2f, 1, 0, 0)]
    [InlineData(1e6f, 0, 3, 4, 1, 2.88f, 3.84f)]
    public void DeviceContactMaterialProducesItsNormalAndTangentialResponse(float stiffness, float damping,
        float initialX, float initialZ, float height, float velocityX, float velocityZ)
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using World world = new(execution: new WorldJobs(jobs));
        using var bodies = new DeviceBodies(backend, device, 2);
        using var equations = new DeviceAvbdBodyEquations(backend, device, bodies);
        var floor = CreateBody(world, bodies, equations, 0, Vector3.Zero, BodyMotion.Static);
        var moving = CreateBody(world, bodies, equations, 1, Vector3.UnitY, BodyMotion.Dynamic);
        world.Set(moving, new BodyVelocity { Linear = new(initialX, 0, initialZ) });
        using var source = CreateAvbdContactValues(jobs, floor, moving,
            new() { Normal = Vector3.UnitY, BodyAnchorDelta = Vector3.UnitY, FirstBodyCount = 1 },
            new() { NormalStiffness = stiffness, NormalDamping = damping, StaticFriction = .3f, DynamicFriction = .2f },
            [new(floor, Vector3.Zero, 1), new(moving, Vector3.Zero, 1)],
            new() { NormalPenalty = stiffness, TangentPenalty = 100, FrictionRegularizationSpeed = .01f });
        using var contacts = new DeviceContactManifolds<DeviceAvbdContactState>(backend, device, new(1, 1, 2), default);
        using var input = new DeviceContactInputs<DeviceAvbdContactState>(backend, device, world, source, contacts);
        Query members = world.Query(new QueryDescription().WithStored<DeviceBodyBinding>());
        using var dynamics = new BodyDynamicsCommands(backend, device, world, members, bodies, CompileDynamics(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        using var contactResults = new DeviceContactResults(backend, device, jobs);
        var bindings = new Dictionary<string, ResourceBinding[]>(); contacts.Current.AddBindings(bindings);
        JobResourceAccess[] reads = [];
        using var graph = new DeviceConstraintGraph(backend, device, bodies, dynamics, CompileContactGraph(), new(1, 2), bindings, reads, 64, contacts: contacts);
        using var solver = new AvbdGraphCommands(backend, device, bodies, equations, graph, CompileContactAvbd(), new(32, 1, 0, 0), bindings, reads, 64);
        using var response = new DeviceContactActions(backend, device, bodies, graph, contacts, CompileContactActions(), bindings, reads, 64, Array.Empty<IDeviceConstraintSelection>());
        var step = input.Upload()
            .Then(bodies.WriteInputs(64)).Then(bodies.UploadInputs()).Then(dynamics.Predict(new(0, -10, 0))).Then(response.Initialize())
            .Then(solver.Solve()).Then(dynamics.UpdateVelocities()).Then(results.Readback(64)).Then(contactResults.Readback(contacts));

        PhysicsSimulation.Create(world, world.Query(new QueryDescription()), step, 1).Schedule(0, 0, .1).Complete();

        Assert.InRange(MathF.Abs(world.ReadComponentData<BodyPose>(moving).Position.Y - height), 0, 2e-5f);
        Assert.InRange(Vector2.Distance(new(velocityX, velocityZ), new(world.ReadComponentData<BodyVelocity>(moving).Linear.X, world.ReadComponentData<BodyVelocity>(moving).Linear.Z)), 0, 1e-3f);
        Assert.InRange(Vector2.Distance(new((velocityX - initialX) / .1f, (velocityZ - initialZ) / .1f),
            new(contactResults.Contacts.OpenView().Responses[0].ForceOnSecond.X, contactResults.Contacts.OpenView().Responses[0].ForceOnSecond.Z)), 0, .01f);
    }
}
