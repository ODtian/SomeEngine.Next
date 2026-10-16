using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Elements;
using SomeEngine.Elements.Render;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Layout;
using SomeEngine.Render;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Views;
using RenderGraphType = SomeEngine.RenderGraph.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Animation.HudChecks;

internal static class HudMeasurements
{
    private const int Warmups = 128, Samples = 512;
    private static readonly PassOptions Keep = new(Culling: PassCullingMode.NeverCull);

    public static int Main(string[] args)
    {
        string? capture = args.Length == 2 && args[0] == "--capture-rgba" ? args[1] : null;
        long startup = Stopwatch.GetTimestamp();
        long memory = GC.GetTotalMemory(true);
        using var window = new HudWindow();
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        var preference = new AdapterEnumerationOptions(AdapterPreference.HighPerformance);
        backend.TryEnumerateAdapters(preference, [], out int adapterCount);
        var adapters = new AdapterInfo[adapterCount];
        backend.TryEnumerateAdapters(preference, adapters, out _);
        using Device device = backend.CreateDevice(new DeviceDesc(adapters[0].Id, [new DeviceQueueDesc(QueueType.Graphics)],
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.CalibratedTimestamps));
        using SomeEngine.Graphics.Surface surface = backend.CreateSurface(new SurfaceDesc(NativeWindowType.Win32, window.Handle));
        using Swapchain swapchain = backend.CreateSwapchain(device, new SwapchainDesc(surface, 3, TextureUsages.ColorAttachment | TextureUsages.CopySource,
            new(1280, 720, Format.R8G8B8A8UNorm, ColorSpace.Srgb, PresentType.Immediate, true, 1)));
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        ulong frequency = backend.CalibrateTimestamps(queue).QueueFrequency;
        using var graph = new RenderGraphType(backend, device, [queue]);
        using var game = new World();
        using var render = new RenderWorld();
        using var renderDevice = new RenderDevice(backend, device);
        using var frames = new RenderFrameCoordinator(renderDevice);
        using var extraction = new RenderExtractionSystems(render, new CanvasRenderExtractor { Parallel = false });
        using var prepare = new RenderPrepareSystems(render, extraction);
        using SurfaceMaterial material = SurfaceMaterial.Create(device.Capabilities.CompileTarget);
        using var renderer = new CanvasRenderer(backend, device, graph, render, material, Format.R8G8B8A8UNorm);
        using var views = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        views.Add(new ViewRebuildSystem());
        using var animation = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        Entity time = game.CreateEntity(new AnimationTime(0, gamePaused: true));
        AnimationSystems.AddDefaults(animation, time);
        AnimationSystems.Add(animation, Spec.Tween<float, FloatOps, CubicOut>(0.15f));
        using var layout = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        layout.Add(new LayoutSystem());
        using var flip = new SystemGroup<ImmediateSystemContext>(new ImmediateSystemDriver(game));
        flip.Add(new FlipSystem());
        var frameNumber = new State<int>();
        State<float>[] values = Enumerable.Range(0, 64).Select(_ => new State<float>()).ToArray();
        Entity canvas = game.CreateEntity(new Canvas { Size = new Size(1280, 720) });
        Views.Views.Spawn(game, canvas, new HudView(frameNumber, values));
        using QueryPool timestamps = backend.CreateQueryPool(device, new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 2));
        using Buffer readback = backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.QueryResolve), MemoryType.Readback);
        TextureDesc targetDescription = new(TextureDimension.Texture2D, 1280, 720, 1, 1, 1, 1,
            Format.R8G8B8A8UNorm, TextureUsages.ColorAttachment | TextureUsages.CopySource);
        TextureCopyFootprint footprint = backend.GetTextureCopyFootprint(device, targetDescription,
            new BufferTextureCopy(null!, 0, 0, 0, null!, 0, 0, TextureAspects.Color, 0, 0, 0, 1280, 720, 1));
        using Buffer pixels = backend.CreateBuffer(device, new BufferDesc(footprint.TotalSize, BufferUsages.CopyDestination), MemoryType.Readback);
        GraphTextureId target;
        GraphPassId begin, end;
        using (RenderGraphEdit edit = graph.BeginEdit())
        {
            target = edit.DeclareExternalTexture(targetDescription);
            GraphQueryPoolId queries = edit.RegisterQueryPool(timestamps);
            GraphBufferId results = edit.RegisterExternalBuffer(readback,
                [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
            begin = edit.AddComputeFramePass("HUD GPU start", PassQueueSelection.AnyOfType(QueueType.Graphics), queries, Keep,
                static (ref PassDefinition pass, ref GraphQueryPoolId queries) => pass.Write(queries, new QueryRange(0, 1)),
                static (ref ComputePassCommandScope commands, in GraphQueryPoolId queries) => commands.WriteTimestamp(queries, 0));
            GraphColorAttachmentViewId color = edit.CreateColorAttachmentView(target);
            GraphPassId clear = edit.AddRasterFramePass("HUD clear", PassQueueSelection.AnyOfType(QueueType.Graphics), color, Keep,
                static (ref PassDefinition pass, ref GraphColorAttachmentViewId color) => pass.ColorAttachment(0, color,
                    LoadType.Clear, StoreType.Store, WriteCoverage.Complete, new Vector4(0.02f, 0.02f, 0.04f, 1)),
                static (ref RasterPassCommandScope commands, in GraphColorAttachmentViewId color) => { });
            edit.OrderAfter(clear, begin);
            end = edit.AddComputeFramePass("HUD GPU end", PassQueueSelection.AnyOfType(QueueType.Graphics), queries, Keep,
                static (ref PassDefinition pass, ref GraphQueryPoolId queries) => pass.Write(queries, new QueryRange(1, 1)),
                static (ref ComputePassCommandScope commands, in GraphQueryPoolId queries) => commands.WriteTimestamp(queries, 1));
            edit.AddCopyFramePass("HUD GPU timing", PassQueueSelection.AnyOfType(QueueType.Graphics), (queries, results), Keep,
                static (ref PassDefinition pass, ref (GraphQueryPoolId Queries, GraphBufferId Results) data) =>
                {
                    pass.Read(data.Queries, new QueryRange(0, 2));
                    pass.Write(data.Results, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                },
                static (ref CopyPassCommandScope commands, in (GraphQueryPoolId Queries, GraphBufferId Results) data) =>
                    commands.ResolveQueries(data.Queries, 0, 2, commands.GetBuffer(data.Results), BufferRange.Whole));
            edit.Commit();
        }
        var animationMs = new double[Samples];
        var cpuMs = new double[Samples];
        var gpuMs = new double[Samples];
        var intervals = new double[Samples];
        long animationBytes = 0, frameBytes = 0;
        double firstFrameMs = 0;
        long previous = Stopwatch.GetTimestamp(), previousPresent = previous;
        QueueCompletion[] completions = [];
        int frameCount = Warmups + Samples + (capture is null ? 0 : 1);
        for (int index = 0; index < frameCount; index++)
        {
            long started = Stopwatch.GetTimestamp();
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            window.Pump();
            float delta = (float)Stopwatch.GetElapsedTime(previous, started).TotalSeconds;
            previous = started;
            frameNumber.Value = index;
            game.Replace(time, new AnimationTime(delta, gamePaused: true));
            views.Update();
            var context = new ImmediateSystemContext(game, 0, 0, delta);
            long animationStarted = Stopwatch.GetTimestamp();
            long beforeAnimation = GC.GetAllocatedBytesForCurrentThread();
            animation.Update(ref context);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - beforeAnimation;
            double elapsed = Stopwatch.GetElapsedTime(animationStarted).TotalMilliseconds;
            layout.Update(ref context);
            flip.Update(ref context);
            extraction.Extract(game);
            if (!frames.TryBeginPrepare(out RenderPrepareScope? scope)) throw new InvalidOperationException("HUD prepare failed.");
            using (scope)
            {
                prepare.ApplyExtraction(scope);
                scope.Commit();
            }
            renderer.ReconcileGraph(target);
            if (index == 0)
            {
                renderer.TryGetPass(extraction.RequireMirror(canvas), out GraphPassId drawing);
                using RenderGraphEdit edit = graph.BeginEdit();
                edit.OrderAfter(drawing, begin);
                edit.OrderAfter(end, drawing);
                edit.Commit();
                completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
            }
            if (backend.Acquire(swapchain, new SwapchainAcquireOptions(TimeSpan.FromSeconds(10)), out SwapchainImage image) != SwapchainAcquireStatus.Success)
                throw new InvalidOperationException("HUD presentation image acquisition failed.");
            RenderGraphFrame frame = graph.BeginFrame();
            try
            {
                frame.BindExternalTexture(target, image, queue);
                renderer.UpdateFrame(ref frame);
                if (capture is not null && index == frameCount - 1)
                {
                    GraphBufferId destination = frame.Import(pixels,
                        [new(BufferRange.Whole, pixels.InitialSync, pixels.InitialAccess, ResourceContentState.Undefined)]);
                    frame.AddCopyPass("HUD capture", PassQueueSelection.Exact(queue), (target, destination, footprint), default,
                        static (ref PassDefinition access, ref (GraphTextureId Source, GraphBufferId Destination, TextureCopyFootprint Footprint) data) =>
                        {
                            access.Read(data.Source, new TextureSubresourceRange(0, 1, 0, 1, TextureAspects.Color),
                                PipelineSync.Copy, ResourceAccess.CopySource, TextureLayout.CopySource);
                            access.Write(data.Destination, BufferRange.Whole, PipelineSync.Copy, ResourceAccess.CopyDestination, WriteCoverage.Complete);
                        },
                        static (ref CopyPassCommandScope commands, in (GraphTextureId Source, GraphBufferId Destination, TextureCopyFootprint Footprint) data) =>
                            commands.CopyTextureToBuffer(new BufferTextureCopy(commands.GetBuffer(data.Destination),
                                data.Footprint.Offset, data.Footprint.RowPitch, data.Footprint.RowCount, commands.GetTexture(data.Source),
                                0, 0, TextureAspects.Color, 0, 0, 0, 1280, 720, 1)));
                }
                int count = frame.Execute(completions);
                renderDevice.TrackSubmission(completions.AsSpan(0, count));
                renderDevice.WaitForTrackedSubmissions(TimeSpan.FromSeconds(10));
            }
            finally { frame.Dispose(); }
            backend.Present(queue, image);
            long presented = Stopwatch.GetTimestamp();
            if (index == 0) firstFrameMs = Stopwatch.GetElapsedTime(started, presented).TotalMilliseconds;
            if (index >= Warmups && index < Warmups + Samples)
            {
                int sample = index - Warmups;
                animationMs[sample] = elapsed;
                animationBytes += bytes;
                cpuMs[sample] = Stopwatch.GetElapsedTime(started, presented).TotalMilliseconds;
                frameBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                intervals[sample] = Stopwatch.GetElapsedTime(previousPresent, presented).TotalMilliseconds;
                using MappedBuffer mapping = backend.Map(readback, MapType.Read, BufferRange.Whole);
                mapping.Invalidate(mapping.Range);
                ReadOnlySpan<ulong> ticks = MemoryMarshal.Cast<byte, ulong>(mapping.Bytes);
                gpuMs[sample] = (ticks[1] - ticks[0]) * 1000d / frequency;
            }
            previousPresent = presented;
        }
        if (capture is not null)
        {
            using MappedBuffer mapping = backend.Map(pixels, MapType.Read, BufferRange.Whole);
            mapping.Invalidate(mapping.Range);
            using FileStream output = File.Create(capture);
            for (int row = 0; row < 720; row++)
                output.Write(mapping.Bytes.Slice((int)(footprint.Offset + (ulong)row * footprint.RowPitch), 1280 * 4));
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Workload = "64 typed animated cards with reference fields and FLIP", Warmups, Samples,
            Device = device.Adapter.Name, Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
            Framework = RuntimeInformation.FrameworkDescription, Backend = "D3D12", Width = 1280, Height = 720,
            PresentType = "Immediate", VSync = false, Delta = "actual elapsed between frame starts",
            Animation = Summarize(animationMs), CpuFrame = Summarize(cpuMs), Gpu = Summarize(gpuMs), PresentInterval = Summarize(intervals),
            AnimationBytesPerFrame = (double)animationBytes / Samples, CpuBytesPerFrame = (double)frameBytes / Samples,
            FirstFrameMs = firstFrameMs, StartupAndRunMs = Stopwatch.GetElapsedTime(startup).TotalMilliseconds,
            RetainedBytes = GC.GetTotalMemory(true) - memory,
            AnimationBudgetMs = 1d, AnimationWithinBudget = Quantile(animationMs, 0.95) <= 1,
            FrameTargetMs = 1000d / 300, PresentedFrames = frameCount, Capture = capture,
        }));
        if (!frames.TryBeginPrepare(out RenderPrepareScope? shutdown)) throw new InvalidOperationException("HUD shutdown prepare failed.");
        using (shutdown)
        {
            prepare.Shutdown(shutdown);
            shutdown.Commit();
        }
        return 0;
    }

    private static object Summarize(double[] samples) => new
    {
        MedianMs = Quantile(samples, 0.5), P95Ms = Quantile(samples, 0.95), P99Ms = Quantile(samples, 0.99),
    };

    private static double Quantile(double[] samples, double fraction)
    {
        double[] sorted = (double[])samples.Clone();
        Array.Sort(sorted);
        return sorted[(int)((sorted.Length - 1) * fraction)];
    }
}
