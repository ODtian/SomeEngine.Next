using SlangShaderSharp;
using SomeEngine.Resources;

namespace SomeEngine.Elements.Render.Tests;

public sealed class BuiltInShaderTests
{
    [Theory]
    [InlineData(SlangCompileTarget.Dxil)]
    [InlineData(SlangCompileTarget.Spirv)]
    public void BuiltInMaterialsProvideCookedEntries(SlangCompileTarget target)
    {
        var global = Shader.CreateSlangGlobalSession();
        Material[] materials = [SurfaceMaterial.Create(target, global), ImageMaterial.Create(target, global), GlyphsMaterial.Create(target, global)];
        foreach (Material material in materials)
        {
            Shader shader = material.Shader!;
            Assert.Same(global, shader.SlangCode.Global);
            int index = shader.RequireSlangTarget(target);
            var vertex = shader.RequireSlangEntry(index, "vertexMain", SlangStage.Vertex);
            var pixel = shader.RequireSlangEntry(index, "pixelMain", SlangStage.Fragment);
            Assert.True(shader.GetSlangEntryBytes(index, vertex).Length > 0);
            Assert.True(shader.GetSlangEntryBytes(index, pixel).Length > 0);
        }
    }
}
