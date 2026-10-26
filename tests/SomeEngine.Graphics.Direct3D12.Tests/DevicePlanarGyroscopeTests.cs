using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed partial class DeviceXpbdTests
{
    [Fact]
    public void APlanarDeviceBodyKeepsItsAngularMotionThroughTheGyroscopicStep()
    {
        using var backend = D3D12GraphicsBackend.Create();
        using Device device = CreateDevice(backend);
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0 });
        using World world = WorldJobs.CreateWorld(scheduler: jobs);
        using var bodies = new DeviceBodies(backend, device, 2);
        var body = AddBody(world, bodies, 1, Vector3.Zero, BodyMotion.Dynamic);
        world.Replace(body, new BodyMass { Motion = BodyMotion.Dynamic, InverseMass = 1,
            InverseInertia = new Matrix4x4 { M33 = .5f, M44 = 1 } });
        world.Replace(body, new BodyVelocity { Angular = new(0, 0, 3) });
        QueryHandle members = world.Query(world.QueryDefinition().Read<DeviceBodyBinding>());
        using var dynamics = new DeviceBodyDynamics(backend, device, world, members, bodies, CompileBodyGyroscope(), 64);
        using var results = new DeviceBodyResults(backend, device, bodies);
        var stages = bodies.WriteInputs(64).Then(bodies.UploadInputs()).Then(dynamics.ApplyGyroscopicTorque())
            .Then(dynamics.Predict(Vector3.Zero)).Then(dynamics.UpdateVelocities()).Then(results.Readback(64));

        PhysicsSimulation.Create(world, QueryDefinition.Empty, stages, 1).Schedule(0, 0, .1).Complete();

        Near(Vector3.Zero, world.Read<BodyPose>(body).Position);
        Near(new(0, 0, 3), world.Read<BodyVelocity>(body).Angular);
        Assert.InRange(MathF.Abs(Quaternion.Dot(new(0, 0, .14943813f, .9887711f), world.Read<BodyPose>(body).Rotation)), .999999f, 1.000001f);
        world.ReleaseQuery(members);
    }
}
