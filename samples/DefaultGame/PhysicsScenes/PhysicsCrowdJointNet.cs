using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Physics;

namespace DefaultGame;

public sealed partial class PhysicsCrowdScene
{
    private void AddJointNet(PhysicsSceneData data, DeviceBodies bodies, Entity floor,
        List<Entity> allBodies, List<Entity> boxes, List<Entity> joints)
    {
        var nodes = new Entity[data.Bodies.Length];
        nodes[1] = floor;
        Vector3 origin = new(-ToYUpVector(data.Bodies[1].Position).X, 0, .25f);
        for (int index = 2; index < nodes.Length; index++)
        {
            SceneBodyData input = data.Bodies[index];
            Entity entity = nodes[index] = _world.CreateEntity(new BodyPose
                { Position = ToYUpVector(input.Position) + origin, Rotation = Quaternion.Identity });
            _world.Add(entity, new BodyMass
            {
                Motion = input.Mass == 0 ? BodyMotion.Static : BodyMotion.Dynamic,
                InverseMass = input.Mass == 0 ? 0 : (float)(1 / input.Mass),
                InverseInertia = new Matrix4x4
                {
                    M11 = input.Mass == 0 ? 0 : (float)(1 / input.Inertia[0]),
                    M22 = input.Mass == 0 ? 0 : (float)(1 / input.Inertia[2]),
                    M33 = input.Mass == 0 ? 0 : (float)(1 / input.Inertia[1]),
                },
            });
            _world.Add(entity, new BodyVelocity()); _world.Add(entity, new BodyStepState());
            _world.Add(entity, new BoxGeometry(new((float)input.Size[0] * .5f,
                (float)input.Size[2] * .5f, (float)input.Size[1] * .5f)));
            _world.Add(entity, new DeviceBodyBinding(bodies, allBodies.Count));
            allBodies.Add(entity); boxes.Add(entity);
        }
        foreach (SceneJointData input in data.Joints)
        {
            Entity first = nodes[input.BodyA], second = nodes[input.BodyB];
            SceneBodyData firstData = data.Bodies[input.BodyA], secondData = data.Bodies[input.BodyB];
            Vector3 firstPosition = _world.Read<BodyPose>(first).Position;
            Vector3 secondPosition = _world.Read<BodyPose>(second).Position;
            Vector3 firstPoint = ToYUpVector(firstData.Position) + origin + ToYUpVector(input.PointA);
            var source = new JointFrame(firstPoint - firstPosition, Quaternion.Identity);
            double offsetX = (secondData.Position[0] + input.PointB[0]) - (firstData.Position[0] + input.PointA[0]);
            double offsetY = (secondData.Position[2] + input.PointB[2]) - (firstData.Position[2] + input.PointA[2]);
            double offsetZ = (firstData.Position[1] + input.PointA[1]) - (secondData.Position[1] + input.PointB[1]);
            // Derive both frames from one realized point and retain the declared offset between the points.
            var target = new JointFrame(new(
                (float)((double)firstPosition.X + source.Position.X + offsetX - secondPosition.X),
                (float)((double)firstPosition.Y + source.Position.Y + offsetY - secondPosition.Y),
                (float)((double)firstPosition.Z + source.Position.Z + offsetZ - secondPosition.Z)), Quaternion.Identity);
            Entity joint = _world.CreateRelation(first, second,
                JointConstraints.CreateBall(source, target, float.PositiveInfinity)).Entity;
            _world.Add(joint, new ScenePhysicsJoint());
            _world.Add(joint, new AvbdJointState { LinearPenalty = new(1e6f), AngularPenalty = new(1e6f) });
            joints.Add(joint);
        }
        foreach (SceneCollisionPair pair in data.CollisionExclusions)
            _world.CreateRelation(nodes[pair.BodyA], nodes[pair.BodyB], new SceneCollisionExclusion());
        foreach (SceneInputEvent input in data.Events)
        {
            Entity entity = nodes[input.Body!.Value];
            _world.AddBuffer<BodyImpulse>(entity);
            var impulse = new BodyImpulse
            {
                Linear = ToYUpVector(input.Velocity!) * (float)data.Bodies[input.Body.Value].Mass,
                Time = input.Frame * data.StepSeconds,
            };
            _world.ExecuteBufferWrite<BodyImpulse, BodyImpulse>(entity, ref impulse,
                static (buffer, ref value) => buffer.Add(value));
        }
    }

    private static Vector3 ToYUpVector(double[] value) => new((float)value[0], (float)value[2], (float)-value[1]);
}
