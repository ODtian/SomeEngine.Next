using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Physics;

public interface IAvbdBodyConstraints
{
    void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation);
    void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation) { }
}

internal static class AvbdBodyIteration
{
    internal static void Update<TConstraints>(scoped QueryRow row, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, TConstraints constraints, int bodyIterations, float initialErrorFraction)
        where TConstraints : IAvbdBodyConstraints, allows ref struct
    {
        ref readonly BodyMass mass = ref row.ReadRef<BodyMass>();
        if (mass.Motion != BodyMotion.Dynamic) return;
        var bodyRows = new PhysicsBodies(bodies, row);
        ref BodyPose pose = ref row.ReadWrite<BodyPose>();
        ref readonly BodyStepState state = ref row.ReadRef<BodyStepState>();
        ref AvbdBodyEquation equation = ref row.ReadWrite<AvbdBodyEquation>();
        for (int local = 0; local < bodyIterations; local++)
        {
            equation.Reset(in pose, in mass, in state, (float)context.Duration);
            constraints.AccumulateBodyEquation(in context, row.Entity, bodyRows, initialErrorFraction, ref equation);
            equation.Factorize();
            equation.SolveResponse(-equation.LinearGradient, -equation.AngularGradient, out var translation, out var rotation);
            constraints.AdjustBodyChange(in context, row.Entity, bodyRows, in equation, ref translation, ref rotation);
            var center = pose.GetCenterOfMass(in mass) + translation;
            pose.Rotation = BodyRotation.ApplyChange(pose.Rotation, rotation);
            pose.SetCenterOfMass(center, in mass);
        }
    }
}
