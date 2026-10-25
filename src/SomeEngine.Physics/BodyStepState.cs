using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Physics;

/// <summary>Shared substep inputs for numerical stages, stored on the simulated entity.</summary>
public struct BodyStepState : IComponent
{
    public Vector3 StartCenterOfMass;
    public Quaternion StartRotation;
    public Vector3 PredictedCenterOfMass;
    public Quaternion PredictedRotation;
    public long Number;
    public int Substep;
    public double Time;
}
