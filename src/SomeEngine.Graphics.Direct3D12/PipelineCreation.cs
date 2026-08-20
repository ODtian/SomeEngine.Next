using System.Diagnostics;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

/// <summary>Reports asynchronous Direct3D 12 Pipeline creation activity for one Device.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable snapshots may be shared.</para>
/// <para><b>Ownership:</b> Pure value; owns no RHI or native lifetime.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; a captured snapshot remains readable.</para>
/// <para>Cache counters report lookups performed by asynchronous creation requests, not all cache
/// operations performed by the Device.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct D3D12PipelineCreationInfo(
    long AcceptedCount,
    int QueueDepth,
    int PeakQueueDepth,
    int RunningCount,
    long ReadyCount,
    long FailedCount,
    long DeviceLostCount,
    long CacheLookupHitCount,
    long CacheLookupMissCount,
    TimeSpan TotalQueueWait,
    TimeSpan MaximumQueueWait,
    TimeSpan TotalNativeCreationTime,
    TimeSpan MaximumNativeCreationTime,
    long GraphicsCount,
    long ComputeCount,
    long MeshCount,
    long RayTracingCount,
    long WorkGraphCount);

internal sealed unsafe partial class D3D12Backend
{
    public Task<Pipeline> CreateGraphicsPipelineAsync(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        GraphicsPipelineCreationRequest request =
            GraphicsPipelineCreationRequest.Capture(nativeDevice, nativeCache, desc);
        return EnqueuePipelineCreation(nativeDevice, request);
    }

    public Task<Pipeline> CreateComputePipelineAsync(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ComputePipelineCreationRequest request =
            ComputePipelineCreationRequest.Capture(nativeDevice, nativeCache, desc);
        return EnqueuePipelineCreation(nativeDevice, request);
    }

    public Task<Pipeline> CreateMeshPipelineAsync(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        _ = nativeDevice.RequireCapability<MeshShaders>(nameof(CreateMeshPipelineAsync));
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        MeshPipelineCreationRequest request =
            MeshPipelineCreationRequest.Capture(nativeDevice, nativeCache, desc);
        return EnqueuePipelineCreation(nativeDevice, request);
    }

    public Task<Pipeline> CreateRayTracingPipelineAsync(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        RayTracing capability =
            nativeDevice.RequireCapability<RayTracing>(nameof(CreateRayTracingPipelineAsync));
        if (!capability.PipelineRayTracing)
            throw new NotSupportedException("Pipeline ray tracing is unavailable.");
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        RayTracingPipelineCreationRequest request =
            RayTracingPipelineCreationRequest.Capture(nativeDevice, nativeCache, desc);
        return EnqueuePipelineCreation(nativeDevice, request);
    }

    public Task<Pipeline> CreateWorkGraphPipelineAsync(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        _ = nativeDevice.RequireCapability<WorkGraphs>(nameof(CreateWorkGraphPipelineAsync));
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        WorkGraphPipelineCreationRequest request =
            WorkGraphPipelineCreationRequest.Capture(nativeDevice, nativeCache, desc);
        return EnqueuePipelineCreation(nativeDevice, request);
    }

    private static Task<Pipeline> EnqueuePipelineCreation(
        D3D12Device device,
        PipelineCreationRequest request)
    {
        try
        {
            return device.PipelineCompiler.Enqueue(request);
        }
        catch
        {
            request.ReleaseCapturedState();
            throw;
        }
    }

    internal static D3D12PipelineCreationInfo GetPipelineCreationInfo(Device device) =>
        device is D3D12Device native
            ? native.PipelineCompiler.GetInfo()
            : throw new ArgumentException(
                "The Device was not created by the Direct3D 12 backend.",
                nameof(device));

    private abstract class PipelineCreationRequest
    {
        private readonly TaskCompletionSource<Pipeline> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private RetainedSlangProgram? _program;
        private D3D12PipelineCache? _cache;
        private int _capturedStateReleased;

        protected PipelineCreationRequest(
            D3D12Device device,
            PipelineType type,
            RetainedSlangProgram program,
            D3D12PipelineCache? cache)
        {
            Device = device;
            Type = type;
            _program = program;
            _cache = cache;
        }

        internal PipelineCreationRequest? QueueNext { get; set; }
        internal long EnqueuedTimestamp { get; set; }
        internal D3D12Device Device { get; }
        internal PipelineType Type { get; }
        internal D3D12PipelineCache? Cache => Volatile.Read(ref _cache);
        internal Task<Pipeline> Task => _completion.Task;
        protected IComponentType Program => Volatile.Read(ref _program)?.Program
            ?? throw new ObjectDisposedException(nameof(PipelineCreationRequest));

        internal abstract Pipeline Create();

        internal void Complete(Pipeline pipeline) => _completion.TrySetResult(pipeline);

        internal void Fail(Exception exception) => _completion.TrySetException(exception);

        internal void ReleaseCapturedState()
        {
            if (Interlocked.Exchange(ref _capturedStateReleased, 1) != 0)
                return;
            Interlocked.Exchange(ref _program, null)?.Dispose();
            Interlocked.Exchange(ref _cache, null)?.ReleasePipelineCreationUse();
        }

        protected static RetainedSlangProgram CaptureProgram(IComponentType program)
        {
            ArgumentNullException.ThrowIfNull(program);
            return RetainProgram(program);
        }

        protected static void ReleaseCaptureOnFailure(
            RetainedSlangProgram? retainedProgram,
            D3D12PipelineCache? cache,
            bool cacheRetained)
        {
            retainedProgram?.Dispose();
            if (cacheRetained)
                cache!.ReleasePipelineCreationUse();
        }
    }

    private sealed class GraphicsPipelineCreationRequest : PipelineCreationRequest
    {
        private readonly EntryPointReflection _vertex;
        private readonly EntryPointReflection _pixel;
        private readonly VertexBufferLayout[] _vertexBuffers;
        private readonly VertexAttribute[] _vertexAttributes;
        private readonly PrimitiveTopology _topology;
        private readonly StripCut _stripCut;
        private readonly RasterizerState _rasterizer;
        private readonly MultisampleState _multisample;
        private readonly DepthStencilState _depthStencil;
        private readonly BlendAttachmentState[] _blendAttachments;
        private readonly bool _independentBlend;
        private readonly LogicOperation? _logicOperation;
        private readonly Format[] _colorFormats;
        private readonly Format? _depthStencilFormat;
        private readonly uint _sampleCount;
        private readonly DynamicStates _dynamicStates;
        private readonly bool _hasStreamOutput;
        private readonly StreamOutputElement[] _streamOutputElements;
        private readonly uint[] _streamOutputStrides;
        private readonly uint? _rasterizedStream;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;

        private GraphicsPipelineCreationRequest(
            D3D12Device device,
            D3D12PipelineCache? cache,
            RetainedSlangProgram program,
            EntryPointReflection vertex,
            EntryPointReflection pixel,
            VertexBufferLayout[] vertexBuffers,
            VertexAttribute[] vertexAttributes,
            PrimitiveTopology topology,
            StripCut stripCut,
            RasterizerState rasterizer,
            MultisampleState multisample,
            DepthStencilState depthStencil,
            BlendAttachmentState[] blendAttachments,
            bool independentBlend,
            LogicOperation? logicOperation,
            Format[] colorFormats,
            Format? depthStencilFormat,
            uint sampleCount,
            DynamicStates dynamicStates,
            bool hasStreamOutput,
            StreamOutputElement[] streamOutputElements,
            uint[] streamOutputStrides,
            uint? rasterizedStream,
            string? label,
            StaticSamplerBinding[] staticSamplers)
            : base(device, PipelineType.Graphics, program, cache)
        {
            _vertex = vertex;
            _pixel = pixel;
            _vertexBuffers = vertexBuffers;
            _vertexAttributes = vertexAttributes;
            _topology = topology;
            _stripCut = stripCut;
            _rasterizer = rasterizer;
            _multisample = multisample;
            _depthStencil = depthStencil;
            _blendAttachments = blendAttachments;
            _independentBlend = independentBlend;
            _logicOperation = logicOperation;
            _colorFormats = colorFormats;
            _depthStencilFormat = depthStencilFormat;
            _sampleCount = sampleCount;
            _dynamicStates = dynamicStates;
            _hasStreamOutput = hasStreamOutput;
            _streamOutputElements = streamOutputElements;
            _streamOutputStrides = streamOutputStrides;
            _rasterizedStream = rasterizedStream;
            _label = label;
            _staticSamplers = staticSamplers;
        }

        internal static GraphicsPipelineCreationRequest Capture(
            D3D12Device device,
            D3D12PipelineCache? cache,
            in GraphicsPipelineDesc desc)
        {
            RetainedSlangProgram retainedProgram = CaptureProgram(desc.Program);
            bool cacheRetained = false;
            try
            {
                VertexBufferLayout[] vertexBuffers = desc.VertexBuffers.ToArray();
                VertexAttribute[] vertexAttributes = desc.VertexAttributes.ToArray();
                BlendAttachmentState[] blendAttachments = desc.Blend.Attachments.ToArray();
                Format[] colorFormats = desc.Attachments.ColorFormats.ToArray();
                bool hasStreamOutput = desc.HasStreamOutput;
                StreamOutputElement[] streamOutputElements = hasStreamOutput
                    ? desc.StreamOutput.Elements.ToArray()
                    : [];
                uint[] streamOutputStrides = hasStreamOutput
                    ? desc.StreamOutput.BufferStrides.ToArray()
                    : [];
                StaticSamplerBinding[] staticSamplers = desc.StaticSamplers.ToArray();
                cache?.RetainForPipelineCreation();
                cacheRetained = cache is not null;
                return new GraphicsPipelineCreationRequest(
                    device,
                    cache,
                    retainedProgram,
                    desc.Vertex,
                    desc.Pixel,
                    vertexBuffers,
                    vertexAttributes,
                    desc.Topology,
                    desc.StripCut,
                    desc.Rasterizer,
                    desc.Multisample,
                    desc.DepthStencil,
                    blendAttachments,
                    desc.Blend.IndependentBlend,
                    desc.Blend.LogicOperation,
                    colorFormats,
                    desc.Attachments.DepthStencilFormat,
                    desc.Attachments.SampleCount,
                    desc.DynamicStates,
                    hasStreamOutput,
                    streamOutputElements,
                    streamOutputStrides,
                    hasStreamOutput ? desc.StreamOutput.RasterizedStreamIndex : null,
                    desc.Label,
                    staticSamplers);
            }
            catch
            {
                ReleaseCaptureOnFailure(retainedProgram, cache, cacheRetained);
                throw;
            }
        }

        internal override Pipeline Create()
        {
            BlendState blend = new(_blendAttachments, _independentBlend, _logicOperation);
            AttachmentFormatSignature attachments = new(
                _colorFormats,
                _depthStencilFormat,
                _sampleCount);
            if (_hasStreamOutput)
            {
                StreamOutputState streamOutput = new(
                    _streamOutputElements,
                    _streamOutputStrides,
                    _rasterizedStream);
                GraphicsPipelineDesc desc = new(
                    Program,
                    _vertex,
                    _pixel,
                    _vertexBuffers,
                    _vertexAttributes,
                    _topology,
                    _stripCut,
                    _rasterizer,
                    _multisample,
                    _depthStencil,
                    blend,
                    attachments,
                    streamOutput,
                    _dynamicStates,
                    _label,
                    _staticSamplers);
                return Device.Backend.CreateGraphicsPipeline(Device, desc, Cache);
            }

            GraphicsPipelineDesc ordinary = new(
                Program,
                _vertex,
                _pixel,
                _vertexBuffers,
                _vertexAttributes,
                _topology,
                _stripCut,
                _rasterizer,
                _multisample,
                _depthStencil,
                blend,
                attachments,
                _dynamicStates,
                _label,
                _staticSamplers);
            return Device.Backend.CreateGraphicsPipeline(Device, ordinary, Cache);
        }
    }

    private sealed class ComputePipelineCreationRequest : PipelineCreationRequest
    {
        private readonly EntryPointReflection _compute;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;

        private ComputePipelineCreationRequest(
            D3D12Device device,
            D3D12PipelineCache? cache,
            RetainedSlangProgram program,
            EntryPointReflection compute,
            string? label,
            StaticSamplerBinding[] staticSamplers)
            : base(device, PipelineType.Compute, program, cache)
        {
            _compute = compute;
            _label = label;
            _staticSamplers = staticSamplers;
        }

        internal static ComputePipelineCreationRequest Capture(
            D3D12Device device,
            D3D12PipelineCache? cache,
            in ComputePipelineDesc desc)
        {
            RetainedSlangProgram retainedProgram = CaptureProgram(desc.Program);
            bool cacheRetained = false;
            try
            {
                StaticSamplerBinding[] staticSamplers = desc.StaticSamplers.ToArray();
                cache?.RetainForPipelineCreation();
                cacheRetained = cache is not null;
                return new ComputePipelineCreationRequest(
                    device,
                    cache,
                    retainedProgram,
                    desc.Compute,
                    desc.Label,
                    staticSamplers);
            }
            catch
            {
                ReleaseCaptureOnFailure(retainedProgram, cache, cacheRetained);
                throw;
            }
        }

        internal override Pipeline Create()
        {
            ComputePipelineDesc desc = new(Program, _compute, _label, _staticSamplers);
            return Device.Backend.CreateComputePipeline(Device, desc, Cache);
        }
    }

    private sealed class MeshPipelineCreationRequest : PipelineCreationRequest
    {
        private readonly EntryPointReflection _mesh;
        private readonly EntryPointReflection _amplification;
        private readonly EntryPointReflection _pixel;
        private readonly RasterizerState _rasterizer;
        private readonly MultisampleState _multisample;
        private readonly DepthStencilState _depthStencil;
        private readonly BlendAttachmentState[] _blendAttachments;
        private readonly bool _independentBlend;
        private readonly LogicOperation? _logicOperation;
        private readonly Format[] _colorFormats;
        private readonly Format? _depthStencilFormat;
        private readonly uint _sampleCount;
        private readonly DynamicStates _dynamicStates;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;

        private MeshPipelineCreationRequest(
            D3D12Device device,
            D3D12PipelineCache? cache,
            RetainedSlangProgram program,
            EntryPointReflection mesh,
            EntryPointReflection amplification,
            EntryPointReflection pixel,
            RasterizerState rasterizer,
            MultisampleState multisample,
            DepthStencilState depthStencil,
            BlendAttachmentState[] blendAttachments,
            bool independentBlend,
            LogicOperation? logicOperation,
            Format[] colorFormats,
            Format? depthStencilFormat,
            uint sampleCount,
            DynamicStates dynamicStates,
            string? label,
            StaticSamplerBinding[] staticSamplers)
            : base(device, PipelineType.Mesh, program, cache)
        {
            _mesh = mesh;
            _amplification = amplification;
            _pixel = pixel;
            _rasterizer = rasterizer;
            _multisample = multisample;
            _depthStencil = depthStencil;
            _blendAttachments = blendAttachments;
            _independentBlend = independentBlend;
            _logicOperation = logicOperation;
            _colorFormats = colorFormats;
            _depthStencilFormat = depthStencilFormat;
            _sampleCount = sampleCount;
            _dynamicStates = dynamicStates;
            _label = label;
            _staticSamplers = staticSamplers;
        }

        internal static MeshPipelineCreationRequest Capture(
            D3D12Device device,
            D3D12PipelineCache? cache,
            in MeshPipelineDesc desc)
        {
            RetainedSlangProgram retainedProgram = CaptureProgram(desc.Program);
            bool cacheRetained = false;
            try
            {
                BlendAttachmentState[] blendAttachments = desc.Blend.Attachments.ToArray();
                Format[] colorFormats = desc.Attachments.ColorFormats.ToArray();
                StaticSamplerBinding[] staticSamplers = desc.StaticSamplers.ToArray();
                cache?.RetainForPipelineCreation();
                cacheRetained = cache is not null;
                return new MeshPipelineCreationRequest(
                    device,
                    cache,
                    retainedProgram,
                    desc.Mesh,
                    desc.Amplification,
                    desc.Pixel,
                    desc.Rasterizer,
                    desc.Multisample,
                    desc.DepthStencil,
                    blendAttachments,
                    desc.Blend.IndependentBlend,
                    desc.Blend.LogicOperation,
                    colorFormats,
                    desc.Attachments.DepthStencilFormat,
                    desc.Attachments.SampleCount,
                    desc.DynamicStates,
                    desc.Label,
                    staticSamplers);
            }
            catch
            {
                ReleaseCaptureOnFailure(retainedProgram, cache, cacheRetained);
                throw;
            }
        }

        internal override Pipeline Create()
        {
            BlendState blend = new(_blendAttachments, _independentBlend, _logicOperation);
            AttachmentFormatSignature attachments = new(
                _colorFormats,
                _depthStencilFormat,
                _sampleCount);
            MeshPipelineDesc desc = new(
                Program,
                _mesh,
                _amplification,
                _pixel,
                _rasterizer,
                _multisample,
                _depthStencil,
                blend,
                attachments,
                _dynamicStates,
                _label,
                _staticSamplers);
            return Device.Backend.CreateMeshPipeline(Device, desc, Cache);
        }
    }

    private sealed class RayTracingPipelineCreationRequest : PipelineCreationRequest
    {
        private readonly EntryPointReflection[] _rayGeneration;
        private readonly EntryPointReflection[] _miss;
        private readonly EntryPointReflection[] _callable;
        private readonly RayTracingHitGroup[] _hitGroups;
        private readonly uint _maximumRecursionDepth;
        private readonly uint _maximumPayloadSize;
        private readonly uint _maximumAttributeSize;
        private readonly RayTracingPipelineOptions _options;
        private readonly uint _nodeMask;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;

        private RayTracingPipelineCreationRequest(
            D3D12Device device,
            D3D12PipelineCache? cache,
            RetainedSlangProgram program,
            EntryPointReflection[] rayGeneration,
            EntryPointReflection[] miss,
            EntryPointReflection[] callable,
            RayTracingHitGroup[] hitGroups,
            uint maximumRecursionDepth,
            uint maximumPayloadSize,
            uint maximumAttributeSize,
            RayTracingPipelineOptions options,
            uint nodeMask,
            string? label,
            StaticSamplerBinding[] staticSamplers)
            : base(device, PipelineType.RayTracing, program, cache)
        {
            _rayGeneration = rayGeneration;
            _miss = miss;
            _callable = callable;
            _hitGroups = hitGroups;
            _maximumRecursionDepth = maximumRecursionDepth;
            _maximumPayloadSize = maximumPayloadSize;
            _maximumAttributeSize = maximumAttributeSize;
            _options = options;
            _nodeMask = nodeMask;
            _label = label;
            _staticSamplers = staticSamplers;
        }

        internal static RayTracingPipelineCreationRequest Capture(
            D3D12Device device,
            D3D12PipelineCache? cache,
            in RayTracingPipelineDesc desc)
        {
            RetainedSlangProgram retainedProgram = CaptureProgram(desc.Program);
            bool cacheRetained = false;
            try
            {
                EntryPointReflection[] rayGeneration = desc.RayGeneration.ToArray();
                EntryPointReflection[] miss = desc.Miss.ToArray();
                EntryPointReflection[] callable = desc.Callable.ToArray();
                RayTracingHitGroup[] hitGroups = desc.HitGroups.ToArray();
                StaticSamplerBinding[] staticSamplers = desc.StaticSamplers.ToArray();
                cache?.RetainForPipelineCreation();
                cacheRetained = cache is not null;
                return new RayTracingPipelineCreationRequest(
                    device,
                    cache,
                    retainedProgram,
                    rayGeneration,
                    miss,
                    callable,
                    hitGroups,
                    desc.MaximumRecursionDepth,
                    desc.MaximumPayloadSize,
                    desc.MaximumAttributeSize,
                    desc.Options,
                    desc.NodeMask,
                    desc.Label,
                    staticSamplers);
            }
            catch
            {
                ReleaseCaptureOnFailure(retainedProgram, cache, cacheRetained);
                throw;
            }
        }

        internal override Pipeline Create()
        {
            RayTracingPipelineDesc desc = new(
                Program,
                _rayGeneration,
                _miss,
                _callable,
                _hitGroups,
                _maximumRecursionDepth,
                _maximumPayloadSize,
                _maximumAttributeSize,
                _options,
                _nodeMask,
                _label,
                _staticSamplers);
            return Device.Backend.CreateRayTracingPipeline(Device, desc, Cache);
        }
    }

    private sealed class WorkGraphPipelineCreationRequest : PipelineCreationRequest
    {
        private readonly uint _nodeMask;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;

        private WorkGraphPipelineCreationRequest(
            D3D12Device device,
            D3D12PipelineCache? cache,
            RetainedSlangProgram program,
            uint nodeMask,
            string? label,
            StaticSamplerBinding[] staticSamplers)
            : base(device, PipelineType.WorkGraph, program, cache)
        {
            _nodeMask = nodeMask;
            _label = label;
            _staticSamplers = staticSamplers;
        }

        internal static WorkGraphPipelineCreationRequest Capture(
            D3D12Device device,
            D3D12PipelineCache? cache,
            in WorkGraphPipelineDesc desc)
        {
            RetainedSlangProgram retainedProgram = CaptureProgram(desc.Program);
            bool cacheRetained = false;
            try
            {
                StaticSamplerBinding[] staticSamplers = desc.StaticSamplers.ToArray();
                cache?.RetainForPipelineCreation();
                cacheRetained = cache is not null;
                return new WorkGraphPipelineCreationRequest(
                    device,
                    cache,
                    retainedProgram,
                    desc.NodeMask,
                    desc.Label,
                    staticSamplers);
            }
            catch
            {
                ReleaseCaptureOnFailure(retainedProgram, cache, cacheRetained);
                throw;
            }
        }

        internal override Pipeline Create()
        {
            WorkGraphPipelineDesc desc = new(Program, _nodeMask, _label, _staticSamplers);
            return Device.Backend.CreateWorkGraphPipeline(Device, desc, Cache);
        }
    }

    private sealed class D3D12PipelineCompiler : IDisposable
    {
        private const int MaximumQueuedRequests = 256;
        private const int MaximumWorkerCount = 4;

        [ThreadStatic]
        private static D3D12PipelineCompiler? t_current;

        [ThreadStatic]
        private static D3D12PipelineCache? t_currentCache;

        private readonly object _gate = new();
        private readonly Queue<PipelineCreationRequest> _pending = new(MaximumQueuedRequests);
        private readonly Thread[] _workers;
        private Exception? _terminalException;
        private bool _stopping;
        private int _queueDepth;
        private int _peakQueueDepth;
        private int _running;
        private long _accepted;
        private long _ready;
        private long _failed;
        private long _deviceLost;
        private long _cacheHits;
        private long _cacheMisses;
        private long _totalQueueWaitTicks;
        private long _maximumQueueWaitTicks;
        private long _totalCreationTicks;
        private long _maximumCreationTicks;
        private long _graphics;
        private long _compute;
        private long _mesh;
        private long _rayTracing;
        private long _workGraph;

        internal D3D12PipelineCompiler(D3D12Device device)
        {
            int workerCount = Math.Clamp(
                (Environment.ProcessorCount + 3) / 4,
                1,
                MaximumWorkerCount);
            _workers = new Thread[workerCount];
            int started = 0;
            try
            {
                for (; started < workerCount; started++)
                {
                    var worker = new Thread(WorkerMain)
                    {
                        IsBackground = true,
                        Name = $"SomeEngine D3D12 Pipeline {started}",
                    };
                    _workers[started] = worker;
                    worker.Start();
                }
            }
            catch
            {
                StopAccepting(new ObjectDisposedException(nameof(D3D12PipelineCompiler)));
                JoinWorkers(started);
                throw;
            }
        }

        internal Task<Pipeline> Enqueue(PipelineCreationRequest request)
        {
            lock (_gate)
            {
                if (_stopping)
                    throw CreateTerminalException();
                if (_pending.Count == MaximumQueuedRequests)
                {
                    throw new InvalidOperationException(
                        "The Direct3D 12 Pipeline creation queue is full.");
                }

                request.EnqueuedTimestamp = Stopwatch.GetTimestamp();
                _pending.Enqueue(request);
                Interlocked.Increment(ref _accepted);
                IncrementTypeCount(request.Type);
                int depth = ++_queueDepth;
                UpdateMaximum(ref _peakQueueDepth, depth);
                Monitor.Pulse(_gate);
                return request.Task;
            }
        }

        internal D3D12PipelineCreationInfo GetInfo() => new(
            Volatile.Read(ref _accepted),
            Volatile.Read(ref _queueDepth),
            Volatile.Read(ref _peakQueueDepth),
            Volatile.Read(ref _running),
            Volatile.Read(ref _ready),
            Volatile.Read(ref _failed),
            Volatile.Read(ref _deviceLost),
            Volatile.Read(ref _cacheHits),
            Volatile.Read(ref _cacheMisses),
            TimeSpan.FromTicks(Volatile.Read(ref _totalQueueWaitTicks)),
            TimeSpan.FromTicks(Volatile.Read(ref _maximumQueueWaitTicks)),
            TimeSpan.FromTicks(Volatile.Read(ref _totalCreationTicks)),
            TimeSpan.FromTicks(Volatile.Read(ref _maximumCreationTicks)),
            Volatile.Read(ref _graphics),
            Volatile.Read(ref _compute),
            Volatile.Read(ref _mesh),
            Volatile.Read(ref _rayTracing),
            Volatile.Read(ref _workGraph));

        internal void MarkDeviceLost(GraphicsException exception) => StopAccepting(exception);

        internal static bool AllowsDisposedCache(D3D12PipelineCache cache) =>
            ReferenceEquals(t_currentCache, cache);

        internal static void RecordCacheLookup(bool hit)
        {
            D3D12PipelineCompiler? current = t_current;
            if (current is null)
                return;
            if (hit)
                Interlocked.Increment(ref current._cacheHits);
            else
                Interlocked.Increment(ref current._cacheMisses);
        }

        public void Dispose()
        {
            StopAccepting(new ObjectDisposedException(
                nameof(Device),
                "The Device was disposed before queued Pipeline creation began."));
            JoinWorkers(_workers.Length);
        }

        private void WorkerMain()
        {
            while (TryTake(out PipelineCreationRequest? request))
                Execute(request!);
        }

        private bool TryTake(out PipelineCreationRequest? request)
        {
            lock (_gate)
            {
                while (!_stopping && _pending.Count == 0)
                    Monitor.Wait(_gate);
                if (_pending.Count == 0)
                {
                    request = null;
                    return false;
                }
                request = _pending.Dequeue();
                _queueDepth--;
                return true;
            }
        }

        private void Execute(PipelineCreationRequest request)
        {
            long started = Stopwatch.GetTimestamp();
            TimeSpan queueWait = Stopwatch.GetElapsedTime(request.EnqueuedTimestamp, started);
            RecordQueueWait(queueWait.Ticks);
            Interlocked.Increment(ref _running);
            t_current = this;
            t_currentCache = request.Cache;
            try
            {
                Pipeline pipeline = request.Create();
                request.ReleaseCapturedState();
                Interlocked.Increment(ref _ready);
                request.Complete(pipeline);
            }
            catch (Exception exception)
            {
                request.ReleaseCapturedState();
                RecordFailure(exception);
                request.Fail(exception);
            }
            finally
            {
                t_currentCache = null;
                t_current = null;
                long creationTicks = Stopwatch.GetElapsedTime(started).Ticks;
                Interlocked.Add(ref _totalCreationTicks, creationTicks);
                UpdateMaximum(ref _maximumCreationTicks, creationTicks);
                Interlocked.Decrement(ref _running);
            }
        }

        private void StopAccepting(Exception exception)
        {
            PipelineCreationRequest? pending;
            lock (_gate)
            {
                if (_stopping)
                    return;
                _stopping = true;
                _terminalException = exception;
                pending = DetachPendingUnderGate();
                Monitor.PulseAll(_gate);
            }
            FailDetached(pending, exception);
        }

        private PipelineCreationRequest? DetachPendingUnderGate()
        {
            PipelineCreationRequest? head = null;
            PipelineCreationRequest? tail = null;
            while (_pending.TryDequeue(out PipelineCreationRequest? request))
            {
                request.QueueNext = null;
                if (tail is null)
                    head = request;
                else
                    tail.QueueNext = request;
                tail = request;
            }
            _queueDepth = 0;
            return head;
        }

        private void FailDetached(PipelineCreationRequest? request, Exception exception)
        {
            while (request is not null)
            {
                PipelineCreationRequest current = request;
                request = current.QueueNext;
                current.QueueNext = null;
                current.ReleaseCapturedState();
                RecordFailure(exception);
                current.Fail(exception);
            }
        }

        private void RecordFailure(Exception exception)
        {
            if (exception is GraphicsException { Error: GraphicsError.DeviceLost })
                Interlocked.Increment(ref _deviceLost);
            else
                Interlocked.Increment(ref _failed);
        }

        private void RecordQueueWait(long ticks)
        {
            Interlocked.Add(ref _totalQueueWaitTicks, ticks);
            UpdateMaximum(ref _maximumQueueWaitTicks, ticks);
        }

        private void IncrementTypeCount(PipelineType type)
        {
            switch (type)
            {
                case PipelineType.Graphics:
                    Interlocked.Increment(ref _graphics);
                    break;
                case PipelineType.Compute:
                    Interlocked.Increment(ref _compute);
                    break;
                case PipelineType.Mesh:
                    Interlocked.Increment(ref _mesh);
                    break;
                case PipelineType.RayTracing:
                    Interlocked.Increment(ref _rayTracing);
                    break;
                case PipelineType.WorkGraph:
                    Interlocked.Increment(ref _workGraph);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        private Exception CreateTerminalException() => _terminalException switch
        {
            GraphicsException graphics => graphics,
            ObjectDisposedException disposed => disposed,
            Exception exception => new InvalidOperationException(
                "The Direct3D 12 Pipeline creation queue is unavailable.",
                exception),
            _ => new ObjectDisposedException(nameof(D3D12PipelineCompiler)),
        };

        private void JoinWorkers(int count)
        {
            for (int index = 0; index < count; index++)
            {
                Thread? worker = _workers[index];
                if (worker is not null && !ReferenceEquals(worker, Thread.CurrentThread))
                    worker.Join();
            }
        }

        private static void UpdateMaximum(ref int location, int value)
        {
            int current = Volatile.Read(ref location);
            while (value > current)
            {
                int observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        private static void UpdateMaximum(ref long location, long value)
        {
            long current = Volatile.Read(ref location);
            while (value > current)
            {
                long observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }

    private sealed partial class D3D12Device
    {
        private readonly D3D12PipelineCompiler _pipelineCompiler;

        internal D3D12PipelineCompiler PipelineCompiler => _pipelineCompiler;
    }
}
