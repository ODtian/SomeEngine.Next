using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;
internal sealed unsafe partial class D3D12Backend
{
    private const string WorkGraphName = "SomeEngine.WorkGraph";
    public Pipeline CreateWorkGraphPipeline(Device device, in WorkGraphPipelineDesc desc, PipelineCache? cache = null) => CreateWorkGraphPipeline(device, desc, cache, null);
    private Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache,
        SlangShaderData? capturedSlangCode
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        WorkGraphs capability = nativeDevice.RequireCapability<WorkGraphs>(nameof(CreateWorkGraphPipeline));
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ShaderReflection reflection = desc.Reflection;
        EntryPointReflection[] workGraphEntryPoints = CollectWorkGraphEntries(reflection);
        CompiledShaderLibrary library = CompileShaderLibrary(desc.Code, desc.CodeBlob, reflection, workGraphEntryPoints);
        D3D12RootSignatureState global = CompileWorkGraphRootSignature(nativeDevice, reflection, workGraphEntryPoints, desc.StaticSamplers);
        ID3D12StateObject* stateObject = null;
        ID3D12StateObjectProperties1* stateProperties = null;
        ID3D12WorkGraphProperties* graphProperties = null;
        NativeResourceData? nativeState = null;
        NativeResourceData? statePropertiesData = null;
        NativeResourceData? graphPropertiesData = null;
        D3D12RootSignatureState? globalToRelease = global;
        SlangShaderData? retainedCode = null;
        D3D12WorkGraphPipeline? result = null;
        try
        {
            stateObject = CreateNativeWorkGraphStateObject(nativeDevice, global, library.Code, workGraphEntryPoints, desc.NodeMask);
            SetNativeName(stateObject, desc.Label ?? "Work Graph State Object");
            QueryWorkGraphInterfaces(nativeDevice, stateObject, out stateProperties, out graphProperties);
            ProgramIdentifier identifier = GetWorkGraphIdentifier(stateProperties);
            uint graphIndex = GetWorkGraphIndex(graphProperties);
            WorkGraphEntryPointState[] entryPoints = ReadMaterializedWorkGraphEntries(capability, graphProperties, graphIndex, workGraphEntryPoints);
            WorkGraphMemoryRequirements requirements = ReadWorkGraphMemoryRequirements(graphProperties, graphIndex);
            statePropertiesData = new NativeResourceData((IUnknown*)stateProperties, ownsReference: true);
            stateProperties = null;
            graphPropertiesData = new NativeResourceData((IUnknown*)graphProperties, ownsReference: true);
            graphProperties = null;
            nativeState = new NativeResourceData((IUnknown*)stateObject, ownsReference: true, global.NativeData);
            stateObject = null;
            retainedCode = capturedSlangCode ?? desc.SlangCode;
            NativeResourceData[] additionalData = [statePropertiesData.Value, graphPropertiesData.Value];
            result = new D3D12WorkGraphPipeline(
                nativeDevice,
                nativeState.Value,
                global,
                additionalData,
                retainedCode,
                identifier,
                requirements,
                entryPoints,
                desc.Label
            );
            nativeState = null;
            statePropertiesData = null;
            graphPropertiesData = null;
            globalToRelease = null;
            retainedCode = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            CleanupFailedWorkGraphPipeline(result, nativeState, statePropertiesData, graphPropertiesData, stateObject, stateProperties, graphProperties);
            throw;
        }
        finally
        {
            globalToRelease?.Release();
        }
    }

    private D3D12RootSignatureState CompileWorkGraphRootSignature(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        EntryPointReflection[] entryPoints,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers
    ) => D3D12RootSignatureBuilder.Compile(
        this,
        nativeDevice,
        reflection,
        entryPoints,
        staticSamplers,
        PipelineType.WorkGraph,
        allowInputAssembler: false,
        allowStreamOutput: false
    );
    private static void CleanupFailedWorkGraphPipeline(
        D3D12WorkGraphPipeline? result,
        NativeResourceData? nativeState,
        NativeResourceData? statePropertiesData,
        NativeResourceData? graphPropertiesData,
        ID3D12StateObject* stateObject,
        ID3D12StateObjectProperties1* stateProperties,
        ID3D12WorkGraphProperties* graphProperties
    )
    {
        if (result is not null)
        {
            result.Dispose();
            return;
        }

        nativeState?.Release();
        graphPropertiesData?.Release();
        statePropertiesData?.Release();
        if (graphProperties is not null)
            _ = PipelineTrace.Release(graphProperties);
        if (stateProperties is not null)
            _ = PipelineTrace.Release(stateProperties);
        if (stateObject is not null)
            _ = PipelineTrace.Release(stateObject);
    }

    private EntryPointReflection[] CollectWorkGraphEntries(ShaderReflection reflection)
    {
        var entries = new List<EntryPointReflection>();
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            EntryPointReflection entry = reflection.GetEntryPointByIndex(index);
            if (entry.Stage is not (SlangStage.Dispatch or SlangStage.Node))
                continue;
            _ = ValidateStateObjectEntryPoint(reflection, entry, [SlangStage.Dispatch, SlangStage.Node], "Work Graph member");
            entries.Add(entry);
        }

        return[..entries];
    }

    private ID3D12StateObject* CreateNativeWorkGraphStateObject(
        D3D12Device nativeDevice,
        D3D12RootSignatureState global,
        byte[] code,
        EntryPointReflection[] entryPoints,
        uint nodeMask
    )
    {
        using NativeStateObjectArena arena = new();
        const int subobjectCount = 4;
        StateSubobject* subobjects = arena.Allocate<StateSubobject>(subobjectCount);
        AddWorkGraphLibrary(arena, code, entryPoints, subobjects);
        int ordinal = 1;
        GlobalRootSignature* globalDescription = arena.Allocate<GlobalRootSignature>();
        globalDescription->PGlobalRootSignature = global.Native;
        subobjects[ordinal++] = new StateSubobject(StateSubobjectType.GlobalRootSignature, globalDescription);
        WorkGraphDesc* graph = arena.Allocate<WorkGraphDesc>();
        graph->ProgramName = arena.String(WorkGraphName);
        graph->Flags = WorkGraphFlags.IncludeAllAvailableNodes;
        graph->NumEntrypoints = 0;
        graph->PEntrypoints = null;
        graph->NumExplicitlyDefinedNodes = 0;
        graph->PExplicitlyDefinedNodes = null;
        subobjects[ordinal++] = new StateSubobject(StateSubobjectType.WorkGraph, graph);
        uint* nativeNodeMask = arena.Allocate<uint>();
        *nativeNodeMask = nodeMask;
        subobjects[ordinal++] = new StateSubobject(StateSubobjectType.NodeMask, nativeNodeMask);
        StateObjectDesc native = new(StateObjectType.Executable, checked((uint)subobjectCount), subobjects);
        ID3D12StateObject* stateObject = null;
        Guid iid = ID3D12StateObject.Guid;
        int createResult = DevicePipelineTrace.CreateStateObject(nativeDevice.Native, &native, &iid, (void**)&stateObject);
        ThrowIfFailed(nativeDevice, createResult, NativeOperationType.PipelineCreation, "ID3D12Device5::CreateStateObject(Work Graph)");
        return stateObject;
    }

    private static void AddWorkGraphLibrary(
        NativeStateObjectArena arena,
        byte[] code,
        EntryPointReflection[] entryPoints,
        StateSubobject* subobjects
    )
    {
        ExportDesc* exports = arena.Allocate<ExportDesc>(entryPoints.Length);
        for (int index = 0; index < entryPoints.Length; index++)
        {
            string name = GetEntryPointName(entryPoints[index]);
            exports[index] = new ExportDesc(arena.String(name), null, ExportFlags.None);
        }

        DxilLibraryDesc* libraryDescription = arena.Allocate<DxilLibraryDesc>();
        *libraryDescription = new DxilLibraryDesc(new ShaderBytecode(arena.Bytes(code), (nuint)code.Length), checked((uint)entryPoints.Length), exports);
        subobjects[0] = new StateSubobject(StateSubobjectType.DxilLibrary, libraryDescription);
    }

    private static void QueryWorkGraphInterfaces(
        D3D12Device nativeDevice,
        ID3D12StateObject* stateObject,
        out ID3D12StateObjectProperties1* stateProperties,
        out ID3D12WorkGraphProperties* graphProperties
    )
    {
        stateProperties = null;
        graphProperties = null;
        ID3D12StateObjectProperties1* queriedStateProperties = null;
        Guid statePropertiesIid = ID3D12StateObjectProperties1.Guid;
        int propertiesResult = PipelineTrace.QueryInterface(stateObject, &statePropertiesIid, (void**)&queriedStateProperties);
        ThrowIfFailed(
            nativeDevice,
            propertiesResult,
            NativeOperationType.PipelineCreation,
            "ID3D12StateObject::QueryInterface(ID3D12StateObjectProperties1)"
        );
        stateProperties = queriedStateProperties;
        ID3D12WorkGraphProperties* queriedGraphProperties = null;
        Guid graphPropertiesIid = ID3D12WorkGraphProperties.Guid;
        int graphPropertiesResult = PipelineTrace.QueryInterface(stateObject, &graphPropertiesIid, (void**)&queriedGraphProperties);
        ThrowIfFailed(
            nativeDevice,
            graphPropertiesResult,
            NativeOperationType.PipelineCreation,
            "ID3D12StateObject::QueryInterface(ID3D12WorkGraphProperties)"
        );
        graphProperties = queriedGraphProperties;
    }

    private static ProgramIdentifier GetWorkGraphIdentifier(ID3D12StateObjectProperties1* stateProperties)
    {
        ProgramIdentifier identifier = PipelineTrace.GetWorkGraphIdentifier(stateProperties, WorkGraphName);
        if (IsZero(identifier))
        {
            throw new GraphicsException(GraphicsError.PipelineCreation, "The Work Graph program identifier is empty.");
        }

        return identifier;
    }

    private static uint GetWorkGraphIndex(ID3D12WorkGraphProperties* graphProperties)
    {
        uint graphIndex = PipelineTrace.GetWorkGraphIndex(graphProperties, WorkGraphName);
        if (graphIndex == uint.MaxValue)
        {
            throw new GraphicsException(GraphicsError.PipelineCreation, "The Work Graph program was not found in its state object.");
        }

        return graphIndex;
    }

    private static WorkGraphEntryPointState[] ReadMaterializedWorkGraphEntries(
        WorkGraphs capability,
        ID3D12WorkGraphProperties* graphProperties,
        uint graphIndex,
        EntryPointReflection[] submittedEntryPoints
    )
    {
        uint nativeEntryPointCount = PipelineTrace.GetNumEntrypoints(graphProperties, graphIndex);
        if (nativeEntryPointCount == 0 || nativeEntryPointCount > capability.MaximumNodeCount)
        {
            throw new GraphicsException(GraphicsError.PipelineCreation, "D3D12 returned an invalid Work Graph entry-point count.");
        }

        return ReadWorkGraphEntries(graphProperties, graphIndex, nativeEntryPointCount, submittedEntryPoints, capability.MaximumInputRecordSize);
    }

    private static WorkGraphMemoryRequirements ReadWorkGraphMemoryRequirements(ID3D12WorkGraphProperties* graphProperties, uint graphIndex)
    {
        Silk.NET.Direct3D12.WorkGraphMemoryRequirements native = default;
        PipelineTrace.GetWorkGraphMemoryRequirements(graphProperties, graphIndex, &native);
        WorkGraphMemoryRequirements requirements = new(native.MinSizeInBytes, native.MaxSizeInBytes, native.SizeGranularityInBytes);
        ValidateWorkGraphRequirements(requirements);
        return requirements;
    }

    public WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12WorkGraphPipeline native = RequireWorkGraphPipeline(pipeline);
        _ = RequireDevice(pipeline.Device, nameof(pipeline)).RequireCapability<WorkGraphs>(nameof(GetWorkGraphMemoryRequirements));
        return native.MemoryRequirements;
    }

    public bool TryGetWorkGraphEntryPoints(Pipeline pipeline, Span<WorkGraphEntryPointInfo> destination, out int requiredCount)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12WorkGraphPipeline native = RequireWorkGraphPipeline(pipeline);
        _ = RequireDevice(pipeline.Device, nameof(pipeline)).RequireCapability<WorkGraphs>(nameof(TryGetWorkGraphEntryPoints));
        return native.TryGetEntryPoints(destination, out requiredCount);
    }

    public void BindWorkGraph(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion? backingMemory,
        WorkGraphInitialization initialization
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        _ = command.NativeDevice.RequireCapability<WorkGraphs>(nameof(BindWorkGraph));
        D3D12WorkGraphPipeline nativePipeline = RequireWorkGraphPipeline(pipeline);
        WorkGraphMemoryRequirements requirements = nativePipeline.MemoryRequirements;
        D3D12Buffer? backing = null;
        BufferRange range = default;
        if (backingMemory is BufferRegion suppliedBacking)
        {
            D3D12Buffer suppliedBuffer = RequireBuffer(suppliedBacking.Buffer);
            BufferRange suppliedRange = suppliedBacking.Range.Expand(suppliedBuffer.Info.Size);
            ulong effectiveSize = requirements.GetBackingSize(suppliedRange.Size);
            if (effectiveSize != 0)
            {
                backing = suppliedBuffer;
                range = new BufferRange(suppliedRange.Offset, effectiveSize);
            }
        }

        D3D12WorkGraphState next = new(nativePipeline, backing, range);
        if (initialization == WorkGraphInitialization.Preserve && command.WorkGraphMatches(next))
        {
            return;
        }

        command.PrepareDescriptorTables(nativePipeline.RootSignature.DefaultTables);
        SetProgramDesc native = new()
        {
            Type = ProgramType.WorkGraph
        };
        native.WorkGraph = new SetWorkGraphDesc
        {
            ProgramIdentifier = nativePipeline.WorkGraphIdentifier,
            Flags = ToSetWorkGraphFlags(initialization),
            BackingMemory = backing is null ? default : new GpuVirtualAddressRange(ResourceTrace.GetGPUVirtualAddress(backing.Native) + range.Offset, range.Size),
            NodeLocalRootArgumentsTable = default,
        };
        D3D12CommandListFastCalls.SetRootSignature(command, command.List, compute: true, nativePipeline.RootSignature.Native);
        CommandBindingTrace.SetWorkGraph(command, command.List, &native);
        command.RememberPipeline(nativePipeline);
        command.RememberWorkGraph(next);
        foreach (DefaultRootTable table in nativePipeline.RootSignature.DefaultTables)
            command.SetRootTable(table.RootParameterIndex, table.Heap, 0);
    }

    public void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12WorkGraphState program = command.GetWorkGraphState();
        D3D12WorkGraphPipeline pipeline = program.Pipeline;
        switch (desc.Mode)
        {
            case WorkGraphDispatchInputMode.NodeCpu:

                DispatchWorkGraphNodeCpu(command, pipeline, desc.EntryPoint, desc.Records, desc.RecordCount, desc.RecordStride);
                return;
            case WorkGraphDispatchInputMode.NodeGpu:

                DispatchWorkGraphNodeGpu(command, pipeline, desc.EntryPoint, desc.GpuRecords, desc.RecordCount, desc.RecordStride);
                return;
            case WorkGraphDispatchInputMode.MultiNodeCpu:

                DispatchWorkGraphMultiNodeCpu(command, pipeline, desc.CpuNodeInputs, desc.Records);
                return;
            case WorkGraphDispatchInputMode.MultiNodeGpu:

                DispatchWorkGraphMultiNodeGpu(command, pipeline, desc.GpuNodeInputs);
                return;
            default:
                return;
        }
    }

    private static void DispatchWorkGraphNodeCpu(
        D3D12CommandContext command,
        D3D12WorkGraphPipeline pipeline,
        EntryPointReflection entryPoint,
        ReadOnlySpan<byte> records,
        uint recordCount,
        uint recordStride
    )
    {
        WorkGraphEntryPointState entry = pipeline.GetEntryPoint(entryPoint);
        GetWorkGraphRecordLayout(entry, recordCount, recordStride, out ulong requiredBytes, out ulong effectiveStride);
        ulong copyBytes = entry.RecordSize == 0 ? 0 : requiredBytes;
        _ = checked((int)copyBytes);
        command.PrepareOrdinaryData(copyBytes);
        DispatchGraphDesc native = new()
        {
            Mode = DispatchMode.NodeCpuInput
        };
        if (copyBytes == 0)
        {
            native.NodeCPUInput = new NodeCpuInput(entry.NativeIndex, recordCount, null, effectiveStride);
            CommandExecutionTrace.DispatchGraph(command, command.List, &native);
            return;
        }

        D3D12OrdinaryDataReservation storage = command.ReserveTransientOrdinaryData(copyBytes);
        Span<byte> copy = storage.CommitSpan(checked((int)copyBytes), clear: false);
        records[..copy.Length].CopyTo(copy);
        fixed (byte* pointer = copy)
        {
            native.NodeCPUInput = new NodeCpuInput(entry.NativeIndex, recordCount, pointer, effectiveStride);
            CommandExecutionTrace.DispatchGraph(command, command.List, &native);
        }
    }

    private void DispatchWorkGraphNodeGpu(
        D3D12CommandContext command,
        D3D12WorkGraphPipeline pipeline,
        EntryPointReflection entryPoint,
        in BufferRegion recordsRegion,
        uint recordCount,
        uint recordStride
    )
    {
        WorkGraphEntryPointState entry = pipeline.GetEntryPoint(entryPoint);
        GetWorkGraphRecordLayout(entry, recordCount, recordStride, out ulong requiredBytes, out ulong effectiveStride);
        D3D12Buffer records = ResolveWorkGraphGpuInput(recordsRegion, entry, requiredBytes, out ulong address);
        command.PrepareOrdinaryData((ulong)sizeof(NodeGpuInput));
        D3D12OrdinaryDataReservation header = command.ReserveTransientOrdinaryData((ulong)sizeof(NodeGpuInput));
        NodeGpuInput input = new()
        {
            EntrypointIndex = entry.NativeIndex,
            NumRecords = recordCount,
            Records = new GpuVirtualAddressAndStride(address, effectiveStride),
        };
        header.Commit(new ReadOnlySpan<byte>(&input, sizeof(NodeGpuInput)));
        DispatchGraphDesc native = new()
        {
            Mode = DispatchMode.NodeGpuInput
        };
        native.NodeGPUInput = header.Address;
        CommandExecutionTrace.DispatchGraph(command, command.List, &native);
    }

    private static void DispatchWorkGraphMultiNodeCpu(
        D3D12CommandContext command,
        D3D12WorkGraphPipeline pipeline,
        ReadOnlySpan<WorkGraphCpuNodeInput> inputs,
        ReadOnlySpan<byte> records
    )
    {
        ulong headerBytes = checked((ulong)inputs.Length * (ulong)sizeof(NodeCpuInput));
        ulong recordsOffset = AlignUp(headerBytes, 16);
        ulong totalBytes = checked(recordsOffset + (ulong)records.Length);
        _ = checked((int)totalBytes);
        command.PrepareOrdinaryData(totalBytes);
        D3D12OrdinaryDataReservation storage = command.ReserveTransientOrdinaryData(totalBytes);
        Span<byte> bytes = storage.CommitSpan(checked((int)totalBytes), clear: true);
        records.CopyTo(bytes[checked((int)recordsOffset)..]);
        fixed (byte* basePointer = bytes)
        {
            NodeCpuInput* nativeInputs = (NodeCpuInput*)basePointer;
            byte* recordBase = basePointer + checked((nint)recordsOffset);
            for (int index = 0; index < inputs.Length; index++)
            {
                ref readonly WorkGraphCpuNodeInput input = ref inputs[index];
                WorkGraphEntryPointState entry = pipeline.GetEntryPoint(input.EntryPoint);
                GetWorkGraphRecordLayout(entry, input.RecordCount, input.RecordStride, out ulong requiredBytes, out ulong effectiveStride);
                NativeMemoryChecks.Range((ulong)records.Length, input.RecordOffset, requiredBytes);
                nativeInputs[index] = new NodeCpuInput(entry.NativeIndex, input.RecordCount, entry.RecordSize == 0 ? null : recordBase + input.RecordOffset, effectiveStride);
            }

            DispatchGraphDesc native = new()
            {
                Mode = DispatchMode.MultiNodeCpuInput
            };
            native.MultiNodeCPUInput = new MultiNodeCpuInput
            {
                NumNodeInputs = checked((uint)inputs.Length),
                PNodeInputs = nativeInputs,
                NodeInputStrideInBytes = (ulong)sizeof(NodeCpuInput),
            };
            CommandExecutionTrace.DispatchGraph(command, command.List, &native);
        }
    }

    private void DispatchWorkGraphMultiNodeGpu(
        D3D12CommandContext command,
        D3D12WorkGraphPipeline pipeline,
        ReadOnlySpan<WorkGraphGpuNodeInput> inputs
    )
    {
        ulong arrayOffset = AlignUp((ulong)sizeof(MultiNodeGpuInput), 16);
        ulong totalBytes = checked(arrayOffset + checked((ulong)inputs.Length * (ulong)sizeof(NodeGpuInput)));
        _ = checked((int)totalBytes);
        command.PrepareOrdinaryData(totalBytes);
        D3D12OrdinaryDataReservation storage = command.ReserveTransientOrdinaryData(totalBytes);
        Span<byte> bytes = storage.CommitSpan(checked((int)totalBytes), clear: true);
        fixed (byte* basePointer = bytes)
        {
            MultiNodeGpuInput* header = (MultiNodeGpuInput*)basePointer;
            NodeGpuInput* nativeInputs = (NodeGpuInput*)(basePointer + checked((nint)arrayOffset));
            for (int index = 0; index < inputs.Length; index++)
            {
                ref readonly WorkGraphGpuNodeInput input = ref inputs[index];
                WorkGraphEntryPointState entry = pipeline.GetEntryPoint(input.EntryPoint);
                GetWorkGraphRecordLayout(entry, input.RecordCount, input.RecordStride, out ulong requiredBytes, out ulong effectiveStride);
                D3D12Buffer records = ResolveWorkGraphGpuInput(input.Records, entry, requiredBytes, out ulong address);
                nativeInputs[index] = new NodeGpuInput
                {
                    EntrypointIndex = entry.NativeIndex,
                    NumRecords = input.RecordCount,
                    Records = new GpuVirtualAddressAndStride(address, effectiveStride),
                };
            }

            *header = new MultiNodeGpuInput
            {
                NumNodeInputs = checked((uint)inputs.Length),
                NodeInputs = new GpuVirtualAddressAndStride(storage.Address + arrayOffset, (ulong)sizeof(NodeGpuInput)),
            };
        }

        DispatchGraphDesc native = new()
        {
            Mode = DispatchMode.MultiNodeGpuInput
        };
        native.MultiNodeGPUInput = storage.Address;
        CommandExecutionTrace.DispatchGraph(command, command.List, &native);
    }

    private D3D12Buffer ResolveWorkGraphGpuInput(in BufferRegion region, in WorkGraphEntryPointState entry, ulong requiredBytes, out ulong address)
    {
        D3D12Buffer records = RequireBuffer(region.Buffer);
        BufferRange range = region.Range.Expand(records.Info.Size);
        address = ResourceTrace.GetGPUVirtualAddress(records.Native) + range.Offset;
        return records;
    }

    private static SetWorkGraphFlags ToSetWorkGraphFlags(WorkGraphInitialization value) => value switch
    {
        WorkGraphInitialization.Initialize => SetWorkGraphFlags.Initialize,
        WorkGraphInitialization.Preserve => SetWorkGraphFlags.None,
        _ => default!,
    };
    private static void GetWorkGraphRecordLayout(
        in WorkGraphEntryPointState entry,
        uint recordCount,
        uint stride,
        out ulong requiredBytes,
        out ulong effectiveStride
    )
    {
        if (entry.RecordSize == 0)
        {
            requiredBytes = 0;
            effectiveStride = 0;
            return;
        }

        effectiveStride = stride == 0 ? entry.RecordSize : stride;
        requiredBytes = checked(checked((ulong)(recordCount - 1) * effectiveStride) + entry.RecordSize);
    }

    private static void ValidateWorkGraphRequirements(in WorkGraphMemoryRequirements value)
    {
        if (value.MaximumSize < value.MinimumSize)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"D3D12 returned invalid Work Graph backing-memory requirements " + $"(minimum {value.MinimumSize}, maximum {value.MaximumSize}, " + $"granularity {value.Granularity})."
            );
        }
    }

    private static WorkGraphEntryPointState[] ReadWorkGraphEntries(
        ID3D12WorkGraphProperties* graphProperties,
        uint graphIndex,
        uint nativeEntryPointCount,
        EntryPointReflection[] submittedEntryPoints,
        uint maximumInputRecordSize
    )
    {
        if (nativeEntryPointCount != checked((uint)submittedEntryPoints.Length))
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"D3D12 materialized {nativeEntryPointCount} Work Graph entry points, but " + $"{submittedEntryPoints.Length} Slang entry points were submitted."
            );
        }

        var result = new WorkGraphEntryPointState[checked((int)nativeEntryPointCount)];
        var materialized = new HashSet<uint>();
        for (uint ordinal = 0; ordinal < nativeEntryPointCount; ordinal++)
        {
            NodeID id = PipelineTrace.GetEntrypointID(graphProperties, graphIndex, ordinal);
            if (id.Name is null)
            {
                throw new GraphicsException(GraphicsError.PipelineCreation, "D3D12 returned a Work Graph entry point without an identity.");
            }

            uint nativeIndex = PipelineTrace.GetEntrypointIndex(graphProperties, graphIndex, id);
            if (nativeIndex == uint.MaxValue)
            {
                throw new GraphicsException(GraphicsError.PipelineCreation, "D3D12 returned a Work Graph entry point that cannot be indexed.");
            }

            if (!materialized.Add(nativeIndex))
            {
                throw new GraphicsException(GraphicsError.PipelineCreation, "D3D12 returned duplicate Work Graph entry-point indices.");
            }

            uint size = PipelineTrace.GetEntrypointRecordSizeInBytes(graphProperties, graphIndex, nativeIndex);
            uint alignment = PipelineTrace.GetEntrypointRecordAlignmentInBytes(graphProperties, graphIndex, nativeIndex);
            RequireValidWorkGraphEntryPointLayout(maximumInputRecordSize, size, alignment);
            result[checked((int)ordinal)] = new WorkGraphEntryPointState(submittedEntryPoints[checked((int)ordinal)], nativeIndex, size, alignment);
        }

        return result;
    }

    private static void RequireValidWorkGraphEntryPointLayout(uint maximumInputRecordSize, uint size, uint alignment)
    {
        if (!WorkGraphValidation.IsEntryPointLayoutValid(maximumInputRecordSize, size, alignment))
        {
            throw new GraphicsException(GraphicsError.PipelineCreation, "D3D12 returned invalid Work Graph entry-point layout data.");
        }
    }

    private static bool IsZero(in ProgramIdentifier identifier)
    {
        ProgramIdentifier copy = identifier;
        return new ReadOnlySpan<byte>(&copy, sizeof(ProgramIdentifier)).IndexOfAnyExcept((byte)0) < 0;
    }

    private readonly record struct WorkGraphEntryPointState(EntryPointReflection EntryPoint, uint NativeIndex, uint RecordSize, uint RecordAlignment);
    private sealed class D3D12WorkGraphPipeline : D3D12Pipeline
    {
        private readonly NativeResourceData _stateProperties;
        private readonly NativeResourceData _graphProperties;
        private readonly WorkGraphEntryPointState[] _entryPoints;
        internal D3D12WorkGraphPipeline(
            D3D12Device device,
            NativeResourceData native,
            D3D12RootSignatureState root,
            NativeResourceData[] additionalData,
            SlangShaderData slangCode,
            in ProgramIdentifier identifier,
            in WorkGraphMemoryRequirements memoryRequirements,
            WorkGraphEntryPointState[] entryPoints,
            string? label
        ) : base(device, native, root, [], additionalData, slangCode, PipelineType.WorkGraph, label)
        {
            _stateProperties = additionalData[0];
            _graphProperties = additionalData[1];
            _entryPoints = entryPoints;
            WorkGraphIdentifier = identifier;
            MemoryRequirements = memoryRequirements;
        }

        internal ProgramIdentifier WorkGraphIdentifier { get; }
        internal WorkGraphMemoryRequirements MemoryRequirements { get; }

        internal WorkGraphEntryPointState GetEntryPoint(EntryPointReflection entryPoint)
        {
            foreach (ref readonly WorkGraphEntryPointState entry in _entryPoints.AsSpan())
            {
                if (entry.EntryPoint == entryPoint)
                    return entry;
            }

            return default!;
        }

        internal bool TryGetEntryPoints(Span<WorkGraphEntryPointInfo> destination, out int requiredCount)
        {
            requiredCount = _entryPoints.Length;
            if (destination.Length < requiredCount)
                return false;
            for (int index = 0; index < _entryPoints.Length; index++)
            {
                WorkGraphEntryPointState entry = _entryPoints[index];
                destination[index] = new WorkGraphEntryPointInfo(entry.EntryPoint, entry.RecordSize, entry.RecordAlignment);
            }

            return true;
        }
    }

    private readonly record struct D3D12WorkGraphState(D3D12WorkGraphPipeline Pipeline, D3D12Buffer? Backing, BufferRange Range);
    private sealed partial class D3D12CommandContext
    {
        private D3D12WorkGraphState? _workGraph;
        internal bool WorkGraphMatches(in D3D12WorkGraphState value) => _workGraph is D3D12WorkGraphState current && ReferenceEquals(current.Pipeline, value.Pipeline) && ReferenceEquals(_pipeline, value.Pipeline) && ReferenceEquals(current.Backing, value.Backing) && current.Range == value.Range;
        internal void RememberWorkGraph(in D3D12WorkGraphState value) => _workGraph = value;
        internal D3D12WorkGraphState GetWorkGraphState()
        {
            return _workGraph!.Value;
        }

        internal void ResetWorkGraphState() => _workGraph = null;
    }

    private static partial class RequireD3D12
    {
        internal static D3D12WorkGraphPipeline WorkGraphPipeline(Pipeline value) => (D3D12WorkGraphPipeline)value;
    }
}
