using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class RhiCommandParityTests
{
    [Fact]
    public void EveryRecordableRhiCommandHasAGraphScopeOrLifecycleOwner()
    {
        HashSet<string> graphMethods =
        [
            .. typeof(RasterPassCommandScope).GetMethods().Select(static method => method.Name),
            .. typeof(ComputePassCommandScope).GetMethods().Select(static method => method.Name),
            .. typeof(CopyPassCommandScope).GetMethods().Select(static method => method.Name),
            .. typeof(GeneralPassCommandScope).GetMethods().Select(static method => method.Name),
        ];
        HashSet<string> lifecycle =
        [
            "Begin",
            "End",
            "EndBundle",
            "Discard",
            "Barrier",
            // RecordedBundle has no immutable resource inventory in the current RHI.
            // Exposing it from a Pass scope would bypass access and lifetime validation.
            "ExecuteBundle",
        ];
        string[] missing = typeof(IGraphicsBackend).GetMethods()
            .Where(static method =>
            {
                var parameters = method.GetParameters();
                return parameters.Length != 0 && parameters[0].ParameterType == typeof(CommandContext);
            })
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(name => !lifecycle.Contains(name) && !graphMethods.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(missing.Length == 0,
            "Missing RenderGraph command scope methods: " + string.Join(", ", missing));
    }
}

