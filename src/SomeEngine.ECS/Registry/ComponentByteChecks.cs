using System.Diagnostics;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.ECS.Registry;

internal static class ComponentByteChecks
{
    [Conditional("DEBUG")]
    internal static void Require(in ComponentInfo info)
    {
        DevelopmentCheck.Require(info.Storage is StoragePath.Table or StoragePath.Sparse,
            "Component byte access requires table or sparse component data.");
        DevelopmentCheck.Require(!info.ContainsReferences,
            "Component byte access requires unmanaged fields.");
        DevelopmentCheck.Require(!info.IsBufferStorage,
            "Dynamic buffer storage requires the buffer access APIs.");
    }
}
