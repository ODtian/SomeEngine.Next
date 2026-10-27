using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using SomeEngine.RenderGraph;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpSwapchainSubmissionTests
{
    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    public void Initial_cross_queue_readback_keeps_image_uses_in_one_submission(FrameSubmissionMode mode)
    {
        using var setup = new PresentationGraph();
        using (RenderGraphEdit edit = setup.Graph.BeginEdit())
        {
            edit.OrderAfter(setup.LastImage, setup.Copy);
            edit.Commit();
        }
        var completions = new QueueCompletion[setup.Graph.MaximumQueueCompletionCount];
        var options = new RenderGraphFrameOptions(mode,
            RenderGraphDebugOptions.DeclarationOrderScheduling | RenderGraphDebugOptions.DisableParallelRecording);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            Assert.Equal(SwapchainAcquireStatus.Success, setup.Backend.Acquire(setup.Swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: false), out SwapchainImage image));
            var observer = new SubmissionObserver([setup.Prefix, setup.FirstImage, setup.Copy, setup.LastImage]);
            int count;
            using (RenderGraphFrame frame = setup.Graph.BeginFrame(options))
            {
                frame.SetTextureInput(setup.Target, frame.Import(image, setup.Graphics));
                frame.ObserveExecution(observer);
                count = frame.Execute(completions);
            }
            Assert.All(observer.HasCompletion, static value => Assert.True(value));
            Assert.Equal(observer.Completions[1], observer.Completions[3]);
            Assert.NotEqual(observer.Completions[0], observer.Completions[2]);
            Assert.NotEqual(observer.Completions[0], observer.Completions[1]);
            Assert.NotEqual(observer.Completions[2], observer.Completions[3]);
            Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
            Assert.Contains(setup.Backend.Present(setup.Graphics, image),
                new[] { PresentStatus.Success, PresentStatus.Suboptimal, PresentStatus.Occluded });
            foreach (QueueCompletion completion in completions.AsSpan(0, count))
                Assert.Equal(WaitStatus.Completed, setup.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            Assert.All(setup.ReadbackValues(), static value => Assert.Equal(0x1234abcdu, value));
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    public void Adding_and_removing_return_dependency_preserves_image_and_readback(FrameSubmissionMode mode)
    {
        using var setup = new PresentationGraph();
        var completions = new QueueCompletion[setup.Graph.MaximumQueueCompletionCount];
        var options = new RenderGraphFrameOptions(mode,
            RenderGraphDebugOptions.DeclarationOrderScheduling | RenderGraphDebugOptions.DisableParallelRecording);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            if (iteration == 1)
            {
                using RenderGraphEdit edit = setup.Graph.BeginEdit();
                edit.OrderAfter(setup.LastImage, setup.Copy);
                edit.Commit();
            }
            if (iteration == 3)
            {
                using RenderGraphEdit edit = setup.Graph.BeginEdit();
                edit.RemoveOrder(setup.LastImage, setup.Copy);
                edit.Commit();
            }
            Assert.Equal(SwapchainAcquireStatus.Success, setup.Backend.Acquire(setup.Swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: false), out SwapchainImage image));
            var observer = new SubmissionObserver([setup.Prefix, setup.FirstImage, setup.Copy, setup.LastImage]);
            int count;
            using (RenderGraphFrame frame = setup.Graph.BeginFrame(options))
            {
                frame.SetTextureInput(setup.Target, frame.Import(image, setup.Graphics));
                frame.ObserveExecution(observer);
                count = frame.Execute(completions);
            }
            Assert.All(observer.HasCompletion, static value => Assert.True(value));
            Assert.Equal(observer.Completions[1], observer.Completions[3]);
            Assert.NotEqual(observer.Completions[0], observer.Completions[2]);
            if (iteration is 1 or 2)
            {
                Assert.NotEqual(observer.Completions[0], observer.Completions[1]);
                Assert.NotEqual(observer.Completions[2], observer.Completions[3]);
            }
            Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
            Assert.Contains(setup.Backend.Present(setup.Graphics, image),
                new[] { PresentStatus.Success, PresentStatus.Suboptimal, PresentStatus.Occluded });
            foreach (QueueCompletion completion in completions.AsSpan(0, count))
                Assert.Equal(WaitStatus.Completed, setup.Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            Assert.All(setup.ReadbackValues(), static value => Assert.Equal(0x1234abcdu, value));
        }
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    public void Image_to_copy_to_image_dependency_is_rejected_before_submission(FrameSubmissionMode mode)
    {
        using var setup = new PresentationGraph();
        using (RenderGraphEdit edit = setup.Graph.BeginEdit())
        {
            edit.OrderAfter(setup.Copy, setup.FirstImage);
            edit.OrderAfter(setup.LastImage, setup.Copy);
            edit.Commit();
        }
        var completions = new QueueCompletion[setup.Graph.MaximumQueueCompletionCount];
        var options = new RenderGraphFrameOptions(mode,
            RenderGraphDebugOptions.DeclarationOrderScheduling | RenderGraphDebugOptions.DisableParallelRecording);
        Assert.Equal(SwapchainAcquireStatus.Success, setup.Backend.Acquire(setup.Swapchain,
            new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: false), out SwapchainImage image));
        var observer = new SubmissionObserver([setup.Prefix, setup.FirstImage, setup.Copy, setup.LastImage]);
        Exception? failure = null;
        using (RenderGraphFrame frame = setup.Graph.BeginFrame(options))
        {
            frame.SetTextureInput(setup.Target, frame.Import(image, setup.Graphics));
            frame.ObserveExecution(observer);
            try { frame.Execute(completions); }
            catch (Exception exception) { failure = exception; }
        }
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, observer.AcceptedCount);
        Assert.All(observer.HasCompletion, static value => Assert.False(value));
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
    }

    [Theory]
    [InlineData(FrameSubmissionMode.Pipelined)]
    [InlineData(FrameSubmissionMode.RecordAhead)]
    [InlineData(FrameSubmissionMode.RecordAllThenSubmit)]
    public void SubmitAfter_inside_image_interval_is_rejected_before_submission(FrameSubmissionMode mode)
    {
        using var setup = new PresentationGraph();
        using (RenderGraphEdit edit = setup.Graph.BeginEdit())
        {
            edit.SetPassOptions(setup.FirstImage,
                new PassOptions(Culling: PassCullingMode.NeverCull) { SubmitAfter = true });
            edit.Commit();
        }
        var completions = new QueueCompletion[setup.Graph.MaximumQueueCompletionCount];
        var options = new RenderGraphFrameOptions(mode,
            RenderGraphDebugOptions.DeclarationOrderScheduling | RenderGraphDebugOptions.DisableParallelRecording);
        Assert.Equal(SwapchainAcquireStatus.Success, setup.Backend.Acquire(setup.Swapchain,
            new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: false), out SwapchainImage image));
        var observer = new SubmissionObserver([setup.Prefix, setup.FirstImage, setup.Copy, setup.LastImage]);
        Exception? failure = null;
        using (RenderGraphFrame frame = setup.Graph.BeginFrame(options))
        {
            frame.SetTextureInput(setup.Target, frame.Import(image, setup.Graphics));
            frame.ObserveExecution(observer);
            try { frame.Execute(completions); }
            catch (Exception exception) { failure = exception; }
        }
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, observer.AcceptedCount);
        Assert.All(observer.HasCompletion, static value => Assert.False(value));
        Assert.Equal(SwapchainImageStatus.Acquired, image.Status);
    }

    private sealed class PresentationGraph : IDisposable
    {
        private D3D12TestWindow _window = null!;
        private Device _device = null!;
        private Surface _surface = null!;
        private Buffer _readback = null!;
        internal ValidationLayer Backend { get; private set; } = null!;
        internal Queue Graphics { get; private set; } = null!;
        internal Swapchain Swapchain { get; private set; } = null!;
        internal SomeEngine.RenderGraph.RenderGraph Graph { get; private set; } = null!;
        internal GraphTextureInput Target { get; private set; } = null!;
        internal GraphPassId Prefix { get; private set; }
        internal GraphPassId FirstImage { get; private set; }
        internal GraphPassId Copy { get; private set; }
        internal GraphPassId LastImage { get; private set; }

        internal PresentationGraph()
        {
            try
            {
                _window = new D3D12TestWindow();
                Backend = new ValidationLayer(D3D12GraphicsBackend.Create(new D3D12BackendOptions(
                    new D3D12ValidationOptions(DisableGpuBasedValidation: true,
                        DisableSynchronizedQueueValidation: true))));
                AdapterInfo adapter = D3D12TestSupport.SelectWarp(Backend);
                _device = Backend.CreateDevice(new DeviceDesc(adapter.Id,
                    [new(QueueType.Graphics), new(QueueType.Copy)],
                    optionalFeatures: DeviceFeatures.Presentation));
                Graphics = Backend.GetQueue(_device, QueueType.Graphics);
                Queue copy = Backend.GetQueue(_device, QueueType.Copy);
                _surface = Backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, _window.Handle));
                Swapchain = Backend.CreateSwapchain(_device, new SwapchainDesc(_surface, 2,
                    TextureUsages.ColorAttachment,
                    new SwapchainConfig(64, 64, Format.R8G8B8A8UNorm, ColorSpace.Srgb,
                        PresentType.Fifo, AllowTearing: false, MaximumFrameLatency: 2)));
                _readback = Backend.CreateBuffer(_device,
                    new BufferDesc(64, BufferUsages.CopyDestination), MemoryType.Readback);
                Graph = new SomeEngine.RenderGraph.RenderGraph(Backend, _device, [Graphics, copy],
                    new RenderGraphDesc(MaximumFramesInFlight: 1));
                using RenderGraphEdit edit = Graph.BeginEdit();
                Target = edit.CreateTextureInput();
                GraphColorAttachmentViewId view = edit.CreateColorAttachmentView(Target,
                    new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                    Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D);
                GraphBufferId source = edit.CreateTransientBuffer(new BufferDesc(64,
                    BufferUsages.CopySource | BufferUsages.CopyDestination));
                GraphBufferId output = edit.RegisterExternalBuffer(_readback,
                    [new(BufferRange.Whole, _readback.InitialSync, _readback.InitialAccess,
                        ResourceContentState.Undefined, copy)]);
                var options = new PassOptions(Culling: PassCullingMode.NeverCull);
                Prefix = edit.AddCopyFramePass("non-image producer prefix", PassQueueSelection.Exact(Graphics),
                    source, options,
                    static (ref PassDefinition access, ref GraphBufferId value) =>
                        access.Write(value, BufferRange.Whole, PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Complete),
                    static (ref CopyPassCommandScope commands, in GraphBufferId value) =>
                        commands.ClearBuffer(commands.GetBuffer(value), new BufferRange(0, 64), 0x1234abcdu));
                FirstImage = edit.AddRasterFramePass("first image use", PassQueueSelection.Exact(Graphics), view, options,
                    static (ref PassDefinition access, ref GraphColorAttachmentViewId target) =>
                        access.ColorAttachment(0, target, LoadType.Clear, StoreType.Store, WriteCoverage.Complete,
                            new Vector4(0.125f, 0.25f, 0.5f, 1)),
                    static (ref RasterPassCommandScope _, in GraphColorAttachmentViewId _) => { });
                Copy = edit.AddCopyFramePass("cross-queue readback", PassQueueSelection.Exact(copy),
                    (Source: source, Destination: output), options,
                    static (ref PassDefinition access, ref (GraphBufferId Source, GraphBufferId Destination) data) =>
                    {
                        access.Read(data.Source, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopySource);
                        access.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy,
                            ResourceAccess.CopyDestination, WriteCoverage.Complete);
                    },
                    static (ref CopyPassCommandScope commands, in (GraphBufferId Source, GraphBufferId Destination) data) =>
                        commands.CopyBuffer(new(commands.GetBuffer(data.Source), 0,
                            commands.GetBuffer(data.Destination), 0, 64)));
                LastImage = edit.AddRasterFramePass("last image use", PassQueueSelection.Exact(Graphics), view, options,
                    static (ref PassDefinition access, ref GraphColorAttachmentViewId target) =>
                        access.ColorAttachment(0, target, LoadType.Load, StoreType.Store, WriteCoverage.Partial, Vector4.Zero),
                    static (ref RasterPassCommandScope _, in GraphColorAttachmentViewId _) => { });
                edit.OrderAfter(FirstImage, Prefix);
                edit.OrderAfter(LastImage, FirstImage);
                edit.Commit();
            }
            catch { Dispose(); throw; }
        }

        internal uint[] ReadbackValues()
        {
            using MappedBuffer mapped = Backend.Map(_readback, MapType.Read, new BufferRange(0, 64));
            mapped.Invalidate(mapped.Range);
            return MemoryMarshal.Cast<byte, uint>(mapped.Bytes).ToArray();
        }

        public void Dispose()
        {
            Graph?.Dispose();
            _readback?.Dispose();
            Swapchain?.Dispose();
            _surface?.Dispose();
            if (Backend is not null && _device is not null) Backend.CollectCompleted(_device);
            _device?.Dispose();
            Backend?.Dispose();
            _window?.Dispose();
        }
    }

    private sealed class SubmissionObserver(GraphPassId[] passes) : IRenderGraphFrameExecutionObserver
    {
        internal int AcceptedCount { get; private set; }
        internal bool[] HasCompletion { get; } = new bool[passes.Length];
        internal QueueCompletion[] Completions { get; } = new QueueCompletion[passes.Length];

        public void OnRenderGraphSubmitted(RenderGraphFrameResult result) => AcceptedCount++;

        public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
        {
            for (int index = 0; index < passes.Length; index++)
                HasCompletion[index] = result.TryGetPassCompletion(passes[index], out Completions[index]);
        }
    }
}
