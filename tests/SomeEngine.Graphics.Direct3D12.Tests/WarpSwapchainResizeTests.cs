using System.Numerics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.RenderGraph;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Trait("Isolation", "PresentationProcess")]
public sealed class WarpSwapchainResizeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completed_graph_compaction_allows_repeated_resize_and_new_generation_presentation(
        bool discardNextFrameReservation)
    {
        using var setup = new ResizeFixture();
        setup.ClearPresentWaitAndCollect(setup.Acquire());
        ulong generation = setup.Swapchain.Info.Generation;

        foreach ((uint width, uint height) in new[] { (128u, 128u), (96u, 80u) })
        {
            // Runtime admits its next slot before observing window size changes. Closing
            // that unused reservation must permit compaction and later reacquisition.
            using RenderGraphNextFrame? unused = discardNextFrameReservation ? setup.OpenNextFrame() : null;
            unused?.Dispose();
            setup.CompactGraph();

            SwapchainConfig requested = setup.Swapchain.Info.Config with { Width = width, Height = height };
            Assert.Equal(ReconfigureStatus.Success, setup.Backend.Reconfigure(setup.Swapchain, requested));
            Assert.Equal(++generation, setup.Swapchain.Info.Generation);
            Assert.Equal(width, setup.Swapchain.Info.Config.Width);
            Assert.Equal(height, setup.Swapchain.Info.Config.Height);
            Assert.Equal(DeviceStatus.Active, setup.Device.Status);

            // Keep the same graph, declarations, and input through both resizes. Every
            // successful resize must produce a submitted and presented new-generation frame.
            using RenderGraphNextFrame next = setup.OpenNextFrame();
            setup.ClearPresentWaitAndCollect(setup.Acquire(), next);
        }

        Assert.Equal(3, setup.CompletedFrames);
    }

    private sealed class ResizeFixture : IDisposable
    {
        private D3D12TestWindow? _window;
        private Surface? _surface;
        private SomeEngine.RenderGraph.RenderGraph? _graph;
        private GraphTextureInput? _target;
        internal IGraphicsBackend Backend { get; private set; } = null!;
        internal Device Device { get; private set; } = null!;
        internal Queue Graphics { get; private set; } = null!;
        internal Swapchain Swapchain { get; private set; } = null!;
        internal int CompletedFrames { get; private set; }

        internal ResizeFixture()
        {
            try
            {
                _window = new D3D12TestWindow();
                Backend = D3D12GraphicsBackend.Create();
                AdapterInfo warp = D3D12TestSupport.SelectWarp(Backend);
                Device = Backend.CreateDevice(new DeviceDesc(warp.Id,
                    [new(QueueType.Graphics)], optionalFeatures: DeviceFeatures.Presentation));
                Graphics = Backend.GetQueue(Device, QueueType.Graphics);
                _surface = Backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, _window.Handle));
                Swapchain = Backend.CreateSwapchain(Device, new SwapchainDesc(_surface, 2,
                    TextureUsages.ColorAttachment,
                    new SwapchainConfig(64, 64, Format.R8G8B8A8UNorm, ColorSpace.Srgb,
                        PresentType.Fifo, AllowTearing: false, MaximumFrameLatency: 2)));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal SwapchainImage Acquire()
        {
            Assert.Equal(SwapchainAcquireStatus.Success, Backend.Acquire(Swapchain,
                new SwapchainAcquireOptions(TimeSpan.FromSeconds(2), PreserveContents: false),
                out SwapchainImage image));
            return image;
        }

        internal void ClearPresentWaitAndCollect(in SwapchainImage image, RenderGraphNextFrame? next = null)
        {
            EnsureGraph();
            var completions = new QueueCompletion[_graph!.MaximumQueueCompletionCount];
            var options = new RenderGraphFrameOptions(FrameSubmissionMode.Pipelined,
                RenderGraphDebugOptions.DisableParallelRecording);
            int count;
            using (RenderGraphFrame frame = next is null ? _graph.BeginFrame(options) : _graph.BeginFrame(next, options))
            {
                frame.SetTextureInput(_target!, frame.Import(image, Graphics));
                count = frame.Execute(completions);
            }
            Assert.True(count > 0);
            Assert.Equal(SwapchainImageStatus.Submitted, image.Status);
            Assert.Contains(Backend.Present(Graphics, image),
                new[] { PresentStatus.Success, PresentStatus.Suboptimal, PresentStatus.Occluded });
            Assert.Equal(SwapchainImageStatus.Presented, image.Status);
            foreach (QueueCompletion completion in completions.AsSpan(0, count))
                Assert.Equal(WaitStatus.Completed, Backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
            _graph.CollectCompleted();
            CompletedFrames++;
        }

        internal RenderGraphNextFrame OpenNextFrame()
        {
            EnsureGraph();
            Assert.True(_graph!.TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.FromSeconds(10)));
            return next;
        }

        internal void CompactGraph()
        {
            Assert.NotNull(_graph);
            _graph!.CollectCompleted();
            _graph.CompactTransientResources();
        }

        private void EnsureGraph()
        {
            if (_graph is not null) return;
            _graph = new SomeEngine.RenderGraph.RenderGraph(Backend, Device, [Graphics],
                new RenderGraphDesc(MaximumFramesInFlight: 2));
            using RenderGraphEdit edit = _graph.BeginEdit();
            _target = edit.CreateTextureInput();
            GraphColorAttachmentViewId view = edit.CreateColorAttachmentView(_target,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                Format.R8G8B8A8UNorm, TextureViewDimension.Texture2D);
            _ = edit.AddRasterFramePass("resize retention clear", PassQueueSelection.Exact(Graphics),
                view, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition access, ref GraphColorAttachmentViewId target) =>
                    access.ColorAttachment(0, target, LoadType.Clear, StoreType.Store,
                        WriteCoverage.Complete, new Vector4(0.125f, 0.25f, 0.5f, 1)),
                static (ref RasterPassCommandScope _, in GraphColorAttachmentViewId _) => { });
            edit.Commit();
        }

        internal void DisposeGraph()
        {
            _graph?.Dispose();
            _graph = null;
            _target = null;
        }

        public void Dispose()
        {
            DisposeGraph();
            Swapchain?.Dispose();
            _surface?.Dispose();
            if (Backend is not null && Device is not null) Backend.CollectCompleted(Device);
            Device?.Dispose();
            Backend?.Dispose();
            _window?.Dispose();
        }
    }
}
