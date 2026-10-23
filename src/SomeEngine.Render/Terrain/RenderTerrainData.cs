using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Systems;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Terrain;

/// <summary>Device terrain pages, extracted compute requests and their GPU completions.</summary>
public sealed partial class RenderTerrainData : ISystem<RenderPrepareSystemContext>, ISystem<RenderFrameSystemContext>,
    IRenderGraphFrameExecutionObserver, IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly GraphExtensionPointId _extensionPoint;
    private readonly ConditionalWeakTable<TerrainFieldPage, DevicePage> _pageData = new();
    private readonly List<DevicePage> _pages = [];
    private readonly Dictionary<(Shader Shader, string Entry), LinkedComputePipeline> _pipelines = [];
    private readonly HashSet<TerrainGpuRequest> _requests = [];
    private readonly List<RecordedRequest> _recorded = [];
    private readonly Dictionary<TerrainGpuRequest, ReadbackData> _readbacks = [];
    private readonly HashSet<DevicePage> _framePages = [];
    private readonly List<Task> _completions = [];
    private QueryHandle _query;

    public RenderTerrainData(IGraphicsBackend backend, Device device, global::SomeEngine.RenderGraph.RenderGraph graph)
    {
        _backend = backend;
        _device = device;
        using RenderGraphEdit edit = graph.BeginEdit();
        _extensionPoint = edit.AddExtensionPoint("Terrain field work");
        edit.Commit();
    }

    public void OnCreate(ref RenderPrepareSystemContext context) =>
        _query = context.World.Query(new QueryDefinitionBuilder().Read<RenderTerrainRequests>());

    public void OnUpdate(ref RenderPrepareSystemContext context)
    {
        _requests.Clear();
        context.World.ExecuteQuery(_query, query =>
        {
            foreach (QueryChunkView chunk in query.Chunks)
            foreach (RenderTerrainRequests data in chunk.Read<RenderTerrainRequests>())
            foreach (TerrainDataRequest request in data.Requests)
                if (request is TerrainGpuRequest gpu && !gpu.IsSubmitted) _requests.Add(gpu);
        });
        _completions.RemoveAll(static task => task.IsCompletedSuccessfully);
    }

    public void OnDestroy(ref RenderPrepareSystemContext context) => context.World.ReleaseQuery(_query);

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        RenderGraphFrame frame = context.Graph;
        _recorded.Clear();
        _framePages.Clear();
        for (int index = _pages.Count - 1; index >= 0; index--)
            if (!_pages[index].Source.TryGetTarget(out _))
            {
                frame.RetireAfterSubmittedFrames(_pages[index].Buffer);
                _pages.RemoveAt(index);
            }

        RenderGraphFrameExtension extension = frame.BeginExtension(_extensionPoint);
        foreach (TerrainGpuRequest request in _requests)
        {
            if (request.IsSubmitted || !request.Dependency.IsCompleted) continue;
            try
            {
                request.Dependency.Complete();
                GraphPassId pass = request.Record(this, ref frame, ref extension);
                if (pass == default)
                {
                    request.AcceptSubmission();
                    request.CheckResult();
                    request.Completion.Complete();
                }
                else _recorded.Add(new(request, pass));
            }
            catch (Exception failure)
            {
                request.AcceptSubmission();
                request.Completion.Fail(failure);
                throw;
            }
        }
        extension.Dispose();
    }

    internal LinkedComputePipeline GetPipeline(Shader shader, string entry)
    {
        var key = (shader, entry);
        if (!_pipelines.TryGetValue(key, out LinkedComputePipeline? pipeline))
            _pipelines.Add(key, pipeline = LinkedComputePipeline.Create(_backend, _device, shader, entry, "Terrain field brush"));
        return pipeline;
    }

    private DevicePage GetPage(TerrainFieldPage page)
    {
        if (_pageData.TryGetValue(page, out DevicePage? devicePage)) return devicePage;
        Buffer buffer = _backend.CreateBuffer(_device,
            new BufferDesc((ulong)page.SampleCount * sizeof(float), BufferUsages.CopySource | BufferUsages.CopyDestination
                | BufferUsages.ShaderRead | BufferUsages.ShaderWrite, "Terrain field page"));
        devicePage = new DevicePage(page, buffer);
        _pageData.Add(page, devicePage);
        _pages.Add(devicePage);
        return devicePage;
    }

    private GraphBufferId ImportPage(ref RenderGraphFrame frame, DevicePage page)
    {
        _framePages.Add(page);
        return frame.Import(page.Buffer, page.States);
    }

    internal GraphBufferId GetOutput(ref RenderGraphFrame frame, TerrainFieldPage page) => ImportPage(ref frame, GetPage(page));

    private GraphBufferId GetInput(ref RenderGraphFrame frame, ref RenderGraphFrameExtension extension, TerrainFieldPage page)
    {
        DevicePage devicePage = GetPage(page);
        GraphBufferId buffer = ImportPage(ref frame, devicePage);
        if (!devicePage.HasContents && devicePage.UploadFrame != frame.FrameIdentity && page.Samples is { } samples)
        {
            GraphBufferSlice upload = frame.Upload<float>(samples, BufferUsages.CopySource, "Terrain CPU page");
            Copy(ref extension, "Upload terrain page", upload, buffer, 0, (ulong)page.SampleCount * sizeof(float));
            devicePage.UploadFrame = frame.FrameIdentity;
        }
        return buffer;
    }

    internal HeightGpuInput CreateHeightInput(ref RenderGraphFrame frame, ref RenderGraphFrameExtension extension,
        HeightGrid grid, HeightFieldData source, HeightRegion range)
    {
        int size = grid.PageSize;
        int minX = range.MinX / size, minZ = range.MinZ / size;
        int width = (range.MaxX + size - 1) / size - minX;
        int depth = (range.MaxZ + size - 1) / size - minZ;
        var offsets = new int[width * depth];
        Array.Fill(offsets, -1);
        var pages = new List<TerrainFieldPage>();
        for (int z = 0; z < depth; z++)
        for (int x = 0; x < width; x++)
            if (source.Pages.TryGetValue((minX + x, minZ + z), out TerrainFieldPage? page))
            {
                offsets[z * width + x] = pages.Count * size * size;
                pages.Add(page);
            }

        GraphBufferSlice samples;
        if (pages.Count == 0)
            samples = frame.Upload<float>([0f], BufferUsages.ShaderRead | BufferUsages.CopySource);
        else
        {
            ulong bytesPerPage = (ulong)size * (ulong)size * sizeof(float);
            ulong bytes = (ulong)pages.Count * bytesPerPage;
            GraphBufferId packed = frame.CreateBuffer(new BufferDesc(bytes,
                BufferUsages.CopyDestination | BufferUsages.CopySource | BufferUsages.ShaderRead, "Terrain brush input"));
            for (int index = 0; index < pages.Count; index++)
            {
                GraphBufferId page = GetInput(ref frame, ref extension, pages[index]);
                Copy(ref extension, "Gather terrain input", new(page, BufferRange.Whole), packed,
                    (ulong)index * bytesPerPage, bytesPerPage);
            }
            samples = new(packed, new(0, bytes));
        }
        return new(samples, frame.Upload<int>(offsets, BufferUsages.ShaderRead | BufferUsages.CopySource,
            "Terrain source page table"), minX, minZ, width);
    }

    internal GraphPassId RecordReadback(TerrainPageReadRequest request, ref RenderGraphFrame frame,
        ref RenderGraphFrameExtension extension)
    {
        GraphBufferId source = GetInput(ref frame, ref extension, request.Page);
        return RecordReadback(request, new(source, BufferRange.Whole), request.Samples, ref frame, ref extension);
    }

    internal GraphPassId RecordReadback(TerrainGpuRequest request, GraphBufferSlice source, float[] samples,
        ref RenderGraphFrame frame, ref RenderGraphFrameExtension extension)
    {
        ulong bytes = (ulong)samples.Length * sizeof(float);
        Buffer readback = _backend.CreateBuffer(_device,
            new BufferDesc(bytes, BufferUsages.CopyDestination, "Terrain page readback"), MemoryType.Readback);
        _readbacks.Add(request, new(readback, samples));
        GraphBufferId destination = frame.Import(readback,
            [new(BufferRange.Whole, readback.InitialSync, readback.InitialAccess, ResourceContentState.Undefined)]);
        return Copy(ref extension, "Read terrain data", source, destination, 0, bytes);
    }

    private static GraphPassId Copy(ref RenderGraphFrameExtension extension, string label, GraphBufferSlice source,
        GraphBufferId destination, ulong offset, ulong bytes) =>
        extension.AddCopyPass(label, PassQueueSelection.AnyCompatible(), new CopyData(source, destination, offset, bytes),
            new PassOptions(Culling: PassCullingMode.NeverCull),
            static (ref PassDefinition access, ref CopyData data) =>
            {
                access.Read(data.Source.Buffer, new(data.Source.Range.Offset, data.Bytes), PipelineSync.Copy, ResourceAccess.CopySource);
                access.Write(data.Destination, new(data.Offset, data.Bytes), PipelineSync.Copy,
                    ResourceAccess.CopyDestination, WriteCoverage.Complete);
            },
            static (ref CopyPassCommandScope commands, in CopyData data) =>
                commands.CopyBuffer(new(commands.GetBuffer(data.Source.Buffer), data.Source.Range.Offset,
                    commands.GetBuffer(data.Destination), data.Offset, data.Bytes)));

    public void OnRenderGraphSubmitted(RenderGraphFrameResult result)
    {
        foreach (DevicePage page in _framePages)
        {
            ReadOnlySpan<BufferBoundaryState> states = result.GetBufferStates(page.Buffer);
            if (states.IsEmpty) continue;
            page.States = states.ToArray();
            page.HasContents = true;
            foreach (BufferBoundaryState state in states)
                page.HasContents &= state.Contents == ResourceContentState.Defined;
        }
        foreach (RecordedRequest work in _recorded)
        {
            TerrainGpuRequest request = work.Request;
            if (request.IsSubmitted || !result.TryGetPassCompletion(work.Pass, out QueueCompletion completion)) continue;
            request.AcceptSubmission();
            _readbacks.TryGetValue(request, out ReadbackData? readback);
            _completions.Add(Task.Run(() => Complete(request, completion, readback)));
        }
    }

    private void Complete(TerrainGpuRequest request, QueueCompletion completion, ReadbackData? readback)
    {
        try
        {
            WaitStatus status = _backend.WaitCpu(completion, Timeout.InfiniteTimeSpan);
            if (status != WaitStatus.Completed) throw new InvalidOperationException($"Terrain GPU work ended with {status}.");
            if (readback is not null)
            {
                using MappedBuffer mapping = _backend.Map(readback.Buffer, MapType.Read, BufferRange.Whole);
                mapping.Invalidate(mapping.Range);
                MemoryMarshal.Cast<byte, float>(mapping.Bytes).CopyTo(readback.Samples);
            }
            request.CheckResult();
            request.Completion.Complete();
        }
        catch (Exception failure) { request.Completion.Fail(failure); }
        finally { readback?.Buffer.Dispose(); }
    }

    public void OnRenderGraphFrameFinished(RenderGraphFrameResult result)
    {
        OnRenderGraphSubmitted(result);
        foreach (var pair in _readbacks.ToArray())
            if (pair.Key.IsSubmitted) _readbacks.Remove(pair.Key);
            else
            {
                pair.Value.Buffer.Dispose();
                _readbacks.Remove(pair.Key);
            }
    }

    public void Dispose()
    {
        Task.WhenAll(_completions).GetAwaiter().GetResult();
        foreach (TerrainGpuRequest request in _requests)
            if (!request.IsSubmitted)
            {
                request.AcceptSubmission();
                request.Completion.Fail(new OperationCanceledException("The terrain graphics feature has finished."));
            }
        foreach (ReadbackData readback in _readbacks.Values) readback.Buffer.Dispose();
        foreach (DevicePage page in _pages) page.Buffer.Dispose();
        foreach (LinkedComputePipeline pipeline in _pipelines.Values) pipeline.Dispose();
    }

    private sealed class DevicePage(TerrainFieldPage source, Buffer buffer)
    {
        internal WeakReference<TerrainFieldPage> Source { get; } = new(source);
        internal Buffer Buffer { get; } = buffer;
        internal BufferBoundaryState[] States =
            [new(BufferRange.Whole, buffer.InitialSync, buffer.InitialAccess, ResourceContentState.Undefined)];
        internal bool HasContents;
        internal ulong UploadFrame;
    }

    private readonly record struct RecordedRequest(TerrainGpuRequest Request, GraphPassId Pass);
    private sealed record ReadbackData(Buffer Buffer, float[] Samples);
    private readonly record struct CopyData(GraphBufferSlice Source, GraphBufferId Destination, ulong Offset, ulong Bytes);
}
