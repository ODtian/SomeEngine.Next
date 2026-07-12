using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using NullDevice = SomeEngine.Graphics.Null.Device;
using AssetShaderStage = SomeEngine.Assets.Schema.ShaderStage;
using RenderGraphInstance = SomeEngine.RenderGraph.RenderGraph;

namespace SomeEngine.Assets.Tests;

[ShaderParameters]
internal partial struct AssetReflectedShaderParameters;

public sealed class ShaderParameterGenerationTests
{
    [Fact]
    public void Asset_reflection_is_the_only_shader_entry_and_binding_truth()
    {
        string assetPath = Path.Combine(
            AppContext.BaseDirectory,
            "Content",
            "Shaders",
            "hello_triangle.shader.asset");
        ShaderAsset asset = ShaderAssetCodec.Load(assetPath);
        ShaderDesc shader = ShaderAssetProjection.Dxil(asset, "VSMain", AssetShaderStage.Vertex);

        Assert.Equal("VSMain", shader.EntryPoint);
        Assert.NotEqual(0UL, shader.Interface.LayoutHash);
        Assert.Empty(shader.Interface.Bindings.ToArray());
        Assert.Throws<InvalidOperationException>(() =>
            ShaderAssetProjection.Dxil(asset, "not-an-asset-entry", AssetShaderStage.Vertex));
        Assert.Throws<ArgumentException>(() =>
            new ShaderParameterBinding(shader, checked(shader.Interface.LayoutHash + 1)));

        Type marker = typeof(ShaderParametersAttribute);
        Assert.DoesNotContain(marker.GetConstructors().SelectMany(static constructor => constructor.GetParameters()),
            static parameter => parameter.ParameterType == typeof(string));
        Assert.DoesNotContain(marker.GetProperties(), static property =>
            property.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Entry", StringComparison.OrdinalIgnoreCase));

        using NullDevice device = new();
        using RenderGraphInstance graph = new(device);
        GraphBuilder builder = graph.Begin();
        PassBuilder pass = builder.AddPass("asset-reflected-parameters", QueueSelection.Graphics);
        AssetReflectedShaderParameters parameters = default;
        GeneratedParameterSet generated = parameters.Pair(
            ref builder,
            ref pass,
            new ShaderParameterBinding(shader, shader.Interface.LayoutHash));
        pass.Execute(static (ICommandContext _, in PassResources _) => { });

        Assert.NotNull(generated);
        Assert.NotNull(typeof(AssetReflectedShaderParameters).GetMethod(nameof(AssetReflectedShaderParameters.Pair)));
        builder.Dispose();
    }
}
