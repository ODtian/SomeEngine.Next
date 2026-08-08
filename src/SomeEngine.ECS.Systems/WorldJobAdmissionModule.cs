using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Systems;

internal static class WorldJobAdmissionModule
{
    // This library owns the scheduling integration: loading it installs the logical World-storage
    // to Job-resource mapper. ECS core references only Job's lightweight execution-context
    // contract so arbitrary raw jobs fail closed; it does not reference or initialize JobSystem.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Initialize()
    {
        World.InstallDefaultJobAdmission(WorldJobAdmission.Instance);
    }
#pragma warning restore CA2255
}
