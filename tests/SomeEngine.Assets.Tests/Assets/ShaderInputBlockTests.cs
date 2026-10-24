using System.Buffers;
using SlangShaderSharp;
using System.Runtime.InteropServices.Marshalling;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class ShaderInputBlockTests
{
    [Fact]
    public async Task SelectedTypedEntryBlocksRestoreOneShaderWithBothPipelineEntries()
    {
        using var files = new AssetTestDirectory();
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        var description = new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = slang.FindProfile("glsl_460") }],
            FileSystem = new EmptyShaderFileSystem(),
        };
        using ShaderSourceBlock material = StandardMaterialInputs.Create();
        using ClusterMaterialEntriesBlock cluster = ClusterMaterialEntriesBlock.Create();
        using var consumer = new ConsumerMaterialEntriesBlock { SourceNames = ["consumer_material.slang"] };
        consumer.SetSerializedSource(0, await SerializeConsumerEntryAsync(slang, description, material));
        Guid id = Guid.NewGuid();
        string path = files.File("material.shader.asset");
        var project = new AssetProject(files.Path, []);
        using (var asset = new ShaderAsset(id) { Name = "material" })
        {
            asset.SetBlock(material);
            asset.SetBlock(cluster);
            asset.SetBlock(consumer);
            asset.SelectInputs<ShaderSourceBlock>();
            asset.SelectInputs<ClusterMaterialEntriesBlock>();
            asset.SelectInputs<ConsumerMaterialEntriesBlock>();
            project.RegisterAsset(await asset.SaveAsync(path));
        }

        using (ShaderAsset asset = await ShaderAsset.OpenAsync(path))
        {
            Assert.Equal(id, asset.Id);
            ConsumerMaterialEntriesBlock entries = await asset.GetBlockAsync<ConsumerMaterialEntriesBlock>();
            Assert.Equal("consumer_material.slang", Assert.Single(entries.SourceNames));
        }

        await using var loader = new AssetLoader(project.CreateStorage());
        using var materialAsset = new MaterialAsset { Name = "Standard" };
        materialAsset.GetBlock<MaterialBlock>().Shader = new(id);
        _ = new StandardMaterialParameters(materialAsset.GetBlock<MaterialBlock>()) { Roughness = 0.37f };
        using ShaderAsset shaderAsset = await ShaderAsset.OpenAsync(loader, new(id));
        Shader shader = await shaderAsset.CreateAsync(slang, description);
        StandardMaterial instance = await materialAsset.CreateAsync<StandardMaterial>(loader,
            new Dictionary<AssetId<ShaderAsset>, Shader> { [new(id)] = shader },
            new Dictionary<AssetId<TextureAsset>, Texture>());
        Assert.Same(shader, instance.Shader);
        Assert.Equal(0.37f, instance.Roughness);

        Assert.True(shader.TryGetSlangEntry(0, "ClusterShade", SlangStage.Compute, out EntryPointReflection clusterEntry));
        Assert.True(shader.TryGetSlangEntry(0, "ConsumerShade", SlangStage.Compute, out EntryPointReflection consumerEntry));
        Assert.NotNull(shader.GetSlangReflection(0).FindTypeByName("StandardScalars"));
        Assert.True(shader.GetSlangEntryBlob(0, clusterEntry)!.GetBufferSize() > 0);
        Assert.True(shader.GetSlangEntryBlob(0, consumerEntry)!.GetBufferSize() > 0);
    }

    [Fact]
    public async Task SelectedTypedEntryBlocksRestoreSavedEntryVariantsWithoutSourceFiles()
    {
        using var files = new AssetTestDirectory();
        IGlobalSession slang = SlangShaderImporter.GlobalSession;
        var description = new SessionDesc
        {
            Targets = [new TargetDesc { Format = SlangCompileTarget.Spirv, Profile = slang.FindProfile("glsl_460") }],
            FileSystem = new EmptyShaderFileSystem(),
        };
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        IModule? source = session.LoadModuleFromSource("variant_entry", "variant_entry.slang",
            Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes("""
                RWStructuredBuffer<uint> Result;
                [shader("compute")]
                [numthreads(1, 1, 1)]
                void WriteValue<let value : uint>(uint3 thread : SV_DispatchThreadID)
                {
                    Result[thread.x] = value;
                }
                """)), out ISlangBlob? diagnostics);
        Assert.True(source is not null, diagnostics?.AsString);
        Assert.True(source!.Serialize(out ISlangBlob serialized).Succeeded);
        using var entries = new ConsumerMaterialEntriesBlock { SourceNames = ["variant_entry.slang"] };
        entries.SetSerializedSource(0, serialized.Buffer);
        string path = files.File("variants.shader.asset");
        using (var asset = new ShaderAsset { Name = "Saved variants" })
        {
            asset.SetBlock(entries);
            asset.SelectInputs<ConsumerMaterialEntriesBlock>();
            asset.GetBlock<ShaderSourceBlock>().Specializations =
            [new() { EntryPoint = "WriteValue", Arguments = ["3"] },
             new() { EntryPoint = "WriteValue", Arguments = ["7"] }];
            await asset.CompileAsync(slang, description);
            await asset.SaveAsync(path);
        }
        using ShaderAsset restored = await ShaderAsset.OpenAsync(path);
        Shader shader = await restored.CreateAsync(slang, description);
        Assert.True(shader.TryGetSpecialization("WriteValue", ["3"], out Shader first));
        Assert.True(shader.TryGetSpecialization("WriteValue", ["7"], out Shader second));
        Assert.True(first.TryGetSlangEntry(0, "WriteValue", SlangStage.Compute, out EntryPointReflection firstEntry));
        Assert.True(second.TryGetSlangEntry(0, "WriteValue", SlangStage.Compute, out EntryPointReflection secondEntry));
        Assert.True(first.GetSlangEntryBytes(0, firstEntry).Length > 0);
        Assert.True(second.GetSlangEntryBytes(0, secondEntry).Length > 0);
        Assert.NotEqual(first.GetSlangEntryBytes(0, firstEntry).ToArray(),
            second.GetSlangEntryBytes(0, secondEntry).ToArray());
    }

    private static async Task<byte[]> SerializeConsumerEntryAsync(
        IGlobalSession slang, SessionDesc description, ShaderSourceBlock material)
    {
        Assert.True(slang.CreateSession(description, out ISession session).Succeeded);
        for (int index = 0; index < material.SourceNames.Count; index++)
        {
            ReadOnlyMemory<byte> bytes = await material.ReadBytesAsync(ShaderInputBlock.SerializedSourceKey(index));
            Assert.NotNull(session.LoadModuleFromIRBlob(Path.GetFileNameWithoutExtension(material.SourceNames[index]),
                material.SourceNames[index], Slang.CreateBlob(bytes.Span), out _));
        }
        ISlangBlob source = Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes("""
            import standard_material;
            StructuredBuffer<StandardScalars> Scalars;
            RWStructuredBuffer<float> Result;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void ConsumerShade(uniform StandardMaterial material, uint3 thread : SV_DispatchThreadID)
            {
                Result[thread.x] = material.AlbedoMap.Load(int3(0, 0, 0)).x * Scalars[thread.x].Roughness;
            }
            """));
        IModule? entries = session.LoadModuleFromSource("consumer_material", "consumer_material.slang", source, out ISlangBlob? diagnostics);
        Assert.True(entries is not null, diagnostics?.AsString);
        Assert.True(entries!.Serialize(out ISlangBlob serialized).Succeeded);
        return serialized.Buffer.ToArray();
    }
}

[AssetBlock("SomeEngine.Tests.ConsumerMaterialEntries")]
public sealed partial class ConsumerMaterialEntriesBlock : ShaderInputBlock;

[GeneratedComClass]
internal sealed partial class EmptyShaderFileSystem : ISlangFileSystem
{
    public unsafe void* CastAs(Guid guid) => null;
    public SlangResult LoadFile(string path, out ISlangBlob blob)
    {
        blob = null!;
        return SlangResult.SLANG_E_NOT_FOUND;
    }
}
