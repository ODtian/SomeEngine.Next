using SomeEngine.ECS;
using SomeEngine.Job;

namespace SomeEngine.Physics;

/// <summary>Separates readonly component inputs needed to collect referenced resources from numerical field permissions.</summary>
public interface IPhysicsConstraintAccess
{
    void AddInputAccesses(World world, List<JobResourceAccess> accesses) { }
    void AddAccesses(World world, List<JobResourceAccess> accesses) { }
}
