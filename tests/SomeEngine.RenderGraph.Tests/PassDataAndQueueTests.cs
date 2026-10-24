using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class PassDataAndQueueTests
{
    [Fact]
    public void PersistentPassDataRemainsUntilUpdated()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        int[] observed = [0];
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            pass = edit.AddCopyPass<ObservationState, int>(
                "retained pass data",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new ObservationState(observed),
                new PassOptions(
                    Culling: PassCullingMode.NeverCull,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition _, ref ObservationState _) => { },
                static (ref CopyPassCommandScope _, in ObservationState state, in int frameValue) =>
                    state.Destination[0] = frameValue);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, 17);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(17, observed[0]);

        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(17, observed[0]);
    }

    [Fact]
    public void FrameOnlyPassRetainsUpdatedData()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        int[] observed = [0];
        GraphPassId pass;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            pass = edit.AddCopyFramePass(
                "retained frame-only data",
                PassQueueSelection.Exact(support.GraphicsQueue),
                new ObservationState(observed),
                new PassOptions(
                    Culling: PassCullingMode.NeverCull,
                    Recording: PassRecordingMode.CallingThread),
                static (ref PassDefinition _, ref ObservationState _) => { },
                static (ref CopyPassCommandScope _, in ObservationState state) =>
                    state.Destination[0]++);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(1, observed[0]);

        int[] overrideObserved = [10];
        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.SetPassData(pass, new ObservationState(overrideObserved));
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(1, observed[0]);
        Assert.Equal(11, overrideObserved[0]);

        completions.AsSpan().Clear();
        using (RenderGraphFrame frame = graph.BeginFrame())
            Assert.Equal(1, frame.Execute(completions));
        support.Wait(completions.AsSpan(0, 1));
        Assert.Equal(1, observed[0]);
        Assert.Equal(12, overrideObserved[0]);
    }

    [Fact]
    public void GeneralPassRequiresAGraphicsQueue()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphEdit edit = graph.BeginEdit();
        _ = edit.DeclareGeneralPass("General on Copy", PassQueueSelection.Exact(support.CopyQueue));
        bool rejected = false;
        try
        {
            edit.Commit();
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A general pass must not be accepted on a Copy Queue.");
    }

    [Fact]
    public void Explicit_order_can_place_a_frame_writer_before_a_retained_texture_reader()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        TextureDesc description = new(
            TextureDimension.Texture2D,
            4,
            4,
            1,
            1,
            1,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsages.Sampled | TextureUsages.CopyDestination,
            label: "late frame upload texture");
        using Texture texture = support.Backend.CreateTexture(support.Device, description);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        GraphTextureId slot;
        GraphPassId consumer;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            slot = edit.DeclareExternalTexture(description);
            consumer = edit.AddGeneralFramePass(
                "retained texture consumer",
                PassQueueSelection.Exact(support.GraphicsQueue),
                slot,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphTextureId state) =>
                    _ = access.Read(
                        state,
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.PixelShading,
                        ResourceAccess.ShaderResource,
                        TextureLayout.ShaderResource),
                static (ref GeneralPassCommandScope _, in GraphTextureId _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        using (RenderGraphFrame frame = graph.BeginFrame())
        {
            frame.BindExternalTexture(
                slot,
                texture,
                [new TextureBoundaryState(
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    texture.InitialSync,
                    texture.InitialAccess,
                    texture.InitialLayout,
                    ResourceContentState.Undefined)]);
            GraphPassId upload = frame.AddGeneralPass(
                "late frame upload",
                PassQueueSelection.Exact(support.GraphicsQueue),
                slot,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphTextureId state) =>
                    _ = access.Write(
                        state,
                        new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        TextureLayout.CopyDestination,
                        WriteCoverage.Complete,
                        ResourceContentState.Defined),
                static (ref GeneralPassCommandScope _, in GraphTextureId _) => { });
            frame.OrderAfter(consumer, upload);
            Assert.Equal(1, frame.Execute(completions));
        }
        support.Wait(completions.AsSpan(0, 1));
    }

    [Fact]
    public void FrameGeneralPassRejectsCopyQueueDuringAuthoring()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);
        using RenderGraphFrame frame = graph.BeginFrame();

        bool rejected = false;
        try
        {
            _ = frame.DeclareGeneralPass("General on Copy", PassQueueSelection.Exact(support.CopyQueue));
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Assert.True(rejected, "A frame-local general pass must reject a Copy Queue during authoring.");
    }
}
