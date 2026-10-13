using System.Numerics;
using SomeEngine.Graphics;

namespace SomeEngine.RenderGraph.Tests;

public sealed class BoundaryStateRefreshTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReboundBufferKeepsTheCurrentUntouchedAccessScope(int framesInFlight)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        var description = new BufferDesc(16, BufferUsages.CopySource | BufferUsages.ShaderRead);
        using Buffer source = support.Backend.CreateBuffer(support.Device, description, MemoryType.Upload);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: (uint)framesInFlight));
        GraphBufferId buffer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.DeclareExternalBuffer(description, MemoryType.Upload);
            edit.DeclareCopyPass("read the first bytes", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition pass) => pass.Read(buffer, new BufferRange(0, 4),
                    PipelineSync.Copy, ResourceAccess.CopySource),
                options: new PassOptions(Culling: PassCullingMode.NeverCull));
            edit.Commit();
        }
        var receiver = new BufferStates(buffer);
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 8; index++)
        {
            PipelineSync sync = index < 5 ? PipelineSync.ComputeShading : PipelineSync.PixelShading;
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.BindExternalBuffer(buffer, source,
                [new(new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource, ResourceContentState.Defined, support.GraphicsQueue),
                 new(new(4, 12), sync, ResourceAccess.ShaderResource, ResourceContentState.Defined, support.GraphicsQueue)]);
            frame.ObserveExecution(receiver);
            frame.Execute(submitted);
            support.Wait(submitted);
            BufferBoundaryState untouched = Assert.Single(receiver.States, state => state.Range == new BufferRange(4, 12));
            Assert.Equal(sync, untouched.Sync);
            Assert.Equal(ResourceAccess.ShaderResource, untouched.Access);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UnchangedBufferShapeKeepsEachRangesCurrentCompletion(int framesInFlight)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        var description = new BufferDesc(16, BufferUsages.CopySource | BufferUsages.ShaderRead);
        using Buffer source = support.Backend.CreateBuffer(support.Device, description, MemoryType.Upload);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: (uint)framesInFlight));
        GraphBufferId buffer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.DeclareExternalBuffer(description, MemoryType.Upload);
            edit.DeclareCopyPass("read the first bytes", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition pass) => pass.Read(buffer, new BufferRange(0, 4),
                    PipelineSync.Copy, ResourceAccess.CopySource),
                options: new PassOptions(Culling: PassCullingMode.NeverCull));
            edit.Commit();
        }
        var receiver = new BufferStates(buffer);
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 8; index++)
        {
            QueueCompletion prior = support.Backend.Submit(support.GraphicsQueue, new([], [], [], [], []));
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.BindExternalBuffer(buffer, source,
                [new(new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource, ResourceContentState.Defined, support.GraphicsQueue, prior),
                 new(new(4, 12), PipelineSync.ComputeShading, ResourceAccess.ShaderResource, ResourceContentState.Defined, support.GraphicsQueue, prior)]);
            frame.ObserveExecution(receiver);
            frame.Execute(submitted);
            support.Wait(submitted);
            Assert.Equal(submitted[0], Assert.Single(receiver.States, state => state.Range == new BufferRange(0, 4)).ReadyAfter);
            Assert.Equal(prior, Assert.Single(receiver.States, state => state.Range == new BufferRange(4, 12)).ReadyAfter);
        }
    }

    [Fact]
    public void AbandonedBufferBindingDoesNotReplaceTheNextSubmittedRanges()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        var description = new BufferDesc(16, BufferUsages.CopySource | BufferUsages.ShaderRead);
        using Buffer source = support.Backend.CreateBuffer(support.Device, description, MemoryType.Upload);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphBufferId buffer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            buffer = edit.DeclareExternalBuffer(description, MemoryType.Upload);
            edit.DeclareCopyPass("read the first bytes", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition pass) => pass.Read(buffer, new BufferRange(0, 4),
                    PipelineSync.Copy, ResourceAccess.CopySource),
                options: new PassOptions(Culling: PassCullingMode.NeverCull));
            edit.Commit();
        }
        BufferBoundaryState[] initial =
            [new(new(0, 4), PipelineSync.Copy, ResourceAccess.CopySource, ResourceContentState.Defined, support.GraphicsQueue),
             new(new(4, 12), PipelineSync.ComputeShading, ResourceAccess.ShaderResource, ResourceContentState.Defined, support.GraphicsQueue)];
        var receiver = new BufferStates(buffer);
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int index = 0; index < 4; index++)
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.BindExternalBuffer(buffer, source, initial);
            frame.ObserveExecution(receiver);
            frame.Execute(submitted);
            support.Wait(submitted);
        }
        using (RenderGraphFrame abandoned = graph.BeginFrame())
            abandoned.BindExternalBuffer(buffer, source,
                [initial[0], initial[1] with { Range = new BufferRange(8, 8), Sync = PipelineSync.PixelShading }]);
        using (RenderGraphFrame next = graph.BeginFrame())
        {
            next.BindExternalBuffer(buffer, source, initial);
            next.ObserveExecution(receiver);
            next.Execute(submitted);
            support.Wait(submitted);
        }
        Assert.Equal(initial[1], Assert.Single(receiver.States, state => state.Range == initial[1].Range));
        Assert.DoesNotContain(receiver.States, state => state.Range == new BufferRange(8, 8));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReboundTextureKeepsTheCurrentUntouchedSubresources(int framesInFlight)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        var description = new TextureDesc(TextureDimension.Texture2D, 4, 4, 1, 1, 3, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment);
        using Texture texture = support.Backend.CreateTexture(support.Device, description);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: (uint)framesInFlight));
        GraphTextureId target;
        TextureSubresourceRange written = new(0, 1, 0, 1, TextureAspects.Color);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.DeclareExternalTexture(description);
            edit.DeclareCopyPass("clear the first layer", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition pass) => pass.Write(target, written, PipelineSync.RenderTarget,
                    ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete),
                (ref CopyPassCommandScope commands) => commands.ClearTexture(commands.GetTexture(target), written, Vector4.One));
            edit.Commit();
        }
        var receiver = new TextureStates(target);
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        TextureBoundaryState first = new(written, texture.InitialSync, texture.InitialAccess,
            texture.InitialLayout, ResourceContentState.Undefined);
        for (int index = 0; index < 8; index++)
        {
            TextureSubresourceRange untouched = new(0, 1, index < 5 ? 1u : 2u, 1, TextureAspects.Color);
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.BindExternalTexture(target, texture,
                [first, new(untouched, texture.InitialSync, texture.InitialAccess,
                    texture.InitialLayout, ResourceContentState.Undefined)]);
            frame.ObserveExecution(receiver);
            frame.Execute(submitted);
            support.Wait(submitted);
            first = Assert.Single(receiver.States, state => state.Range == written);
            Assert.Equal(ResourceContentState.Undefined, Assert.Single(receiver.States, state => state.Range == untouched).Contents);
            Assert.Equal(2, receiver.States.Length);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void UnchangedTextureShapeKeepsEachSubresourcesCurrentCompletion(int framesInFlight)
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        var description = new TextureDesc(TextureDimension.Texture2D, 4, 4, 1, 1, 2, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment);
        using Texture texture = support.Backend.CreateTexture(support.Device, description);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: (uint)framesInFlight));
        GraphTextureId target;
        TextureSubresourceRange written = new(0, 1, 0, 1, TextureAspects.Color);
        TextureSubresourceRange untouched = new(0, 1, 1, 1, TextureAspects.Color);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.DeclareExternalTexture(description);
            edit.DeclareCopyPass("clear the first layer", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition pass) => pass.Write(target, written, PipelineSync.RenderTarget,
                    ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete),
                (ref CopyPassCommandScope commands) => commands.ClearTexture(commands.GetTexture(target), written, Vector4.One));
            edit.Commit();
        }
        var receiver = new TextureStates(target);
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        TextureBoundaryState first = new(written, texture.InitialSync, texture.InitialAccess,
            texture.InitialLayout, ResourceContentState.Undefined);
        for (int index = 0; index < 8; index++)
        {
            QueueCompletion prior = support.Backend.Submit(support.GraphicsQueue, new([], [], [], [], []));
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.BindExternalTexture(target, texture,
                [first, new(untouched, texture.InitialSync, texture.InitialAccess,
                    texture.InitialLayout, ResourceContentState.Undefined, support.GraphicsQueue, prior)]);
            frame.ObserveExecution(receiver);
            frame.Execute(submitted);
            support.Wait(submitted);
            first = Assert.Single(receiver.States, state => state.Range == written);
            Assert.Equal(submitted[0], first.ReadyAfter);
            Assert.Equal(prior, Assert.Single(receiver.States, state => state.Range == untouched).ReadyAfter);
        }
    }

    [Fact]
    public void AbandonedTextureBindingDoesNotReplaceTheNextSubmittedSubresources()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        var description = new TextureDesc(TextureDimension.Texture2D, 4, 4, 1, 1, 3, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment);
        using Texture texture = support.Backend.CreateTexture(support.Device, description);
        using var graph = new RenderGraph(support.Backend, support.Device, [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        GraphTextureId target;
        TextureSubresourceRange written = new(0, 1, 0, 1, TextureAspects.Color);
        TextureSubresourceRange untouched = new(0, 1, 1, 1, TextureAspects.Color);
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.DeclareExternalTexture(description);
            edit.DeclareCopyPass("clear the first layer", PassQueueSelection.Exact(support.GraphicsQueue),
                (ref PassDefinition pass) => pass.Write(target, written, PipelineSync.RenderTarget,
                    ResourceAccess.RenderTarget, TextureLayout.RenderTarget, WriteCoverage.Complete),
                (ref CopyPassCommandScope commands) => commands.ClearTexture(commands.GetTexture(target), written, Vector4.One));
            edit.Commit();
        }
        var receiver = new TextureStates(target);
        var submitted = new QueueCompletion[graph.MaximumQueueCompletionCount];
        TextureBoundaryState[] states =
            [new(written, texture.InitialSync, texture.InitialAccess, texture.InitialLayout, ResourceContentState.Undefined),
             new(untouched, texture.InitialSync, texture.InitialAccess, texture.InitialLayout, ResourceContentState.Undefined)];
        for (int index = 0; index < 4; index++)
        {
            using RenderGraphFrame frame = graph.BeginFrame();
            frame.BindExternalTexture(target, texture, states);
            frame.ObserveExecution(receiver);
            frame.Execute(submitted);
            support.Wait(submitted);
            states = [Assert.Single(receiver.States, state => state.Range == written),
                Assert.Single(receiver.States, state => state.Range == untouched)];
        }
        using (RenderGraphFrame abandoned = graph.BeginFrame())
            abandoned.BindExternalTexture(target, texture,
                [states[0], states[1] with { Range = new TextureSubresourceRange(0, 1, 2, 1, TextureAspects.Color) }]);
        using (RenderGraphFrame next = graph.BeginFrame())
        {
            next.BindExternalTexture(target, texture, states);
            next.ObserveExecution(receiver);
            next.Execute(submitted);
            support.Wait(submitted);
        }
        Assert.Equal(2, receiver.States.Length);
        Assert.Equal(ResourceContentState.Undefined, Assert.Single(receiver.States, state => state.Range == untouched).Contents);
    }

    private sealed class BufferStates(GraphBufferId buffer) : IRenderGraphFrameExecutionObserver
    {
        internal BufferBoundaryState[] States = [];
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) => States = result.GetBufferStates(buffer).ToArray();
    }

    private sealed class TextureStates(GraphTextureId texture) : IRenderGraphFrameExecutionObserver
    {
        internal TextureBoundaryState[] States = [];
        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result) => States = result.GetTextureStates(texture).ToArray();
    }
}
