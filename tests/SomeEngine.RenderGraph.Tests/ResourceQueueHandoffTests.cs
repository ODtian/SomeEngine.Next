using static SomeEngine.RenderGraph.Tests.CopyGraphTestSupport;

namespace SomeEngine.RenderGraph.Tests;

[Collection("Render graph trace")]
public sealed class ResourceQueueHandoffTests
{
    [Fact]
    public void CrossQueueReaderHandoffPublishesOnlyTheFinalOwner_DoesNotThrow()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(
                64,
                BufferUsages.CopySource | BufferUsages.CopyDestination));
            GraphPassId write = AddClear(edit, "write on copy", support.CopyQueue, buffer, 7);
            GraphPassId computeRead = AddRead(edit, "read on compute", support.ComputeQueue, buffer);
            GraphPassId graphicsRead = AddRead(edit, "read on graphics", support.GraphicsQueue, buffer);
            edit.OrderAfter(computeRead, write);
            edit.OrderAfter(graphicsRead, computeRead);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
                submitted = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void MixedBufferAccessPublishesTheMergedPassState_DoesNotThrow()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.ComputeQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(
                64,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                    BufferUsages.CopyDestination));
            GraphPassId reset = AddClear(edit, "mixed-state reset", support.CopyQueue, buffer, 0);
            GraphPassId mixed = edit.AddComputePass<GraphBufferId, byte>(
                "mixed read and write",
                PassQueueSelection.Exact(support.ComputeQueue),
                buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId resource) =>
                {
                    _ = access.Write(
                        resource,
                        new BufferRange(0, 64),
                        PipelineSync.ComputeShading,
                        ResourceAccess.UnorderedAccess,
                        WriteCoverage.Partial);
                    _ = access.Read(
                        resource,
                        new BufferRange(0, 64),
                        PipelineSync.ComputeShading,
                        ResourceAccess.ShaderResource);
                },
                static (ref ComputePassCommandScope _, in GraphBufferId _, in byte _) => { });
            edit.OrderAfter(mixed, reset);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
                submitted = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void PartialBufferReadPublishesWholeResourcePhysicalState_DoesNotThrow()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination | BufferUsages.Indirect));
            GraphPassId reset = AddClear(
                edit,
                "partial-state reset",
                support.GraphicsQueue,
                buffer,
                0);
            GraphPassId partialRead = edit.AddComputePass<GraphBufferId, byte>(
                "partial indirect read",
                PassQueueSelection.Exact(support.GraphicsQueue),
                buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId resource) =>
                    _ = access.Read(
                        resource,
                        new BufferRange(0, 12),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                static (ref ComputePassCommandScope _, in GraphBufferId _, in byte _) => { });
            edit.OrderAfter(partialRead, reset);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
                submitted = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void PartialCrossQueueReadPublishesWholeResourceOwnership_DoesNotThrow()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create(enableValidation: true);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.ComputeQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId buffer = edit.CreatePersistentBuffer(new BufferDesc(
                64,
                BufferUsages.CopyDestination | BufferUsages.ShaderWrite |
                    BufferUsages.Indirect));
            GraphPassId reset = AddClear(
                edit,
                "ownership reset",
                support.CopyQueue,
                buffer,
                0);
            GraphPassId write = edit.AddComputePass<GraphBufferId, byte>(
                "whole compute write",
                PassQueueSelection.Exact(support.ComputeQueue),
                buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId resource) =>
                    _ = access.Write(
                        resource,
                        new BufferRange(0, 64),
                        PipelineSync.ComputeShading,
                        ResourceAccess.UnorderedAccess,
                        WriteCoverage.Partial),
                static (ref ComputePassCommandScope _, in GraphBufferId _, in byte _) => { });
            GraphPassId partialRead = edit.AddComputePass<GraphBufferId, byte>(
                "partial cross-queue indirect read",
                PassQueueSelection.Exact(support.GraphicsQueue),
                buffer,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId resource) =>
                    _ = access.Read(
                        resource,
                        new BufferRange(0, 12),
                        PipelineSync.ExecuteIndirect,
                        ResourceAccess.IndirectArgument),
                static (ref ComputePassCommandScope _, in GraphBufferId _, in byte _) => { });
            edit.OrderAfter(write, reset);
            edit.OrderAfter(partialRead, write);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        for (int frameIndex = 0; frameIndex < 2; frameIndex++)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
                submitted = frame.Execute(completions);
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void DynamicResourceIndexReuseDoesNotAliasAcrossQueueOwners_DoesNotThrow()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue],
            new RenderGraphDesc(MaximumFramesInFlight: 1));
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];

        ExecuteUpload(support.GraphicsQueue, "graphics-owned dynamic upload");
        ExecuteUpload(support.CopyQueue, "copy-owned dynamic upload");

        void ExecuteUpload(Queue queue, string label)
        {
            Array.Clear(completions);
            int submitted;
            using (RenderGraphFrame frame = graph.BeginFrame())
            {
                GraphBufferSlice upload = frame.Upload(
                    new byte[64],
                    BufferUsages.CopySource,
                    label);
                _ = frame.AddCopyPass(
                    $"consume {label}",
                    PassQueueSelection.Exact(queue),
                    upload,
                    new PassOptions(Culling: PassCullingMode.NeverCull),
                    static (ref PassDefinition access, ref GraphBufferSlice resource) =>
                        _ = access.Read(
                            resource.Buffer,
                            resource.Range,
                            PipelineSync.Copy,
                            ResourceAccess.CopySource),
                    static (ref CopyPassCommandScope _, in GraphBufferSlice _) => { });
                submitted = frame.Execute(completions);
            }
            support.Wait(completions.AsSpan(0, submitted));
        }
    }

    [Fact]
    public void ExternalBufferFirstUseAcquiresFromItsPublishedQueue_DoesNotThrow()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer buffer = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination, "external first-use buffer"));
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId resource = edit.RegisterExternalBuffer(
                buffer,
                [Endpoint(buffer, support.GraphicsQueue, ResourceContentState.Undefined)]);
            _ = edit.AddCopyPass<GraphBufferId, byte>(
                "write acquired external buffer",
                PassQueueSelection.Exact(support.CopyQueue),
                resource,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphBufferId target) =>
                    _ = access.Write(
                        target,
                        new BufferRange(0, 64),
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in GraphBufferId _, in byte _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int submitted;
        using (RenderGraphFrame frame = graph.BeginFrame())
            submitted = frame.Execute(completions);
        support.Wait(completions.AsSpan(0, submitted));
    }

    [Fact]
    public void ExternalTextureFirstUseAcquiresFromItsPublishedQueue_DoesNotThrow()
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
            TextureUsages.CopyDestination,
            label: "external first-use texture");
        using Texture texture = support.Backend.CreateTexture(support.Device, description);
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            TextureSubresourceRange range = new(0, 1, 0, 1, TextureAspects.Color);
            GraphTextureId resource = edit.RegisterExternalTexture(
                texture,
                [new TextureBoundaryState(
                    range,
                    texture.InitialSync,
                    texture.InitialAccess,
                    texture.InitialLayout,
                    ResourceContentState.Undefined,
                    support.GraphicsQueue)]);
            _ = edit.AddCopyPass<GraphTextureId, byte>(
                "write acquired external texture",
                PassQueueSelection.Exact(support.CopyQueue),
                resource,
                new PassOptions(Culling: PassCullingMode.NeverCull),
                (ref PassDefinition access, ref GraphTextureId target) =>
                    _ = access.Write(
                        target,
                        range,
                        PipelineSync.Copy,
                        ResourceAccess.CopyDestination,
                        TextureLayout.CopyDestination,
                        WriteCoverage.Complete),
                static (ref CopyPassCommandScope _, in GraphTextureId _, in byte _) => { });
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int submitted;
        using (RenderGraphFrame frame = graph.BeginFrame())
            submitted = frame.Execute(completions);
        support.Wait(completions.AsSpan(0, submitted));
    }

    [Fact]
    public void IncomingAcquireCanFollowIndependentCommandsInTheSameRecording_DoesNotThrow()
    {
        using WarpGraphTestSupport support = WarpGraphTestSupport.Create();
        using Buffer local = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination, "copy-local work"));
        using Buffer transferred = support.Backend.CreateBuffer(
            support.Device,
            new BufferDesc(64, BufferUsages.CopyDestination, "later acquired work"));
        using var graph = new RenderGraph(
            support.Backend,
            support.Device,
            [support.GraphicsQueue, support.CopyQueue]);

        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            GraphBufferId localResource = edit.RegisterExternalBuffer(
                local,
                [Endpoint(local, support.CopyQueue, ResourceContentState.Undefined)]);
            GraphBufferId transferredResource = edit.RegisterExternalBuffer(
                transferred,
                [Endpoint(transferred, support.GraphicsQueue, ResourceContentState.Undefined)]);
            GraphPassId first = AddClear(
                edit,
                "copy-local pass before acquire",
                support.CopyQueue,
                localResource,
                1);
            GraphPassId second = AddClear(
                edit,
                "later pass with incoming acquire",
                support.CopyQueue,
                transferredResource,
                2);
            edit.OrderAfter(second, first);
            edit.Commit();
        }

        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        int submitted;
        using (RenderGraphFrame frame = graph.BeginFrame())
            submitted = frame.Execute(completions);
        support.Wait(completions.AsSpan(0, submitted));
    }
}
