using SomeEngine.Resources;
using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.Render.Features.Motion;
using SomeEngine.Render.Features.Temporal;
using SomeEngine.Render.Frame;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class TemporalRenderingTests
{
    [Fact]
    public async Task HistoryBlendsOnlyCompletedFramesFromTheSameView()
    {
        string root = FindRoot();
        AssetManifest manifest = AssetManifest.Load(Path.Combine(root, "Library", "AssetManifest"));
        await using var assets = new AssetLoader(new LooseAssetStorage(root, manifest));
        var record = manifest.List(ShaderAsset.FileType).Single(item => item.Name == "temporal_resolve");
        using ShaderAsset file = await ShaderAsset.OpenAsync(assets, new(record.AssetGuid));
        using Shader shader = await file.GetBlock<ShaderBlock>().CreateAsync(file.Name);
        using IGraphicsBackend backend = new ValidationLayer(D3D12GraphicsBackend.Create());
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(enumeration, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(enumeration, adapters, out _);
        using Device device = backend.CreateDevice(new DeviceDesc(adapters.First(item => !item.HardwareAccelerated).Id,
            [new DeviceQueueDesc(QueueType.Graphics)]));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using var world = new World();
        Entity firstView = world.CreateEntity();
        Entity secondView = world.CreateEntity();
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(1024, BufferUsages.CopyDestination), MemoryType.Readback);
        using var graph = new global::SomeEngine.RenderGraph.RenderGraph(backend, device, [queue]);
        GraphTextureId color, motion, depth;
        GraphBufferInput input;
        GraphPassId upload;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            color = edit.CreateTexture(Description(Format.R16G16B16A16Float));
            motion = edit.CreateTexture(Description(Format.R16G16Float));
            depth = edit.CreateTexture(Description(Format.D32Float));
            input = edit.CreateBufferInput(BufferUsages.CopySource);
            upload = edit.AddCopyFramePass("Upload temporal inputs", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (input, color, motion, depth), default,
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphTextureId Color, GraphTextureId Motion, GraphTextureId Depth) data) =>
                {
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Color, Range(TextureAspects.Color), PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
                    pass.Write(data.Motion, Range(TextureAspects.Color), PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
                    pass.Write(data.Depth, Range(TextureAspects.Depth), PipelineSync.Copy, ResourceAccess.CopyDestination, TextureLayout.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphBufferInput Input, GraphTextureId Color, GraphTextureId Motion, GraphTextureId Depth) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    commands.CopyBufferToTexture(new BufferTextureCopy(source.Buffer, source.Range.Offset, 256, 4,
                        commands.GetTexture(data.Color), 0, 0, TextureAspects.Color, 0, 0, 0, 4, 4, 1));
                    commands.CopyBufferToTexture(new BufferTextureCopy(source.Buffer, source.Range.Offset + 1024, 256, 4,
                        commands.GetTexture(data.Motion), 0, 0, TextureAspects.Color, 0, 0, 0, 4, 4, 1));
                    commands.CopyBufferToTexture(new BufferTextureCopy(source.Buffer, source.Range.Offset + 2048, 256, 4,
                        commands.GetTexture(data.Depth), 0, 0, TextureAspects.Depth, 0, 0, 0, 4, 4, 1));
                });
            edit.Commit();
        }
        using var temporal = new TemporalFeature(backend, device, graph, 4, 4, shader, "VSMain", "PSMain");
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            var ordering = edit;
            temporal.OrderAfter(ref ordering, upload);
            GraphBufferId result = edit.RegisterExternalBuffer(readback,
                [new BufferBoundaryState(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            GraphPassId copy = edit.AddCopyFramePass("Read temporal color", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (temporal.Output.ResolvedColor, result), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphTextureId Color, GraphBufferId Result) data) =>
                {
                    pass.Read(data.Color, Range(TextureAspects.Color), PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                    pass.Write(data.Result, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphTextureId Color, GraphBufferId Result) data) =>
                    commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Result), 0, 256, 4,
                        commands.GetTexture(data.Color), 0, 0, TextureAspects.Color, 0, 0, 0, 4, 4, 1)));
            edit.OrderAfter(copy, temporal.Output.CompletePass);
            edit.Commit();
        }

        Assert.InRange(Render(1, firstView), .999f, 1.001f);
        Assert.InRange(Render(.8f, firstView), .979f, .981f);
        Render(.1f, firstView, cancel: true);
        Assert.InRange(Render(.8f, firstView), .961f, .963f);
        Assert.InRange(Render(.2f, firstView, enabled: false), .199f, .201f);
        Assert.InRange(Render(.4f, firstView, cameraCut: true), .399f, .401f);
        Assert.InRange(Render(.6f, secondView), .599f, .601f);
        temporal.InvalidateHistory();
        Assert.InRange(Render(.3f, secondView), .299f, .301f);
        Render(1, secondView, cameraCut: true, depthValue: .9999f);
        Assert.Equal(0, Render(0, secondView, depthValue: 1));
        Render(1, secondView, cameraCut: true, depthValue: .9999f);
        Assert.InRange(Render(0, secondView, depthValue: 1, foregroundEdge: true, sampleX: 1), .89f, .91f);
        Assert.Equal(0, Render(0, secondView, depthValue: 1, sampleX: 1));

        float Render(float red, Entity view, bool enabled = true, bool cameraCut = false, bool cancel = false,
            float depthValue = .5f, bool foregroundEdge = false, int sampleX = 0)
        {
            byte[] bytes = new byte[3072];
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                bool foreground = foregroundEdge && x == 0;
                Half[] pixel = [(Half)(foreground ? 1 : red), (Half)0, (Half)0, (Half)1];
                MemoryMarshal.AsBytes(pixel.AsSpan()).CopyTo(bytes.AsSpan(y * 256 + x * 8));
                float distance = foreground ? .9999f : depthValue;
                MemoryMarshal.Write(bytes.AsSpan(2048 + y * 256 + x * 4), in distance);
            }
            var frame = graph.BeginFrame();
            try
            {
                frame.SetBufferInput(input, frame.Upload<byte>(bytes, BufferUsages.CopySource));
                temporal.Record(ref frame, color, depth, new MotionFeatureOutput(motion, 4, 4, 1),
                    new TemporalResolveSettings(.9f, 2, 1, 0), enabled, cameraCut, view);
                if (cancel) return 0;
                var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
                frame.Execute(completions);
                foreach (QueueCompletion completion in completions)
                    if (completion != default)
                        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(20)));
            }
            finally { frame.Dispose(); }
            using MappedBuffer mapping = backend.Map(readback, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            float value = (float)MemoryMarshal.Read<Half>(mapping.Bytes[(sampleX * 8)..]);
            Assert.True(float.IsFinite(value));
            return value;
        }
    }

    private static TextureDesc Description(Format format) => new(TextureDimension.Texture2D, 4, 4, 1, 1, 1, 1,
        format, TextureUsages.Sampled | TextureUsages.CopySource | TextureUsages.CopyDestination);
    private static TextureSubresourceRange Range(TextureAspects aspects) => new(0, 1, 0, 1, aspects);
    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SomeEngine.slnx"))) return Path.Combine(directory.FullName, "templates", "DefaultProject");
        throw new DirectoryNotFoundException();
    }
}
