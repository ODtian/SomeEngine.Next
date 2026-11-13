using System.Reflection;

namespace SomeEngine.Render.Tests.Architecture;

public sealed class ScreenBinningGraphEditChecks
{
    [Fact, Trait("Architecture", "ARCH-37")]
    public void ScreenBinningRegistrationKeepsItsIdsWithTheCaller()
    {
        Assert.Empty(typeof(ScreenBinningPasses).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact, Trait("Architecture", "ARCH-37")]
    public void LayerCompositionCommitsItsOwnGraphChanges()
    {
        Type composition = typeof(Features.Layers.LayerComposition);
        const BindingFlags members = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var methods = composition.GetMethods(members).Cast<MethodBase>().Concat(composition.GetConstructors(members));
        Assert.DoesNotContain(methods.SelectMany(method => method.GetParameters()), parameter =>
            parameter.ParameterType == typeof(RenderGraph.RenderGraphEdit) ||
            parameter.ParameterType == typeof(RenderGraph.RenderGraphEdit).MakeByRefType());
    }
}
