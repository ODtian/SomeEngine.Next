using System.Numerics;
using DefaultGame;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
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
        using World world = WorldJobs.CreateWorld(scheduler: scheduler);
        PhysicsSceneExample example = PhysicsSceneExample.CreateSiloDevice(24, backend, device);
        using PhysicsSceneSimulation scene = example.CreateSimulation(world);
        Entity[] particles = scene.Bodies.ToArray().Where(body => world.Read<BodyMass>(body).Motion == BodyMotion.Dynamic).ToArray();
        Assert.Equal(24, particles.Length);
        Assert.Equal(8, particles.Count(world.Has<SphereGeometry>));
        Assert.Equal(8, particles.Count(world.Has<BoxGeometry>));
        Assert.Equal(8, particles.Count(world.Has<ConvexHullGeometry>));
        QueryHandle contacts = world.Query(new QueryDefinitionBuilder().Read<ContactPoint>().ReadBuffer<ContactBodyLink>());
        float[] released = particles.Select(body => world.Read<BodyPose>(body).Position.Z).ToArray();
        double initialEnergy = MechanicalEnergy();
        for (int number = 0; number < 180; number++) Advance(number);
        var touching = ContactBodies().SelectMany(bodies => bodies).ToHashSet();
        for (int index = 0; index < particles.Length; index++)
        {
            Vector3 position = world.Read<BodyPose>(particles[index]).Position;
            Assert.InRange(position.Z, .1f, released[index] - .1f);
            Assert.InRange(MathF.Abs(position.X), 0, 3);
            Assert.InRange(MathF.Abs(position.Y), 0, 3);
            Assert.Contains(particles[index], touching);
        }
        Assert.InRange(MechanicalEnergy(), 0, initialEnergy);

        Vector3[] previous = particles.Select(body => world.Read<BodyPose>(body).Position).ToArray();
        var throughOutlet = new HashSet<Entity>();
        for (int number = 180; number < example.StepCount; number++)
        {
            Advance(number);
            for (int index = 0; index < particles.Length; index++)
            {
                Vector3 position = world.Read<BodyPose>(particles[index]).Position;
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
        Assert.InRange(world.Read<BodyPose>(scene.Bodies.Span[0]).Position.X, 4, float.MaxValue);
        var drained = throughOutlet.Where(body => world.Read<BodyPose>(body).Position.Z < -2).ToHashSet();
        Assert.Contains(drained, world.Has<SphereGeometry>);
        Assert.Contains(drained, world.Has<BoxGeometry>);
        Assert.Contains(drained, world.Has<ConvexHullGeometry>);
        foreach (Entity[] participants in ContactBodies())
            if (participants.Any(drained.Contains))
                Assert.DoesNotContain(participants, body => world.Read<BodyMass>(body).Motion == BodyMotion.Static);
        world.ReleaseQuery(contacts);

        void Advance(int number)
        {
            scene.AdvanceTime(scene.StepSeconds * (number == 0 ? 1.5 : 1));
            Assert.Equal(number, scene.CompleteForRead().Number);
        }

        List<Entity[]> ContactBodies()
        {
            var links = new List<ContactBodyLink[]>();
            world.ExecuteReadQuery(contacts, rows =>
            {
                foreach (QueryRow row in rows.Rows) links.Add(row.ReadBuffer<ContactBodyLink>().AsSpan().ToArray());
            });
            return links.Select(point => point.Select(link => world.GetDirectedRelationEndpoints(link.Participant).Target).ToArray()).ToList();
        }

        double MechanicalEnergy()
        {
            double energy = 0;
            foreach (Entity body in particles)
            {
                BodyMass mass = world.Read<BodyMass>(body);
                BodyPose pose = world.Read<BodyPose>(body);
                BodyVelocity velocity = world.Read<BodyVelocity>(body);
                Vector3 spin = Vector3.Transform(velocity.Angular, Quaternion.Conjugate(pose.Rotation));
                energy += .5 / mass.InverseMass * velocity.Linear.LengthSquared()
                    + .5 * (spin.X * spin.X / mass.InverseInertia.M11 + spin.Y * spin.Y / mass.InverseInertia.M22 + spin.Z * spin.Z / mass.InverseInertia.M33)
                    + 10 / mass.InverseMass * (pose.GetCenterOfMass(in mass).Z + 2);
            }
            return energy;
        }
    }
}
