using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace SomeEngine.Runtime.Tests;

public sealed class PhysicsMixedSiloTests
{
    [Fact]
    public void MixedSiloDropsPilesAndDrainsThroughTheCompletedSceneFrame()
    {
        using var backend = D3D12GraphicsBackend.Create();
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(new(AdapterPreference.HighPerformance, IncludeSoftware: false), adapters, out _);
        using Device device = backend.CreateDevice(new(adapters.First(adapter => adapter.HardwareAccelerated).Id,
            [new(QueueType.Compute)]));
        using var scheduler = new Scheduler(new() { WorkerCount = 2 });
        using World world = new World(execution: new WorldJobs(scheduler));
        PhysicsSceneExample example = PhysicsSceneExample.CreateSiloDevice(24, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world, readContacts: true);
        Entity[] particles = scene.Bodies.ToArray().Where(body => world.ReadComponentData<BodyMass>(body).Motion == BodyMotion.Dynamic).ToArray();
        Assert.Equal(24, particles.Length);
        Assert.Equal(8, particles.Count(world.Has<SphereGeometry>));
        Assert.Equal(8, particles.Count(world.Has<BoxGeometry>));
        Assert.Equal(8, particles.Count(world.Has<ConvexHullGeometry>));
        float[] released = particles.Select(body => world.ReadComponentData<BodyPose>(body).Position.Z).ToArray();
        double initialEnergy = MechanicalEnergy();
        for (int number = 0; number < 180; number++) Advance(number);
        var touching = ContactBodies().SelectMany(bodies => bodies).ToHashSet();
        for (int index = 0; index < particles.Length; index++)
        {
            Vector3 position = world.ReadComponentData<BodyPose>(particles[index]).Position;
            Assert.InRange(position.Z, .1f, released[index] - .1f);
            Assert.InRange(MathF.Abs(position.X), 0, 3);
            Assert.InRange(MathF.Abs(position.Y), 0, 3);
            Assert.Contains(particles[index], touching);
        }
        Assert.InRange(MechanicalEnergy(), 0, initialEnergy);

        Vector3[] previous = particles.Select(body => world.ReadComponentData<BodyPose>(body).Position).ToArray();
        var throughOutlet = new HashSet<Entity>();
        for (int number = 180; number < example.StepCount; number++)
        {
            Advance(number);
            for (int index = 0; index < particles.Length; index++)
            {
                Vector3 position = world.ReadComponentData<BodyPose>(particles[index]).Position;
                if (previous[index].Z >= 0 && position.Z < 0)
                {
                    Vector3 crossing = Vector3.Lerp(previous[index], position, previous[index].Z / (previous[index].Z - position.Z));
                    Assert.InRange(MathF.Abs(crossing.X), 0, 2);
                    Assert.InRange(MathF.Abs(crossing.Y), 0, 2);
                    throughOutlet.Add(particles[index]);
                }
                previous[index] = position;
            }
        }
        Assert.InRange(world.ReadComponentData<BodyPose>(scene.Bodies.Span[0]).Position.X, 4, float.MaxValue);
        var drained = throughOutlet.Where(body => world.ReadComponentData<BodyPose>(body).Position.Z < -2).ToHashSet();
        Assert.Contains(drained, world.Has<SphereGeometry>);
        Assert.Contains(drained, world.Has<BoxGeometry>);
        Assert.Contains(drained, world.Has<ConvexHullGeometry>);
        foreach (Entity[] participants in ContactBodies())
            if (participants.Any(drained.Contains))
                Assert.DoesNotContain(participants, body => world.ReadComponentData<BodyMass>(body).Motion == BodyMotion.Static);
        

        void Advance(int number)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            Assert.Equal(number, scene.CompleteForRead().Number);
        }

        List<Entity[]> ContactBodies()
        {
            ContactView view = scene.Contacts!.OpenView();
            var values = new List<Entity[]>();
            for (int point = 0; point < view.PointCount; point++)
            {
                ReadOnlySpan<ContactBodyLink> links = view.BodyLinksOf(point);
                var bodies = new Entity[links.Length];
                for (int index = 0; index < links.Length; index++) bodies[index] = links[index].Body;
                values.Add(bodies);
            }
            return values;
        }

        double MechanicalEnergy()
        {
            double energy = 0;
            foreach (Entity body in particles)
            {
                BodyMass mass = world.ReadComponentData<BodyMass>(body);
                BodyPose pose = world.ReadComponentData<BodyPose>(body);
                BodyVelocity velocity = world.ReadComponentData<BodyVelocity>(body);
                Vector3 spin = Vector3.Transform(velocity.Angular, Quaternion.Conjugate(pose.Rotation));
                energy += .5 / mass.InverseMass * velocity.Linear.LengthSquared()
                    + .5 * (spin.X * spin.X / mass.InverseInertia.M11 + spin.Y * spin.Y / mass.InverseInertia.M22 + spin.Z * spin.Z / mass.InverseInertia.M33)
                    + 10 / mass.InverseMass * (pose.GetCenterOfMass(in mass).Z + 2);
            }
            return energy;
        }
    }
}
