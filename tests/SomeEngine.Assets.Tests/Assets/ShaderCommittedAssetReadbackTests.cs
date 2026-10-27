using SlangShaderSharp;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderCommittedAssetReadbackTests
{
    [Fact]
    public async Task VirtualShadowFrameResetRestoresSavedDxilAndSpirv()
    {
        string shaderDirectory = SomeEngine.Tests.TestProjectPaths.ShaderDirectory();
        string definitionPath = Path.Combine(shaderDirectory, "vsm_frame_reset.shader");
        using ShaderAsset asset = await ShaderAsset.OpenAsync(definitionPath + ".asset");
        Assert.Equal("vsm_frame_reset", asset.Name);
        ShaderBlock code = await asset.GetBlockAsync<ShaderBlock>();
        Assert.Equal(ShaderBlock.Version, code.SchemaVersion);

        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        TargetDesc[] targets = SlangShaderImporter.CreateDefaultTargets(slang);
        SessionDesc settings = SlangShaderImporter.CreateSessionDescription(definitionPath, targets);

        // Saved-only restoration must fail instead of compiling absent or stale code.
        Shader shader = await asset.CreateSavedAsync(slang, settings);

        Assert.Contains(targets, target => target.Format == SlangCompileTarget.Dxil);
        Assert.Contains(targets, target => target.Format == SlangCompileTarget.Spirv);
        Assert.Equal(targets.Length, shader.SlangTargetCount);
        foreach (TargetDesc requested in targets)
        {
            int target = shader.RequireSlangTarget(requested.Format);
            Assert.True(shader.TryGetSlangEntry(target, "ResetVirtualShadowFrame",
                SlangStage.Compute, out EntryPointReflection entry));
            Assert.False(shader.GetSlangEntryBytes(target, entry).IsEmpty);
        }
    }
}
