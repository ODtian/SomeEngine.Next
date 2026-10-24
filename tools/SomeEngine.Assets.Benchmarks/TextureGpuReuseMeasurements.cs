using System.Runtime.CompilerServices;
using SomeEngine.Core.Collections;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using EngineTexture = SomeEngine.Resources.Texture;
using RhiTexture = SomeEngine.Graphics.Texture;

internal static class TextureGpuReuseMeasurements
{
    public static int Run()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(options, adapters, out _);
        AdapterInfo adapter = adapters.First(value => !value.HardwareAccelerated);
        using Device device = backend.CreateDevice(new(adapter.Id, [new(QueueType.Graphics)]));
        using var renderer = new RenderDevice(backend, device);
        using var graph = new RenderGraph(backend, device, [backend.GetQueue(device, QueueType.Graphics)]);
        var values = new WeakValues<int, EngineTexture>();
        byte[] pixels = new byte[1024 * 1024];
        Array.Fill(pixels, (byte)71);
        MemoryRequirements memory = backend.GetTextureMemoryRequirements(device, Description());
        Console.WriteLine($"texture-reuse-gpu adapter={adapter.Name} backend=D3D12 pixels={pixels.Length} gpu-allocation={memory.Size} alignment={memory.Alignment}");

        (WeakReference<EngineTexture> reference, RhiTexture first, RhiTexture independent) =
            MeasureLiveReferences(values, pixels, graph, renderer);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        renderer.CollectCompleted();
        Console.WriteLine($"texture-reuse-gpu after-gap collected={!reference.TryGetTarget(out _)} first-released={ObserveRelease(backend, device, first)} independent-released={ObserveRelease(backend, device, independent)}");
        EngineTexture rebuilt = GetTexture(values, pixels);
        (RhiTexture resource, int uploads) = Upload(graph, renderer, rebuilt);
        Console.WriteLine($"texture-reuse-gpu rebuilt upload-copy-passes={uploads} new-allocation={!ReferenceEquals(first, resource)} live-gpu-bytes={memory.Size}");
        GC.KeepAlive(rebuilt);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<EngineTexture>, RhiTexture, RhiTexture) MeasureLiveReferences(
        WeakValues<int, EngineTexture> values, byte[] pixels, RenderGraph graph, RenderDevice renderer)
    {
        EngineTexture first = GetTexture(values, pixels);
        (RhiTexture resource, int uploads) = Upload(graph, renderer, first);
        TextureSrv view = renderer.GetTextureSrv(first);
        int hitUploads = 0;
        for (int index = 0; index < 32; index++)
        {
            EngineTexture hit = GetTexture(values, pixels);
            (RhiTexture reused, int copies) = Upload(graph, renderer, hit);
            if (!ReferenceEquals(first, hit) || !ReferenceEquals(resource, reused) ||
                !ReferenceEquals(view, renderer.GetTextureSrv(hit)))
                throw new InvalidOperationException("Live texture acquisition changed the resource or its view.");
            hitUploads += copies;
        }
        EngineTexture independent = CreateTexture(pixels);
        (RhiTexture separate, int independentUploads) = Upload(graph, renderer, independent);
        Console.WriteLine($"texture-reuse-gpu live first-upload-copy-passes={uploads} hits=32 hit-upload-copy-passes={hitUploads} shared-texture=true shared-allocation=true shared-srv=true");
        Console.WriteLine($"texture-reuse-gpu independent shared-pixel-array=true separate-texture={!ReferenceEquals(first, independent)} separate-allocation={!ReferenceEquals(resource, separate)} upload-copy-passes={independentUploads} live-allocations=2");
        return (new(first), resource, separate);
    }

    private static EngineTexture GetTexture(WeakValues<int, EngineTexture> values, byte[] pixels)
    {
        ValueTask<EngineTexture> value = values.GetOrBegin(1, out var completion);
        if (completion is not null)
            value = values.CompleteAsync(1, completion, ValueTask.FromResult(CreateTexture(pixels)));
        return value.Result;
    }

    private static TextureDesc Description() => new(TextureDimension.Texture2D, 1024, 1024, 1, 1, 1, 1,
        Format.R8UNorm, TextureUsages.Sampled | TextureUsages.CopyDestination);

    private static EngineTexture CreateTexture(byte[] pixels) => EngineTexture.Create(Description(),
        [new TexturePixels(new TextureMipTile { Width = 1024, Height = 1024, RowPitch = 1024, SlicePitch = 1024 * 1024 }, pixels)],
        TextureViewDimension.Texture2D);

    private static bool ObserveRelease(IGraphicsBackend backend, Device device, RhiTexture texture)
    {
        try
        {
            using TextureSrv view = backend.CreateTextureSrv(device, new(texture,
                new(0, 1, 0, 1, TextureAspects.Color), Format.R8UNorm, TextureViewDimension.Texture2D));
            return false;
        }
        catch (ObjectDisposedException) { return true; }
    }

    private static (RhiTexture, int) Upload(RenderGraph graph, RenderDevice renderer, EngineTexture texture)
    {
        int copies = 0;
        void Inspect(in RenderGraphDiagnosticsView report)
        {
            foreach (RenderGraphPassDiagnostic pass in report.Passes)
                if (pass.Live && pass.Kind == GraphPassKind.Copy) copies++;
        }
        var completions = new QueueCompletion[graph.MaximumQueueCompletionCount];
        RhiTexture resource;
        int count;
        RenderGraphFrame frame = graph.BeginFrame(new(Diagnostics: Inspect));
        try
        {
            GraphPassId consumer = frame.AddGeneralPass("Texture reuse consumer", PassQueueSelection.AnyOfType(QueueType.Graphics),
                0, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition _, ref int _) => { }, static (ref GeneralPassCommandScope _, in int _) => { });
            resource = renderer.PrepareTexture(ref frame, consumer, texture);
            GraphTextureId imported = frame.GetImported(resource);
            GraphTextureSrvId view = frame.CreateTextureSrv(imported, new(0, 1, 0, 1, TextureAspects.Color),
                Format.R8UNorm, TextureViewDimension.Texture2D);
            GraphPassId reader = frame.AddGeneralPass("Texture reuse read", PassQueueSelection.AnyOfType(QueueType.Graphics),
                view, new PassOptions(Culling: PassCullingMode.NeverCull),
                static (ref PassDefinition definition, ref GraphTextureSrvId value) =>
                    _ = definition.Read(value, PipelineSync.AllShading),
                static (ref GeneralPassCommandScope _, in GraphTextureSrvId _) => { });
            renderer.PrepareTexture(ref frame, reader, texture);
            count = frame.Execute(completions);
        }
        finally { frame.Dispose(); }
        renderer.TrackSubmission(completions.AsSpan(0, count));
        renderer.WaitForTrackedSubmissions(TimeSpan.FromSeconds(30));
        graph.CollectCompleted();
        renderer.CollectCompleted();
        return (resource, copies);
    }
}
