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
        using ShaderSourceBlock material = StandardMaterialInputs.Create();
        using var forward = new ForwardMaterialEntriesBlock { SourceNames = ["forward_material.slang"] };
        using var probe = new ProbeMaterialEntriesBlock { SourceNames = ["probe_material.slang"] };
        forward.SetSerializedSource(0, await SerializeEntryAsync(slang, description, material, "forward_material", ForwardEntry));
        probe.SetSerializedSource(0, await SerializeEntryAsync(slang, description, material, "probe_material", ProbeEntry));

        Shader compiled;
        string path = files.File("published.shader.asset");
        using (var asset = new ShaderAsset { Name = "published" })
        {
            asset.SetBlock(material);
            asset.SetBlock(forward);
            asset.SetBlock(probe);
            asset.SelectInputs<ShaderSourceBlock>();
            asset.SelectInputs<ForwardMaterialEntriesBlock>();
            asset.SelectInputs<ProbeMaterialEntriesBlock>();
            compiled = (await asset.CompileAsync(slang, description)).Shader;
            await asset.SaveAsync(path);
        }

        Shader saved;
        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
            saved = await asset.CreateAsync(slang, description);

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
        consumer.SetSerializedSource(0, await SerializeEntryAsync(slang, description, material, "consumer_material",
            ForwardEntry.Replace("ForwardShade", "ConsumerShade", StringComparison.Ordinal)
                .Replace("ForwardColors", "ConsumerColors", StringComparison.Ordinal)));
        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            asset.SetBlock(consumer);
            asset.SelectInputs<ConsumerMaterialEntriesBlock>();
            ShaderSourceBlock inputs = asset.GetBlock<ShaderSourceBlock>();
            inputs.Inputs = [inputs.Inputs[0], inputs.Inputs[1], inputs.Inputs[3], inputs.Inputs[4], inputs.Inputs[2]];
            Shader extended = await asset.CreateAsync(slang, description);
            Assert.Equal(3, extended.SlangEntryCount);
            Assert.Equal("ProbeShade", extended.GetSlangReflection(0).GetEntryPointByIndex(0).Name);
            Assert.True(extended.TryGetSlangEntry(0, "ConsumerShade", SlangStage.Compute, out _));
            compiled = (await asset.CompileAsync(slang, description)).Shader;
            await asset.SaveAsync(path);
        }
        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            Shader rebuilt = await asset.CreateAsync(slang, description);
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
        using ShaderSourceBlock material = StandardMaterialInputs.Create();
        using var forward = new ForwardMaterialEntriesBlock { SourceNames = ["forward_material.slang"] };
        forward.SetSerializedSource(0, await SerializeEntryAsync(slang, description, material, "forward_material", ForwardEntry));
        string path = files.File("changed.shader.asset");
        using (var asset = new ShaderAsset { Name = "changed" })
        {
            asset.SetBlock(material);
            asset.SetBlock(forward);
            asset.SelectInputs<ShaderSourceBlock>();
            asset.SelectInputs<ForwardMaterialEntriesBlock>();
            await asset.CompileAsync(slang, description);
            await asset.SaveAsync(path);
        }

        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            ForwardMaterialEntriesBlock entry = await asset.GetBlockAsync<ForwardMaterialEntriesBlock>();
            entry.SetSerializedSource(0, await SerializeEntryAsync(slang, description, material, "forward_material",
                ForwardEntry.Replace("int3(0, 0, 0)).x;", "int3(0, 0, 0)).x + 0.25;", StringComparison.Ordinal)));
            await asset.SaveAsync(path);
        }

        Shader compiled;
        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            using ShaderAsset ir = asset.Duplicate();
            ir.RemoveBlock<ShaderBlock>();
            compiled = await ir.CreateAsync(slang, description);
            Shader changed = await asset.CreateAsync(slang, description);
            EntryPointReflection changedEntry = changed.RequireSlangEntry(0, "ForwardShade", SlangStage.Compute);
            Assert.Equal(compiled.GetSlangEntryBlob(0, 0)!.Buffer.ToArray(), ReadCode(changed, changedEntry));
            await asset.CompileAsync(slang, description);
            await asset.SaveAsync(path);
        }

        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            Shader rebuilt = await asset.CreateAsync(slang, description);
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

    private static async Task<byte[]> SerializeEntryAsync(
        IGlobalSession slang, SessionDesc description, ShaderSourceBlock material, string name, string entrySource)
    {
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        for (int index = 0; index < material.SourceNames.Count; index++)
        {
            ReadOnlyMemory<byte> bytes = await material.ReadBytesAsync(ShaderInputBlock.SerializedSourceKey(index));
            Assert.NotNull(session.LoadModuleFromIRBlob(Path.GetFileNameWithoutExtension(material.SourceNames[index]),
                material.SourceNames[index], Slang.CreateBlob(bytes.Span), out _));
        }
        IModule? entry = session.LoadModuleFromSource(name, name + ".slang",
            Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes(entrySource)), out ISlangBlob? diagnostics);
        Assert.True(entry is not null, diagnostics?.AsString);
        Assert.True(entry!.Serialize(out ISlangBlob serialized).Succeeded);
        return serialized.Buffer.ToArray();
    }
}

[AssetBlock("SomeEngine.Tests.ForwardMaterialEntries")]
public sealed partial class ForwardMaterialEntriesBlock : ShaderInputBlock;

[AssetBlock("SomeEngine.Tests.ProbeMaterialEntries")]
public sealed partial class ProbeMaterialEntriesBlock : ShaderInputBlock;
