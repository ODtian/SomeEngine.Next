using System.Buffers;
using SlangShaderSharp;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderBytecodeTests
{
    [Fact]
    public async Task PublishedShaderLoadsTheSelectedPipelineCodesAndTheirRebuiltOrder()
    {
        using var files = new AssetTestDirectory();
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        SessionDesc description = CreateDescription(slang);
        using var material = new ShaderSourceBlock { Dependencies = [ShaderTestCompilation.StandardMaterial] };
        await using AssetLoader engine = ShaderTestCompilation.OpenEngineFiles();
        using var compiler = new ShaderLibrary(slang, description);
        await compiler.GetAsync(engine, ShaderTestCompilation.StandardMaterial);
        using var forward = new ForwardMaterialEntriesBlock { SourceNames = ["forward_material.slang"] };
        using var probe = new ProbeMaterialEntriesBlock { SourceNames = ["probe_material.slang"] };
        await ShaderTestCompilation.SetEntryAsync(compiler, forward, "forward_material", ForwardEntry);
        await ShaderTestCompilation.SetEntryAsync(compiler, probe, "probe_material", ProbeEntry);

        Shader compiled;
        string path = files.File("published.shader.asset");
        using (var asset = new ShaderAsset { Name = "published" })
        {
            asset.SetBlock(material);
            asset.SetBlock(forward);
            asset.SetBlock(probe);
            asset.SelectInputs<ForwardMaterialEntriesBlock>();
            asset.SelectInputs<ProbeMaterialEntriesBlock>();
            compiled = (await compiler.CompileAsync(asset, engine)).Shader;
            await asset.SaveAsync(path);
        }

        Shader saved;
        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
            saved = await CreateShaderAsync(asset, slang, description, engine);

        Assert.Equal(2, saved.SlangEntryCount);
        EntryPointReflection forwardEntry = saved.RequireSlangEntry(0, "ForwardShade", SlangStage.Compute);
        EntryPointReflection probeEntry = saved.RequireSlangEntry(0, "ProbeShade", SlangStage.Compute);
        Assert.Equal(compiled.GetSlangEntryBlob(0, compiled.RequireSlangEntry(0, "ForwardShade", SlangStage.Compute))!.Buffer.ToArray(),
            saved.GetSlangEntryBytes(0, forwardEntry).ToArray());
        Assert.Equal(compiled.GetSlangEntryBlob(0, compiled.RequireSlangEntry(0, "ProbeShade", SlangStage.Compute))!.Buffer.ToArray(),
            saved.GetSlangEntryBytes(0, probeEntry).ToArray());
        if (compiled.GetSlangTargetBlob(0) is { } target)
            Assert.Equal(target.Buffer.ToArray(), saved.GetSlangTargetBytes(0).ToArray());

        using var consumer = new ConsumerMaterialEntriesBlock { SourceNames = ["consumer_material.slang"] };
        await ShaderTestCompilation.SetEntryAsync(compiler, consumer, "consumer_material",
            ForwardEntry.Replace("ForwardShade", "ConsumerShade", StringComparison.Ordinal)
                .Replace("ForwardColors", "ConsumerColors", StringComparison.Ordinal));
        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            asset.SetBlock(consumer);
            asset.SelectInputs<ConsumerMaterialEntriesBlock>();
            ShaderSourceBlock inputs = asset.GetBlock<ShaderSourceBlock>();
            inputs.Inputs = [inputs.Inputs[1], inputs.Inputs[2], inputs.Inputs[0]];
            Shader extended = await CreateShaderAsync(asset, slang, description, engine);
            Assert.Equal(3, extended.SlangEntryCount);
            Assert.Equal("ProbeShade", extended.GetSlangReflection(0).GetEntryPointByIndex(0).Name);
            Assert.True(extended.TryGetSlangEntry(0, "ConsumerShade", SlangStage.Compute, out _));
            compiled = (await compiler.CompileAsync(asset, engine)).Shader;
            await asset.SaveAsync(path);
        }
        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            Shader rebuilt = await CreateShaderAsync(asset, slang, description, engine);
            Assert.Equal(3, rebuilt.SlangEntryCount);
            Assert.Equal("ProbeShade", rebuilt.GetSlangReflection(0).GetEntryPointByIndex(0).Name);
            Assert.Equal(compiled.GetSlangEntryBlob(0, 0)!.Buffer.ToArray(), rebuilt.GetSlangEntryBytes(0, 0).ToArray());
        }
    }

    [Fact]
    public async Task ChangedEntryIrUsesItsNewCodeAndRebuildsItsPublishedBytes()
    {
        using var files = new AssetTestDirectory();
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        SessionDesc description = CreateDescription(slang);
        using var material = new ShaderSourceBlock { Dependencies = [ShaderTestCompilation.StandardMaterial] };
        await using AssetLoader engine = ShaderTestCompilation.OpenEngineFiles();
        using var compiler = new ShaderLibrary(slang, description);
        await compiler.GetAsync(engine, ShaderTestCompilation.StandardMaterial);
        using var forward = new ForwardMaterialEntriesBlock { SourceNames = ["forward_material.slang"] };
        await ShaderTestCompilation.SetEntryAsync(compiler, forward, "forward_material", ForwardEntry);
        string path = files.File("changed.shader.asset");
        using (var asset = new ShaderAsset { Name = "changed" })
        {
            asset.SetBlock(material);
            asset.SetBlock(forward);
            asset.SelectInputs<ForwardMaterialEntriesBlock>();
            await compiler.CompileAsync(asset, engine);
            await asset.SaveAsync(path);
        }

        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            ForwardMaterialEntriesBlock entry = await asset.GetBlockAsync<ForwardMaterialEntriesBlock>();
            await ShaderTestCompilation.SetEntryAsync(compiler, entry, "forward_replacement",
                ForwardEntry.Replace("int3(0, 0, 0)).x;", "int3(0, 0, 0)).x + 0.25;", StringComparison.Ordinal));
            await asset.SaveAsync(path);
        }

        Shader compiled;
        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            using ShaderAsset ir = asset.Duplicate();
            ir.RemoveBlock<ShaderBlock>();
            compiled = await CreateShaderAsync(ir, slang, description, engine);
            Shader changed = await CreateShaderAsync(asset, slang, description, engine);
            EntryPointReflection changedEntry = changed.RequireSlangEntry(0, "ForwardShade", SlangStage.Compute);
            Assert.Equal(compiled.GetSlangEntryBlob(0, 0)!.Buffer.ToArray(), ReadCode(changed, changedEntry));
            await compiler.CompileAsync(asset, engine);
            await asset.SaveAsync(path);
        }

        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            Shader rebuilt = await CreateShaderAsync(asset, slang, description, engine);
            Assert.Equal(compiled.GetSlangEntryBlob(0, 0)!.Buffer.ToArray(), rebuilt.GetSlangEntryBytes(0, 0).ToArray());
        }
    }

    private static byte[] ReadCode(Shader shader, EntryPointReflection entry)
        => shader.GetSlangEntryBlob(0, entry) is { } code
            ? code.Buffer.ToArray() : shader.GetSlangEntryBytes(0, entry).ToArray();

    private static SessionDesc CreateDescription(IGlobalSession slang) => new()
    {
        Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = slang.FindProfile("glsl_460") }],
        FileSystem = new EmptyShaderFileSystem(),
    };

    private const string ForwardEntry = """
        import standard_material;
        RWStructuredBuffer<float> ForwardColors;
        [shader("compute")]
        [numthreads(1, 1, 1)]
        void ForwardShade(uniform StandardMaterial material, uint3 thread : SV_DispatchThreadID)
        {
            ForwardColors[thread.x] = material.AlbedoMap.Load(int3(0, 0, 0)).x;
        }
        """;

    private const string ProbeEntry = """
        import standard_material;
        RWStructuredBuffer<float> ProbeColors;
        [shader("compute")]
        [numthreads(1, 1, 1)]
        void ProbeShade(uniform StandardMaterial material, uint3 thread : SV_DispatchThreadID)
        {
            ProbeColors[thread.x] = material.NormalMap.Load(int3(0, 0, 0)).x;
        }
        """;

    private static async Task<Shader> CreateShaderAsync(ShaderAsset asset, IGlobalSession slang,
        SessionDesc description, AssetLoader files)
    {
        using var shaders = new ShaderLibrary(slang, description);
        return await shaders.CreateAsync(asset, files);
    }

}

[AssetBlock("SomeEngine.Tests.ForwardMaterialEntries")]
public sealed partial class ForwardMaterialEntriesBlock : ShaderInputBlock;

[AssetBlock("SomeEngine.Tests.ProbeMaterialEntries")]
public sealed partial class ProbeMaterialEntriesBlock : ShaderInputBlock;
