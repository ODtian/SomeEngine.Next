using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Core.Collections;
using SomeEngine.Assets.Importers;
using SomeEngine.Core.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.Graphics;
using SomeEngine.Render.Components;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class SharedMaterialEntryTests(RenderTestDevice graphics) : IClassFixture<RenderTestDevice>
{
    [Fact]
    public async Task SharedStandardMaterialFeedsBothPipelineEntriesAfterExtraction()
    {
        await using var content = await SharedMaterialContent.CreateAsync();
        Shader shader = await ShaderAsset.GetAsync(content.Assets, content.Shader, content.Shaders, content.Slang, content.Settings);
        StandardMaterial material = await content.Material.CreateAsync<StandardMaterial>(content.Assets,
            new Dictionary<AssetId<ShaderAsset>, Shader> { [content.Shader] = shader },
            new Dictionary<AssetId<TextureAsset>, Texture>());
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor());
        var entity = game.CreateEntity(new WorldTransform());
        game.Add(entity, new MeshInstance { Mesh = new TestAssets().Mesh(1) });
        game.AddBuffer<MeshMaterialBinding>(entity);
        game.ExecuteBufferWrite<MeshMaterialBinding>(entity, values => values.Add(new(material)));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Same(material, render.ReadMaterial(material.MaterialId));
        Assert.Equal(new float[] { .25f, .75f }, ExecuteEntries((StandardMaterial)render.ReadMaterial(material.MaterialId)));

        material.Roughness = .75f;
        material.BaseColorTint = new(.25f, 1, 1, 1);
        extraction.Extract(game);
        graphics.Apply(extraction, render);

        Assert.Equal(material.Revision, render.ReadMaterialRevision(material.MaterialId));
        Assert.Equal(new float[] { .75f, 1 }, ExecuteEntries((StandardMaterial)render.ReadMaterial(material.MaterialId)));
    }

    [Fact]
    public async Task SavedCommonCodeFeedsTheSameExtractedMaterialFields()
    {
        await using var content = await SharedMaterialContent.CreateAsync();
        using var output = new StringWriter();
        Assert.Equal(0, await AssetCommands.RunAsync(content.Project, ["build", content.ShaderPath], output));
        Shader shader = await ShaderAsset.GetAsync(content.Assets, content.Shader, content.Shaders, content.Slang, content.Settings);
        StandardMaterial material = await content.Material.CreateAsync<StandardMaterial>(content.Assets,
            new Dictionary<AssetId<ShaderAsset>, Shader> { [content.Shader] = shader },
            new Dictionary<AssetId<TextureAsset>, Texture>());
        Assert.True(material.Shader!.GetSlangEntryBytes(0,
            material.Shader.RequireSlangEntry(0, "FirstShade", SlangStage.Compute)).Length > 0);
        using var game = new World();
        using var render = new RenderWorld();
        using var extraction = new RenderExtractionSystems(render, new MeshRenderExtractor());
        var entity = game.CreateEntity(new WorldTransform());
        game.Add(entity, new MeshInstance { Mesh = new TestAssets().Mesh(1) });
        game.AddBuffer<MeshMaterialBinding>(entity);
        game.ExecuteBufferWrite<MeshMaterialBinding>(entity, values => values.Add(new(material)));
        extraction.Extract(game);
        graphics.Apply(extraction, render);
        Assert.Equal(new float[] { .25f, .75f }, ExecuteEntries((StandardMaterial)render.ReadMaterial(material.MaterialId)));

        material.Roughness = .75f;
        material.BaseColorTint = new(.25f, 1, 1, 1);
        extraction.Extract(game);
        graphics.Apply(extraction, render);

        Assert.Same(material.Shader, await ShaderAsset.GetAsync(content.Assets, content.Shader, content.Shaders, content.Slang, content.Settings));
        Assert.Equal(new float[] { .75f, 1 }, ExecuteEntries((StandardMaterial)render.ReadMaterial(material.MaterialId)));
    }

    private float[] ExecuteEntries(StandardMaterial material)
    {
        var backend = graphics.Backend;
        var device = graphics.Device;
        Shader shader = material.Shader!;
        int target = shader.RequireSlangTarget(SlangCompileTarget.Dxil);
        ShaderReflection reflection = shader.GetSlangReflection(target);
        EntryPointReflection firstEntry = shader.RequireSlangEntry(target, "FirstShade", SlangStage.Compute);
        EntryPointReflection secondEntry = shader.RequireSlangEntry(target, "SecondShade", SlangStage.Compute);
        using Pipeline first = backend.CreateComputePipeline(device, new(shader.SlangCode, reflection, firstEntry,
            shader.GetSlangEntryBytes(target, firstEntry), CodeBlob: shader.GetSlangEntryBlob(target, firstEntry)));
        using Pipeline second = backend.CreateComputePipeline(device, new(shader.SlangCode, reflection, secondEntry,
            shader.GetSlangEntryBytes(target, secondEntry), CodeBlob: shader.GetSlangEntryBlob(target, secondEntry)));
        using Buffer firstResult = backend.CreateBuffer(device, new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using Buffer secondResult = backend.CreateBuffer(device, new(4, BufferUsages.ShaderWrite | BufferUsages.CopySource));
        using BufferUav firstView = backend.CreateBufferUav(device, new(firstResult, BufferRange.Whole, StructureStride: 4));
        using BufferUav secondView = backend.CreateBufferUav(device, new(secondResult, BufferRange.Whole, StructureStride: 4));
        using Buffer readback = backend.CreateBuffer(device, new(8, BufferUsages.CopyDestination), MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(device, new(QueueType.Graphics, 0, InitialSlotCount: 1));
        backend.Begin(context, new CommandRecordingDesc(16, 4, 16));
        foreach (Buffer result in new[] { firstResult, secondResult })
            backend.Barrier(context, new BufferBarrier(result, PipelineSync.None, PipelineSync.ComputeShading,
                ResourceAccess.NoAccess, ResourceAccess.UnorderedAccess));
        VariableLayoutReflection globals = reflection.GetGlobalParamsVarLayout()!.Value;
        backend.SetPipeline(context, first);
        backend.SetTransientParameterBindings(context, new(FindParameters(globals, "FirstResult"),
            [ResourceBinding.WritableBuffer(firstView)], []));
        backend.SetTransientParameterBindings(context, new(firstEntry.VarLayout, [], ScalarBytes(firstEntry, material)));
        backend.Dispatch(context, new(1, 1, 1));
        backend.SetPipeline(context, second);
        backend.SetTransientParameterBindings(context, new(FindParameters(globals, "SecondResult"),
            [ResourceBinding.WritableBuffer(secondView)], []));
        backend.SetTransientParameterBindings(context, new(secondEntry.VarLayout, [], ScalarBytes(secondEntry, material)));
        backend.Dispatch(context, new(1, 1, 1));
        foreach (Buffer result in new[] { firstResult, secondResult })
            backend.Barrier(context, new BufferBarrier(result, PipelineSync.ComputeShading, PipelineSync.Copy,
                ResourceAccess.UnorderedAccess, ResourceAccess.CopySource));
        backend.CopyBuffer(context, new(firstResult, 0, readback, 0, 4));
        backend.CopyBuffer(context, new(secondResult, 0, readback, 4, 4));
        using RecordedCommands commands = backend.End(context);
        QueueCompletion completed = backend.Submit(backend.GetQueue(device, QueueType.Graphics), new([], [], [commands], [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completed, TimeSpan.FromSeconds(30)));
        using MappedBuffer pixels = backend.Map(readback, MapType.Read, BufferRange.Whole);
        pixels.Invalidate(new(0, 8));
        return MemoryMarshal.Cast<byte, float>(pixels.Bytes).ToArray();
    }

    private static byte[] ScalarBytes(EntryPointReflection entry, StandardMaterial material)
    {
        TypeLayoutReflection parameters = entry.VarLayout.TypeLayout;
        if (parameters.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            parameters = parameters.ElementTypeLayout;
        var bytes = new byte[checked((int)parameters.GetSize(SlangParameterCategory.Uniform))];
        VariableLayoutReflection values = FindField(parameters, "values");
        int offset = checked((int)values.GetOffset(SlangParameterCategory.Uniform));
        int tint = offset + checked((int)FindField(values.TypeLayout, "BaseColorTint").GetOffset(SlangParameterCategory.Uniform));
        int roughness = offset + checked((int)FindField(values.TypeLayout, "Roughness").GetOffset(SlangParameterCategory.Uniform));
        Vector4 color = material.BaseColorTint;
        float surface = material.Roughness;
        MemoryMarshal.Write(bytes.AsSpan(tint), in color);
        MemoryMarshal.Write(bytes.AsSpan(roughness), in surface);
        return bytes;
    }

    private static VariableLayoutReflection FindField(TypeLayoutReflection layout, string name)
    {
        for (uint index = 0; index < layout.FieldCount; index++)
            if (layout.GetFieldByIndex(index).Name == name) return layout.GetFieldByIndex(index);
        throw new InvalidDataException(name);
    }

    private static VariableLayoutReflection FindParameters(VariableLayoutReflection globals, string name)
    {
        TypeLayoutReflection data = globals.TypeLayout;
        if (data.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            data = data.ElementTypeLayout;
        for (nint index = 0; index < data.SubObjectRangeCount; index++)
            if (data.GetBindingRangeLeafVariable(data.GetSubObjectRangeBindingRangeIndex(index)).Name == name)
                return data.GetSubObjectRangeOffset(index);
        throw new InvalidDataException(name);
    }
}

internal sealed class SharedMaterialContent : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-SharedMaterial-{Guid.NewGuid():N}");
    public AssetProject Project { get; private set; } = null!;
    private AssetLoader? _assets;
    private IGlobalSession _slang = null!;
    private SessionDesc _settings;
    public AssetLoader Assets => _assets ??= new(Project.CreateStorage());
    public WeakValues<AssetResourceKey<ShaderAsset>, Shader> Shaders { get; } = new();
    public IGlobalSession Slang => _slang;
    public SessionDesc Settings => _settings;
    public MaterialAsset Material { get; } = new() { Name = "Shared business material" };
    public AssetId<ShaderAsset> Shader { get; private set; }
    public string ShaderPath => Path.Combine(_directory, "shared.shader.asset");

    public static async Task<SharedMaterialContent> CreateAsync()
    {
        var result = new SharedMaterialContent();
        Directory.CreateDirectory(result._directory);
        try
        {
            result.Project = new AssetProject(result._directory, []);
            IGlobalSession slang = SlangShaderImporter.GlobalSession;
            SessionDesc settings = SlangShaderImporter.CreateSessionDescription(result.ShaderPath, SlangShaderImporter.CreateDefaultTargets(slang));
            using var asset = new ShaderAsset { Name = "Shared Standard" };
            using ShaderSourceBlock inputs = StandardMaterialInputs.Create();
            using var first = new FirstMaterialEntriesBlock { SourceNames = ["first_material.slang"] };
            using var second = new SecondMaterialEntriesBlock { SourceNames = ["second_material.slang"] };
            slang.CreateSession(settings, out ISession session);
            for (int index = 0; index < inputs.SourceNames.Count; index++)
                session.LoadModuleFromIRBlob(Path.GetFileNameWithoutExtension(inputs.SourceNames[index]), inputs.SourceNames[index],
                    Slang.CreateBlob((await inputs.ReadBytesAsync(ShaderInputBlock.SerializedSourceKey(index))).Span), out _);
            first.SetSerializedSource(0, Serialize(session, "first_material", "FirstShade", "FirstResult", "values.Roughness"));
            second.SetSerializedSource(0, Serialize(session, "second_material", "SecondShade", "SecondResult", "values.Roughness + values.BaseColorTint.x"));
            asset.SetBlock(inputs);
            asset.SetBlock(first);
            asset.SetBlock(second);
            asset.SelectInputs<ShaderSourceBlock>();
            asset.SelectInputs<FirstMaterialEntriesBlock>();
            asset.SelectInputs<SecondMaterialEntriesBlock>();
            result.Project.RegisterAsset(await asset.SaveAsync(result.ShaderPath));
            result.Shader = asset.Reference;
            result._slang = slang;
            result._settings = settings;
            result.Material.GetBlock<MaterialBlock>().Shader = asset.Reference;
            _ = new StandardMaterialParameters(result.Material.GetBlock<MaterialBlock>()) { Roughness = .25f, BaseColorTint = new(.5f, 1, 1, 1) };
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    private static byte[] Serialize(ISession session, string sourceName, string entryName, string resultName, string value)
    {
        IModule source = session.LoadModuleFromSource(sourceName, sourceName + ".slang", Slang.CreateBlob(System.Text.Encoding.UTF8.GetBytes($$"""
            import standard_material;
            struct {{resultName}}Data { RWStructuredBuffer<float> Value; };
            ParameterBlock<{{resultName}}Data> {{resultName}};
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void {{entryName}}(uniform StandardScalars values) { {{resultName}}.Value[0] = {{value}}; }
            """)), out ISlangBlob? diagnostics)!;
        Assert.True(source is not null, diagnostics?.AsString);
        Assert.True(source!.Serialize(out ISlangBlob serialized).Succeeded);
        return serialized.Buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        Material.Dispose();
        try { if (_assets is not null) await _assets.DisposeAsync(); }
        finally { Directory.Delete(_directory, recursive: true); }
    }
}

[AssetBlock("SomeEngine.Render.Tests.FirstMaterialEntries")]
public sealed partial class FirstMaterialEntriesBlock : ShaderInputBlock;

[AssetBlock("SomeEngine.Render.Tests.SecondMaterialEntries")]
public sealed partial class SecondMaterialEntriesBlock : ShaderInputBlock;
