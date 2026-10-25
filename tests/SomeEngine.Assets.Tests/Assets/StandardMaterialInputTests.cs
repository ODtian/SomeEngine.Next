using SomeEngine.Assets.Schema;
using SlangShaderSharp;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class StandardMaterialInputTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericAlphaMaskCompilesToSavedCodeWithExplicitDepthPolicy(bool trackDepthInputs)
    {
        using var files = new AssetTestDirectory();
        string source = files.File("mask.slang");
        File.WriteAllText(source, """
            #include "visibility_evaluate.slang"
            struct StandardVertex { float2 uv0; };
            """ + "\n" + StandardMaterialInputs.CreateAlphaMaskSource(0.5f, trackDepthInputs) + "\n" + """
            StandardAlphaMask Mask;
            RWStructuredBuffer<uint> Result;
            [shader("compute")]
            [numthreads(1,1,1)]
            void main()
            {
                VisibilityPixelInput input = (VisibilityPixelInput)0;
                StandardAlphaMaskVarying varying;
                varying.UV = float2(0.5, 0.5);
                Result[0] = Mask.evaluate(input, varying).Visible;
            }
            """);
        using ShaderAsset asset = ShaderTestCompilation.Compile(source);
        var shader = await asset.CreateSavedAsync(ShaderTestCompilation.Slang,
            ShaderTestCompilation.CreateSessionDescription());
        foreach (SlangCompileTarget kind in new[] { SlangCompileTarget.Dxil, SlangCompileTarget.Spirv })
        {
            int target = shader.RequireSlangTarget(kind);
            Assert.False(shader.GetSlangEntryBytes(target, 0).IsEmpty);
            var type = shader.GetSlangReflection(target).FindTypeByName("StandardAlphaMask");
            Assert.NotNull(type);
            var declarations = Enumerable.Range(0, (int)type!.Value.AttributeCount)
                .Select(index => type.Value.GetAttribute((uint)index))
                .Where(attribute => attribute.Name == "MaterialDepthInputs").ToArray();
            if (trackDepthInputs)
                Assert.Equal("AlbedoMap,MaterialSampler", Assert.Single(declarations).GetArgumentValueString(0));
            else Assert.Empty(declarations);
        }
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.1f)]
    public void GenericAlphaMaskRejectsInvalidCutoffs(float cutoff)
        => Assert.Throws<ArgumentOutOfRangeException>(() => StandardMaterialInputs.CreateAlphaMaskSource(cutoff));
}
