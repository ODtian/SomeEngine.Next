using System.Diagnostics;

namespace SomeEngine.RenderGraph;

public sealed partial class RenderGraph
{
    private static long s_nextGraphSerial;
    private readonly int _coordinatorThread;
    private readonly JobPriority _commandPriority;
    private bool _disposed;

    public RenderGraph(IGraphicsBackend backend, Device device)
        : this(backend, device, JobPriority.High)
    {
    }

    public RenderGraph(
        IGraphicsBackend backend,
        Device device,
        JobPriority commandPriority)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (!Enum.IsDefined(commandPriority))
            throw new ArgumentOutOfRangeException(nameof(commandPriority));
        _commandPriority = commandPriority;
        GraphSerial = CreateGraphSerial();
        _buffers = new ArenaColumn<ResourceUnversionedData>(_arena, 128);
        _textures = new ArenaColumn<ResourceUnversionedData>(_arena, 64);
        _passes = new ArenaColumn<PassData>(_arena, 64);
        _bufferViewResources = new ArenaColumn<int>(_arena, 256);
        _bufferViewRanges = new ArenaColumn<BufferRange>(_arena, 256);
        _bufferViewTypes = new ArenaColumn<GraphBindingType>(_arena, 256);
        _bufferViewFormats = new ArenaColumn<Format>(_arena, 256);
        _bufferViewStrides = new ArenaColumn<uint>(_arena, 256);
        _textureViewResources = new ArenaColumn<int>(_arena, 128);
        _textureViewRanges = new ArenaColumn<TextureSubresourceRange>(_arena, 128);
        _textureViewUsages = new ArenaColumn<GraphTextureViewUsage>(_arena, 128);
        _textureViewFormats = new ArenaColumn<Format>(_arena, 128);
        _textureViewDimensions = new ArenaColumn<TextureViewDimension>(_arena, 128);
        _accelerationStructureBuffers = new ArenaColumn<int>(_arena);
        _accelerationStructureRanges = new ArenaColumn<BufferRange>(_arena);
        _accelerationStructureTypes = new ArenaColumn<AccelerationStructureType>(_arena);
        _accesses = new ArenaColumn<PassInputData>(_arena, 512);
        _accessPredecessors = new ArenaColumn<int>(_arena, 512);
        _bufferAccessHeads = new ArenaColumn<PassAccessHead>(_arena, 128);
        _textureAccessHeads = new ArenaColumn<PassAccessHead>(_arena, 64);
        _colorAttachments = new ArenaColumn<PassFragmentData>(_arena);
        _depthStencilAttachments = new ArenaColumn<PassFragmentData>(_arena);
        _shaderArgumentGroups = new ArenaColumn<uint>(_arena, 384);
        _shaderArgumentBindings = new ArenaColumn<uint>(_arena, 384);
        _shaderArgumentElements = new ArenaColumn<uint>(_arena, 384);
        _shaderArgumentTypes = new ArenaColumn<GraphBindingType>(_arena, 384);
        _shaderArgumentAccesses = new ArenaColumn<int>(_arena, 384);
        _shaderArgumentViews = new ArenaColumn<int>(_arena, 384);
        _shaderArgumentSamplers = new ArenaColumn<int>(_arena, 384);
        _bindlessAccessTables = new ArenaColumn<int>(_arena);
        _bindlessAccessTypes = new ArenaColumn<GraphBindingType>(_arena);
        _bindlessAccesses = new ArenaColumn<int>(_arena);
        _bindlessAccessViews = new ArenaColumn<int>(_arena);
        _passQueries = new ArenaColumn<int>(_arena);
        CommandBatches = new ArenaColumn<CommandBatch>(_arena);
        CommandUnits = new ArenaColumn<RuntimeCmd>(_arena);
        BatchDependencyRows = new ArenaColumn<int>(_arena);
        BatchRuntimeCmds = new ArenaColumn<int>(_arena);
        BatchResourceRows = new ArenaColumn<int>(_arena);
        BatchExternalWaitRows = new ReferenceColumn<QueueCompletion>(0);
        CommandUnitDependencyRows = new ArenaColumn<int>(_arena);
        CommandUnitPassRows = new ArenaColumn<int>(_arena);
        CommandUnitAliasRows = new ArenaColumn<PlannedAliasingBarrier>(_arena);
        CommandUnitResourceBarriers = new ArenaColumn<PlannedBarrier>(_arena);
        DependencyRows = new ArenaColumn<int>(_arena);
        BeforeResourceBarriers = new ArenaColumn<PlannedBarrier>(_arena);
        AfterResourceBarriers = new ArenaColumn<PlannedBarrier>(_arena);
        _coordinatorThread = Environment.CurrentManagedThreadId;
    }

    private static long CreateGraphSerial()
    {
        long value = Interlocked.Increment(ref s_nextGraphSerial);
        if (value == 0)
            value = Interlocked.Increment(ref s_nextGraphSerial);
        return value;
    }

    public QueueCompletion[] Execute() =>
        ExecuteCore(
            collectTimings: false,
            collectDetailedTimings: false,
            out _);

    internal QueueCompletion[] ExecuteForSnapshot(out InvocationCpuTimings timings) =>
        ExecuteCore(
            collectTimings: true,
            collectDetailedTimings: false,
            out timings);

    internal QueueCompletion[] ExecuteForBenchmark(out InvocationCpuTimings timings) =>
        ExecuteCore(
            collectTimings: true,
            collectDetailedTimings: false,
            out timings);

    private QueueCompletion[] ExecuteCore(
        bool collectTimings,
        bool collectDetailedTimings,
        out InvocationCpuTimings timings)
    {
        EnsureCoordinator();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_consumed) throw new InvalidOperationException("A RenderGraph invocation can execute only once.");
        timings = default;
        long started = collectTimings ? Stopwatch.GetTimestamp() : 0;

        try
        {
            try
            {
                Close();
            }
            catch
            {
                throw;
            }
            long closed = collectTimings ? Stopwatch.GetTimestamp() : 0;

            CompilerCpuTimings compiler;
            try
            {
                RenderGraphCompiler.Compile(
                    this,
                    _backend,
                    _device,
                    collectTimings,
                    timings: out compiler);
            }
            catch
            {
                throw;
            }

            QueueCompletion[] position;
            ResourceAcquisitionCpuTimings acquisition;
            CommandSubmissionCpuTimings commands;
            try
            {
                AcquireResourcesAndViews(collectTimings, out acquisition);
                position = EncodeAndSubmit(
                    collectTimings,
                    collectDetailedTimings,
                    out commands);
            }
            catch (RenderGraphExecutionException) { throw; }
            catch { throw; }

            timings = collectTimings
                ? new InvocationCpuTimings(
                    Stopwatch.GetElapsedTime(started, closed),
                    compiler,
                    acquisition,
                    commands)
                : default;
            return position;
        }
        catch
        {
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        EnsureCoordinator();
        ReturnExecutionStorage();
        DisposeReferenceColumns();
        _arena.Dispose();
        _disposed = true;
    }

    private void EnsureCoordinator()
    {
        if (Environment.CurrentManagedThreadId != _coordinatorThread)
            throw new InvalidOperationException("RenderGraph authoring, publication, realization, and submission have one coordinator thread owner.");
    }
}
