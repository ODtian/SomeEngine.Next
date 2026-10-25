using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Job;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Physics;

/// <summary>Resident entity collision inputs, hash-grid work and direct device type bins.</summary>
public sealed partial class DeviceCollisionGrid : IDisposable
{
    public const int InputStride = 80;
    public const int PairStride = 32;
    private const int ScanWidth = 256;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly CommandContext _commands;
    private readonly List<IDisposable> _objects = [];
    private readonly List<Buffer> _gpuBuffers = [];
    private readonly Pipeline[] _pipelines;
    private readonly VariableLayoutReflection[] _layouts;
    private readonly ResourceBinding[][] _bindings;
    private readonly JobResourceAccess[] _extraAccesses;
    private readonly Buffer _parameters;
    private readonly Buffer _readback;
    private readonly Buffer _binOffsets;
    private readonly DeviceCollisionPair[] _resultPairs;
    private readonly int[] _resultOffsets;
    private MappedBuffer _inputMapping;
    private uint _uploadRevision;
    private uint _deviceRevision;
    private bool _buffersInitialized;
    private bool _inputInitialized;

    public DeviceCollisionGrid(IGraphicsBackend backend, Device device, in DeviceGridKernels kernels,
        DeviceGridSettings settings, DeviceGridCapacity capacity,
        IReadOnlyDictionary<string, ResourceBinding[]>? extraBindings = null,
        ReadOnlyMemory<JobResourceAccess> extraAccesses = default)
    {
        _backend = backend;
        _device = device;
        Settings = settings;
        Capacity = capacity;
        _extraAccesses = extraAccesses.ToArray();
        var bindings = new Dictionary<string, ResourceBinding[]>(StringComparer.Ordinal);
        Bodies = CreateGpu("CollisionBodies", (ulong)capacity.Bodies * InputStride, InputStride, bindings);
        CreateGpu("GridHeads", (ulong)capacity.Buckets * 4, 4, bindings);
        CreateGpu("GridCells", (ulong)capacity.Cells * 32, 32, bindings);
        CreateGpu("GridCounters", 8, 4, bindings);
        CreateGpu("BodyLevels", (ulong)capacity.Bodies * 4, 4, bindings);
        CreateGpu("PairCounts", (ulong)capacity.Bodies * (ulong)capacity.Bins * 4, 4, bindings);
        CreateGpu("PairOffsets", (ulong)capacity.Bodies * (ulong)capacity.Bins * 4, 4, bindings);
        CreateGpu("BlockTotals", (ulong)BlockCount * (ulong)capacity.Bins * 4, 4, bindings);
        CreateGpu("BlockOffsets", (ulong)BlockCount * (ulong)capacity.Bins * 4, 4, bindings);
        _binOffsets = CreateGpu("BinOffsets", (ulong)(capacity.Bins + 1) * 4, 4, bindings);
        CreateGpu("PairWritePositions", (ulong)capacity.Bodies * (ulong)capacity.Bins * 4, 4, bindings);
        Pairs = CreateGpu("CollisionPairs", (ulong)capacity.Pairs * PairStride, PairStride, bindings);
        Upload = Keep(backend.CreateBuffer(device, new((ulong)capacity.Bodies * InputStride,
            BufferUsages.CopySource, "Device collision upload"), MemoryType.Upload));
        _inputMapping = backend.Map(Upload, MapType.Write, BufferRange.Whole);
        _inputMapping.Bytes.Clear();
        _parameters = Keep(backend.CreateBuffer(device, new(256, BufferUsages.Constant,
            "Device grid parameters"), MemoryType.Upload));
        BufferCbv parametersView = Keep(backend.CreateBufferCbv(device, new(_parameters, new(0, 256))));
        bindings.Add("GridParameters", [ResourceBinding.ConstantBuffer(parametersView)]);
        if (extraBindings is not null)
            foreach (var binding in extraBindings)
                bindings.Add(binding.Key, binding.Value);
        _readback = Keep(backend.CreateBuffer(device, new((ulong)capacity.Pairs * PairStride +
            (ulong)(capacity.Bins + 1) * 4, BufferUsages.CopyDestination, "Device collision readback"), MemoryType.Readback));
        _resultPairs = new DeviceCollisionPair[capacity.Pairs];
        _resultOffsets = new int[capacity.Bins + 1];

        ComputePipelineDesc[] descriptions = [kernels.Clear, kernels.Build, kernels.Count, kernels.Scan,
            kernels.OffsetBlocks, kernels.OffsetBins, kernels.Write];
        _pipelines = new Pipeline[descriptions.Length];
        _layouts = new VariableLayoutReflection[descriptions.Length];
        _bindings = new ResourceBinding[descriptions.Length][];
        for (int index = 0; index < descriptions.Length; index++)
        {
            _pipelines[index] = Keep(backend.CreateComputePipeline(device, descriptions[index]));
            _layouts[index] = descriptions[index].Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            _bindings[index] = OrderBindings(_layouts[index], bindings);
        }
        _commands = Keep(backend.CreateCommandContext(device,
            new CommandContextDesc(QueueType.Compute, 0, 1, Label: "Entity collision grid")));
    }

    public DeviceGridSettings Settings { get; }
    public DeviceGridCapacity Capacity { get; }
    public Buffer Upload { get; }
    public Buffer Bodies { get; }
    public Buffer Pairs { get; }
    public Buffer BinOffsets => _binOffsets;
    public QueueCompletion Completion { get; private set; }
    public long Number { get; private set; }
    public double Time { get; private set; }
    public int Count => _resultOffsets[^1];
    private int BlockCount => (Capacity.Bodies + ScanWidth - 1) / ScanWidth;

    /// <summary>Reads the last Pairs readback after its returned physics completion.</summary>
    public ReadOnlySpan<DeviceCollisionPair> ReadBin(int bin) =>
        _resultPairs.AsSpan(_resultOffsets[bin], _resultOffsets[bin + 1] - _resultOffsets[bin]);

    public JobResourceAccess[] GetPairReadbackAccesses(Scheduler scheduler) =>
        [JobResourceAccess.Read(scheduler, _resultPairs, 0, _resultPairs.Length),
            JobResourceAccess.Read(scheduler, _resultOffsets, 0, _resultOffsets.Length)];

    internal void BeginInputWrite() => _uploadRevision++;

    internal void WriteInput(int index, in DeviceCollisionInput input)
    {
        DeviceCollisionInput value = input;
        value.Revision = _uploadRevision;
        MemoryMarshal.Write(_inputMapping.Bytes.Slice(index * InputStride, InputStride), in value);
    }

    private T Keep<T>(T value) where T : IDisposable
    {
        _objects.Add(value);
        return value;
    }

    private Buffer CreateGpu(string name, ulong bytes, uint stride, Dictionary<string, ResourceBinding[]> bindings)
    {
        Buffer buffer = Keep(_backend.CreateBuffer(_device, new(bytes,
            BufferUsages.ShaderRead | BufferUsages.ShaderWrite | BufferUsages.CopySource | BufferUsages.CopyDestination,
            name), MemoryType.DeviceLocal));
        BufferUav view = Keep(_backend.CreateBufferUav(_device, new(buffer, new(0, bytes), StructureStride: stride)));
        bindings.Add(name, [ResourceBinding.WritableBuffer(view)]);
        _gpuBuffers.Add(buffer);
        return buffer;
    }

    internal static ResourceBinding[] OrderBindings(VariableLayoutReflection root,
        Dictionary<string, ResourceBinding[]> bindings)
    {
        TypeLayoutReflection layout = root.TypeLayout.UnwrapArray();
        if (layout.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            layout = layout.ElementTypeLayout.UnwrapArray();
        var values = new List<ResourceBinding>();
        for (nint range = 0; range < layout.BindingRangeCount; range++)
        {
            nint ranges = layout.GetBindingRangeDescriptorRangeCount(range);
            if (ranges == 0) continue;
            string name = layout.GetBindingRangeLeafVariable(range).Name;
            nint set = layout.GetBindingRangeDescriptorSetIndex(range);
            nint first = layout.GetBindingRangeFirstDescriptorRangeIndex(range);
            int position = 0;
            for (nint relative = 0; relative < ranges; relative++)
            {
                nint count = layout.GetDescriptorSetDescriptorRangeDescriptorCount(set, first + relative);
                for (nint element = 0; element < count; element++)
                    values.Add(bindings[name][position++]);
            }
        }
        return [.. values];
    }

    public void Dispose()
    {
        _inputMapping.Dispose();
        for (int index = _objects.Count - 1; index >= 0; index--)
            _objects[index].Dispose();
    }
}
