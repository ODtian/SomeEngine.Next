using System.Globalization;
using System.Collections.Immutable;

namespace SomeEngine.Assets.Schema;

public sealed partial class ClusterShadersBlock
{
    internal readonly record struct MaterialSurfaceTarget(string EntryPoint, string Input, string Output);

    internal static ImmutableArray<MaterialSurfaceTarget> MaterialSurfaceTargets { get; } =
    [
        new("ClusterShadeMode", "VisibilityBufferInput", "OpaqueOutput"),
        new("LayerShadeMode", "LayerFragmentInput", "LayerPayloadOutput"),
    ];

    internal static ShaderSpecialization[] CreateMaterialSpecializations()
    {
        var variants = new List<ShaderSpecialization>();
        uint[] debugModes = [0, 8, 9, 10, 11];
        foreach (MaterialSurfaceTarget target in MaterialSurfaceTargets)
            foreach (uint mode in debugModes)
                variants.Add(new()
                {
                    EntryPoint = target.EntryPoint,
                    Arguments = [mode.ToString(CultureInfo.InvariantCulture)],
                });
        foreach (uint depth in new uint[] { 0, 1, 2 })
            foreach (uint mode in debugModes)
                variants.Add(new()
                {
                    EntryPoint = "ClusterForwardShade",
                    Arguments = [depth.ToString(CultureInfo.InvariantCulture), mode.ToString(CultureInfo.InvariantCulture)],
                });
        foreach (string requests in new[] { "LayerNoPageRequests", "LayerShadowPageRequests<32>" })
            variants.Add(new() { EntryPoint = "LayerRasterSoftware", Arguments = [requests] });
        return variants.ToArray();
    }
}
