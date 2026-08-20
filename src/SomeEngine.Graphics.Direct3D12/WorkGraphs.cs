using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private const string WorkGraphProgramName = "SomeEngine.WorkGraph";

    public Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        WorkGraphs capability = ValidateWorkGraphPipelineDescription(nativeDevice, desc);
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ShaderReflection reflection = GetProgramReflection(desc.Program);
        EntryPointReflection[] workGraphEntryPoints = CollectWorkGraphEntries(
            desc.Program,
            reflection,
            capability,
            out Dictionary<(string Name, uint ArrayIndex), EntryPointReflection> reflectedNodes);
        CompiledProgramLibrary library = CompileProgramLibrary(
            desc.Program,
            reflection,
            workGraphEntryPoints);
        D3D12RootSignatureState global = CompileWorkGraphRootSignature(
            nativeDevice,
            reflection,
            workGraphEntryPoints,
            desc.StaticSamplers);
        ID3D12StateObject* stateObject = null;
        ID3D12StateObjectProperties1* stateProperties = null;
        ID3D12WorkGraphProperties* graphProperties = null;
        NativeLease? nativeState = null;
        NativeLease? statePropertiesLease = null;
        NativeLease? graphPropertiesLease = null;
        D3D12RootSignatureState? globalToRelease = global;
        RetainedSlangProgram? retainedProgram = null;
        D3D12WorkGraphPipeline? result = null;
        try
        {
            byte[] key = CreateWorkGraphPipelineKey(
                nativeDevice,
                global,
                library,
                reflectedNodes,
                desc);
            byte[][] replayLibraries = ResolveStateObjectReplayCode(
                nativeCache,
                5,
                key,
                library);
            stateObject = CreateNativeWorkGraphStateObject(
                nativeDevice,
                global,
                replayLibraries,
                workGraphEntryPoints,
                desc.NodeMask);
            SetNativeName(stateObject, desc.Label ?? "Work Graph State Object");
            QueryWorkGraphInterfaces(
                nativeDevice,
                stateObject,
                out stateProperties,
                out graphProperties);
            ProgramIdentifier identifier = GetWorkGraphProgramIdentifier(stateProperties);
            uint graphIndex = GetWorkGraphIndex(graphProperties);
            WorkGraphEntryPointState[] entryPoints = ReadMaterializedWorkGraphEntries(
                capability,
                graphProperties,
                graphIndex,
                reflectedNodes);
            WorkGraphMemoryRequirements requirements = ReadWorkGraphMemoryRequirements(
                graphProperties,
                graphIndex);

            statePropertiesLease = new NativeLease(
                (IUnknown*)stateProperties,
                ownsReference: true);
            stateProperties = null;
            graphPropertiesLease = new NativeLease(
                (IUnknown*)graphProperties,
                ownsReference: true);
            graphProperties = null;
            nativeState = new NativeLease(
                (IUnknown*)stateObject,
                ownsReference: true,
                global.NativeLifetime);
            stateObject = null;
            retainedProgram = RetainProgram(desc.Program);
            NativeLease[] additionalLeases =
                [statePropertiesLease, graphPropertiesLease];
            result = new D3D12WorkGraphPipeline(
                nativeDevice,
                nativeState,
                global,
                additionalLeases,
                retainedProgram,
                identifier,
                requirements,
                entryPoints,
                desc.Label);
            nativeState = null;
            statePropertiesLease = null;
            graphPropertiesLease = null;
            globalToRelease = null;
            retainedProgram = null;
            nativeDevice.RegisterChild(result);
            StoreStateObjectReplay(nativeCache, 5, key, library);
            return result;
        }
        catch
        {
            CleanupFailedWorkGraphPipeline(
                result,
                nativeState,
                statePropertiesLease,
                graphPropertiesLease,
                stateObject,
                stateProperties,
                graphProperties,
                retainedProgram);
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
        ReadOnlySpan<StaticSamplerBinding> staticSamplers) =>
        D3D12RootSignatureBuilder.Compile(
            this,
            nativeDevice,
            reflection,
            entryPoints,
            staticSamplers,
            PipelineType.WorkGraph,
            allowInputAssembler: false,
            allowStreamOutput: false);

    private static WorkGraphs ValidateWorkGraphPipelineDescription(
        D3D12Device nativeDevice,
        in WorkGraphPipelineDesc desc)
    {
        WorkGraphs capability =
            nativeDevice.RequireCapability<WorkGraphs>(nameof(CreateWorkGraphPipeline));
        ArgumentNullException.ThrowIfNull(desc.Program);
        if (desc.NodeMask == 0 || (desc.NodeMask & ~nativeDevice.EnabledNodeMask) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        return capability;
    }

    private static void CleanupFailedWorkGraphPipeline(
        D3D12WorkGraphPipeline? result,
        NativeLease? nativeState,
        NativeLease? statePropertiesLease,
        NativeLease? graphPropertiesLease,
        ID3D12StateObject* stateObject,
        ID3D12StateObjectProperties1* stateProperties,
        ID3D12WorkGraphProperties* graphProperties,
        RetainedSlangProgram? retainedProgram)
    {
        if (result is not null)
        {
            result.Dispose();
            return;
        }
        nativeState?.Release();
        graphPropertiesLease?.Release();
        statePropertiesLease?.Release();
        if (graphProperties is not null)
            _ = graphProperties->Release();
        if (stateProperties is not null)
            _ = stateProperties->Release();
        if (stateObject is not null)
            _ = stateObject->Release();
        retainedProgram?.Dispose();
    }

    private EntryPointReflection[] CollectWorkGraphEntries(
        IComponentType program,
        ShaderReflection reflection,
        WorkGraphs capability,
        out Dictionary<(string Name, uint ArrayIndex), EntryPointReflection> reflectedNodes)
    {
        var entries = new List<EntryPointReflection>();
        reflectedNodes = [];
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            EntryPointReflection entry = reflection.GetEntryPointByIndex(index);
            if (entry.Stage is not (SlangStage.Dispatch or SlangStage.Node))
                continue;
            _ = ValidateStateObjectEntryPoint(
                reflection,
                entry,
                [SlangStage.Dispatch, SlangStage.Node],
                "Work Graph member");
            (string Name, uint ArrayIndex) nodeID = GetWorkGraphNodeIdentity(program, entry);
            if (!reflectedNodes.TryAdd(nodeID, entry))
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"Slang produced duplicate Work Graph node identity " +
                    $"'{nodeID.Name}[{nodeID.ArrayIndex}]'.");
            }
            ValidateWorkGraphNodeGrid(
                program,
                entry,
                nodeID,
                capability,
                "NodeMaxDispatchGrid",
                fixedGrid: false);
            ValidateWorkGraphNodeGrid(
                program,
                entry,
                nodeID,
                capability,
                "NodeDispatchGrid",
                fixedGrid: true);
            entries.Add(entry);
        }
        if (entries.Count == 0 || (uint)entries.Count > capability.MaximumNodeCount)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                "The linked Slang program exposes on valid Work Graph nodes or exceeds the Device limit.");
        }
        return [.. entries];
    }

    private void ValidateWorkGraphNodeGrid(
        IComponentType program,
        EntryPointReflection entry,
        (string Name, uint ArrayIndex) nodeID,
        WorkGraphs capability,
        string attribute,
        bool fixedGrid)
    {
        if (!TryGetWorkGraphGrid(
                program,
                entry,
                attribute,
                out uint x,
                out uint y,
                out uint z) ||
            WorkGraphValidation.IsMaximumDispatchGridValid(
                capability.MaximumDispatchGridDimension,
                capability.MaximumOneDimensionalDispatchGridX,
                capability.MaximumDispatchGridVolume,
                x,
                y,
                z))
        {
            return;
        }
        string detail = fixedGrid
            ? "has a fixed dispatch grid outside the Device limits."
            : "exceeds the Device Work Graph dispatch-grid limits.";
        throw new GraphicsException(
            GraphicsError.PipelineCreation,
            $"Slang node '{nodeID.Name}[{nodeID.ArrayIndex}]' {detail}");
    }

    private ID3D12StateObject* CreateNativeWorkGraphStateObject(
        D3D12Device nativeDevice,
        D3D12RootSignatureState global,
        byte[][] replayLibraries,
        EntryPointReflection[] entryPoints,
        uint nodeMask)
    {
        using NativeStateObjectArena arena = new();
        int subobjectCount = checked(replayLibraries.Length + 3);
        StateSubobject* subobjects = arena.Allocate<StateSubobject>(subobjectCount);
        AddWorkGraphLibraries(arena, replayLibraries, entryPoints, subobjects);
        int ordinal = replayLibraries.Length;
        GlobalRootSignature* globalDescription = arena.Allocate<GlobalRootSignature>();
        globalDescription->PGlobalRootSignature = global.Native;
        subobjects[ordinal++] = new StateSubobject(
            StateSubobjectType.GlobalRootSignature,
            globalDescription);
        WorkGraphDesc* graph = arena.Allocate<WorkGraphDesc>();
        graph->ProgramName = arena.String(WorkGraphProgramName);
        graph->Flags = WorkGraphFlags.IncludeAllAvailableNodes;
        graph->NumEntrypoints = 0;
        graph->PEntrypoints = null;
        graph->NumExplicitlyDefinedNodes = 0;
        graph->PExplicitlyDefinedNodes = null;
        subobjects[ordinal++] = new StateSubobject(StateSubobjectType.WorkGraph, graph);
        uint* nativeNodeMask = arena.Allocate<uint>();
        *nativeNodeMask = nodeMask;
        subobjects[ordinal++] = new StateSubobject(
            StateSubobjectType.NodeMask,
            nativeNodeMask);
        if (ordinal != subobjectCount)
            throw new InvalidOperationException("The Work Graph state-object layout is incomplete.");

        StateObjectDesc native = new(
            StateObjectType.Executable,
            checked((uint)subobjectCount),
            subobjects);
        ID3D12StateObject* stateObject = null;
        Guid iid = ID3D12StateObject.Guid;
        int createResult = nativeDevice.Native->CreateStateObject(
            &native,
            &iid,
            (void**)&stateObject);
        ThrowIfFailed(
            nativeDevice,
            createResult,
            NativeOperationType.PipelineCreation,
            "ID3D12Device5::CreateStateObject(Work Graph)");
        return stateObject;
    }

    private static void AddWorkGraphLibraries(
        NativeStateObjectArena arena,
        byte[][] replayLibraries,
        EntryPointReflection[] entryPoints,
        StateSubobject* subobjects)
    {
        for (int index = 0; index < replayLibraries.Length; index++)
        {
            string name = GetStableEntryPointName(entryPoints[index]);
            ExportDesc* export = arena.Allocate<ExportDesc>();
            *export = new ExportDesc(arena.String(name), null, ExportFlags.None);
            byte[] code = replayLibraries[index];
            DxilLibraryDesc* libraryDescription = arena.Allocate<DxilLibraryDesc>();
            *libraryDescription = new DxilLibraryDesc(
                new ShaderBytecode(arena.Bytes(code), (nuint)code.Length),
                1,
                export);
            subobjects[index] = new StateSubobject(
                StateSubobjectType.DxilLibrary,
                libraryDescription);
        }
    }

    private static void QueryWorkGraphInterfaces(
        D3D12Device nativeDevice,
        ID3D12StateObject* stateObject,
        out ID3D12StateObjectProperties1* stateProperties,
        out ID3D12WorkGraphProperties* graphProperties)
    {
        stateProperties = null;
        graphProperties = null;
        ID3D12StateObjectProperties1* queriedStateProperties = null;
        Guid statePropertiesIid = ID3D12StateObjectProperties1.Guid;
        int propertiesResult = stateObject->QueryInterface(
            &statePropertiesIid,
            (void**)&queriedStateProperties);
        ThrowIfFailed(
            nativeDevice,
            propertiesResult,
            NativeOperationType.PipelineCreation,
            "ID3D12StateObject::QueryInterface(ID3D12StateObjectProperties1)");
        stateProperties = queriedStateProperties;
        ID3D12WorkGraphProperties* queriedGraphProperties = null;
        Guid graphPropertiesIid = ID3D12WorkGraphProperties.Guid;
        int graphPropertiesResult = stateObject->QueryInterface(
            &graphPropertiesIid,
            (void**)&queriedGraphProperties);
        ThrowIfFailed(
            nativeDevice,
            graphPropertiesResult,
            NativeOperationType.PipelineCreation,
            "ID3D12StateObject::QueryInterface(ID3D12WorkGraphProperties)");
        graphProperties = queriedGraphProperties;
    }

    private static ProgramIdentifier GetWorkGraphProgramIdentifier(
        ID3D12StateObjectProperties1* stateProperties)
    {
        ProgramIdentifier identifier =
            stateProperties->GetProgramIdentifier(WorkGraphProgramName);
        if (IsZero(identifier))
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                "The Work Graph program identifier is empty.");
        }
        return identifier;
    }

    private static uint GetWorkGraphIndex(ID3D12WorkGraphProperties* graphProperties)
    {
        uint graphIndex = graphProperties->GetWorkGraphIndex(WorkGraphProgramName);
        if (graphIndex == uint.MaxValue)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                "The Work Graph program was not found in its state object.");
        }
        return graphIndex;
    }

    private static WorkGraphEntryPointState[] ReadMaterializedWorkGraphEntries(
        WorkGraphs capability,
        ID3D12WorkGraphProperties* graphProperties,
        uint graphIndex,
        Dictionary<(string Name, uint ArrayIndex), EntryPointReflection> reflectedNodes)
    {
        uint nativeEntryPointCount = graphProperties->GetNumEntrypoints(graphIndex);
        if (nativeEntryPointCount == 0 || nativeEntryPointCount > capability.MaximumNodeCount)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                "D3D12 returned an invalid Work Graph entry-point count.");
        }
        return ReadWorkGraphEntries(
            graphProperties,
            graphIndex,
            nativeEntryPointCount,
            reflectedNodes,
            capability.MaximumInputRecordSize);
    }

    private static WorkGraphMemoryRequirements ReadWorkGraphMemoryRequirements(
        ID3D12WorkGraphProperties* graphProperties,
        uint graphIndex)
    {
        Silk.NET.Direct3D12.WorkGraphMemoryRequirements native = default;
        graphProperties->GetWorkGraphMemoryRequirements(graphIndex, &native);
        WorkGraphMemoryRequirements requirements = new(
            native.MinSizeInBytes,
            native.MaxSizeInBytes,
            native.SizeGranularityInBytes);
        ValidateWorkGraphRequirements(requirements);
        return requirements;
    }

    public WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline)
    {
        D3D12WorkGraphPipeline native = RequireWorkGraphPipeline(pipeline);
        _ = RequireDevice(pipeline.Device, nameof(pipeline))
            .RequireCapability<WorkGraphs>(nameof(GetWorkGraphMemoryRequirements));
        return native.MemoryRequirements;
    }

    public bool TryGetWorkGraphEntryPoints(
        Pipeline pipeline,
        Span<WorkGraphEntryPointInfo> destination,
        out int requiredCount)
    {
        D3D12WorkGraphPipeline native = RequireWorkGraphPipeline(pipeline);
        _ = RequireDevice(pipeline.Device, nameof(pipeline))
            .RequireCapability<WorkGraphs>(nameof(TryGetWorkGraphEntryPoints));
        return native.TryGetEntryPoints(destination, out requiredCount);
    }

    public void BindWorkGraph(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion? backingMemory,
        WorkGraphInitialization initialization)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        _ = command.NativeDevice.RequireCapability<WorkGraphs>(nameof(BindWorkGraph));
        D3D12WorkGraphPipeline nativePipeline = RequireWorkGraphPipeline(pipeline);

        WorkGraphMemoryRequirements requirements = nativePipeline.MemoryRequirements;
        D3D12Buffer? backing = null;
        BufferRange range = default;
        if (backingMemory is BufferRegion suppliedBacking)
        {
            D3D12Buffer suppliedBuffer = RequireBuffer(suppliedBacking.Buffer);
            BufferRange suppliedRange = suppliedBacking.Range.Resolve(suppliedBuffer.Info.Size);
            ulong effectiveSize = requirements.NormalizeBackingSize(suppliedRange.Size);
            if (effectiveSize != 0 &&
                ((suppliedBuffer.Info.Usages & BufferUsages.ShaderWrite) == 0 ||
                 suppliedRange.Offset % 8 != 0))
            {
                throw new ArgumentException(
                    "Work Graph backing memory does not satisfy its native size, alignment, or ShaderWrite requirements.",
                    nameof(backingMemory));
            }
            if (effectiveSize != 0)
            {
                backing = suppliedBuffer;
                range = new BufferRange(suppliedRange.Offset, effectiveSize);
            }
        }
        else if (requirements.MinimumSize != 0)
        {
            throw new ArgumentException(
                "This Work Graph requires backing memory.",
                nameof(backingMemory));
        }

        D3D12WorkGraphProgramState next = new(
            nativePipeline,
            backing,
            range);
        if (initialization == WorkGraphInitialization.Preserve &&
            command.WorkGraphProgramMatches(next))
        {
            return;
        }
        command.PrepareCaptures(backing is null ? 1 : 2, 0, backing is null ? 0 : 1);
        command.PrepareDescriptorTables(nativePipeline.RootSignature.DefaultTables);
        SetProgramDesc native = new() { Type = ProgramType.WorkGraph };
        native.WorkGraph = new SetWorkGraphDesc
        {
            ProgramIdentifier = nativePipeline.ProgramIdentifier,
            Flags = ToSetWorkGraphFlags(initialization),
            BackingMemory = backing is null
                ? default
                : new GpuVirtualAddressRange(
                    backing.Native->GetGPUVirtualAddress() + range.Offset,
                    range.Size),
            NodeLocalRootArgumentsTable = default,
        };
        if (backing is not null)
            command.Capture(backing);
        command.CapturePipeline(nativePipeline);
        D3D12CommandListFastCalls.SetRootSignature(
            command.List,
            compute: true,
            nativePipeline.RootSignature.Native);
        command.List->SetProgram(&native);
        command.RememberPipeline(nativePipeline);
        command.RememberWorkGraphProgram(next);
        foreach (DefaultRootTable table in nativePipeline.RootSignature.DefaultTables)
            command.SetRootTable(table.RootParameterIndex, table.Heap, 0);
    }

    public void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        WorkGraphs capability =
            command.NativeDevice.RequireCapability<WorkGraphs>(nameof(DispatchWorkGraph));
        D3D12WorkGraphProgramState program = command.RequireWorkGraphProgram();
        D3D12WorkGraphPipeline pipeline = program.Pipeline;
        switch (desc.Mode)
        {
            case WorkGraphDispatchInputMode.NodeCpu:
                if (!capability.CpuInput)
                    throw new NotSupportedException("CPU Work Graph input is unavailable.");
                DispatchWorkGraphNodeCpu(
                    command,
                    pipeline,
                    desc.EntryPoint,
                    desc.Records,
                    desc.RecordCount,
                    desc.RecordStride);
                return;
            case WorkGraphDispatchInputMode.NodeGpu:
                if (!capability.GpuInput)
                    throw new NotSupportedException("GPU Work Graph input is unavailable.");
                DispatchWorkGraphNodeGpu(
                    command,
                    pipeline,
                    desc.EntryPoint,
                    desc.GpuRecords,
                    desc.RecordCount,
                    desc.RecordStride);
                return;
            case WorkGraphDispatchInputMode.MultiNodeCpu:
                if (!capability.CpuInput)
                    throw new NotSupportedException("CPU Work Graph input is unavailable.");
                DispatchWorkGraphMultiNodeCpu(
                    command,
                    pipeline,
                    capability,
                    desc.CpuNodeInputs,
                    desc.Records);
                return;
            case WorkGraphDispatchInputMode.MultiNodeGpu:
                if (!capability.GpuInput)
                    throw new NotSupportedException("GPU Work Graph input is unavailable.");
                DispatchWorkGraphMultiNodeGpu(
                    command,
                    pipeline,
                    capability,
                    desc.GpuNodeInputs);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(desc));
        }
    }

    private static void DispatchWorkGraphNodeCpu(
        D3D12CommandContext command,
        D3D12WorkGraphPipeline pipeline,
        EntryPointReflection entryPoint,
        ReadOnlySpan<byte> records,
        uint recordCount,
        uint recordStride)
    {
        WorkGraphEntryPointState entry = pipeline.GetEntryPoint(entryPoint);
        ValidateWorkGraphRecordLayout(
            entry,
            recordCount,
            recordStride,
            out ulong requiredBytes,
            out ulong effectiveStride);
        if ((ulong)records.Length < requiredBytes)
            throw new ArgumentException("The CPU Work Graph input span is too small.", nameof(records));

        ulong copyBytes = entry.RecordSize == 0 ? 0 : requiredBytes;
        _ = checked((int)copyBytes);
        command.PrepareOrdinaryData(copyBytes);
        DispatchGraphDesc native = new() { Mode = DispatchMode.NodeCpuInput };
        if (copyBytes == 0)
        {
            native.NodeCPUInput = new NodeCpuInput(
                entry.NativeIndex,
                recordCount,
                null,
                effectiveStride);
            command.List->DispatchGraph(&native);
            return;
        }

        D3D12OrdinaryDataReservation storage =
            command.ReserveTransientOrdinaryData(copyBytes);
        Span<byte> copy = storage.CommitSpan(checked((int)copyBytes), clear: false);
        records[..copy.Length].CopyTo(copy);
        fixed (byte* pointer = copy)
        {
            native.NodeCPUInput = new NodeCpuInput(
                entry.NativeIndex,
                recordCount,
                pointer,
                effectiveStride);
            command.List->DispatchGraph(&native);
        }
    }

    private void DispatchWorkGraphNodeGpu(
        D3D12CommandContext command,
        D3D12WorkGraphPipeline pipeline,
        EntryPointReflection entryPoint,
        in BufferRegion recordsRegion,
        uint recordCount,
        uint recordStride)
    {
        WorkGraphEntryPointState entry = pipeline.GetEntryPoint(entryPoint);
        ValidateWorkGraphRecordLayout(
            entry,
            recordCount,
            recordStride,
            out ulong requiredBytes,
            out ulong effectiveStride);
        D3D12Buffer records = ResolveWorkGraphGpuInput(
            recordsRegion,
            entry,
            requiredBytes,
            out ulong address);

        command.PrepareCaptures(1, 0, 1);
        command.PrepareOrdinaryData((ulong)sizeof(NodeGpuInput));
        D3D12OrdinaryDataReservation header =
            command.ReserveTransientOrdinaryData((ulong)sizeof(NodeGpuInput));
        command.Capture(records);
        NodeGpuInput input = new()
        {
            EntrypointIndex = entry.NativeIndex,
            NumRecords = recordCount,
            Records = new GpuVirtualAddressAndStride(address, effectiveStride),
        };
        header.Commit(new ReadOnlySpan<byte>(&input, sizeof(NodeGpuInput)));
        DispatchGraphDesc native = new() { Mode = DispatchMode.NodeGpuInput };
        native.NodeGPUInput = header.Address;
        command.List->DispatchGraph(&native);
    }

    private static void DispatchWorkGraphMultiNodeCpu(
        D3D12CommandContext command,
        D3D12WorkGraphPipeline pipeline,
        WorkGraphs capability,
        ReadOnlySpan<WorkGraphCpuNodeInput> inputs,
        ReadOnlySpan<byte> records)
    {
        if (inputs.IsEmpty || (uint)inputs.Length > capability.MaximumNodeCount)
            throw new ArgumentOutOfRangeException(nameof(inputs));
        ValidateUniqueCpuEntries(inputs);
        for (int index = 0; index < inputs.Length; index++)
        {
            ref readonly WorkGraphCpuNodeInput input = ref inputs[index];
            WorkGraphEntryPointState entry = pipeline.GetEntryPoint(input.EntryPoint);
            ValidateWorkGraphRecordLayout(
                entry,
                input.RecordCount,
                input.RecordStride,
                out ulong requiredBytes,
                out _);
            if (input.RecordOffset > (uint)records.Length ||
                requiredBytes > (ulong)records.Length - input.RecordOffset)
            {
                throw new ArgumentException(
                    "A multi-node CPU Work Graph input slice is outside the record packet.",
                    nameof(inputs));
            }
        }

        ulong headerBytes = checked((ulong)inputs.Length * (ulong)sizeof(NodeCpuInput));
        ulong recordsOffset = AlignUp(headerBytes, 16);
        ulong totalBytes = checked(recordsOffset + (ulong)records.Length);
        _ = checked((int)totalBytes);
        command.PrepareOrdinaryData(totalBytes);
        D3D12OrdinaryDataReservation storage =
            command.ReserveTransientOrdinaryData(totalBytes);
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
                ValidateWorkGraphRecordLayout(
                    entry,
                    input.RecordCount,
                    input.RecordStride,
                    out _,
                    out ulong effectiveStride);
                nativeInputs[index] = new NodeCpuInput(
                    entry.NativeIndex,
                    input.RecordCount,
                    entry.RecordSize == 0 ? null : recordBase + input.RecordOffset,
                    effectiveStride);
            }

            DispatchGraphDesc native = new() { Mode = DispatchMode.MultiNodeCpuInput };
            native.MultiNodeCPUInput = new MultiNodeCpuInput
            {
                NumNodeInputs = checked((uint)inputs.Length),
                PNodeInputs = nativeInputs,
                NodeInputStrideInBytes = (ulong)sizeof(NodeCpuInput),
            };
            command.List->DispatchGraph(&native);
        }
    }

    private void DispatchWorkGraphMultiNodeGpu(
        D3D12CommandContext command,
        D3D12WorkGraphPipeline pipeline,
        WorkGraphs capability,
        ReadOnlySpan<WorkGraphGpuNodeInput> inputs)
    {
        if (inputs.IsEmpty || (uint)inputs.Length > capability.MaximumNodeCount)
            throw new ArgumentOutOfRangeException(nameof(inputs));
        ValidateUniqueGpuEntries(inputs);
        for (int index = 0; index < inputs.Length; index++)
        {
            ref readonly WorkGraphGpuNodeInput input = ref inputs[index];
            WorkGraphEntryPointState entry = pipeline.GetEntryPoint(input.EntryPoint);
            ValidateWorkGraphRecordLayout(
                entry,
                input.RecordCount,
                input.RecordStride,
                out ulong requiredBytes,
                out _);
            _ = ResolveWorkGraphGpuInput(input.Records, entry, requiredBytes, out _);
        }

        ulong arrayOffset = AlignUp((ulong)sizeof(MultiNodeGpuInput), 16);
        ulong totalBytes = checked(
            arrayOffset + checked((ulong)inputs.Length * (ulong)sizeof(NodeGpuInput)));
        _ = checked((int)totalBytes);
        command.PrepareCaptures(inputs.Length, 0, inputs.Length);
        command.PrepareOrdinaryData(totalBytes);
        D3D12OrdinaryDataReservation storage =
            command.ReserveTransientOrdinaryData(totalBytes);
        Span<byte> bytes = storage.CommitSpan(checked((int)totalBytes), clear: true);
        fixed (byte* basePointer = bytes)
        {
            MultiNodeGpuInput* header = (MultiNodeGpuInput*)basePointer;
            NodeGpuInput* nativeInputs =
                (NodeGpuInput*)(basePointer + checked((nint)arrayOffset));
            for (int index = 0; index < inputs.Length; index++)
            {
                ref readonly WorkGraphGpuNodeInput input = ref inputs[index];
                WorkGraphEntryPointState entry = pipeline.GetEntryPoint(input.EntryPoint);
                ValidateWorkGraphRecordLayout(
                    entry,
                    input.RecordCount,
                    input.RecordStride,
                    out ulong requiredBytes,
                    out ulong effectiveStride);
                D3D12Buffer records = ResolveWorkGraphGpuInput(
                    input.Records,
                    entry,
                    requiredBytes,
                    out ulong address);
                command.Capture(records);
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
                NodeInputs = new GpuVirtualAddressAndStride(
                    storage.Address + arrayOffset,
                    (ulong)sizeof(NodeGpuInput)),
            };
        }
        DispatchGraphDesc native = new() { Mode = DispatchMode.MultiNodeGpuInput };
        native.MultiNodeGPUInput = storage.Address;
        command.List->DispatchGraph(&native);
    }

    private D3D12Buffer ResolveWorkGraphGpuInput(
        in BufferRegion region,
        in WorkGraphEntryPointState entry,
        ulong requiredBytes,
        out ulong address)
    {
        D3D12Buffer records = RequireBuffer(region.Buffer);
        BufferRange range = region.Range.Resolve(records.Info.Size);
        address = records.Native->GetGPUVirtualAddress() + range.Offset;
        if ((records.Info.Usages & BufferUsages.ShaderRead) == 0 ||
            range.Size < requiredBytes ||
            (entry.RecordSize != 0 && address % entry.RecordAlignment != 0))
        {
            throw new ArgumentException("A GPU Work Graph input range is invalid.", nameof(region));
        }
        return records;
    }

    private static void ValidateUniqueCpuEntries(ReadOnlySpan<WorkGraphCpuNodeInput> inputs)
    {
        for (int index = 0; index < inputs.Length; index++)
        {
            if (inputs[index].EntryPoint == EntryPointReflection.Null)
                throw new ArgumentException("A Work Graph node input requires a Slang entry point.");
            for (int prior = 0; prior < index; prior++)
            {
                if (inputs[prior].EntryPoint == inputs[index].EntryPoint)
                    throw new ArgumentException("A multi-node Work Graph dispatch repeats as entry point.");
            }
        }
    }

    private static void ValidateUniqueGpuEntries(ReadOnlySpan<WorkGraphGpuNodeInput> inputs)
    {
        for (int index = 0; index < inputs.Length; index++)
        {
            if (inputs[index].EntryPoint == EntryPointReflection.Null)
                throw new ArgumentException("A Work Graph node input requires a Slang entry point.");
            for (int prior = 0; prior < index; prior++)
            {
                if (inputs[prior].EntryPoint == inputs[index].EntryPoint)
                    throw new ArgumentException("A multi-node Work Graph dispatch repeats as entry point.");
            }
        }
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
        out ulong requiredBytes,
        out ulong effectiveStride)
    {
        if (recordCount == 0)
            throw new ArgumentOutOfRangeException(nameof(recordCount));
        if (entry.RecordSize == 0)
        {
            if (stride != 0)
                throw new ArgumentOutOfRangeException(nameof(stride));
            requiredBytes = 0;
            effectiveStride = 0;
            return;
        }
        if (stride != 0 &&
            (stride < entry.RecordSize || stride % entry.RecordAlignment != 0 || stride % 4 != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(stride));
        }
        effectiveStride = stride == 0 ? entry.RecordSize : stride;
        requiredBytes = checked(
            checked((ulong)(recordCount - 1) * effectiveStride) + entry.RecordSize);
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

    private static (string Name, uint ArrayIndex) GetWorkGraphNodeIdentity(
        IComponentType program,
        EntryPointReflection entryPoint)
    {
        AttributeReflection attribute = FindWorkGraphAttribute(program, entryPoint, "NodeID");
        if (attribute == AttributeReflection.Null)
        {
            string name = WorkGraphValidation.GetEffectiveEntryPointName(entryPoint);
            if (string.IsNullOrWhiteSpace(name))
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    "Slang returned a Work Graph entry point without a name.");
            return (name, 0);
        }
        if (attribute.ArgumentCount is < 1 or > 2)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                "Slang's NodeID attribute must contain a node name and optional array index.");
        }
        string nodeName = attribute.GetArgumentValueString(0);
        if (string.IsNullOrWhiteSpace(nodeName))
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                "Slang's NodeID attribute contains an empty node name.");
        }
        uint arrayIndex = 0;
        if (attribute.ArgumentCount == 2)
        {
            SlangResult result = attribute.GetArgumentValueInt(1, out int value);
            if (result.Failed || value < 0)
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"Slang's NodeID array index is invalid: {result}.");
            }
            arrayIndex = checked((uint)value);
        }
        return (nodeName, arrayIndex);
    }

    private static bool TryGetWorkGraphGrid(
        IComponentType program,
        EntryPointReflection entryPoint,
        string attributeName,
        out uint x,
        out uint y,
        out uint z)
    {
        AttributeReflection attribute = FindWorkGraphAttribute(program, entryPoint, attributeName);
        if (attribute == AttributeReflection.Null)
        {
            x = y = z = 0;
            return false;
        }
        if (attribute.ArgumentCount != 3)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang's {attributeName} attribute must contain three integers.");
        }
        x = ReadPositiveWorkGraphInteger(attribute, 0, attributeName);
        y = ReadPositiveWorkGraphInteger(attribute, 1, attributeName);
        z = ReadPositiveWorkGraphInteger(attribute, 2, attributeName);
        return true;
    }

    private static uint ReadPositiveWorkGraphInteger(
        AttributeReflection attribute,
        uint index,
        string attributeName)
    {
        SlangResult result = attribute.GetArgumentValueInt(index, out int value);
        if (result.Failed || value <= 0)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang's {attributeName} component {index} is invalid: {result}.");
        }
        return checked((uint)value);
    }

    private static AttributeReflection FindWorkGraphAttribute(
        IComponentType program,
        EntryPointReflection entryPoint,
        string name)
    {
        FunctionReflection function = entryPoint.Function;
        if (function == FunctionReflection.Null)
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                $"Slang did not expose function reflection for Work Graph entry point " +
                $"'{WorkGraphValidation.GetEffectiveEntryPointName(entryPoint)}'.");
        }
        IGlobalSession globalSession = program.GetSession().GetGlobalSession();
        return function.FindAttributeByName(globalSession, name) ?? AttributeReflection.Null;
    }

    private static WorkGraphEntryPointState[] ReadWorkGraphEntries(
        ID3D12WorkGraphProperties* graphProperties,
        uint graphIndex,
        uint nativeEntryPointCount,
        IReadOnlyDictionary<(string Name, uint ArrayIndex), EntryPointReflection> reflectedNodes,
        uint maximumInputRecordSize)
    {
        var result = new WorkGraphEntryPointState[checked((int)nativeEntryPointCount)];
        var materialized = new HashSet<uint>();
        for (uint ordinal = 0; ordinal < nativeEntryPointCount; ordinal++)
        {
            NodeID id = graphProperties->GetEntrypointID(graphIndex, ordinal);
            if (id.Name is null)
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    "D3D12 returned a Work Graph entry point without an identity.");
            }
            uint nativeIndex = graphProperties->GetEntrypointIndex(graphIndex, id);
            if (nativeIndex == uint.MaxValue)
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    "D3D12 returned a Work Graph entry point that cannot be indexed.");
            }
            if (!materialized.Add(nativeIndex))
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    "D3D12 returned duplicate Work Graph entry-point indices.");
            }

            (string Name, uint ArrayIndex) nodeID = (new string(id.Name), id.ArrayIndex);
            if (!reflectedNodes.TryGetValue(nodeID, out EntryPointReflection entryPoint))
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"D3D12 returned Work Graph entry '{nodeID.Name}" +
                    $"[{nodeID.ArrayIndex}]', which is not an identity reflected by Slang.");
            }

            uint size = graphProperties->GetEntrypointRecordSizeInBytes(graphIndex, nativeIndex);
            uint alignment = graphProperties->GetEntrypointRecordAlignmentInBytes(graphIndex, nativeIndex);
            RequireValidWorkGraphEntryPointLayout(maximumInputRecordSize, size, alignment);
            result[checked((int)ordinal)] = new WorkGraphEntryPointState(
                entryPoint,
                nativeIndex,
                size,
                alignment);
        }
        return result;
    }

    private static void RequireValidWorkGraphEntryPointLayout(
        uint maximumInputRecordSize,
        uint size,
        uint alignment)
    {
        if (!WorkGraphValidation.IsEntryPointLayoutValid(
                maximumInputRecordSize,
                size,
                alignment))
        {
            throw new GraphicsException(
                GraphicsError.PipelineCreation,
                "D3D12 returned invalid Work Graph entry-point layout data.");
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
        D3D12RootSignatureState global,
        CompiledProgramLibrary library,
        IReadOnlyDictionary<(string Name, uint ArrayIndex), EntryPointReflection> reflectedNodes,
        in WorkGraphPipelineDesc desc)
    {
        (string Name, uint ArrayIndex)[] sortedNodes = [.. reflectedNodes.Keys];
        Array.Sort(sortedNodes, static (left, right) =>
        {
            int name = string.CompareOrdinal(left.Name, right.Name);
            return name != 0 ? name : left.ArrayIndex.CompareTo(right.ArrayIndex);
        });
        uint nodeMask = desc.NodeMask;
        return CreateCanonicalPipelineKey(
            device,
            5,
            writer =>
            {
                writer.Write(1u);
                writer.Write(true);
                WriteCompiledProgramIdentity(writer, library);
            },
            writer =>
            {
                writer.Write(1u);
                WriteCanonicalBytes(writer, global.Serialized);
                writer.Write(0u);
            },
            writer =>
            {
                writer.Write(nodeMask);
                WriteCanonicalString(writer, WorkGraphProgramName);
                writer.Write((byte)WorkGraphFlags.IncludeAllAvailableNodes);
                writer.Write(checked((uint)sortedNodes.Length));
                foreach ((string Name, uint ArrayIndex) node in sortedNodes)
                {
                    WriteCanonicalString(writer, node.Name);
                    writer.Write(node.ArrayIndex);
                }
            });
    }

    private readonly record struct WorkGraphEntryPointState(
        EntryPointReflection EntryPoint,
        uint NativeIndex,
        uint RecordSize,
        uint RecordAlignment);

    private sealed class D3D12WorkGraphPipeline : D3D12Pipeline
    {
        private readonly NativeLease _stateProperties;
        private readonly NativeLease _graphProperties;
        private readonly WorkGraphEntryPointState[] _entryPoints;

        internal D3D12WorkGraphPipeline(
            D3D12Device device,
            NativeLease native,
            D3D12RootSignatureState root,
            NativeLease[] additionalLeases,
            RetainedSlangProgram program,
            in ProgramIdentifier identifier,
            in WorkGraphMemoryRequirements memoryRequirements,
            WorkGraphEntryPointState[] entryPoints,
            string? label)
            : base(
                device,
                native,
                root,
                [],
                additionalLeases,
                program,
                PipelineType.WorkGraph,
                label)
        {
            _stateProperties = additionalLeases[0];
            _graphProperties = additionalLeases[1];
            _entryPoints = entryPoints;
            ProgramIdentifier = identifier;
            MemoryRequirements = memoryRequirements;
        }

        internal ProgramIdentifier ProgramIdentifier { get; }
        internal WorkGraphMemoryRequirements MemoryRequirements { get; }

        internal WorkGraphEntryPointState GetEntryPoint(EntryPointReflection entryPoint)
        {
            if (entryPoint == EntryPointReflection.Null)
                throw new ArgumentException("A Work Graph dispatch requires a Slang entry point.", nameof(entryPoint));
            foreach (ref readonly WorkGraphEntryPointState entry in _entryPoints.AsSpan())
            {
                if (entry.EntryPoint == entryPoint)
                    return entry;
            }
            throw new ArgumentException(
                "The Slang entry point is not a materialized program entry of this Work Graph Pipeline.",
                nameof(entryPoint));
        }

        internal bool TryGetEntryPoints(
            Span<WorkGraphEntryPointInfo> destination,
            out int requiredCount)
        {
            requiredCount = _entryPoints.Length;
            if (destination.Length < requiredCount)
                return false;
            for (int index = 0; index < _entryPoints.Length; index++)
            {
                WorkGraphEntryPointState entry = _entryPoints[index];
                destination[index] = new WorkGraphEntryPointInfo(
                    entry.EntryPoint,
                    entry.RecordSize,
                    entry.RecordAlignment);
            }
            return true;
        }

    }

    private readonly record struct D3D12WorkGraphProgramState(
        D3D12WorkGraphPipeline Pipeline,
        D3D12Buffer? Backing,
        BufferRange Range);

    private sealed partial class D3D12CommandContext
    {
        private D3D12WorkGraphProgramState? _workGraphProgram;

        internal bool WorkGraphProgramMatches(in D3D12WorkGraphProgramState value) =>
            _workGraphProgram is D3D12WorkGraphProgramState current &&
            ReferenceEquals(current.Pipeline, value.Pipeline) &&
            ReferenceEquals(_pipeline, value.Pipeline) &&
            ReferenceEquals(current.Backing, value.Backing) &&
            current.Range == value.Range;

        internal void RememberWorkGraphProgram(in D3D12WorkGraphProgramState value) =>
            _workGraphProgram = value;

        internal D3D12WorkGraphProgramState RequireWorkGraphProgram()
        {
            if (_workGraphProgram is not D3D12WorkGraphProgramState program ||
                _pipeline is not D3D12WorkGraphPipeline selected ||
                !ReferenceEquals(selected, program.Pipeline))
            {
                throw new InvalidOperationException(
                    "DispatchWorkGraph requires as active Work Graph program.");
            }
            return program;
        }

        internal void ResetWorkGraphState() => _workGraphProgram = null;
    }

    private static partial class RequireD3D12
    {
        internal static D3D12WorkGraphPipeline WorkGraphPipeline(Pipeline value) =>
            value as D3D12WorkGraphPipeline ??
            throw new ArgumentException(
                "The Pipeline is not a Direct3D 12 Work Graph pipeline.",
                nameof(value));
    }
}
