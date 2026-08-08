using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ArgumentNullException.ThrowIfNull(desc.Program);
        ArgumentException.ThrowIfNullOrWhiteSpace(desc.ProgramName);
        if (desc.MaximumInputRecordCount == 0 ||
            desc.NodeMask == 0 || (desc.NodeMask & ~nativeDevice.EnabledNodeMask) != 0 ||
            !Enum.IsDefined(desc.Options))
        {
            throw new ArgumentOutOfRangeException(nameof(desc));
        }
        if (desc.EntryPoints.IsEmpty &&
            (desc.Options & WorkGraphPipelineOptions.IncludeAllAvailableNodes) == 0)
        {
            throw new ArgumentException(
                "A Work Graph must declare entry points or include all available nodes.",
                nameof(desc));
        }

        ShaderReflection reflection = GetProgramReflection(desc.Program);
        CompiledProgramLibrary library = CompileProgramLibrary(desc.Program);
        WorkGraphEntryPointState[] entryPoints = new WorkGraphEntryPointState[desc.EntryPoints.Length];
        HashSet<(string Name, uint ArrayIndex)> nodeIdentities = [];
        Dictionary<EntryPointReflection, string> shaderNames = [];
        for (int index = 0; index < entryPoints.Length; index++)
        {
            ref readonly WorkGraphEntryPointLayout entry = ref desc.EntryPoints[index];
            string name = ValidateStateObjectEntryPoint(
                reflection,
                entry.EntryPoint,
                [SlangStage.Dispatch],
                "Work Graph");
            if (!nodeIdentities.Add((name, entry.NodeIndex)) ||
                entry.MaximumInputRecordCount == 0)
            {
                throw new ArgumentException("A Work Graph entry-point layout is invalid.", nameof(desc));
            }
            shaderNames[entry.EntryPoint] = name;
            entryPoints[index] = new WorkGraphEntryPointState(
                entry.EntryPoint,
                name,
                entry.NodeIndex,
                entry.MaximumInputRecordCount,
                0,
                0,
                0);
        }

        WorkGraphNodeOverrideState[] overrides = new WorkGraphNodeOverrideState[desc.NodeOverrides.Length];
        HashSet<EntryPointReflection> overridden = [];
        for (int index = 0; index < overrides.Length; index++)
        {
            ref readonly WorkGraphNodeOverride value = ref desc.NodeOverrides[index];
            string name = ValidateStateObjectEntryPoint(
                reflection,
                value.EntryPoint,
                [SlangStage.Dispatch],
                "Work Graph node override");
            if (!overridden.Add(value.EntryPoint) ||
                value.MaximumDispatchGridX == 0 ||
                value.MaximumDispatchGridY == 0 ||
                value.MaximumDispatchGridZ == 0 ||
                value.MaximumInputRecordCount == 0)
            {
                throw new ArgumentException("A Work Graph node override is invalid.", nameof(desc));
            }
            shaderNames[value.EntryPoint] = name;
            overrides[index] = new WorkGraphNodeOverrideState(
                value.EntryPoint,
                name,
                value.MaximumDispatchGridX,
                value.MaximumDispatchGridY,
                value.MaximumDispatchGridZ,
                value.MaximumInputRecordCount);
        }

        D3D12RootLayout global = D3D12RootLayoutBuilder.CompileGlobal(
            this,
            nativeDevice,
            desc.Program,
            reflection,
            PipelineType.WorkGraph);
        ID3D12StateObject* stateObject = null;
        ID3D12StateObjectProperties1* stateProperties = null;
        ID3D12WorkGraphProperties* graphProperties = null;
        D3D12WorkGraphPipeline? result = null;
        try
        {
            using NativeStateObjectArena arena = new();
            StateSubobject* subobjects = arena.Allocate<StateSubobject>(4);

            ExportDesc* exports = arena.Allocate<ExportDesc>(Math.Max(1, shaderNames.Count));
            int exportIndex = 0;
            foreach (string name in shaderNames.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                exports[exportIndex++] = new ExportDesc(arena.String(name), null, ExportFlags.None);
            DxilLibraryDesc* libraryDescription = arena.Allocate<DxilLibraryDesc>();
            fixed (byte* code = library.Code)
            {
                *libraryDescription = new DxilLibraryDesc(
                    new ShaderBytecode(code, (nuint)library.Code.Length),
                    checked((uint)exportIndex),
                    exportIndex == 0 ? null : exports);
                subobjects[0] = new StateSubobject(
                    StateSubobjectType.DxilLibrary,
                    libraryDescription);

                GlobalRootSignature* globalDescription = arena.Allocate<GlobalRootSignature>();
                globalDescription->PGlobalRootSignature = global.Native;
                subobjects[1] = new StateSubobject(
                    StateSubobjectType.GlobalRootSignature,
                    globalDescription);

                NodeID* nativeEntries = entryPoints.Length == 0
                    ? null
                    : arena.Allocate<NodeID>(entryPoints.Length);
                for (int index = 0; index < entryPoints.Length; index++)
                {
                    nativeEntries[index] = new NodeID(
                        arena.String(entryPoints[index].Name),
                        entryPoints[index].NodeIndex);
                }

                Node* nativeOverrides = overrides.Length == 0
                    ? null
                    : arena.Allocate<Node>(overrides.Length);
                for (int index = 0; index < overrides.Length; index++)
                {
                    WorkGraphNodeOverrideState value = overrides[index];
                    uint* maximumGrid = arena.Allocate<uint>(3);
                    maximumGrid[0] = value.MaximumDispatchGridX;
                    maximumGrid[1] = value.MaximumDispatchGridY;
                    maximumGrid[2] = value.MaximumDispatchGridZ;
                    BroadcastingLaunchOverrides* nativeOverride =
                        arena.Allocate<BroadcastingLaunchOverrides>();
                    nativeOverride->PMaxDispatchGrid = maximumGrid;
                    ShaderNode shader = new()
                    {
                        Shader = arena.String(value.Name),
                        OverridesType = NodeOverridesType.BroadcastingLaunch,
                    };
                    shader.PBroadcastingLaunchOverrides = nativeOverride;
                    Node node = new() { NodeType = NodeType.Shader };
                    node.Shader = shader;
                    nativeOverrides[index] = node;
                }

                WorkGraphDesc* graph = arena.Allocate<WorkGraphDesc>();
                graph->ProgramName = arena.String(desc.ProgramName);
                graph->Flags = (desc.Options & WorkGraphPipelineOptions.IncludeAllAvailableNodes) != 0
                    ? WorkGraphFlags.IncludeAllAvailableNodes
                    : WorkGraphFlags.None;
                graph->NumEntrypoints = checked((uint)entryPoints.Length);
                graph->PEntrypoints = nativeEntries;
                graph->NumExplicitlyDefinedNodes = checked((uint)overrides.Length);
                graph->PExplicitlyDefinedNodes = nativeOverrides;
                subobjects[2] = new StateSubobject(StateSubobjectType.WorkGraph, graph);

                uint* nodeMask = arena.Allocate<uint>();
                *nodeMask = desc.NodeMask;
                subobjects[3] = new StateSubobject(StateSubobjectType.NodeMask, nodeMask);

                StateObjectDesc native = new(StateObjectType.Executable, 4, subobjects);
                Guid iid = ID3D12StateObject.Guid;
                int createResult = nativeDevice.Native->CreateStateObject(
                    &native,
                    &iid,
                    (void**)&stateObject);
                ThrowIfDeviceFailed(
                    nativeDevice,
                    createResult,
                    "ID3D12Device5::CreateStateObject(Work Graph)");
            }

            Guid statePropertiesIid = ID3D12StateObjectProperties1.Guid;
            int propertiesResult = stateObject->QueryInterface(
                &statePropertiesIid,
                (void**)&stateProperties);
            ThrowIfDeviceFailed(
                nativeDevice,
                propertiesResult,
                "ID3D12StateObject::QueryInterface(ID3D12StateObjectProperties1)");
            Guid graphPropertiesIid = ID3D12WorkGraphProperties.Guid;
            int graphPropertiesResult = stateObject->QueryInterface(
                &graphPropertiesIid,
                (void**)&graphProperties);
            ThrowIfDeviceFailed(
                nativeDevice,
                graphPropertiesResult,
                "ID3D12StateObject::QueryInterface(ID3D12WorkGraphProperties)");

            ProgramIdentifier identifier = stateProperties->GetProgramIdentifier(desc.ProgramName);
            if (IsZero(identifier))
                throw new GraphicsException(GraphicsError.PipelineCreation, "The Work Graph program identifier is empty.");
            uint graphIndex = graphProperties->GetWorkGraphIndex(desc.ProgramName);
            if (graphIndex == uint.MaxValue)
                throw new GraphicsException(GraphicsError.PipelineCreation, "The Work Graph program was not found in its state object.");

            for (int index = 0; index < entryPoints.Length; index++)
            {
                WorkGraphEntryPointState entry = entryPoints[index];
                NodeID id = new()
                {
                    Name = arena.String(entry.Name),
                    ArrayIndex = entry.NodeIndex,
                };
                uint nativeIndex = graphProperties->GetEntrypointIndex(graphIndex, id);
                if (nativeIndex == uint.MaxValue)
                    throw new GraphicsException(GraphicsError.PipelineCreation, $"Work Graph entry '{entry.Name}' was not materialized.");
                uint size = graphProperties->GetEntrypointRecordSizeInBytes(graphIndex, nativeIndex);
                uint alignment = graphProperties->GetEntrypointRecordAlignmentInBytes(graphIndex, nativeIndex);
                bool invalidAlignment = size == 0
                    ? alignment != 0
                    : alignment < 4 || (alignment & (alignment - 1)) != 0;
                if (size == uint.MaxValue || alignment == uint.MaxValue || invalidAlignment)
                {
                    throw new GraphicsException(GraphicsError.PipelineCreation, "D3D12 returned invalid Work Graph entry-point layout data.");
                }
                entryPoints[index] = entry with
                {
                    NativeIndex = nativeIndex,
                    RecordSize = size,
                    RecordAlignment = alignment,
                };
            }

            Silk.NET.Direct3D12.WorkGraphMemoryRequirements nativeRequirements = default;
            graphProperties->GetWorkGraphMemoryRequirements(graphIndex, &nativeRequirements);
            WorkGraphMemoryRequirements requirements = new(
                nativeRequirements.MinSizeInBytes,
                nativeRequirements.MaxSizeInBytes,
                nativeRequirements.SizeGranularityInBytes);
            ValidateWorkGraphRequirements(requirements);

            byte[] key = CreateWorkGraphPipelineKey(
                nativeDevice,
                global,
                library,
                entryPoints,
                overrides,
                desc);
            nativeCache?.Store(5, key, library.Hash);
            result = new D3D12WorkGraphPipeline(
                nativeDevice,
                stateObject,
                stateProperties,
                graphProperties,
                global,
                identifier,
                requirements,
                entryPoints,
                desc.MaximumInputRecordCount,
                ToPipelineSignature(key),
                desc.Label);
            stateObject = null;
            stateProperties = null;
            graphProperties = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
            {
                if (graphProperties is not null)
                    _ = graphProperties->Release();
                if (stateProperties is not null)
                    _ = stateProperties->Release();
                if (stateObject is not null)
                    _ = stateObject->Release();
                global.Release();
            }
            else
            {
                result.Dispose();
            }
            throw;
        }
    }

    public WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline)
    {
        D3D12WorkGraphPipeline native = NativeCast.WorkGraphPipeline(pipeline);
        return native.MemoryRequirements;
    }

    public void SetWorkGraphProgram(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion backingMemory,
        WorkGraphInitialization initialization,
        uint maximumInputRecordCount)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12WorkGraphPipeline nativePipeline = NativeCast.WorkGraphPipeline(pipeline);

        D3D12Buffer backing = NativeCast.Buffer(backingMemory.Buffer);
        BufferRange range = backingMemory.Range.Resolve(backing.Info.Size);
        WorkGraphMemoryRequirements requirements = nativePipeline.MemoryRequirements;
        if ((backing.Info.Usages & BufferUsages.ShaderWrite) == 0 ||
            range.Offset % 8 != 0 ||
            range.Size < requirements.MinimumSize ||
            (requirements.MaximumSize != 0 && range.Size > requirements.MaximumSize) ||
            (range.Size > requirements.MinimumSize && requirements.Granularity != 0 &&
             (range.Size - requirements.MinimumSize) % requirements.Granularity != 0))
        {
            throw new ArgumentException(
                "Work Graph backing memory does not satisfy its native size, alignment, or ShaderWrite requirements.",
                nameof(backingMemory));
        }

        D3D12WorkGraphProgramState next = new(
            nativePipeline,
            backing,
            range,
            maximumInputRecordCount);
        if (initialization == WorkGraphInitialization.Preserve && command.WorkGraphProgramEquals(next))
            return;

        SetProgramDesc native = new() { Type = ProgramType.WorkGraph };
        native.WorkGraph = new SetWorkGraphDesc
        {
            ProgramIdentifier = nativePipeline.ProgramIdentifier,
            Flags = ToSetWorkGraphFlags(initialization),
            BackingMemory = new GpuVirtualAddressRange(
                backing.Native->GetGPUVirtualAddress() + range.Offset,
                range.Size),
            NodeLocalRootArgumentsTable = default,
        };
        command.List->SetComputeRootSignature(nativePipeline.RootLayout.Native);
        command.List->SetProgram(&native);
        command.RememberPipeline(nativePipeline);
        command.RememberWorkGraphProgram(next);
        command.Capture(backing);
        command.CapturePipelineArtifact(nativePipeline);
        foreach (DefaultRootTable table in nativePipeline.RootLayout.DefaultTables)
            command.SetRootTable(table.RootParameterIndex, table.Heap, 0);
    }

    public void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12WorkGraphProgramState program = command.RequireWorkGraphProgram();
        D3D12WorkGraphPipeline pipeline = program.Pipeline;
        WorkGraphEntryPointState entry = pipeline.GetEntryPoint(desc.EntryPointIndex);
        if (desc.RecordCount == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        ValidateWorkGraphRecordLayout(entry, desc.RecordCount, desc.RecordStride, out ulong requiredBytes);

        DispatchGraphDesc native = default;
        if (desc.UsesGpuRecords)
        {
            D3D12Buffer records = NativeCast.Buffer(desc.GpuRecords.Buffer);
            BufferRange range = desc.GpuRecords.Range.Resolve(records.Info.Size);
            ulong address = records.Native->GetGPUVirtualAddress() + range.Offset;
            if ((records.Info.Usages & BufferUsages.ShaderRead) == 0 ||
                range.Size < requiredBytes ||
                (entry.RecordSize != 0 && address % entry.RecordAlignment != 0))
            {
                throw new ArgumentException("The GPU Work Graph input range is invalid.", nameof(desc));
            }

            D3D12OrdinaryDataReservation header =
                command.ReserveTransientOrdinaryData((ulong)sizeof(NodeGpuInput));
            command.Capture(records);
            NodeGpuInput input = new()
            {
                EntrypointIndex = entry.NativeIndex,
                NumRecords = desc.RecordCount,
                Records = new GpuVirtualAddressAndStride(address, desc.RecordStride),
            };
            header.Commit(new ReadOnlySpan<byte>(&input, sizeof(NodeGpuInput)));
            native.Mode = DispatchMode.NodeGpuInput;
            native.NodeGPUInput = header.Address;
        }
        else
        {
            if ((ulong)desc.Records.Length < requiredBytes)
                throw new ArgumentException("The CPU Work Graph input span is too small.", nameof(desc));
            fixed (byte* records = desc.Records)
            {
                if (entry.RecordSize != 0 && (nuint)records % entry.RecordAlignment != 0)
                    throw new ArgumentException("The CPU Work Graph input span is not naturally aligned.", nameof(desc));
                native.Mode = DispatchMode.NodeCpuInput;
                native.NodeCPUInput = new NodeCpuInput(
                    entry.NativeIndex,
                    desc.RecordCount,
                    entry.RecordSize == 0 ? null : records,
                    desc.RecordStride);
                command.List->DispatchGraph(&native);
            }
            return;
        }

        command.List->DispatchGraph(&native);
    }

    private static SetWorkGraphFlags ToSetWorkGraphFlags(
        WorkGraphInitialization value) => value switch
    {
        WorkGraphInitialization.Initialize => SetWorkGraphFlags.Initialize,
        WorkGraphInitialization.Preserve => SetWorkGraphFlags.None,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static void ValidateWorkGraphRecordLayout(
        in WorkGraphEntryPointState entry,
        uint recordCount,
        uint stride,
        out ulong requiredBytes)
    {
        if (entry.RecordSize == 0)
        {
            if (stride != 0)
                throw new ArgumentOutOfRangeException(nameof(stride));
            requiredBytes = 0;
            return;
        }
        if (stride != 0 &&
            (stride < entry.RecordSize || stride % entry.RecordAlignment != 0 || stride % 4 != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }
        requiredBytes = stride == 0
            ? entry.RecordSize
            : checked(checked((ulong)(recordCount - 1) * stride) + entry.RecordSize);
    }

    private static void ValidateWorkGraphRequirements(in WorkGraphMemoryRequirements value)
    {
        if (value.MaximumSize < value.MinimumSize)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"D3D12 returned invalid Work Graph backing-memory requirements " +
                $"(minimum {value.MinimumSize}, maximum {value.MaximumSize}, " +
                $"granularity {value.Granularity}).");
        }
    }

    private static bool IsZero(in ProgramIdentifier identifier)
    {
        ProgramIdentifier copy = identifier;
        return new ReadOnlySpan<byte>(&copy, sizeof(ProgramIdentifier))
            .IndexOfAnyExcept((byte)0) < 0;
    }

    private static byte[] CreateWorkGraphPipelineKey(
        D3D12Device device,
        D3D12RootLayout global,
        CompiledProgramLibrary library,
        ReadOnlySpan<WorkGraphEntryPointState> entries,
        ReadOnlySpan<WorkGraphNodeOverrideState> overrides,
        in WorkGraphPipelineDesc desc)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(RootLayoutSchemaVersion);
            writer.Write((byte)5);
            writer.Write(device.EnabledNodeMask);
            writer.Write(desc.NodeMask);
            writer.Write(library.Hash);
            writer.Write(global.Serialized.Length);
            writer.Write(global.Serialized);
            writer.Write(desc.ProgramName);
            writer.Write((byte)desc.Options);
            writer.Write(desc.MaximumInputRecordCount);
            writer.Write(entries.Length);
            foreach (ref readonly WorkGraphEntryPointState entry in entries)
            {
                writer.Write(entry.Name);
                writer.Write(entry.NodeIndex);
                writer.Write(entry.MaximumInputRecordCount);
            }
            writer.Write(overrides.Length);
            foreach (ref readonly WorkGraphNodeOverrideState value in overrides)
            {
                writer.Write(value.Name);
                writer.Write(value.MaximumDispatchGridX);
                writer.Write(value.MaximumDispatchGridY);
                writer.Write(value.MaximumDispatchGridZ);
                writer.Write(value.MaximumInputRecordCount);
            }
        }
        return System.Security.Cryptography.SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private readonly record struct WorkGraphEntryPointState(
        EntryPointReflection EntryPoint,
        string Name,
        uint NodeIndex,
        uint MaximumInputRecordCount,
        uint NativeIndex,
        uint RecordSize,
        uint RecordAlignment);

    private readonly record struct WorkGraphNodeOverrideState(
        EntryPointReflection EntryPoint,
        string Name,
        uint MaximumDispatchGridX,
        uint MaximumDispatchGridY,
        uint MaximumDispatchGridZ,
        uint MaximumInputRecordCount);

    private sealed class D3D12WorkGraphPipeline : D3D12Pipeline
    {
        private readonly NativeLease _stateProperties;
        private readonly NativeLease _graphProperties;
        private readonly WorkGraphEntryPointState[] _entryPoints;

        internal D3D12WorkGraphPipeline(
            D3D12Device device,
            ID3D12StateObject* native,
            ID3D12StateObjectProperties1* stateProperties,
            ID3D12WorkGraphProperties* graphProperties,
            D3D12RootLayout global,
            in ProgramIdentifier identifier,
            in WorkGraphMemoryRequirements memoryRequirements,
            WorkGraphEntryPointState[] entryPoints,
            uint maximumInputRecordCount,
            in PipelineSignature signature,
            string? label)
            : base(
                device,
                (IUnknown*)native,
                global,
                ReadOnlySpan<D3D12RootLayout>.Empty,
                PipelineType.WorkGraph,
                signature,
                label)
        {
            _stateProperties = new NativeLease((IUnknown*)stateProperties, ownsReference: true);
            _graphProperties = new NativeLease((IUnknown*)graphProperties, ownsReference: true);
            _entryPoints = entryPoints;
            ProgramIdentifier = identifier;
            MemoryRequirements = memoryRequirements;
            MaximumInputRecordCount = maximumInputRecordCount;
        }

        internal ProgramIdentifier ProgramIdentifier { get; }
        internal WorkGraphMemoryRequirements MemoryRequirements { get; }
        internal uint MaximumInputRecordCount { get; }

        internal WorkGraphEntryPointState GetEntryPoint(uint index) =>
            index < (uint)_entryPoints.Length
                ? _entryPoints[index]
                : throw new ArgumentOutOfRangeException(nameof(index));

        protected override void ReleaseAdditional()
        {
            _graphProperties.Release();
            _stateProperties.Release();
        }
    }

    private readonly record struct D3D12WorkGraphProgramState(
        D3D12WorkGraphPipeline Pipeline,
        D3D12Buffer Backing,
        BufferRange Range,
        uint MaximumInputRecordCount);

    private sealed partial class D3D12CommandContext
    {
        private D3D12WorkGraphProgramState? _workGraphProgram;

        internal bool WorkGraphProgramEquals(in D3D12WorkGraphProgramState value) =>
            _workGraphProgram is D3D12WorkGraphProgramState current && current == value;

        internal void RememberWorkGraphProgram(in D3D12WorkGraphProgramState value) =>
            _workGraphProgram = value;

        internal D3D12WorkGraphProgramState RequireWorkGraphProgram() =>
            _workGraphProgram.GetValueOrDefault();

        internal void ResetWorkGraphState() => _workGraphProgram = null;
    }

    private static partial class NativeCast
    {
        internal static D3D12WorkGraphPipeline WorkGraphPipeline(Pipeline value)
        {
#if DEBUG
            return (D3D12WorkGraphPipeline)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<
                Pipeline,
                D3D12WorkGraphPipeline>(ref value);
#endif
        }
    }
}
