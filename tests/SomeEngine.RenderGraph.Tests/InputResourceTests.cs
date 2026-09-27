using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class InputResourceTests
{
    [Fact]
    public void BufferInputsRefreshViewsAndCopySlicesAcrossFrames()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        using Buffer result = support.Backend.CreateBuffer(support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphBufferInput input;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateBufferInput(BufferUsages.ShaderRead | BufferUsages.CopySource);
            GraphBufferSrvId view = edit.CreateBufferSrv(input);
            GraphBufferId destination = edit.RegisterExternalBuffer(result,
                [new BufferBoundaryState(BufferRange.Whole, result.InitialSync, result.InitialAccess, ResourceContentState.Undefined)]);
            edit.AddGeneralFramePass("copy input slice", PassQueueSelection.AnyOfType(QueueType.Graphics),
                (input, view, destination), new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition pass, ref (GraphBufferInput Input, GraphBufferSrvId View, GraphBufferId Destination) data) =>
                {
                    pass.Read(data.View, PipelineSync.ComputeShading);
                    pass.Read(data.Input, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                    pass.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Partial);
                },
                static (ref GeneralPassCommandScope commands, in (GraphBufferInput Input, GraphBufferSrvId View, GraphBufferId Destination) data) =>
                {
                    BufferRegion source = commands.GetBufferRegion(data.Input);
                    BufferSrv view = commands.GetBufferSrv(data.View);
                    Assert.Same(source.Buffer, view.Description.Buffer);
                    Assert.Equal(source.Range, view.Description.Range);
                    commands.CopyBuffer(new BufferCopy(source.Buffer, source.Range.Offset,
                        commands.GetBuffer(data.Destination), 0, source.Range.Size));
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 18; index++)
        {
            byte[] bytes = Enumerable.Range(0, index % 4 == 0 ? 32 : 16).Select(value => (byte)(index * 7 + value)).ToArray();
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                if (index % 2 != 0) _ = frame.Upload(new byte[64], BufferUsages.ShaderRead | BufferUsages.CopySource);
                frame.SetBufferInput(input, frame.Upload(bytes, BufferUsages.ShaderRead | BufferUsages.CopySource));
                frame.Execute(completions);
                support.Wait(completions);
            }
            using MappedBuffer mapping = support.Backend.Map(result, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            Assert.True(mapping.Bytes[..bytes.Length].SequenceEqual(bytes));
        }
    }

    [Fact]
    public void TextureInputsRefreshViewsWhenTheirExternalTextureChanges()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        TextureDesc description = new(TextureDimension.Texture2D, 8, 8, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.Sampled);
        using Texture first = support.Backend.CreateTexture(support.Device, description);
        using Texture second = support.Backend.CreateTexture(support.Device, description);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue]);
        GraphTextureInput input;
        GraphTextureId resource;
        Texture expected = first;
        var used = new HashSet<Texture>();
        TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            input = edit.CreateTextureInput();
            resource = edit.DeclareExternalTexture(description);
            GraphTextureSrvId view = edit.CreateTextureSrv(input, Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D);
            edit.AddGeneralFramePass("read input texture view", PassQueueSelection.AnyOfType(QueueType.Graphics), view,
                new PassOptions(Culling: PassCullingMode.NeverCull, Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition pass, ref GraphTextureSrvId data) => pass.Read(data, PipelineSync.ComputeShading),
                (ref GeneralPassCommandScope commands, in GraphTextureSrvId data) =>
                {
                    Assert.Same(expected, commands.GetTexture(input));
                    Assert.Same(expected, commands.GetTextureSrv(data).Description.Texture);
                    Assert.Equal(range, commands.GetTextureSrv(data).Description.Range);
                });
            edit.Commit();
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 18; index++)
        {
            expected = index % 4 < 2 ? first : second;
            bool firstUse = used.Add(expected);
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.BindExternalTexture(resource, expected,
                [new TextureBoundaryState(range,
                    firstUse ? expected.InitialSync : PipelineSync.ComputeShading,
                    firstUse ? expected.InitialAccess : ResourceAccess.ShaderResource,
                    firstUse ? expected.InitialLayout : TextureLayout.ShaderResource, ResourceContentState.Defined)]);
            frame.SetTextureInput(input, resource);
            frame.Execute(completions);
            support.Wait(completions);
        }
    }
}
