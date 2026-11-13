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
    IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly RenderPipelines _renderPipelines;
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly GraphRegionId _extensionPoint;
    private readonly ConditionalWeakTable<TerrainFieldPage, DevicePage> _pageData = new();
    private readonly List<DevicePage> _pages = [];
    private readonly Dictionary<(Shader Shader, string Entry), LinkedComputePipeline> _pipelines = [];
    private readonly HashSet<TerrainGpuRequest> _requests = [];
    private readonly List<Task> _completions = [];
    private QueryHandle _query;

    public RenderTerrainData(IGraphicsBackend backend, Device device, global::SomeEngine.RenderGraph.RenderGraph graph,
        RenderPipelines pipelines)
    {
        _backend = backend;
        _device = device;
        _renderPipelines = pipelines;
        _graph = graph;
        using RenderGraphEdit edit = graph.BeginEdit();
        _extensionPoint = edit.AddFrameRegion("Terrain field work");
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
        foreach (TerrainGpuRequest request in _requests) request.Prepare(this);
        for (int index = _pages.Count - 1; index >= 0; index--)
            if (!_pages[index].Source.TryGetTarget(out _))
            {
                using RenderGraphEdit edit = _graph.BeginEdit();
                edit.Remove(_pages[index].Buffer);
                edit.Commit();
                _pages.RemoveAt(index);
            }
    }

    public void OnDestroy(ref RenderPrepareSystemContext context)
    {
        Task.WhenAll(_completions).GetAwaiter().GetResult();
        foreach (TerrainGpuRequest request in _requests) request.Cancel();
        context.World.ReleaseQuery(_query);
    }

    public void OnUpdate(ref RenderFrameSystemContext context)
    {
        RenderGraphFrame frame = context.Graph;
        TerrainFrame work = GetFrame(ref frame);
        TerrainFrameCommands extension = new TerrainFrameCommands(frame, _extensionPoint);
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
                else work.Requests.Add(request);
            }
            catch (Exception failure)
            {
                request.AcceptSubmission();
                request.Completion.Fail(failure);
                throw;
            }
        }
    }

    internal LinkedComputePipeline GetPipeline(Shader shader, string entry)
    {
        var key = (shader, entry);
        if (!_pipelines.TryGetValue(key, out LinkedComputePipeline? pipeline))
            _pipelines.Add(key, pipeline = LinkedComputePipeline.Create(_renderPipelines, shader, entry, "Terrain field brush"));
        return pipeline;
    }

    internal void PreparePage(TerrainFieldPage page)
    {
        if (_pageData.TryGetValue(page, out _)) return;
        using RenderGraphEdit edit = _graph.BeginEdit();
        GraphBufferId buffer = edit.CreatePersistentBuffer(new((ulong)page.SampleCount * sizeof(float),
            BufferUsages.CopySource | BufferUsages.CopyDestination | BufferUsages.ShaderRead | BufferUsages.ShaderWrite,
            "Terrain field page"));
        edit.Commit();
        var data = new DevicePage(page, buffer);
        _pageData.Add(page, data);
        _pages.Add(data);
    }

    private TerrainFrame GetFrame(ref RenderGraphFrame frame)
    {
        TerrainFrame data = frame.GetFrameData(this, () => new TerrainFrame(this));
        frame.ObserveExecution(data);
        return data;
    }

    private GraphBufferId ImportPage(ref RenderGraphFrame frame, DevicePage page)
    {
        GetFrame(ref frame).Pages.Add(page);
        return page.Buffer;
    }

    internal GraphBufferId GetOutput(ref RenderGraphFrame frame, TerrainFieldPage page) =>
        ImportPage(ref frame, _pageData.GetValue(page, static _ => throw new InvalidOperationException()));

    private GraphBufferId GetInput(ref RenderGraphFrame frame, ref TerrainFrameCommands extension, TerrainFieldPage page)
    {
        DevicePage data = _pageData.GetValue(page, static _ => throw new InvalidOperationException());
        GraphBufferId buffer = ImportPage(ref frame, data);
        if (!data.HasContents && GetFrame(ref frame).Uploads.Add(data) && page.CpuContents is { } samples)
        {
            GraphBufferSlice upload = frame.Upload<float>(samples, BufferUsages.CopySource, "Terrain CPU page");
            Copy(ref extension, "Upload terrain page", upload, buffer, 0, (ulong)page.SampleCount * sizeof(float));
        }
        return buffer;
    }

    internal HeightGpuInput CreateHeightInput(ref RenderGraphFrame frame, ref TerrainFrameCommands extension,
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
        ref TerrainFrameCommands extension)
    {
        GraphBufferId source = GetInput(ref frame, ref extension, request.Page);
        return RecordReadback(request, new(source, BufferRange.Whole), request.Samples, ref frame, ref extension);
    }

    internal GraphPassId RecordReadback(TerrainGpuRequest request, GraphBufferSlice source, float[] samples,
        ref RenderGraphFrame frame, ref TerrainFrameCommands extension)
    {
        var receiver = new TerrainReadback(samples);
        GraphReadbackId readback = frame.AddReadback(source, receiver, "Terrain page readback", region: _extensionPoint);
        GraphPassId completion = extension.AddCopyPass("Complete terrain readback", PassQueueSelection.AnyCompatible(), 0,
            new PassOptions(Culling: PassCullingMode.NeverCull), static (ref PassDefinition _, ref int _) => { },
            static (ref CopyPassCommandScope _, in int _) => { });
        frame.OrderAfter(completion, readback);
        return completion;
    }

    private static GraphPassId Copy(ref TerrainFrameCommands extension, string label, GraphBufferSlice source,
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

    private void Complete(TerrainGpuRequest request, ulong identity)
    {
        try
        {
            if (!_graph.IsDisposed) _graph.WaitForFrame(identity, Timeout.InfiniteTimeSpan);
            request.CheckResult();
            request.Completion.Complete();
        }
        catch (Exception failure) { request.Completion.Fail(failure); }
    }

    public void Dispose()
    {
        Task.WhenAll(_completions).GetAwaiter().GetResult();
        foreach (TerrainGpuRequest request in _requests) request.Cancel();
        if (!_graph.IsDisposed && !_graph.IsLost)
        {
            PreserveCpuContents();
            using RenderGraphEdit edit = _graph.BeginEdit();
            edit.Remove(_extensionPoint);
            foreach (DevicePage page in _pages) edit.Remove(page.Buffer);
            edit.Commit();
        }
        foreach (LinkedComputePipeline pipeline in _pipelines.Values) pipeline.Dispose();
        _pipelines.Clear();
        _pages.Clear();
        _pageData.Clear();
        _requests.Clear();
        _completions.Clear();
    }

    private void PreserveCpuContents()
    {
        DevicePage[] pages = _pages.Where(page => page.HasContents &&
            page.Source.TryGetTarget(out TerrainFieldPage? source) && source.CpuContents is null).ToArray();
        if (pages.Length == 0) return;
        ulong identity;
        using (RenderGraphFrame frame = _graph.BeginFrame())
        {
            foreach (DevicePage page in pages)
                if (page.Source.TryGetTarget(out TerrainFieldPage? source))
                    frame.AddReadback(new(page.Buffer, new(0, (ulong)source.SampleCount * sizeof(float))),
                        new RetainedTerrainReadback(source), "Retain terrain page", region: _extensionPoint);
            identity = frame.FrameIdentity;
            frame.Execute();
        }
        _graph.WaitForFrame(identity, Timeout.InfiniteTimeSpan);
    }

    private sealed class TerrainFrame(RenderTerrainData data) : IRenderGraphFrameExecutionObserver
    {
        internal readonly HashSet<DevicePage> Pages = [];
        internal readonly HashSet<DevicePage> Uploads = [];
        internal readonly List<TerrainGpuRequest> Requests = [];
        public void OnFrameSubmitted(in RenderGraphSubmission submission)
        {
            foreach (DevicePage page in Pages) page.HasContents = true;
            ulong identity = submission.FrameIdentity;
            foreach (TerrainGpuRequest request in Requests)
            {
                request.AcceptSubmission();
                data._completions.Add(Task.Run(() => data.Complete(request, identity)));
            }
        }
    }

    private sealed class TerrainReadback(float[] samples) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => MemoryMarshal.Cast<byte, float>(bytes).CopyTo(samples);
    }
    private sealed class RetainedTerrainReadback(TerrainFieldPage source) : IGraphReadbackReceiver
    {
        public void OnReadback(ReadOnlySpan<byte> bytes, ulong tag) => source.SetCpuContents(MemoryMarshal.Cast<byte, float>(bytes).ToArray());
    }
    private sealed class DevicePage(TerrainFieldPage source, GraphBufferId buffer)
    {
        internal WeakReference<TerrainFieldPage> Source { get; } = new(source);
        internal GraphBufferId Buffer { get; } = buffer;
        internal bool HasContents;
    }
    private readonly record struct CopyData(GraphBufferSlice Source, GraphBufferId Destination, ulong Offset, ulong Bytes);
}
