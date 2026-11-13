using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.Physics;

namespace DefaultGame;

public static partial class PhysicsScenes
{
    private static Entity[] CreateBodies<TGeometry, TBindings>(World world, PhysicsSceneData data, TGeometry geometry,
        TBindings bindings)
        where TGeometry : struct, ISceneBodyGeometry
        where TBindings : struct, ISceneBodyBindings
    {
        using var structure = world.OpenStructureWrite();
        var entities = new Entity[data.Bodies.Length];
        ReadOnlySpan<int> bodyComponents =
        [
            ComponentId<ScenePhysicsBody>.Value,
            ComponentId<BodyPose>.Value,
            ComponentId<BodyMass>.Value,
            ComponentId<BodyVelocity>.Value,
            ComponentId<BodyStepState>.Value,
            ComponentId<BodyActivity>.Value,
            ComponentId<CollisionBounds>.Value,
            ComponentId<ContactMaterial>.Value,
            ComponentId<LocalTransform>.Value,
        ];
        var impulses = data.Events.Where(change => change.Body.HasValue && change.Velocity is not null)
            .GroupBy(change => change.Body!.Value).ToDictionary(group => group.Key,
                group => group.Select(change => new BodyImpulse
                {
                    Linear = Vector(change.Velocity!) * (float)data.Bodies[group.Key].Mass,
                    Time = change.Frame * data.StepSeconds,
                }).ToArray());
        var geometryBodies = new Dictionary<(int Geometry, int Bindings, bool Impulses), List<int>>();
        for (int index = 0; index < entities.Length; index++)
        {
            var group = (geometry.GetComponentId(index), bindings.GetGroup(index), impulses.ContainsKey(index));
            if (!geometryBodies.TryGetValue(group, out List<int>? indices)) geometryBodies.Add(group, indices = []);
            indices.Add(index);
        }
        foreach (var entry in geometryBodies)
        {
            ReadOnlySpan<int> bindingComponents = bindings.GetComponentIds(entry.Key.Bindings);
            int[] componentIds = entry.Key.Impulses
                ? [.. bodyComponents, entry.Key.Geometry, .. bindingComponents, ComponentId<Buffer<BodyImpulse>>.Value]
                : [.. bodyComponents, entry.Key.Geometry, .. bindingComponents];
            var batch = new SceneBodyBatch<TGeometry, TBindings>(data, entities, geometry, bindings, entry.Value, impulses);
            world.ExecuteBundleSpawnBatch(componentIds, entry.Value.Count, batch, new SceneBodyWriter<TGeometry, TBindings>());
        }
        return entities;
    }

    private readonly struct SceneBodyWriter<TGeometry, TBindings> : IBundleWriter<SceneBodyBatch<TGeometry, TBindings>>
        where TGeometry : struct, ISceneBodyGeometry where TBindings : struct, ISceneBodyBindings
    {
        public void Write(BundleWriteView view, in SceneBodyBatch<TGeometry, TBindings> bodies) => bodies.Write(view);
    }

    private readonly struct SceneBodyBatch<TGeometry, TBindings>(PhysicsSceneData data, Entity[] entities,
        TGeometry geometry, TBindings bindings, List<int> indices, IReadOnlyDictionary<int, BodyImpulse[]> impulses)
        where TGeometry : struct, ISceneBodyGeometry
        where TBindings : struct, ISceneBodyBindings
    {
        internal void Write(BundleWriteView view)
        {
            int index = indices[view.Index];
            SceneBodyData input = data.Bodies[index];
            view.Write(new ScenePhysicsBody());
            var pose = new BodyPose { Position = Vector(input.Position), Rotation = Rotation(input.Rotation) };
            view.Write(in pose);
            view.Write(new BodyMass
            {
                Motion = input.Mass == 0 ? BodyMotion.Static : BodyMotion.Dynamic,
                InverseMass = Inverse(input.Mass),
                InverseInertia = new Matrix4x4
                {
                    M11 = Inverse(input.Inertia[0]), M22 = Inverse(input.Inertia[1]), M33 = Inverse(input.Inertia[2]),
                },
            });
            view.Write(new BodyVelocity { Linear = Vector(input.Velocity), Angular = Vector(input.AngularVelocity) });
            view.Write(new BodyStepState());
            view.Write(new BodyActivity { Radius = geometry.Write(view, input, index) });
            view.Write(new CollisionBounds());
            view.Write(new ContactMaterial
            {
                NormalStiffness = (float)(data.Materials?.ContactStiffness ?? double.PositiveInfinity),
                NormalDamping = (float)(data.Materials?.ContactDamping ?? 0),
                StaticFriction = (float)input.Friction, DynamicFriction = (float)input.Friction,
            });
            view.Write(new LocalTransform
            {
                Value = new TransformQvvs(pose.Position, pose.Rotation) { Stretch = Vector(input.Size) },
            });
            bindings.Write(view, index);
            if (impulses.TryGetValue(index, out BodyImpulse[]? values)) view.WriteBuffer<BodyImpulse>(values.AsMemory());
            entities[index] = view.Entity;
        }
    }
}
