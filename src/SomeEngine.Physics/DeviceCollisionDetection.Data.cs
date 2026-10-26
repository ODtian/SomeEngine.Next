using System.Runtime.InteropServices;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.Physics;

public readonly record struct DeviceDetectionCapacity(int Pairs, int FaceVertices, int WorkItems)
{
    public int BodiesPerContact { get; init; } = 6;
}

[Flags]
public enum DeviceCollisionReadback { None = 0, Distances = 1, Contacts = 2 }

[StructLayout(LayoutKind.Sequential)]
public readonly record struct DeviceCollisionDistance(Entity First, Entity Second, CollisionDistance Distance);

public readonly struct DetectDeviceCollision(DeviceCollisionDetection detection) : IPhysicsStage
{
    public JobHandle Schedule(in PhysicsStepContext context, JobHandle dependency) => detection.Schedule(in context, dependency);
}
