using System.Numerics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using SomeEngine.Physics;

namespace DefaultGame;

public static partial class PhysicsScenes
{
    private static Entity[] CreateBodies<TGeometry, TBindings>(World world, PhysicsSceneData data, TGeometry geometry,
        TBindings bindings)
        where TGeometry : struct, ISceneBodyGeometry
        where TBindings : struct, ISceneBodyBindings
    {
        var entities = new Entity[data.Bodies.Length];
        ReadOnlySpan<int> bodyComponents =
        [
            ComponentMetadata<ScenePhysicsBody>.Id,
            ComponentMetadata<BodyPose>.Id,
            ComponentMetadata<BodyMass>.Id,
            ComponentMetadata<BodyVelocity>.Id,
            ComponentMetadata<BodyStepState>.Id,
            ComponentMetadata<BodyActivity>.Id,
            ComponentMetadata<CollisionBounds>.Id,
            ComponentMetadata<ContactMaterial>.Id,
            ComponentMetadata<LocalTransform>.Id,
        ];
        ReadOnlySpan<int> bindingComponents = bindings.ComponentIds;
        Span<int> componentIds = stackalloc int[bodyComponents.Length + 1 + bindingComponents.Length];
        bodyComponents.CopyTo(componentIds);
        bindingComponents.CopyTo(componentIds[(bodyComponents.Length + 1)..]);
        for (int first = 0; first < entities.Length;)
        {
            int geometryId = geometry.GetComponentId(first);
            int end = first + 1;
            while (end < entities.Length && geometry.GetComponentId(end) == geometryId) end++;
            componentIds[bodyComponents.Length] = geometryId;
            var batch = new SceneBodyBatch<TGeometry, TBindings>(data, entities, geometry, bindings, first);
            world.ExecuteBundleSpawnBatch(componentIds, end - first, ref batch,
                static (BundleWriteView view, ref SceneBodyBatch<TGeometry, TBindings> bodies) => bodies.Write(view));
            first = end;
        }
        return entities;
    }

    private readonly struct SceneBodyBatch<TGeometry, TBindings>(PhysicsSceneData data, Entity[] entities,
        TGeometry geometry, TBindings bindings, int first)
        where TGeometry : struct, ISceneBodyGeometry
        where TBindings : struct, ISceneBodyBindings
    {
        internal void Write(BundleWriteView view)
        {
            int index = first + view.Index;
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
            entities[index] = view.Entity;
        }
    }
}
