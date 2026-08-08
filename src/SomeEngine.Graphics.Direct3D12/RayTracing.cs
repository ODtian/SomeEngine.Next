using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using SlangShaderSharp;
using NativeFormat = Silk.NET.DXGI.Format;
using NativeRange = Silk.NET.Direct3D12.Range;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public AccelerationStructure CreateAccelerationStructure(
        Device device,
        Buffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12Buffer nativeStorage = NativeCast.Buffer(storage);
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));

        BufferRange range = storageRange.Resolve(nativeStorage.Info.Size);
        if ((nativeStorage.Info.Usages & BufferUsages.AccelerationStructure) == 0 ||
            nativeStorage.Info.MemoryType != MemoryType.DeviceLocal ||
            range.Offset % 256 != 0 ||
            range.Size % 256 != 0)
        {
            throw new ArgumentException(
                "Acceleration-structure storage must be an aligned DeviceLocal Buffer range with AccelerationStructure usage.",
                nameof(storageRange));
        }

        AccelerationStructureInfo info = new(type, range.Size, storage, range);
        D3D12AccelerationStructure result = new(nativeDevice, nativeStorage, info, label);
        try
        {
            nativeDevice.RegisterChild(result);
            nativeStorage.RegisterView(result);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public AccelerationStructureSrv CreateAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12AccelerationStructure structure =
            NativeCast.AccelerationStructure(desc.AccelerationStructure);

        DescriptorLease descriptor = nativeDevice.ResourceDescriptors.Allocate();
        D3D12AccelerationStructureSrv? result = null;
        try
        {
            WriteAccelerationStructureSrv(nativeDevice, structure, descriptor.Cpu);
            result = new D3D12AccelerationStructureSrv(
                nativeDevice,
                structure,
                desc,
                descriptor);
            RegisterAccelerationStructureView(nativeDevice, structure, result);
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            throw;
        }
    }

    public BindlessAccelerationStructureSrv CreateBindlessAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12AccelerationStructure structure =
            NativeCast.AccelerationStructure(desc.AccelerationStructure);

        DescriptorLease descriptor = nativeDevice.ResourceDescriptors.Allocate();
        D3D12BindlessAccelerationStructureSrv? result = null;
        DescriptorRange? rangeReservation = null;
        bool staged = false;
        try
        {
            WriteAccelerationStructureSrv(nativeDevice, structure, descriptor.Cpu);
            rangeReservation = nativeDevice.Descriptors.Reserve(DescriptorTableType.Resource, 1);
            result = new D3D12BindlessAccelerationStructureSrv(
                nativeDevice,
                structure,
                desc,
                descriptor,
                rangeReservation.First);
            RegisterAccelerationStructureView(nativeDevice, structure, result);
            nativeDevice.Descriptors.StageDescriptor(rangeReservation, descriptor, result);
            staged = true;
            return result;
        }
        catch
        {
            ReleaseFailedView(result, descriptor);
            if (rangeReservation is not null && !staged)
                nativeDevice.Descriptors.Cancel(rangeReservation);
            throw;
        }
    }

    public AccelerationStructureBuildInfo GetAccelerationStructureBuildInfo(
        Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        BuildRaytracingAccelerationStructureInputs inputs = CreateBuildInputs(
            nativeDevice,
            type,
            options,
            geometries,
            out RaytracingGeometryDesc[] nativeGeometries);
        RaytracingAccelerationStructurePrebuildInfo info = default;
        fixed (RaytracingGeometryDesc* geometryPointer = nativeGeometries)
        {
            if (nativeGeometries.Length != 0)
                inputs.PGeometryDescs = geometryPointer;
            nativeDevice.Native->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
        }
        if (info.ResultDataMaxSizeInBytes == 0 || info.ScratchDataSizeInBytes == 0)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "D3D12 returned empty acceleration-structure prebuild requirements.");
        }

        return new AccelerationStructureBuildInfo(
            info.ResultDataMaxSizeInBytes,
            256,
            info.ScratchDataSizeInBytes,
            256,
            info.UpdateScratchDataSizeInBytes,
            256);
    }

    public Pipeline CreateRayTracingPipeline(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        ArgumentNullException.ThrowIfNull(desc.Program);
        IComponentType program = desc.Program;
        if (desc.RayGeneration.IsEmpty)
            throw new ArgumentException("A ray-tracing pipeline requires a ray-generation export.", nameof(desc));
        if (desc.NodeMask == 0 || (desc.NodeMask & ~nativeDevice.EnabledNodeMask) != 0 ||
            !Enum.IsDefined(desc.Options))
        {
            throw new ArgumentOutOfRangeException(nameof(desc));
        }

        ShaderReflection reflection = GetProgramReflection(program);
        CompiledProgramLibrary library = CompileProgramLibrary(program);
        Dictionary<string, EntryPointReflection> shaderExports = new(StringComparer.Ordinal);
        List<D3D12RayTracingExport> records = [];
        List<D3D12RootLayout> localRoots = [];
        HashSet<string> stateObjectNames = new(StringComparer.Ordinal);

        AddEntries(desc.RayGeneration, SlangStage.RayGeneration, RayRecordType.RayGeneration, "ray-generation");
        AddEntries(desc.Miss, SlangStage.Miss, RayRecordType.Miss, "miss");
        AddEntries(desc.Callable, SlangStage.Callable, RayRecordType.Callable, "callable");

        List<RayTracingHitGroupState> hitGroups = [];
        foreach (ref readonly RayTracingHitGroup hitGroup in desc.HitGroups)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hitGroup.Name);
            if (!stateObjectNames.Add(hitGroup.Name))
                throw new ArgumentException("Ray-tracing state-object export names must be unique.", nameof(desc));

            List<EntryPointReflection> members = [];
            string? closest = AddHitMember(hitGroup.ClosestHit, SlangStage.ClosestHit, "closest-hit", members);
            string? anyHit = AddHitMember(hitGroup.AnyHit, SlangStage.AnyHit, "any-hit", members);
            string? intersection = AddHitMember(
                hitGroup.Intersection,
                SlangStage.Intersection,
                "intersection",
                members);
            if (members.Count == 0)
                throw new ArgumentException("A ray-tracing hit group must contain at least one shader.", nameof(desc));

            VariableLayoutReflection layout = members[0].VarLayout;
            D3D12RootLayout local = D3D12RootLayoutBuilder.CompileLocal(
                this,
                nativeDevice,
                desc.Program,
                reflection,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(members),
                PipelineType.RayTracing);
            localRoots.Add(local);
            records.Add(new D3D12RayTracingExport(
                hitGroup.Name,
                EntryPointReflection.Null,
                layout,
                local,
                RayRecordType.Hit));
            hitGroups.Add(new RayTracingHitGroupState(
                hitGroup.Name,
                closest,
                anyHit,
                intersection,
                intersection is null ? HitGroupType.Triangles : HitGroupType.ProceduralPrimitive));
        }

        D3D12RootLayout global = D3D12RootLayoutBuilder.CompileGlobal(
            this,
            nativeDevice,
            desc.Program,
            reflection,
            PipelineType.RayTracing);
        ID3D12StateObject* stateObject = null;
        ID3D12StateObjectProperties* properties = null;
        D3D12RayTracingPipeline? result = null;
        try
        {
            using NativeStateObjectArena arena = new();
            int subobjectCount = checked(5 + hitGroups.Count + (records.Count * 2));
            StateSubobject* subobjects = arena.Allocate<StateSubobject>(subobjectCount);
            int ordinal = 0;

            ExportDesc* exports = arena.Allocate<ExportDesc>(shaderExports.Count);
            int exportOrdinal = 0;
            foreach ((string name, _) in shaderExports.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                exports[exportOrdinal++] = new ExportDesc(
                    arena.String(name),
                    null,
                    ExportFlags.None);
            }
            DxilLibraryDesc* libraryDescription = arena.Allocate<DxilLibraryDesc>();
            fixed (byte* code = library.Code)
            {
                *libraryDescription = new DxilLibraryDesc(
                    new ShaderBytecode(code, (nuint)library.Code.Length),
                    checked((uint)shaderExports.Count),
                    exports);
                subobjects[ordinal++] = new StateSubobject(
                    StateSubobjectType.DxilLibrary,
                    libraryDescription);

                GlobalRootSignature* globalDescription = arena.Allocate<GlobalRootSignature>();
                globalDescription->PGlobalRootSignature = global.Native;
                subobjects[ordinal++] = new StateSubobject(
                    StateSubobjectType.GlobalRootSignature,
                    globalDescription);

                foreach (RayTracingHitGroupState hitGroup in hitGroups)
                {
                    HitGroupDesc* nativeHitGroup = arena.Allocate<HitGroupDesc>();
                    nativeHitGroup->HitGroupExport = arena.String(hitGroup.Name);
                    nativeHitGroup->Type = hitGroup.Type;
                    nativeHitGroup->ClosestHitShaderImport =
                        hitGroup.ClosestHit is null ? null : arena.String(hitGroup.ClosestHit);
                    nativeHitGroup->AnyHitShaderImport =
                        hitGroup.AnyHit is null ? null : arena.String(hitGroup.AnyHit);
                    nativeHitGroup->IntersectionShaderImport =
                        hitGroup.Intersection is null ? null : arena.String(hitGroup.Intersection);
                    subobjects[ordinal++] = new StateSubobject(
                        StateSubobjectType.HitGroup,
                        nativeHitGroup);
                }

                foreach (D3D12RayTracingExport record in records)
                {
                    LocalRootSignature* localDescription = arena.Allocate<LocalRootSignature>();
                    localDescription->PLocalRootSignature = record.LocalRoot.Native;
                    int localOrdinal = ordinal++;
                    subobjects[localOrdinal] = new StateSubobject(
                        StateSubobjectType.LocalRootSignature,
                        localDescription);

                    char** associatedExports = (char**)arena.Allocate<nint>();
                    *associatedExports = arena.String(record.Name);
                    SubobjectToExportsAssociation* association =
                        arena.Allocate<SubobjectToExportsAssociation>();
                    association->PSubobjectToAssociate = subobjects + localOrdinal;
                    association->NumExports = 1;
                    association->PExports = associatedExports;
                    subobjects[ordinal++] = new StateSubobject(
                        StateSubobjectType.SubobjectToExportsAssociation,
                        association);
                }

                RaytracingShaderConfig* shaderConfig = arena.Allocate<RaytracingShaderConfig>();
                shaderConfig->MaxPayloadSizeInBytes = desc.MaximumPayloadSize;
                shaderConfig->MaxAttributeSizeInBytes = desc.MaximumAttributeSize;
                subobjects[ordinal++] = new StateSubobject(
                    StateSubobjectType.RaytracingShaderConfig,
                    shaderConfig);

                RaytracingPipelineConfig1* pipelineConfig = arena.Allocate<RaytracingPipelineConfig1>();
                pipelineConfig->MaxTraceRecursionDepth = desc.MaximumRecursionDepth;
                pipelineConfig->Flags = ToNativePipelineFlags(desc.Options);
                subobjects[ordinal++] = new StateSubobject(
                    StateSubobjectType.RaytracingPipelineConfig1,
                    pipelineConfig);

                uint* nodeMask = arena.Allocate<uint>();
                *nodeMask = desc.NodeMask;
                subobjects[ordinal++] = new StateSubobject(
                    StateSubobjectType.NodeMask,
                    nodeMask);
                if (ordinal != subobjectCount)
                    throw new InvalidOperationException("The ray-tracing state-object layout is incomplete.");

                StateObjectDesc native = new(
                    StateObjectType.RaytracingPipeline,
                    checked((uint)subobjectCount),
                    subobjects);
                Guid iid = ID3D12StateObject.Guid;
                NativeCall.ThrowIfFailed(
                    nativeDevice.Native->CreateStateObject(&native, &iid, (void**)&stateObject),
                    "ID3D12Device5::CreateStateObject(ray tracing)");
            }

            Guid propertiesIid = ID3D12StateObjectProperties.Guid;
            NativeCall.ThrowIfFailed(
                stateObject->QueryInterface(&propertiesIid, (void**)&properties),
                "ID3D12StateObject::QueryInterface(ID3D12StateObjectProperties)");
            foreach (D3D12RayTracingExport record in records)
            {
                void* identifier = properties->GetShaderIdentifier(record.Name);
                if (identifier is null)
                {
                    throw new GraphicsException(
                        GraphicsError.PipelineCreation,
                        $"D3D12 did not materialize shader identifier '{record.Name}'.");
                }
                record.Identifier = new ReadOnlySpan<byte>(identifier, 32).ToArray();
            }

            byte[] key = CreateRayTracingPipelineKey(
                nativeDevice,
                global,
                localRoots.ToArray(),
                library,
                records,
                hitGroups,
                desc);
            nativeCache?.Store(4, key, library.Hash);
            result = new D3D12RayTracingPipeline(
                nativeDevice,
                stateObject,
                properties,
                global,
                localRoots.ToArray(),
                records,
                ToPipelineSignature(key),
                desc.Label);
            stateObject = null;
            properties = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
            {
                if (properties is not null)
                    _ = properties->Release();
                if (stateObject is not null)
                    _ = stateObject->Release();
                foreach (D3D12RootLayout root in localRoots)
                    root.Release();
                global.Release();
            }
            else
            {
                result.Dispose();
            }
            throw;
        }

        void AddEntries(
            ReadOnlySpan<EntryPointReflection> entries,
            SlangStage stage,
            RayRecordType type,
            string role)
        {
            foreach (EntryPointReflection entry in entries)
            {
                string name = AddShader(entry, stage, role);
                D3D12RootLayout local = D3D12RootLayoutBuilder.CompileLocal(
                    this,
                    nativeDevice,
                    program,
                    reflection,
                    [entry],
                    PipelineType.RayTracing);
                localRoots.Add(local);
                records.Add(new D3D12RayTracingExport(
                    name,
                    entry,
                    entry.VarLayout,
                    local,
                    type));
            }
        }

        string AddShader(EntryPointReflection entry, SlangStage stage, string role)
        {
            string name = ValidateStateObjectEntryPoint(reflection, entry, [stage], role);
            if (shaderExports.TryGetValue(name, out EntryPointReflection existing) && existing != entry)
                throw new ArgumentException("Two Slang entry points use the same state-object export name.", nameof(desc));
            shaderExports[name] = entry;
            if (!stateObjectNames.Add(name) && !records.Any(record => record.EntryPoint == entry))
                throw new ArgumentException("Ray-tracing state-object export names must be unique.", nameof(desc));
            return name;
        }

        string? AddHitMember(
            EntryPointReflection entry,
            SlangStage stage,
            string role,
            List<EntryPointReflection> members)
        {
            if (entry == EntryPointReflection.Null)
                return null;
            members.Add(entry);
            return AddShader(entry, stage, role);
        }
    }

    public RayTracingShaderTable CreateRayTracingShaderTable(
        Device device,
        in RayTracingShaderTableDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12RayTracingPipeline pipeline = NativeCast.RayTracingPipeline(desc.Pipeline);
        if (desc.RayGenerationRecordCount != 1)
            throw new ArgumentOutOfRangeException(nameof(desc), "D3D12 dispatch requires exactly one ray-generation record.");
        if (desc.MaximumRecordSize < 32 ||
            desc.MaximumRecordSize > 4_096 ||
            desc.MaximumRecordSize % 32 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desc), "MaximumRecordSize must be a 32-byte-aligned shader-record capacity.");
        }
        if (pipeline.MaximumLocalRecordSize > desc.MaximumRecordSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(desc),
                "MaximumRecordSize is smaller than a Pipeline local-root record.");
        }
        _ = GetRayTableSize(desc);
        D3D12RayTracingShaderTable result = new(nativeDevice, pipeline, desc);
        nativeDevice.RegisterChild(result);
        return result;
    }

    public void UpdateRayTracingShaderTable(
        CommandContext context,
        RayTracingShaderTable table,
        in RayTracingShaderTableUpdate update)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12RayTracingShaderTable nativeTable = NativeCast.RayTracingShaderTable(table);

        D3D12RayTableSnapshot snapshot = command.CaptureRayTracingSnapshot(nativeTable, update);
        command.CaptureObject(nativeTable);
        command.CapturePipelineArtifact(nativeTable.Pipeline);
        command.PrepareTransientBindingCaptures(snapshot.Resources);
        foreach (ResourceBinding binding in snapshot.Resources)
            command.Capture(binding);
        _ = snapshot.Materialize(command, nativeTable);
    }

    public void DispatchRays(CommandContext context, in DispatchRaysDesc desc)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12RayTracingShaderTable table = NativeCast.RayTracingShaderTable(desc.ShaderTable);
        if (desc.Width == 0 || desc.Height == 0 || desc.Depth == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));

        D3D12RayTableMaterialization materialization =
            command.MaterializeRayTracingTable(table);
        Silk.NET.Direct3D12.DispatchRaysDesc native = materialization.Description;
        native.Width = desc.Width;
        native.Height = desc.Height;
        native.Depth = desc.Depth;
        command.CaptureObject(table);
        command.CapturePipelineArtifact(table.Pipeline);
        command.List->DispatchRays(&native);
    }

    public void DispatchRaysIndirect(
        CommandContext context,
        RayTracingShaderTable table,
        in BufferRegion arguments)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12RayTracingShaderTable nativeTable = NativeCast.RayTracingShaderTable(table);
        _ = command.MaterializeRayTracingTable(nativeTable);

        D3D12Buffer buffer = NativeCast.Buffer(arguments.Buffer);
        BufferRange range = arguments.Range.Resolve(buffer.Info.Size);
        if ((buffer.Info.Usages & BufferUsages.Indirect) == 0 ||
            range.Offset % 8 != 0 ||
            range.Size < (ulong)sizeof(Silk.NET.Direct3D12.DispatchRaysDesc))
        {
            throw new ArgumentException(
                "Indirect ray arguments must name an aligned native DispatchRaysDesc in an Indirect Buffer.",
                nameof(arguments));
        }

        command.Capture(buffer);
        command.CaptureObject(nativeTable);
        command.CapturePipelineArtifact(nativeTable.Pipeline);
        command.List->ExecuteIndirect(
            command.NativeDevice.RayDispatchSignature,
            1,
            buffer.Native,
            range.Offset,
            null,
            0);
    }

    private static ulong GetRayTableSize(
        in RayTracingShaderTableDesc desc)
    {
        ulong stride = desc.MaximumRecordSize;
        ulong result = 0;
        Add(desc.RayGenerationRecordCount);
        Add(desc.MissRecordCount);
        Add(desc.HitRecordCount);
        Add(desc.CallableRecordCount);
        return result;

        void Add(uint count)
        {
            if (count == 0)
                return;
            result = AlignUp(result, 64);
            result = checked(result + checked((ulong)count * stride));
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));
        return checked((value + alignment - 1) & ~(alignment - 1));
    }

    public void BuildAccelerationStructure(
        CommandContext context,
        in AccelerationStructureBuildDesc desc)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);

        D3D12AccelerationStructure destination =
            NativeCast.AccelerationStructure(desc.Destination);

        bool update = (desc.Options & AccelerationStructureBuildOptions.PerformUpdate) != 0;
        D3D12AccelerationStructure? source = desc.Source is null
            ? null
            : NativeCast.AccelerationStructure(desc.Source);

        D3D12Buffer scratch = NativeCast.Buffer(desc.Scratch);
        BufferRange scratchRange = desc.ScratchRange.Resolve(scratch.Info.Size);
        if ((scratch.Info.Usages & BufferUsages.ShaderWrite) == 0 ||
            scratchRange.Offset % 256 != 0)
        {
            throw new ArgumentException(
                "Acceleration-structure scratch storage must be an aligned ShaderWrite Buffer range.",
                nameof(desc));
        }

        BuildRaytracingAccelerationStructureInputs inputs = CreateBuildInputs(
            command.NativeDevice,
            desc.Type,
            desc.Options,
            desc.Geometries,
            out RaytracingGeometryDesc[] nativeGeometries);
        RaytracingAccelerationStructurePrebuildInfo requirements = default;
        fixed (RaytracingGeometryDesc* geometryPointer = nativeGeometries)
        {
            if (nativeGeometries.Length != 0)
                inputs.PGeometryDescs = geometryPointer;
            command.NativeDevice.Native->GetRaytracingAccelerationStructurePrebuildInfo(
                &inputs,
                &requirements);
            ulong requiredScratch = update
                ? requirements.UpdateScratchDataSizeInBytes
                : requirements.ScratchDataSizeInBytes;
            if (destination.Info.Size < requirements.ResultDataMaxSizeInBytes ||
                scratchRange.Size < requiredScratch)
            {
                throw new ArgumentException(
                    "The destination or scratch range is smaller than the native build requirements.",
                    nameof(desc));
            }

            BuildRaytracingAccelerationStructureDesc native = new()
            {
                DestAccelerationStructureData = destination.Address,
                Inputs = inputs,
                SourceAccelerationStructureData = source?.Address ?? 0,
                ScratchAccelerationStructureData =
                    scratch.Native->GetGPUVirtualAddress() + scratchRange.Offset,
            };
            command.Capture(destination);
            if (source is not null)
                command.Capture(source);
            command.Capture(scratch);
            CaptureGeometryResources(command, desc.Geometries);
            command.List->BuildRaytracingAccelerationStructure(&native, 0, null);
        }
    }

    public void CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12AccelerationStructure nativeDestination = NativeCast.AccelerationStructure(destination);
        D3D12AccelerationStructure nativeSource = NativeCast.AccelerationStructure(source);

        command.Capture(nativeDestination);
        command.Capture(nativeSource);
        command.List->CopyRaytracingAccelerationStructure(
            nativeDestination.Address,
            nativeSource.Address,
            ToNativeCopyMode(type));
    }

    public void SerializeAccelerationStructure(
        CommandContext context,
        in BufferRegion destination,
        AccelerationStructure source)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12Buffer nativeDestination = NativeCast.Buffer(destination.Buffer);
        D3D12AccelerationStructure nativeSource = NativeCast.AccelerationStructure(source);
        BufferRange destinationRange = destination.Range.Resolve(nativeDestination.Info.Size);
        if (nativeDestination.Info.MemoryType != MemoryType.DeviceLocal ||
            (nativeDestination.Info.Usages & BufferUsages.ShaderWrite) == 0 ||
            destinationRange.Offset % 256 != 0)
        {
            throw new ArgumentException(
                "Serialized acceleration-structure output requires an aligned, non-empty " +
                "DeviceLocal ShaderWrite Buffer range.",
                nameof(destination));
        }
        command.Capture(nativeDestination);
        command.Capture(nativeSource);
        command.List->CopyRaytracingAccelerationStructure(
            nativeDestination.Native->GetGPUVirtualAddress() + destinationRange.Offset,
            nativeSource.Address,
            RaytracingAccelerationStructureCopyMode.Serialize);
    }

    public void DeserializeAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        in BufferRegion source)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12AccelerationStructure nativeDestination =
            NativeCast.AccelerationStructure(destination);
        D3D12Buffer nativeSource = NativeCast.Buffer(source.Buffer);
        BufferRange sourceRange = source.Range.Resolve(nativeSource.Info.Size);
        if ((nativeSource.Info.Usages & BufferUsages.ShaderRead) == 0 ||
            sourceRange.Offset % 256 != 0)
        {
            throw new ArgumentException(
                "Serialized acceleration-structure input requires an aligned, non-empty " +
                "ShaderRead Buffer range.",
                nameof(source));
        }
        command.Capture(nativeDestination);
        command.Capture(nativeSource);
        command.List->CopyRaytracingAccelerationStructure(
            nativeDestination.Address,
            nativeSource.Native->GetGPUVirtualAddress() + sourceRange.Offset,
            RaytracingAccelerationStructureCopyMode.Deserialize);
    }

    public void EmitAccelerationStructurePostBuildInfo(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        Buffer destination,
        ulong destinationOffset)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12AccelerationStructure nativeSource = NativeCast.AccelerationStructure(source);
        D3D12Buffer nativeDestination = NativeCast.Buffer(destination);
        ulong resultSize = type == AccelerationStructurePostBuildInfoType.SerializationSize
            ? 2UL * sizeof(ulong)
            : sizeof(ulong);
        if (nativeDestination.Info.MemoryType != MemoryType.DeviceLocal ||
            (nativeDestination.Info.Usages & BufferUsages.ShaderWrite) == 0 ||
            destinationOffset % sizeof(ulong) != 0 ||
            nativeDestination.Info.Size < resultSize ||
            destinationOffset > nativeDestination.Info.Size - resultSize)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationOffset));
        }

        RaytracingAccelerationStructurePostbuildInfoDesc native = new()
        {
            DestBuffer = nativeDestination.Native->GetGPUVirtualAddress() + destinationOffset,
            InfoType = ToNativePostBuildInfo(type),
        };
        ulong address = nativeSource.Address;
        command.Capture(nativeSource);
        command.Capture(nativeDestination);
        command.List->EmitRaytracingAccelerationStructurePostbuildInfo(&native, 1, &address);
    }

    private static BuildRaytracingAccelerationStructureInputs CreateBuildInputs(
        D3D12Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries,
        out RaytracingGeometryDesc[] nativeGeometries)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        if (geometries.IsEmpty)
            throw new ArgumentException("An acceleration-structure build requires geometry.", nameof(geometries));

        BuildRaytracingAccelerationStructureInputs result = new()
        {
            Type = type == AccelerationStructureType.BottomLevel
                ? RaytracingAccelerationStructureType.BottomLevel
                : RaytracingAccelerationStructureType.TopLevel,
            Flags = ToNativeBuildFlags(options),
            DescsLayout = ElementsLayout.Array,
        };

        if (type == AccelerationStructureType.TopLevel)
        {
            if (geometries.Length != 1 ||
                geometries[0].Type != AccelerationStructureGeometryType.Instances)
            {
                throw new ArgumentException(
                    "A top-level build requires exactly one Instances geometry.",
                    nameof(geometries));
            }
            ref readonly AccelerationStructureGeometry geometry = ref geometries[0];
            D3D12Buffer instances = ResolveInputRegion(
                device,
                geometry.Primary,
                checked((ulong)geometry.Count * 64),
                16,
                nameof(geometries),
                out BufferRange range);
            result.NumDescs = geometry.Count;
            result.InstanceDescs = instances.Native->GetGPUVirtualAddress() + range.Offset;
            nativeGeometries = [];
            return result;
        }

        nativeGeometries = new RaytracingGeometryDesc[geometries.Length];
        for (int index = 0; index < geometries.Length; index++)
        {
            ref readonly AccelerationStructureGeometry geometry = ref geometries[index];
            if (geometry.Count == 0)
            {
                throw new ArgumentException("A bottom-level geometry description is invalid.", nameof(geometries));
            }

            RaytracingGeometryDesc native = new()
            {
                Type = geometry.Type switch
                {
                    AccelerationStructureGeometryType.Triangles =>
                        RaytracingGeometryType.Triangles,
                    AccelerationStructureGeometryType.AxisAlignedBoundingBoxes =>
                        RaytracingGeometryType.ProceduralPrimitiveAabbs,
                    _ => throw new ArgumentOutOfRangeException(nameof(geometries)),
                },
                Flags = ToNativeGeometryFlags(geometry.Options),
            };
            if (geometry.Type == AccelerationStructureGeometryType.Triangles)
            {
                (uint positionSize, uint componentAlignment) =
                    GetRayTracingVertexLayout(geometry.PrimaryFormat);
                if (geometry.PrimaryStride < positionSize ||
                    geometry.PrimaryStride % componentAlignment != 0)
                {
                    throw new ArgumentException("A triangle vertex format or stride is invalid.", nameof(geometries));
                }
                D3D12Buffer vertices = ResolveInputRegion(
                    device,
                    geometry.Primary,
                    checked((ulong)geometry.Count * geometry.PrimaryStride),
                    componentAlignment,
                    nameof(geometries),
                    out BufferRange vertexRange);
                RaytracingGeometryTrianglesDesc triangles = new()
                {
                    VertexFormat = FormatMappings.ToDxgi(geometry.PrimaryFormat),
                    VertexCount = geometry.Count,
                    VertexBuffer = new GpuVirtualAddressAndStride(
                        vertices.Native->GetGPUVirtualAddress() + vertexRange.Offset,
                        geometry.PrimaryStride),
                    IndexFormat = NativeFormat.FormatUnknown,
                };

                if (geometry.Secondary.Buffer is not null)
                {
                    uint indexSize = geometry.IndexType switch
                    {
                        IndexType.UInt16 => 2,
                        IndexType.UInt32 => 4,
                        _ => throw new ArgumentOutOfRangeException(nameof(geometries)),
                    };
                    D3D12Buffer indices = ResolveInputRegion(
                        device,
                        geometry.Secondary,
                        indexSize,
                        indexSize,
                        nameof(geometries),
                        out BufferRange indexRange);
                    if (indexRange.Size % indexSize != 0 || indexRange.Size / indexSize > uint.MaxValue)
                        throw new ArgumentException("The triangle index range is invalid.", nameof(geometries));
                    triangles.IndexFormat = geometry.IndexType switch
                    {
                        IndexType.UInt16 => NativeFormat.FormatR16Uint,
                        IndexType.UInt32 => NativeFormat.FormatR32Uint,
                        _ => throw new ArgumentOutOfRangeException(nameof(geometries)),
                    };
                    triangles.IndexCount = checked((uint)(indexRange.Size / indexSize));
                    triangles.IndexBuffer = indices.Native->GetGPUVirtualAddress() + indexRange.Offset;
                }
                if (geometry.Transform.Buffer is not null)
                {
                    D3D12Buffer transform = ResolveInputRegion(
                        device,
                        geometry.Transform,
                        48,
                        16,
                        nameof(geometries),
                        out BufferRange transformRange);
                    triangles.Transform3x4 =
                        transform.Native->GetGPUVirtualAddress() + transformRange.Offset;
                }
                native.Triangles = triangles;
            }
            else
            {
                if (geometry.PrimaryStride < 24 || geometry.PrimaryStride % 8 != 0)
                    throw new ArgumentException("An AABB stride must be at least 24 bytes and 8-byte aligned.", nameof(geometries));
                D3D12Buffer aabbs = ResolveInputRegion(
                    device,
                    geometry.Primary,
                    checked((ulong)geometry.Count * geometry.PrimaryStride),
                    8,
                    nameof(geometries),
                    out BufferRange aabbRange);
                native.AABBs = new RaytracingGeometryAabbsDesc(
                    geometry.Count,
                    new GpuVirtualAddressAndStride(
                        aabbs.Native->GetGPUVirtualAddress() + aabbRange.Offset,
                        geometry.PrimaryStride));
            }
            nativeGeometries[index] = native;
        }
        result.NumDescs = checked((uint)nativeGeometries.Length);
        return result;
    }

    private static D3D12Buffer ResolveInputRegion(
        D3D12Device device,
        in BufferRegion region,
        ulong minimumSize,
        ulong alignment,
        string parameter,
        out BufferRange range)
    {
        D3D12Buffer buffer = NativeCast.Buffer(region.Buffer);
        range = region.Range.Resolve(buffer.Info.Size);
        if ((buffer.Info.Usages & BufferUsages.AccelerationStructureInput) == 0 ||
            range.Size < minimumSize || range.Offset % alignment != 0)
        {
            throw new ArgumentException(
                "An acceleration-structure input Buffer range has invalid usage, size, or alignment.",
                parameter);
        }
        return buffer;
    }

    private static void CaptureGeometryResources(
        D3D12CommandContext command,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
        foreach (ref readonly AccelerationStructureGeometry geometry in geometries)
        {
            command.Capture(NativeCast.Buffer(geometry.Primary.Buffer));
            if (geometry.Secondary.Buffer is not null)
                command.Capture(NativeCast.Buffer(geometry.Secondary.Buffer));
            if (geometry.Transform.Buffer is not null)
                command.Capture(NativeCast.Buffer(geometry.Transform.Buffer));
        }
    }

    private static (uint PositionSize, uint ComponentAlignment)
        GetRayTracingVertexLayout(Format format) => format switch
    {
        Format.R32G32Float => (8, 4),
        Format.R32G32B32Float => (12, 4),
        Format.R16G16Float or Format.R16G16SNorm => (4, 2),
        Format.R16G16B16A16Float or Format.R16G16B16A16SNorm => (6, 2),
        Format.R16G16UNorm => (4, 2),
        Format.R16G16B16A16UNorm => (6, 2),
        Format.R10G10B10A2UNorm => (4, 4),
        Format.R8G8UNorm or Format.R8G8SNorm => (2, 1),
        Format.R8G8B8A8UNorm or Format.R8G8B8A8SNorm => (3, 1),
        _ => throw new ArgumentException(
            $"{format} is not a D3D12 ray-tracing vertex format.",
            nameof(format)),
    };

    private static RaytracingAccelerationStructureBuildFlags ToNativeBuildFlags(
        AccelerationStructureBuildOptions value)
    {
        const AccelerationStructureBuildOptions supported =
            AccelerationStructureBuildOptions.AllowUpdate |
            AccelerationStructureBuildOptions.AllowCompaction |
            AccelerationStructureBuildOptions.PreferFastTrace |
            AccelerationStructureBuildOptions.PreferFastBuild |
            AccelerationStructureBuildOptions.MinimizeMemory |
            AccelerationStructureBuildOptions.PerformUpdate;
        if ((value & ~supported) != 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        RaytracingAccelerationStructureBuildFlags result =
            RaytracingAccelerationStructureBuildFlags.None;
        if ((value & AccelerationStructureBuildOptions.AllowUpdate) != 0)
            result |= RaytracingAccelerationStructureBuildFlags.AllowUpdate;
        if ((value & AccelerationStructureBuildOptions.AllowCompaction) != 0)
            result |= RaytracingAccelerationStructureBuildFlags.AllowCompaction;
        if ((value & AccelerationStructureBuildOptions.PreferFastTrace) != 0)
            result |= RaytracingAccelerationStructureBuildFlags.PreferFastTrace;
        if ((value & AccelerationStructureBuildOptions.PreferFastBuild) != 0)
            result |= RaytracingAccelerationStructureBuildFlags.PreferFastBuild;
        if ((value & AccelerationStructureBuildOptions.MinimizeMemory) != 0)
            result |= RaytracingAccelerationStructureBuildFlags.MinimizeMemory;
        if ((value & AccelerationStructureBuildOptions.PerformUpdate) != 0)
            result |= RaytracingAccelerationStructureBuildFlags.PerformUpdate;
        return result;
    }

    private static RaytracingPipelineFlags ToNativePipelineFlags(
        RayTracingPipelineOptions value)
    {
        const RayTracingPipelineOptions supported =
            RayTracingPipelineOptions.SkipTriangles |
            RayTracingPipelineOptions.SkipProceduralPrimitives;
        if ((value & ~supported) != 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        RaytracingPipelineFlags result = RaytracingPipelineFlags.None;
        if ((value & RayTracingPipelineOptions.SkipTriangles) != 0)
            result |= RaytracingPipelineFlags.SkipTriangles;
        if ((value & RayTracingPipelineOptions.SkipProceduralPrimitives) != 0)
            result |= RaytracingPipelineFlags.SkipProceduralPrimitives;
        return result;
    }

    private static byte[] CreateRayTracingPipelineKey(
        D3D12Device device,
        D3D12RootLayout global,
        IReadOnlyList<D3D12RootLayout> localRoots,
        CompiledProgramLibrary library,
        IReadOnlyList<D3D12RayTracingExport> exports,
        IReadOnlyList<RayTracingHitGroupState> hitGroups,
        in RayTracingPipelineDesc desc)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(RootLayoutSchemaVersion);
            writer.Write((byte)4);
            writer.Write(device.EnabledNodeMask);
            writer.Write(desc.NodeMask);
            writer.Write(library.Hash);
            writer.Write(global.Serialized.Length);
            writer.Write(global.Serialized);
            writer.Write(localRoots.Count);
            foreach (D3D12RootLayout root in localRoots)
            {
                writer.Write(root.Serialized.Length);
                writer.Write(root.Serialized);
            }
            writer.Write(exports.Count);
            foreach (D3D12RayTracingExport value in exports)
            {
                writer.Write((byte)value.Type);
                writer.Write(value.Name);
            }
            writer.Write(hitGroups.Count);
            foreach (RayTracingHitGroupState value in hitGroups)
            {
                writer.Write(value.Name);
                writer.Write(value.ClosestHit ?? string.Empty);
                writer.Write(value.AnyHit ?? string.Empty);
                writer.Write(value.Intersection ?? string.Empty);
                writer.Write((int)value.Type);
            }
            writer.Write(desc.MaximumRecursionDepth);
            writer.Write(desc.MaximumPayloadSize);
            writer.Write(desc.MaximumAttributeSize);
            writer.Write((byte)desc.Options);
        }
        return System.Security.Cryptography.SHA256.HashData(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static RaytracingGeometryFlags ToNativeGeometryFlags(
        AccelerationStructureGeometryOptions value)
    {
        const AccelerationStructureGeometryOptions supported =
            AccelerationStructureGeometryOptions.Opaque |
            AccelerationStructureGeometryOptions.NoDuplicateAnyHitInvocation;
        if ((value & ~supported) != 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        RaytracingGeometryFlags result = RaytracingGeometryFlags.None;
        if ((value & AccelerationStructureGeometryOptions.Opaque) != 0)
            result |= RaytracingGeometryFlags.Opaque;
        if ((value & AccelerationStructureGeometryOptions.NoDuplicateAnyHitInvocation) != 0)
            result |= RaytracingGeometryFlags.NoDuplicateAnyhitInvocation;
        return result;
    }

    private static RaytracingAccelerationStructureCopyMode ToNativeCopyMode(
        AccelerationStructureCopyType value) => value switch
    {
        AccelerationStructureCopyType.Clone => RaytracingAccelerationStructureCopyMode.Clone,
        AccelerationStructureCopyType.Compact => RaytracingAccelerationStructureCopyMode.Compact,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static RaytracingAccelerationStructurePostbuildInfoType ToNativePostBuildInfo(
        AccelerationStructurePostBuildInfoType value) => value switch
    {
        AccelerationStructurePostBuildInfoType.CompactedSize =>
            RaytracingAccelerationStructurePostbuildInfoType.CompactedSize,
        AccelerationStructurePostBuildInfoType.SerializationSize =>
            RaytracingAccelerationStructurePostbuildInfoType.Serialization,
        AccelerationStructurePostBuildInfoType.CurrentSize =>
            RaytracingAccelerationStructurePostbuildInfoType.CurrentSize,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static void WriteAccelerationStructureSrv(
        D3D12Device device,
        D3D12AccelerationStructure structure,
        CpuDescriptorHandle destination)
    {
        ShaderResourceViewDesc native = new()
        {
            Format = NativeFormat.FormatUnknown,
            ViewDimension = SrvDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = 5768,
        };
        native.RaytracingAccelerationStructure = new RaytracingAccelerationStructureSrv(
            structure.Address);
        device.Native->CreateShaderResourceView(null, &native, destination);
    }

    private static void RegisterAccelerationStructureView(
        D3D12Device device,
        D3D12AccelerationStructure structure,
        GraphicsObject view)
    {
        device.RegisterChild(view);
        structure.RegisterView(view);
    }

    private sealed class D3D12AccelerationStructure : AccelerationStructure
    {
        private readonly D3D12Device _device;
        private readonly D3D12Buffer _storage;
        private readonly ChildRegistry _views = new();
        private int _released;

        internal D3D12AccelerationStructure(
            D3D12Device device,
            D3D12Buffer storage,
            in AccelerationStructureInfo info,
            string? label)
            : base(device, info, label)
        {
            _device = device;
            _storage = storage;
        }

        internal ulong Address =>
            _storage.Native->GetGPUVirtualAddress() + Info.StorageRange.Offset;
        internal D3D12Buffer Storage => _storage;
        internal NativeLease NativeLifetime => _storage.NativeLifetime;
        internal void RegisterView(GraphicsObject view) => _views.Register(this, view);
        internal void UnregisterView(GraphicsObject view) => _views.Unregister(view);

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _views.DisposeAll();
            _storage.UnregisterView(this);
            _device.UnregisterChild(this);
        }
    }

    private enum RayRecordType : byte
    {
        RayGeneration,
        Miss,
        Hit,
        Callable,
    }

    private sealed class D3D12RayTracingExport
    {
        internal D3D12RayTracingExport(
            string name,
            EntryPointReflection entryPoint,
            VariableLayoutReflection layout,
            D3D12RootLayout localRoot,
            RayRecordType type)
        {
            Name = name;
            EntryPoint = entryPoint;
            Layout = layout;
            LocalRoot = localRoot;
            Type = type;
        }

        internal string Name { get; }
        internal EntryPointReflection EntryPoint { get; }
        internal VariableLayoutReflection Layout { get; }
        internal D3D12RootLayout LocalRoot { get; }
        internal RayRecordType Type { get; }
        internal byte[] Identifier { get; set; } = [];
    }

    private sealed record RayTracingHitGroupState(
        string Name,
        string? ClosestHit,
        string? AnyHit,
        string? Intersection,
        HitGroupType Type);

    private sealed class D3D12RayTracingPipeline : D3D12Pipeline
    {
        private readonly NativeLease _properties;
        private readonly Dictionary<EntryPointReflection, D3D12RayTracingExport> _entries;
        private readonly Dictionary<string, D3D12RayTracingExport> _hitGroups;

        internal D3D12RayTracingPipeline(
            D3D12Device device,
            ID3D12StateObject* native,
            ID3D12StateObjectProperties* properties,
            D3D12RootLayout global,
            ReadOnlySpan<D3D12RootLayout> localRoots,
            IReadOnlyList<D3D12RayTracingExport> exports,
            in PipelineSignature signature,
            string? label)
            : base(
                device,
                (IUnknown*)native,
                global,
                localRoots,
                PipelineType.RayTracing,
                signature,
                label)
        {
            _properties = new NativeLease((IUnknown*)properties, ownsReference: true);
            _entries = exports
                .Where(static value => value.EntryPoint != EntryPointReflection.Null)
                .ToDictionary(static value => value.EntryPoint);
            _hitGroups = exports
                .Where(static value => value.Type == RayRecordType.Hit)
                .ToDictionary(static value => value.Name, StringComparer.Ordinal);
        }

        internal ID3D12StateObject* Native => (ID3D12StateObject*)NativeObject;
        internal uint MaximumLocalRecordSize => _entries.Values
            .Concat(_hitGroups.Values)
            .Select(static value => checked(32u + value.LocalRoot.RootArgumentSize))
            .DefaultIfEmpty(32u)
            .Max();

        internal bool HasExportType(RayRecordType type) =>
            _entries.Values.Any(value => value.Type == type) ||
            _hitGroups.Values.Any(value => value.Type == type);

        internal D3D12RayTracingExport GetEntry(
            EntryPointReflection entryPoint,
            RayRecordType type)
        {
            if (!_entries.TryGetValue(entryPoint, out D3D12RayTracingExport? value) ||
                value.Type != type)
            {
                throw new ArgumentException(
                    "The shader record entry point is not a compatible export of this Pipeline.",
                    nameof(entryPoint));
            }
            return value;
        }

        internal D3D12RayTracingExport GetHitGroup(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (!_hitGroups.TryGetValue(name, out D3D12RayTracingExport? value))
            {
                throw new ArgumentException(
                    "The shader record hit-group export does not belong to this Pipeline.",
                    nameof(name));
            }
            return value;
        }

        protected override void ReleaseAdditional() => _properties.Release();
    }

    private sealed class D3D12RayTracingShaderTable : RayTracingShaderTable
    {
        private readonly D3D12Device _device;
        private int _released;

        internal D3D12RayTracingShaderTable(
            D3D12Device device,
            D3D12RayTracingPipeline pipeline,
            in RayTracingShaderTableDesc description)
            : base(device, description)
        {
            _device = device;
            Pipeline = pipeline;
        }

        internal D3D12RayTracingPipeline Pipeline { get; }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _device.UnregisterChild(this);
        }
    }

    private readonly record struct D3D12RayRecordSnapshot(
        D3D12RayTracingExport Export,
        uint ResourceOffset,
        uint ResourceCount,
        uint OrdinaryDataOffset,
        uint OrdinaryDataSize);

    private sealed class D3D12RayTableSnapshot
    {
        private D3D12RayRecordSnapshot[] _rayGeneration = [];
        private D3D12RayRecordSnapshot[] _miss = [];
        private D3D12RayRecordSnapshot[] _hit = [];
        private D3D12RayRecordSnapshot[] _callable = [];
        private ResourceBinding[] _resources = [];
        private byte[] _ordinaryData = [];
        private int _rayGenerationCount;
        private int _missCount;
        private int _hitCount;
        private int _callableCount;
        private int _resourceCount;
        private int _ordinaryDataCount;
        private D3D12RayTableMaterialization _materialization;
        private bool _hasMaterialization;

        internal ReadOnlySpan<D3D12RayRecordSnapshot> RayGeneration =>
            _rayGeneration.AsSpan(0, _rayGenerationCount);
        internal ReadOnlySpan<D3D12RayRecordSnapshot> Miss => _miss.AsSpan(0, _missCount);
        internal ReadOnlySpan<D3D12RayRecordSnapshot> Hit => _hit.AsSpan(0, _hitCount);
        internal ReadOnlySpan<D3D12RayRecordSnapshot> Callable =>
            _callable.AsSpan(0, _callableCount);
        internal ReadOnlySpan<ResourceBinding> Resources => _resources.AsSpan(0, _resourceCount);
        internal ReadOnlySpan<byte> OrdinaryData => _ordinaryData.AsSpan(0, _ordinaryDataCount);

        internal void Capture(
            D3D12RayTracingShaderTable table,
            in RayTracingShaderTableUpdate update)
        {
            RayTracingShaderTableDesc description = table.Description;
            if ((uint)update.RayGeneration.Length != description.RayGenerationRecordCount ||
                (uint)update.Miss.Length != description.MissRecordCount ||
                (uint)update.Hit.Length != description.HitRecordCount ||
                (uint)update.Callable.Length != description.CallableRecordCount)
            {
                throw new ArgumentException(
                    "Every shader-table update category must exactly match its declared record count.",
                    nameof(update));
            }

            EnsureCapacity(ref _resources, update.Resources.Length);
            EnsureCapacity(ref _ordinaryData, update.OrdinaryData.Length);
            update.Resources.CopyTo(_resources);
            update.OrdinaryData.CopyTo(_ordinaryData);
            _resourceCount = update.Resources.Length;
            _ordinaryDataCount = update.OrdinaryData.Length;
            EnsureCapacity(ref _rayGeneration, update.RayGeneration.Length);
            EnsureCapacity(ref _miss, update.Miss.Length);
            EnsureCapacity(ref _hit, update.Hit.Length);
            EnsureCapacity(ref _callable, update.Callable.Length);
            _rayGenerationCount = CaptureRecords(
                table,
                update.RayGeneration,
                RayRecordType.RayGeneration,
                _resources.AsSpan(0, _resourceCount),
                _ordinaryData.AsSpan(0, _ordinaryDataCount),
                _rayGeneration);
            _missCount = CaptureRecords(
                table,
                update.Miss,
                RayRecordType.Miss,
                _resources.AsSpan(0, _resourceCount),
                _ordinaryData.AsSpan(0, _ordinaryDataCount),
                _miss);
            _hitCount = CaptureRecords(
                table,
                update.Hit,
                RayRecordType.Hit,
                _resources.AsSpan(0, _resourceCount),
                _ordinaryData.AsSpan(0, _ordinaryDataCount),
                _hit);
            _callableCount = CaptureRecords(
                table,
                update.Callable,
                RayRecordType.Callable,
                _resources.AsSpan(0, _resourceCount),
                _ordinaryData.AsSpan(0, _ordinaryDataCount),
                _callable);
            _hasMaterialization = false;
        }

        internal D3D12RayTableMaterialization Materialize(
            D3D12CommandContext command,
            D3D12RayTracingShaderTable table)
        {
            D3D12CommandSlot slot = command.Recording;
            if (_hasMaterialization &&
                _materialization.DescriptorArenaVersion == slot.DescriptorArenaVersion)
            {
                return _materialization;
            }

            D3D12RayTableMaterialization next = CreateMaterialization(command, table);
            _materialization = next;
            _hasMaterialization = true;
            return next;
        }

        private static int CaptureRecords(
            D3D12RayTracingShaderTable table,
            ReadOnlySpan<RayTracingShaderRecord> records,
            RayRecordType type,
            ReadOnlySpan<ResourceBinding> resources,
            ReadOnlySpan<byte> ordinaryData,
            D3D12RayRecordSnapshot[] destination)
        {
            for (int index = 0; index < records.Length; index++)
            {
                ref readonly RayTracingShaderRecord record = ref records[index];
                D3D12RayTracingExport export = type == RayRecordType.Hit
                    ? table.Pipeline.GetHitGroup(record.HitGroupName!)
                    : table.Pipeline.GetEntry(record.EntryPoint, type);

                ValidateSlice(record.ResourceOffset, record.ResourceCount, resources.Length, nameof(records));
                ValidateSlice(record.OrdinaryDataOffset, record.OrdinaryDataSize, ordinaryData.Length, nameof(records));
                ReadOnlySpan<ResourceBinding> bindings = resources.Slice(
                    checked((int)record.ResourceOffset),
                    checked((int)record.ResourceCount));
                ReadOnlySpan<byte> data = ordinaryData.Slice(
                    checked((int)record.OrdinaryDataOffset),
                    checked((int)record.OrdinaryDataSize));
                if (record.Layout != VariableLayoutReflection.Null)
                {
                    export.LocalRoot.GetBlock(record.Layout).Shape.RequireMaterializationShape(
                        bindings,
                        data);
                }
                destination[index] = new D3D12RayRecordSnapshot(
                    export,
                    record.ResourceOffset,
                    record.ResourceCount,
                    record.OrdinaryDataOffset,
                    record.OrdinaryDataSize);
            }
            return records.Length;
        }

        private static void ValidateSlice(uint offset, uint count, int length, string parameter)
        {
            if (offset > (uint)length || count > (uint)length - offset)
                throw new ArgumentOutOfRangeException(parameter);
        }

        internal void Reset()
        {
            Array.Clear(_rayGeneration, 0, _rayGenerationCount);
            Array.Clear(_miss, 0, _missCount);
            Array.Clear(_hit, 0, _hitCount);
            Array.Clear(_callable, 0, _callableCount);
            Array.Clear(_resources, 0, _resourceCount);
            _rayGenerationCount = 0;
            _missCount = 0;
            _hitCount = 0;
            _callableCount = 0;
            _resourceCount = 0;
            _ordinaryDataCount = 0;
            _materialization = default;
            _hasMaterialization = false;
        }

        private static void EnsureCapacity<T>(ref T[] values, int capacity)
        {
            if (values.Length < capacity)
                Array.Resize(ref values, capacity);
        }

        private D3D12RayTableMaterialization CreateMaterialization(
            D3D12CommandContext command,
            D3D12RayTracingShaderTable table)
        {
            uint resourceCount = 0;
            uint samplerCount = 0;
            ulong ordinarySize = 0;
            Count(RayGeneration);
            Count(Miss);
            Count(Hit);
            Count(Callable);

            (uint resourceBase, uint samplerBase) = command.AllocateTransientDescriptorPair(
                resourceCount,
                samplerCount);

            ulong stride = table.Description.MaximumRecordSize;
            ulong rayGenerationOffset = 0;
            ulong missOffset = AlignUp(
                checked(rayGenerationOffset + checked((ulong)RayGeneration.Length * stride)),
                64);
            ulong hitOffset = AlignUp(
                checked(missOffset + checked((ulong)Miss.Length * stride)),
                64);
            ulong callableOffset = AlignUp(
                checked(hitOffset + checked((ulong)Hit.Length * stride)),
                64);
            ulong tableEnd = checked(callableOffset + checked((ulong)Callable.Length * stride));
            ulong ordinaryOffset = AlignUp(tableEnd, 256);
            ulong totalSize = checked(ordinaryOffset + ordinarySize);
            if (totalSize == 0)
                throw new InvalidOperationException("A ray-tracing table materialization cannot be empty.");

            D3D12OrdinaryDataReservation storage =
                command.ReserveTransientOrdinaryData(totalSize);
            {
                Span<byte> destination = storage.CommitSpan(checked((int)totalSize), clear: true);
                ulong baseAddress = storage.Address;
                uint resourceCursor = resourceBase;
                uint samplerCursor = samplerBase;
                ulong ordinaryCursor = ordinaryOffset;
                Write(RayGeneration, rayGenerationOffset, destination);
                Write(Miss, missOffset, destination);
                Write(Hit, hitOffset, destination);
                Write(Callable, callableOffset, destination);

                Silk.NET.Direct3D12.DispatchRaysDesc native = new()
                {
                    RayGenerationShaderRecord = new GpuVirtualAddressRange(
                        baseAddress + rayGenerationOffset,
                        stride),
                    MissShaderTable = CreateRange(Miss.Length, missOffset),
                    HitGroupTable = CreateRange(Hit.Length, hitOffset),
                    CallableShaderTable = CreateRange(Callable.Length, callableOffset),
                    Width = 1,
                    Height = 1,
                    Depth = 1,
                };
                return new D3D12RayTableMaterialization(
                    command.Recording.DescriptorArenaVersion,
                    native);

                GpuVirtualAddressRangeAndStride CreateRange(int count, ulong offset) =>
                    count == 0
                        ? default
                        : new GpuVirtualAddressRangeAndStride(
                            baseAddress + offset,
                            checked((ulong)count * stride),
                            stride);

                void Write(
                    ReadOnlySpan<D3D12RayRecordSnapshot> records,
                    ulong categoryOffset,
                    Span<byte> destinationBytes)
                {
                    for (int index = 0; index < records.Length; index++)
                    {
                        D3D12RayRecordSnapshot record = records[index];
                        Span<byte> target = destinationBytes.Slice(
                            checked((int)(categoryOffset + checked((ulong)index * stride))),
                            checked((int)stride));
                        record.Export.Identifier.CopyTo(target);
                        if (record.Export.Layout == VariableLayoutReflection.Null)
                            continue;

                        D3D12ParameterBlockLayout layout =
                            record.Export.LocalRoot.GetBlock(record.Export.Layout);
                        ReadOnlySpan<ResourceBinding> bindings = Resources.Slice(
                            checked((int)record.ResourceOffset),
                            checked((int)record.ResourceCount));
                        int bindingOrdinal = 0;
                        foreach (BlockLeafBinding leaf in layout.Leaves)
                        {
                            if (leaf.Unbounded)
                                continue;
                            uint first = leaf.Heap == ParameterHeap.Resource
                                ? resourceCursor
                                : samplerCursor;
                            for (uint element = 0; element < leaf.DescriptorCount; element++)
                            {
                                ref readonly ResourceBinding binding = ref bindings[bindingOrdinal++];
                                command.CopyTransientDescriptor(
                                    leaf.Heap,
                                    checked(first + leaf.HeapOffset + element),
                                    binding,
                                    leaf.Type);
                            }
                            GpuDescriptorHandle handle = command.Recording.GetGpuHandle(
                                leaf.Heap,
                                checked(first + leaf.HeapOffset));
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                                target.Slice(checked(32 + (int)leaf.RootParameterIndex * sizeof(ulong))),
                                handle.Ptr);
                        }
                        resourceCursor = checked(resourceCursor + layout.Shape.ResourceDescriptorCount);
                        samplerCursor = checked(samplerCursor + layout.Shape.SamplerDescriptorCount);
                        if (layout.OrdinaryRootParameter is uint ordinaryRoot)
                        {
                            ordinaryCursor = AlignUp(ordinaryCursor, 256);
                            ReadOnlySpan<byte> data = OrdinaryData.Slice(
                                checked((int)record.OrdinaryDataOffset),
                                checked((int)record.OrdinaryDataSize));
                            data.CopyTo(destinationBytes.Slice(
                                checked((int)ordinaryCursor),
                                checked((int)(totalSize - ordinaryCursor))));
                            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                                target.Slice(checked(32 + (int)ordinaryRoot * sizeof(ulong))),
                                baseAddress + ordinaryCursor);
                            ordinaryCursor = checked(ordinaryCursor + AlignUp((ulong)data.Length, 256));
                        }
                    }
                }
            }

            void Count(ReadOnlySpan<D3D12RayRecordSnapshot> records)
            {
                foreach (D3D12RayRecordSnapshot record in records)
                {
                    if (record.Export.Layout == VariableLayoutReflection.Null)
                        continue;
                    D3D12ParameterBlockLayout layout =
                        record.Export.LocalRoot.GetBlock(record.Export.Layout);
                    resourceCount = checked(resourceCount + layout.Shape.ResourceDescriptorCount);
                    samplerCount = checked(samplerCount + layout.Shape.SamplerDescriptorCount);
                    if (layout.OrdinaryRootParameter.HasValue)
                        ordinarySize = checked(ordinarySize + AlignUp(record.OrdinaryDataSize, 256));
                }
            }
        }
    }

    private readonly record struct D3D12RayTableMaterialization(
        ulong DescriptorArenaVersion,
        Silk.NET.Direct3D12.DispatchRaysDesc Description);

    private sealed partial class D3D12CommandSlot
    {
        private readonly Dictionary<D3D12RayTracingShaderTable, D3D12RayTableSnapshot>
            _rayTracingSnapshots = new(ReferenceEqualityComparer.Instance);
        private readonly List<D3D12RayTableSnapshot> _rayTracingSnapshotPool = [];
        private int _rayTracingSnapshotCount;

        internal D3D12RayTableSnapshot CaptureRayTracingSnapshot(
            D3D12RayTracingShaderTable table,
            in RayTracingShaderTableUpdate update)
        {
            _rayTracingSnapshots.EnsureCapacity(checked(_rayTracingSnapshots.Count + 1));
            D3D12RayTableSnapshot snapshot;
            if (_rayTracingSnapshotCount < _rayTracingSnapshotPool.Count)
            {
                snapshot = _rayTracingSnapshotPool[_rayTracingSnapshotCount];
            }
            else
            {
                _rayTracingSnapshotPool.EnsureCapacity(checked(_rayTracingSnapshotPool.Count + 1));
                snapshot = new D3D12RayTableSnapshot();
                _rayTracingSnapshotPool.Add(snapshot);
            }
            try
            {
                snapshot.Capture(table, update);
            }
            catch
            {
                snapshot.Reset();
                throw;
            }
            _rayTracingSnapshotCount++;
            _rayTracingSnapshots[table] = snapshot;
            return snapshot;
        }

        internal D3D12RayTableSnapshot GetRayTracingSnapshot(
            D3D12RayTracingShaderTable table) =>
            _rayTracingSnapshots.TryGetValue(table, out D3D12RayTableSnapshot? snapshot)
                ? snapshot
                : throw new InvalidOperationException(
                    "UpdateRayTracingShaderTable must precede dispatch in this recording.");

        internal void ClearRayTracingSnapshots()
        {
            for (int index = 0; index < _rayTracingSnapshotCount; index++)
                _rayTracingSnapshotPool[index].Reset();
            _rayTracingSnapshotCount = 0;
            _rayTracingSnapshots.Clear();
        }
    }

    private sealed partial class D3D12CommandContext
    {
        internal D3D12RayTableSnapshot CaptureRayTracingSnapshot(
            D3D12RayTracingShaderTable table,
            in RayTracingShaderTableUpdate update) =>
            Recording.CaptureRayTracingSnapshot(table, update);

        internal D3D12RayTableMaterialization MaterializeRayTracingTable(
            D3D12RayTracingShaderTable table) =>
            Recording.GetRayTracingSnapshot(table).Materialize(this, table);
    }

    private sealed class D3D12AccelerationStructureSrv : AccelerationStructureSrv, INativeDescriptor
    {
        private readonly D3D12Device _device;
        private readonly D3D12AccelerationStructure _structure;
        private int _released;

        internal D3D12AccelerationStructureSrv(
            D3D12Device device,
            D3D12AccelerationStructure structure,
            in AccelerationStructureSrvDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            _device = device;
            _structure = structure;
            NativeDescriptor = descriptor;
        }

        public DescriptorLease NativeDescriptor { get; }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _device.Descriptors.NotifyDisposed(this);
            NativeDescriptor.Release();
            _structure.UnregisterView(this);
            _device.UnregisterChild(this);
        }
    }

    private sealed class D3D12BindlessAccelerationStructureSrv :
        BindlessAccelerationStructureSrv,
        INativeDescriptor
    {
        private readonly D3D12Device _device;
        private readonly D3D12AccelerationStructure _structure;
        private int _released;

        internal D3D12BindlessAccelerationStructureSrv(
            D3D12Device device,
            D3D12AccelerationStructure structure,
            in AccelerationStructureSrvDesc description,
            DescriptorLease descriptor,
            uint descriptorIndex)
            : base(device, description, descriptorIndex)
        {
            _device = device;
            _structure = structure;
            NativeDescriptor = descriptor;
        }

        public DescriptorLease NativeDescriptor { get; }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _device.Descriptors.NotifyDisposed(this);
            NativeDescriptor.Release();
            _structure.UnregisterView(this);
            _device.UnregisterChild(this);
        }
    }

    private sealed partial class D3D12CommandContext
    {
        internal void Capture(D3D12AccelerationStructure value) =>
            Recording.Capture(value, value.NativeLifetime);

        internal void Capture(AccelerationStructureSrv value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12AccelerationStructure structure =
                NativeCast.AccelerationStructure(value.Resource);
            D3D12CommandSlot slot = Recording;
            slot.Capture(value, descriptor.NativeDescriptor, structure.NativeLifetime);
            slot.Capture(structure, structure.NativeLifetime);
        }
    }

    private static partial class NativeCast
    {
        internal static D3D12RayTracingPipeline RayTracingPipeline(Pipeline value)
        {
#if DEBUG
            return (D3D12RayTracingPipeline)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<
                Pipeline,
                D3D12RayTracingPipeline>(ref value);
#endif
        }

        internal static D3D12AccelerationStructure AccelerationStructure(
            AccelerationStructure value)
        {
#if DEBUG
            return (D3D12AccelerationStructure)value;
#else
            return System.Runtime.CompilerServices.Unsafe
                .As<AccelerationStructure, D3D12AccelerationStructure>(ref value);
#endif
        }

        internal static D3D12RayTracingShaderTable RayTracingShaderTable(
            RayTracingShaderTable value)
        {
#if DEBUG
            return (D3D12RayTracingShaderTable)value;
#else
            return System.Runtime.CompilerServices.Unsafe
                .As<RayTracingShaderTable, D3D12RayTracingShaderTable>(ref value);
#endif
        }
    }
}
