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
    private static Entity[] CreateBodies<TGeometry>(World world, PhysicsSceneData data, TGeometry geometry)
        where TGeometry : struct, ISceneBodyGeometry
    {
        var entities = new Entity[data.Bodies.Length];
        Span<int> componentIds =
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
            0,
        ];
        for (int first = 0; first < entities.Length;)
        {
            int geometryId = geometry.GetComponentId(first);
            int end = first + 1;
            while (end < entities.Length && geometry.GetComponentId(end) == geometryId) end++;
            componentIds[^1] = geometryId;
            var batch = new SceneBodyBatch<TGeometry>(data, entities, geometry, first);
            world.ExecuteBundleSpawnBatch(componentIds, end - first, ref batch,
                static (BundleWriteView view, ref SceneBodyBatch<TGeometry> bodies) => bodies.Write(view));
            first = end;
        }
        return entities;
    }

    private readonly struct SceneBodyBatch<TGeometry>(PhysicsSceneData data, Entity[] entities, TGeometry geometry, int first)
        where TGeometry : struct, ISceneBodyGeometry
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
            entities[index] = view.Entity;
        }
    }
}
