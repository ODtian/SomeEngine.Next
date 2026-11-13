using System.Numerics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

public interface IAvbdBodyConstraints
{
    void AccumulateBodyEquation(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        float initialErrorFraction, ref AvbdBodyEquation equation);
    void AdjustBodyChange(in PhysicsStepContext context, Entity body, scoped PhysicsBodies bodies,
        in AvbdBodyEquation equation, ref Vector3 translation, ref Vector3 rotation)
    { }
}

internal static class AvbdBodyIteration
{
    internal static void Update<TConstraints>(Entity body, scoped PhysicsBodies bodies,
        in PhysicsStepContext context, TConstraints constraints, int bodyIterations, float initialErrorFraction)
        where TConstraints : IAvbdBodyConstraints, allows ref struct
    {
        ref readonly BodyMass mass = ref bodies.ReadMass(body);
        if (mass.Motion != BodyMotion.Dynamic) return;
        ref BodyPose pose = ref bodies.WritePose(body);
        ref readonly BodyStepState state = ref bodies.ReadStepState(body);
        ref AvbdBodyEquation equation = ref bodies.WriteEquation(body);
        for (int local = 0; local < bodyIterations; local++)
        {
            equation.Reset(in pose, in mass, in state, (float)context.Duration);
            constraints.AccumulateBodyEquation(in context, body, bodies, initialErrorFraction, ref equation);
            equation.Factorize();
            equation.SolveResponse(-equation.LinearGradient, -equation.AngularGradient, out var translation, out var rotation);
            constraints.AdjustBodyChange(in context, body, bodies, in equation, ref translation, ref rotation);
            var center = pose.GetCenterOfMass(in mass) + translation;
            pose.Rotation = BodyRotation.ApplyChange(pose.Rotation, rotation);
            pose.SetCenterOfMass(center, in mass);
        }
    }
}
