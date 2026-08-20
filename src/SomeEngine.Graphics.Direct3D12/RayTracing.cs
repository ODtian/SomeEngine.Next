using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using SlangShaderSharp;
using NativeFormat = Silk.NET.DXGI.Format;
using NativeRange = Silk.NET.Direct3D12.Range;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public AccelerationStructure CreateAccelerationStructure(
        Device device,
        Buffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label = null)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        _ = nativeDevice.RequireCapability<RayTracing>(nameof(CreateAccelerationStructure));
        D3D12Buffer nativeStorage = RequireBuffer(storage);
        RequireSameDevice(nativeDevice, nativeStorage, nameof(storage));
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
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        _ = nativeDevice.RequireCapability<RayTracing>(nameof(CreateAccelerationStructureSrv));
        D3D12AccelerationStructure structure =
            RequireAccelerationStructure(desc.AccelerationStructure);
        RequireSameDevice(nativeDevice, structure, nameof(desc));

        DescriptorLease descriptor = nativeDevice
            .GetResourceDescriptors(
                nativeDevice.ResolveResourceHomeNodeIndex(
                    structure.Storage.Info.CreationNodeMask))
            .Allocate();
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

    public AccelerationStructureBuildInfo GetAccelerationStructureBuildInfo(
        Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        RayTracing capability =
            nativeDevice.RequireCapability<RayTracing>(nameof(GetAccelerationStructureBuildInfo));
        AccelerationStructurePrebuildMode mode =
            RequireAccelerationStructureBuildSupport(capability, options);
        RaytracingGeometryDesc[] nativeGeometries =
            type == AccelerationStructureType.BottomLevel
                ? new RaytracingGeometryDesc[geometries.Length]
                : [];
        BuildRaytracingAccelerationStructureInputs inputs = CreateBuildInputs(
            nativeDevice,
            type,
            options,
            geometries,
            nativeGeometries);
        RaytracingAccelerationStructurePrebuildInfo info = default;
        fixed (RaytracingGeometryDesc* geometryPointer = nativeGeometries)
        {
            if (nativeGeometries.Length != 0)
                inputs.PGeometryDescs = geometryPointer;
            nativeDevice.Native->GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &info);
        }
        _ = RequireValidAccelerationStructurePrebuildInfo(info, mode);

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
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        ValidateRayTracingPipelineDescription(nativeDevice, desc);
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        IComponentType program = desc.Program;
        StaticSamplerBinding[] staticSamplers = desc.StaticSamplers.ToArray();
        ShaderReflection reflection = GetProgramReflection(program);
        int localRootCapacity = checked(
            desc.RayGeneration.Length + desc.Miss.Length +
            desc.Callable.Length + desc.HitGroups.Length);
        var shaderExports = new Dictionary<string, EntryPointReflection>(
            localRootCapacity,
            StringComparer.Ordinal);
        var records = new List<D3D12RayTracingExport>(localRootCapacity);
        var localRoots = new List<D3D12RootSignatureState>(localRootCapacity);
        var localRootArray = new D3D12RootSignatureState[localRootCapacity];
        var identifiers = new byte[localRootCapacity][];
        for (int index = 0; index < identifiers.Length; index++)
            identifiers[index] = new byte[32];
        var entryExports = new Dictionary<EntryPointReflection, D3D12RayTracingExport>(
            checked(desc.RayGeneration.Length + desc.Miss.Length + desc.Callable.Length));
        var hitGroupExports = new Dictionary<string, D3D12RayTracingExport>(
            desc.HitGroups.Length,
            StringComparer.Ordinal);
        var stateObjectNames = new HashSet<string>(
            localRootCapacity + desc.HitGroups.Length,
            StringComparer.Ordinal);
        var hitGroups = new List<RayTracingHitGroupState>(desc.HitGroups.Length);

        D3D12RootSignatureState? globalToRelease = null;
        bool rootsTransferred = false;
        try
        {
            AddAllRayTracingExports(
                nativeDevice,
                reflection,
                desc,
                staticSamplers,
                shaderExports,
                stateObjectNames,
                records,
                localRoots,
                identifiers,
                hitGroups);

            EntryPointReflection[] allEntries = GetOrderedRayTracingEntries(
                reflection,
                shaderExports.Values);
            CompiledProgramLibrary library = CompileProgramLibrary(
                program,
                reflection,
                allEntries);
            D3D12RootSignatureState global = CompileRayTracingGlobalRootSignature(
                nativeDevice,
                reflection,
                allEntries,
                staticSamplers);
            globalToRelease = global;
            localRoots.CopyTo(localRootArray);
            return MaterializeRayTracingPipeline(
                nativeDevice,
                nativeCache,
                program,
                desc,
                global,
                localRootArray,
                library,
                allEntries,
                records,
                hitGroups,
                entryExports,
                hitGroupExports,
                ref rootsTransferred);
        }
        finally
        {
            if (!rootsTransferred)
            {
                globalToRelease?.Release();
                for (int index = localRoots.Count - 1; index >= 0; index--)
                    localRoots[index].Release();
            }
        }
    }

    private static void ValidateRayTracingPipelineDescription(
        D3D12Device nativeDevice,
        in RayTracingPipelineDesc desc)
    {
        RayTracing capability =
            nativeDevice.RequireCapability<RayTracing>(nameof(CreateRayTracingPipeline));
        if (!capability.PipelineRayTracing)
            throw new NotSupportedException("Pipeline ray tracing is unavailable.");
        ArgumentNullException.ThrowIfNull(desc.Program);
        if (desc.RayGeneration.IsEmpty)
        {
            throw new ArgumentException(
                "A ray-tracing pipeline requires a ray-generation export.",
                nameof(desc));
        }
        if (desc.NodeMask == 0 ||
            (desc.NodeMask & ~nativeDevice.EnabledNodeMask) != 0 ||
            !Enum.IsDefined(desc.Options))
        {
            throw new ArgumentOutOfRangeException(nameof(desc));
        }
    }

    private void AddAllRayTracingExports(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        in RayTracingPipelineDesc desc,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers,
        Dictionary<string, EntryPointReflection> shaderExports,
        HashSet<string> stateObjectNames,
        List<D3D12RayTracingExport> records,
        List<D3D12RootSignatureState> localRoots,
        byte[][] identifiers,
        List<RayTracingHitGroupState> hitGroups)
    {
        AddRayTracingEntries(
            nativeDevice,
            reflection,
            desc.RayGeneration,
            SlangStage.RayGeneration,
            RayRecordType.RayGeneration,
            "ray-generation",
            staticSamplers,
            shaderExports,
            stateObjectNames,
            records,
            localRoots,
            identifiers);
        AddRayTracingEntries(
            nativeDevice,
            reflection,
            desc.Miss,
            SlangStage.Miss,
            RayRecordType.Miss,
            "miss",
            staticSamplers,
            shaderExports,
            stateObjectNames,
            records,
            localRoots,
            identifiers);
        AddRayTracingEntries(
            nativeDevice,
            reflection,
            desc.Callable,
            SlangStage.Callable,
            RayRecordType.Callable,
            "callable",
            staticSamplers,
            shaderExports,
            stateObjectNames,
            records,
            localRoots,
            identifiers);
        AddRayTracingHitGroups(
            nativeDevice,
            reflection,
            desc.HitGroups,
            staticSamplers,
            shaderExports,
            stateObjectNames,
            records,
            localRoots,
            identifiers,
            hitGroups);
    }

    private void AddRayTracingEntries(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        ReadOnlySpan<EntryPointReflection> entries,
        SlangStage stage,
        RayRecordType type,
        string role,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers,
        Dictionary<string, EntryPointReflection> shaderExports,
        HashSet<string> stateObjectNames,
        List<D3D12RayTracingExport> records,
        List<D3D12RootSignatureState> localRoots,
        byte[][] identifiers)
    {
        foreach (EntryPointReflection entry in entries)
        {
            string name = AddRayTracingShader(
                reflection,
                entry,
                stage,
                role,
                shaderExports,
                stateObjectNames,
                records);
            D3D12RootSignatureState local = CompileTrackedRayTracingLocalRoot(
                nativeDevice,
                reflection,
                [entry],
                staticSamplers,
                localRoots);
            records.Add(new D3D12RayTracingExport(
                name,
                entry,
                entry.VarLayout == VariableLayoutReflection.Null
                    ? []
                    : [entry.VarLayout],
                local,
                type,
                identifiers[records.Count]));
        }
    }

    private void AddRayTracingHitGroups(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        ReadOnlySpan<RayTracingHitGroup> descriptions,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers,
        Dictionary<string, EntryPointReflection> shaderExports,
        HashSet<string> stateObjectNames,
        List<D3D12RayTracingExport> records,
        List<D3D12RootSignatureState> localRoots,
        byte[][] identifiers,
        List<RayTracingHitGroupState> hitGroups)
    {
        foreach (ref readonly RayTracingHitGroup hitGroup in descriptions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(hitGroup.Name);
            if (!stateObjectNames.Add(hitGroup.Name))
            {
                throw new ArgumentException(
                    "Ray-tracing state-object export names must be unique.",
                    "desc");
            }
            var members = new List<EntryPointReflection>(3);
            string? closest = AddRayTracingHitMember(
                reflection,
                hitGroup.ClosestHit,
                SlangStage.ClosestHit,
                "closest-hit",
                members,
                shaderExports,
                stateObjectNames,
                records);
            string? anyHit = AddRayTracingHitMember(
                reflection,
                hitGroup.AnyHit,
                SlangStage.AnyHit,
                "any-hit",
                members,
                shaderExports,
                stateObjectNames,
                records);
            string? intersection = AddRayTracingHitMember(
                reflection,
                hitGroup.Intersection,
                SlangStage.Intersection,
                "intersection",
                members,
                shaderExports,
                stateObjectNames,
                records);
            if (members.Count == 0)
            {
                throw new ArgumentException(
                    "A ray-tracing hit group must contain at least one shader.",
                    "desc");
            }
            VariableLayoutReflection[] layouts = members
                .Select(static member => member.VarLayout)
                .Where(static layout => layout != VariableLayoutReflection.Null)
                .Distinct()
                .ToArray();
            D3D12RootSignatureState local = CompileTrackedRayTracingLocalRoot(
                nativeDevice,
                reflection,
                CollectionsMarshal.AsSpan(members),
                staticSamplers,
                localRoots);
            records.Add(new D3D12RayTracingExport(
                hitGroup.Name,
                EntryPointReflection.Null,
                layouts,
                local,
                RayRecordType.Hit,
                identifiers[records.Count]));
            hitGroups.Add(new RayTracingHitGroupState(
                hitGroup.Name,
                closest,
                anyHit,
                intersection,
                intersection is null
                    ? HitGroupType.Triangles
                    : HitGroupType.ProceduralPrimitive));
        }
    }

    private string? AddRayTracingHitMember(
        ShaderReflection reflection,
        EntryPointReflection entry,
        SlangStage stage,
        string role,
        List<EntryPointReflection> members,
        Dictionary<string, EntryPointReflection> shaderExports,
        HashSet<string> stateObjectNames,
        List<D3D12RayTracingExport> records)
    {
        if (entry == EntryPointReflection.Null)
            return null;
        members.Add(entry);
        return AddRayTracingShader(
            reflection,
            entry,
            stage,
            role,
            shaderExports,
            stateObjectNames,
            records);
    }

    private string AddRayTracingShader(
        ShaderReflection reflection,
        EntryPointReflection entry,
        SlangStage stage,
        string role,
        Dictionary<string, EntryPointReflection> shaderExports,
        HashSet<string> stateObjectNames,
        List<D3D12RayTracingExport> records)
    {
        string name = ValidateStateObjectEntryPoint(reflection, entry, [stage], role);
        if (shaderExports.TryGetValue(name, out EntryPointReflection existing) &&
            existing != entry)
        {
            throw new ArgumentException(
                "Two Slang entry points use the same state-object export name.",
                "desc");
        }
        shaderExports[name] = entry;
        if (!stateObjectNames.Add(name) &&
            !records.Any(record => record.EntryPoint == entry))
        {
            throw new ArgumentException(
                "Ray-tracing state-object export names must be unique.",
                "desc");
        }
        return name;
    }

    private D3D12RootSignatureState CompileTrackedRayTracingLocalRoot(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        ReadOnlySpan<EntryPointReflection> entries,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers,
        List<D3D12RootSignatureState> localRoots)
    {
        D3D12RootSignatureState root = D3D12RootSignatureBuilder.CompileLocal(
            this,
            nativeDevice,
            reflection,
            entries,
            staticSamplers,
            PipelineType.RayTracing);
        try
        {
            localRoots.Add(root);
            return root;
        }
        catch
        {
            root.Release();
            throw;
        }
    }

    private static EntryPointReflection[] GetOrderedRayTracingEntries(
        ShaderReflection reflection,
        IEnumerable<EntryPointReflection> selected)
    {
        HashSet<EntryPointReflection> selectedEntries = [.. selected];
        List<EntryPointReflection> orderedEntries = [];
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            EntryPointReflection entry = reflection.GetEntryPointByIndex(index);
            if (selectedEntries.Contains(entry))
                orderedEntries.Add(entry);
        }
        return [.. orderedEntries];
    }

    private D3D12RootSignatureState CompileRayTracingGlobalRootSignature(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        ReadOnlySpan<EntryPointReflection> entries,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers)
    {
        D3D12RootSignatureBuilder.ValidateStaticSamplers(
            this,
            nativeDevice,
            reflection,
            entries,
            staticSamplers,
            PipelineType.RayTracing);
        return D3D12RootSignatureBuilder.CompileGlobal(
            this,
            nativeDevice,
            reflection,
            staticSamplers,
            PipelineType.RayTracing);
    }

    private D3D12RayTracingPipeline MaterializeRayTracingPipeline(
        D3D12Device nativeDevice,
        D3D12PipelineCache? nativeCache,
        IComponentType program,
        in RayTracingPipelineDesc desc,
        D3D12RootSignatureState global,
        D3D12RootSignatureState[] localRoots,
        CompiledProgramLibrary library,
        EntryPointReflection[] allEntries,
        IReadOnlyList<D3D12RayTracingExport> records,
        IReadOnlyList<RayTracingHitGroupState> hitGroups,
        Dictionary<EntryPointReflection, D3D12RayTracingExport> entryExports,
        Dictionary<string, D3D12RayTracingExport> hitGroupExports,
        ref bool rootsTransferred)
    {
        byte[] key = CreateRayTracingPipelineKey(
            nativeDevice,
            global,
            localRoots,
            library,
            records,
            hitGroups,
            desc);
        byte[][] replayLibraries = ResolveStateObjectReplayCode(
            nativeCache,
            4,
            key,
            library);
        ID3D12StateObject* stateObject = null;
        ID3D12StateObjectProperties* properties = null;
        NativeLease? nativeState = null;
        NativeLease? propertiesLease = null;
        RetainedSlangProgram? retainedProgram = null;
        D3D12RayTracingPipeline? result = null;
        try
        {
            stateObject = CreateNativeRayTracingStateObject(
                nativeDevice,
                global,
                replayLibraries,
                allEntries,
                records,
                hitGroups,
                desc);
            SetNativeName(stateObject, desc.Label ?? "Ray Tracing State Object");
            properties = QueryRayTracingStateObjectProperties(nativeDevice, stateObject);
            ReadRayTracingShaderIdentifiers(properties, records);
            PopulateRayTracingExportMaps(records, entryExports, hitGroupExports);
            propertiesLease = new NativeLease((IUnknown*)properties, ownsReference: true);
            properties = null;
            NativeLease[] rootDependencies = CreateRayTracingRootDependencies(
                global,
                localRoots);
            nativeState = new NativeLease(
                (IUnknown*)stateObject,
                ownsReference: true,
                rootDependencies);
            stateObject = null;
            retainedProgram = RetainProgram(program);
            NativeLease[] additionalLeases = [propertiesLease];
            result = new D3D12RayTracingPipeline(
                nativeDevice,
                nativeState,
                global,
                localRoots,
                additionalLeases,
                retainedProgram,
                entryExports,
                hitGroupExports,
                desc.Label);
            rootsTransferred = true;
            nativeState = null;
            propertiesLease = null;
            retainedProgram = null;
            nativeDevice.RegisterChild(result);
            StoreStateObjectReplay(nativeCache, 4, key, library);
            return result;
        }
        catch
        {
            CleanupFailedRayTracingPipeline(
                result,
                nativeState,
                propertiesLease,
                stateObject,
                properties,
                retainedProgram);
            throw;
        }
    }

    private ID3D12StateObject* CreateNativeRayTracingStateObject(
        D3D12Device nativeDevice,
        D3D12RootSignatureState global,
        byte[][] replayLibraries,
        EntryPointReflection[] allEntries,
        IReadOnlyList<D3D12RayTracingExport> records,
        IReadOnlyList<RayTracingHitGroupState> hitGroups,
        in RayTracingPipelineDesc desc)
    {
        using NativeStateObjectArena arena = new();
        int subobjectCount = checked(
            replayLibraries.Length + 4 + hitGroups.Count + (records.Count * 2));
        StateSubobject* subobjects = arena.Allocate<StateSubobject>(subobjectCount);
        int ordinal = AddRayTracingLibraries(
            arena,
            replayLibraries,
            allEntries,
            subobjects);
        GlobalRootSignature* globalDescription = arena.Allocate<GlobalRootSignature>();
        globalDescription->PGlobalRootSignature = global.Native;
        subobjects[ordinal++] = new StateSubobject(
            StateSubobjectType.GlobalRootSignature,
            globalDescription);
        ordinal = AddNativeRayTracingHitGroups(arena, hitGroups, subobjects, ordinal);
        ordinal = AddRayTracingLocalRootAssociations(arena, records, subobjects, ordinal);
        ordinal = AddRayTracingConfiguration(arena, desc, subobjects, ordinal);
        if (ordinal != subobjectCount)
        {
            throw new InvalidOperationException(
                "The ray-tracing state-object layout is incomplete.");
        }
        StateObjectDesc native = new(
            StateObjectType.RaytracingPipeline,
            checked((uint)subobjectCount),
            subobjects);
        ID3D12StateObject* stateObject = null;
        Guid iid = ID3D12StateObject.Guid;
        int stateObjectResult = nativeDevice.Native->CreateStateObject(
            &native,
            &iid,
            (void**)&stateObject);
        try
        {
            ThrowIfFailed(
                nativeDevice,
                stateObjectResult,
                NativeOperationType.PipelineCreation,
                "ID3D12Device5::CreateStateObject(ray tracing)");
            return stateObject;
        }
        catch
        {
            if (stateObject is not null)
                _ = stateObject->Release();
            throw;
        }
    }

    private static int AddRayTracingLibraries(
        NativeStateObjectArena arena,
        byte[][] replayLibraries,
        EntryPointReflection[] allEntries,
        StateSubobject* subobjects)
    {
        int ordinal = 0;
        for (int index = 0; index < replayLibraries.Length; index++)
        {
            string name = GetStableEntryPointName(allEntries[index]);
            ExportDesc* export = arena.Allocate<ExportDesc>();
            *export = new ExportDesc(arena.String(name), null, ExportFlags.None);
            byte[] code = replayLibraries[index];
            DxilLibraryDesc* libraryDescription = arena.Allocate<DxilLibraryDesc>();
            *libraryDescription = new DxilLibraryDesc(
                new ShaderBytecode(arena.Bytes(code), (nuint)code.Length),
                1,
                export);
            subobjects[ordinal++] = new StateSubobject(
                StateSubobjectType.DxilLibrary,
                libraryDescription);
        }
        return ordinal;
    }

    private static int AddNativeRayTracingHitGroups(
        NativeStateObjectArena arena,
        IReadOnlyList<RayTracingHitGroupState> hitGroups,
        StateSubobject* subobjects,
        int ordinal)
    {
        foreach (RayTracingHitGroupState hitGroup in hitGroups)
        {
            HitGroupDesc* nativeHitGroup = arena.Allocate<HitGroupDesc>();
            nativeHitGroup->HitGroupExport = arena.String(hitGroup.Name);
            nativeHitGroup->Type = hitGroup.Type;
            nativeHitGroup->ClosestHitShaderImport = hitGroup.ClosestHit is null
                ? null
                : arena.String(hitGroup.ClosestHit);
            nativeHitGroup->AnyHitShaderImport = hitGroup.AnyHit is null
                ? null
                : arena.String(hitGroup.AnyHit);
            nativeHitGroup->IntersectionShaderImport = hitGroup.Intersection is null
                ? null
                : arena.String(hitGroup.Intersection);
            subobjects[ordinal++] = new StateSubobject(
                StateSubobjectType.HitGroup,
                nativeHitGroup);
        }
        return ordinal;
    }

    private static int AddRayTracingLocalRootAssociations(
        NativeStateObjectArena arena,
        IReadOnlyList<D3D12RayTracingExport> records,
        StateSubobject* subobjects,
        int ordinal)
    {
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
        return ordinal;
    }

    private static int AddRayTracingConfiguration(
        NativeStateObjectArena arena,
        in RayTracingPipelineDesc desc,
        StateSubobject* subobjects,
        int ordinal)
    {
        RaytracingShaderConfig* shaderConfig = arena.Allocate<RaytracingShaderConfig>();
        shaderConfig->MaxPayloadSizeInBytes = desc.MaximumPayloadSize;
        shaderConfig->MaxAttributeSizeInBytes = desc.MaximumAttributeSize;
        subobjects[ordinal++] = new StateSubobject(
            StateSubobjectType.RaytracingShaderConfig,
            shaderConfig);
        RaytracingPipelineConfig1* pipelineConfig =
            arena.Allocate<RaytracingPipelineConfig1>();
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
        return ordinal;
    }

    private static ID3D12StateObjectProperties* QueryRayTracingStateObjectProperties(
        D3D12Device nativeDevice,
        ID3D12StateObject* stateObject)
    {
        ID3D12StateObjectProperties* properties = null;
        Guid iid = ID3D12StateObjectProperties.Guid;
        int result = stateObject->QueryInterface(&iid, (void**)&properties);
        try
        {
            ThrowIfFailed(
                nativeDevice,
                result,
                NativeOperationType.PipelineCreation,
                "ID3D12StateObject::QueryInterface(ID3D12StateObjectProperties)");
            return properties;
        }
        catch
        {
            if (properties is not null)
                _ = properties->Release();
            throw;
        }
    }

    private static void ReadRayTracingShaderIdentifiers(
        ID3D12StateObjectProperties* properties,
        IReadOnlyList<D3D12RayTracingExport> records)
    {
        foreach (D3D12RayTracingExport record in records)
        {
            void* identifier = properties->GetShaderIdentifier(record.Name);
            if (identifier is null)
            {
                throw new GraphicsException(
                    GraphicsError.PipelineCreation,
                    $"D3D12 did not materialize shader identifier '{record.Name}'.");
            }
            new ReadOnlySpan<byte>(identifier, 32).CopyTo(record.Identifier);
        }
    }

    private static void PopulateRayTracingExportMaps(
        IReadOnlyList<D3D12RayTracingExport> records,
        Dictionary<EntryPointReflection, D3D12RayTracingExport> entryExports,
        Dictionary<string, D3D12RayTracingExport> hitGroupExports)
    {
        foreach (D3D12RayTracingExport record in records)
        {
            if (record.EntryPoint != EntryPointReflection.Null)
                entryExports.Add(record.EntryPoint, record);
            if (record.Type == RayRecordType.Hit)
                hitGroupExports.Add(record.Name, record);
        }
    }

    private static NativeLease[] CreateRayTracingRootDependencies(
        D3D12RootSignatureState global,
        IReadOnlyList<D3D12RootSignatureState> localRoots)
    {
        var dependencies = new NativeLease[checked(localRoots.Count + 1)];
        dependencies[0] = global.NativeLifetime;
        for (int index = 0; index < localRoots.Count; index++)
            dependencies[index + 1] = localRoots[index].NativeLifetime;
        return dependencies;
    }

    private static void CleanupFailedRayTracingPipeline(
        D3D12RayTracingPipeline? result,
        NativeLease? nativeState,
        NativeLease? propertiesLease,
        ID3D12StateObject* stateObject,
        ID3D12StateObjectProperties* properties,
        RetainedSlangProgram? retainedProgram)
    {
        if (result is not null)
        {
            result.Dispose();
            return;
        }
        nativeState?.Release();
        propertiesLease?.Release();
        if (properties is not null)
            _ = properties->Release();
        if (stateObject is not null)
            _ = stateObject->Release();
        retainedProgram?.Dispose();
    }

    public RayTracingShaderTable CreateRayTracingShaderTable(
        Device device,
        in RayTracingShaderTableDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        RayTracing capability =
            nativeDevice.RequireCapability<RayTracing>(nameof(CreateRayTracingShaderTable));
        if (!capability.PipelineRayTracing)
            throw new NotSupportedException("Pipeline ray tracing is unavailable.");
        D3D12RayTracingPipeline pipeline = RequireRayTracingPipeline(desc.Pipeline);
        RequireSameDevice(nativeDevice, pipeline, nameof(desc));
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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RayTracing capability = command.NativeDevice
            .RequireCapability<RayTracing>(nameof(UpdateRayTracingShaderTable));
        if (!capability.PipelineRayTracing)
            throw new NotSupportedException("Pipeline ray tracing is unavailable.");
        D3D12RayTracingShaderTable nativeTable = RequireRayTracingShaderTable(table);

        int resourceCount = update.Resources.Length;
        int recordCount = checked(
            update.RayGeneration.Length + update.Miss.Length +
            update.Hit.Length + update.Callable.Length);
        command.PrepareCaptures(checked(resourceCount * 2 + 1), resourceCount, resourceCount);
        command.PrepareSwapchainUses(resourceCount);
        command.PrepareOrdinaryData(checked(
                GetRayTableSize(nativeTable.Description) +
                (ulong)recordCount * 256UL +
                (ulong)update.OrdinaryData.Length));
        command.PrepareDescriptors(checked((uint)resourceCount), checked((uint)resourceCount));
        command.PrepareTransientObjects(checked((checked((uint)resourceCount) != 0 ? 1 : 0) + (checked((uint)resourceCount) != 0 ? 1 : 0)));
        command.PrepareRecordedRayTable(1, 1, update.ParameterBlocks.Length, resourceCount, update.OrdinaryData.Length, update.RayGeneration.Length, update.Miss.Length, update.Hit.Length, update.Callable.Length);
        D3D12RecordedRayTable recordedTable = command.RecordRayTableUpdate(nativeTable, update);
        command.CapturePipeline(nativeTable.Pipeline);
        foreach (ResourceBinding binding in recordedTable.Resources)
            command.Capture(binding);
        _ = recordedTable.GetDispatchData(command, nativeTable);
    }

    public void DispatchRays(CommandContext context, in DispatchRaysDesc desc)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RayTracing capability =
            command.NativeDevice.RequireCapability<RayTracing>(nameof(DispatchRays));
        if (!capability.PipelineRayTracing)
            throw new NotSupportedException("Pipeline ray tracing is unavailable.");
        D3D12RayTracingShaderTable table = RequireRayTracingShaderTable(desc.ShaderTable);
        if (desc.Width == 0 || desc.Height == 0 || desc.Depth == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));

        D3D12RayDispatchData dispatchData = command.GetRayDispatchData(table);
        Silk.NET.Direct3D12.DispatchRaysDesc native = dispatchData.Description;
        native.Width = desc.Width;
        native.Height = desc.Height;
        native.Depth = desc.Depth;
        command.PrepareCaptures(1);
        command.CapturePipeline(table.Pipeline);
        command.List->DispatchRays(&native);
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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RayTracing capability = command.NativeDevice
            .RequireCapability<RayTracing>(nameof(BuildAccelerationStructure));
        AccelerationStructurePrebuildMode mode =
            RequireAccelerationStructureBuildSupport(capability, desc.Options);

        D3D12AccelerationStructure destination =
            RequireAccelerationStructure(desc.Destination);

        D3D12AccelerationStructure? source = desc.Source is null
            ? null
            : RequireAccelerationStructure(desc.Source);

        D3D12Buffer scratch = RequireBuffer(desc.Scratch);
        BufferRange scratchRange = desc.ScratchRange.Resolve(scratch.Info.Size);
        if ((scratch.Info.Usages & BufferUsages.ShaderWrite) == 0 ||
            scratchRange.Offset % 256 != 0)
        {
            throw new ArgumentException(
                "Acceleration-structure scratch storage must be an aligned ShaderWrite Buffer range.",
                nameof(desc));
        }

        int geometryCount = desc.Type == AccelerationStructureType.BottomLevel
            ? desc.Geometries.Length
            : 0;
        int retainedCount = checked(3 + geometryCount * 3);
        command.PrepareCaptures(retainedCount, 0, checked(1 + geometryCount * 3));
        command.PrepareAccelerationStructureGeometries(geometryCount);
        Span<RaytracingGeometryDesc> nativeGeometries =
            command.ReserveAccelerationStructureGeometryDescriptions(geometryCount);
        BuildRaytracingAccelerationStructureInputs inputs = CreateBuildInputs(
            command.NativeDevice,
            desc.Type,
            desc.Options,
            desc.Geometries,
            nativeGeometries);
        RaytracingAccelerationStructurePrebuildInfo requirements = default;
        fixed (RaytracingGeometryDesc* geometryPointer = nativeGeometries)
        {
            if (nativeGeometries.Length != 0)
                inputs.PGeometryDescs = geometryPointer;
            command.NativeDevice.Native->GetRaytracingAccelerationStructurePrebuildInfo(
                &inputs,
                &requirements);
            ulong requiredScratch = RequireValidAccelerationStructurePrebuildInfo(
                requirements,
                mode);
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

    internal static ulong RequireValidAccelerationStructurePrebuildInfo(
        in RaytracingAccelerationStructurePrebuildInfo info,
        AccelerationStructurePrebuildMode mode)
    {
        ulong requiredScratch = mode.PerformUpdate
            ? info.UpdateScratchDataSizeInBytes
            : info.ScratchDataSizeInBytes;
        if (info.ResultDataMaxSizeInBytes == 0 ||
            (!mode.AllowUpdate && info.UpdateScratchDataSizeInBytes != 0))
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "D3D12 returned invalid acceleration-structure prebuild requirements: " +
                $"result={info.ResultDataMaxSizeInBytes}, " +
                $"buildScratch={info.ScratchDataSizeInBytes}, " +
                $"updateScratch={info.UpdateScratchDataSizeInBytes}, " +
                $"allowUpdate={mode.AllowUpdate}, performUpdate={mode.PerformUpdate}.");
        }

        return requiredScratch;
    }

    public void CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RayTracing capability = command.NativeDevice
            .RequireCapability<RayTracing>(nameof(CopyAccelerationStructure));
        if (type == AccelerationStructureCopyType.Compact && !capability.Compaction)
            throw new NotSupportedException("Acceleration-structure compaction is unavailable.");
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        D3D12AccelerationStructure nativeDestination = RequireAccelerationStructure(destination);
        D3D12AccelerationStructure nativeSource = RequireAccelerationStructure(source);

        command.PrepareCaptures(2);
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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RayTracing capability = command.NativeDevice
            .RequireCapability<RayTracing>(nameof(SerializeAccelerationStructure));
        if (!capability.Serialization)
            throw new NotSupportedException("Acceleration-structure serialization is unavailable.");
        D3D12Buffer nativeDestination = RequireBuffer(destination.Buffer);
        D3D12AccelerationStructure nativeSource = RequireAccelerationStructure(source);
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
        command.PrepareCaptures(2, 0, 1);
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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RayTracing capability = command.NativeDevice
            .RequireCapability<RayTracing>(nameof(DeserializeAccelerationStructure));
        if (!capability.Serialization)
            throw new NotSupportedException("Acceleration-structure serialization is unavailable.");
        D3D12AccelerationStructure nativeDestination =
            RequireAccelerationStructure(destination);
        D3D12Buffer nativeSource = RequireBuffer(source.Buffer);
        BufferRange sourceRange = source.Range.Resolve(nativeSource.Info.Size);
        if ((nativeSource.Info.Usages & BufferUsages.ShaderRead) == 0 ||
            sourceRange.Offset % 256 != 0)
        {
            throw new ArgumentException(
                "Serialized acceleration-structure input requires an aligned, non-empty " +
                "ShaderRead Buffer range.",
                nameof(source));
        }
        command.PrepareCaptures(2, 0, 1);
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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RayTracing capability = command.NativeDevice
            .RequireCapability<RayTracing>(nameof(EmitAccelerationStructurePostBuildInfo));
        if (type == AccelerationStructurePostBuildInfoType.CompactedSize &&
            !capability.Compaction)
        {
            throw new NotSupportedException("Acceleration-structure compaction is unavailable.");
        }
        if (type == AccelerationStructurePostBuildInfoType.SerializationSize &&
            !capability.Serialization)
        {
            throw new NotSupportedException("Acceleration-structure serialization is unavailable.");
        }
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        D3D12AccelerationStructure nativeSource = RequireAccelerationStructure(source);
        D3D12Buffer nativeDestination = RequireBuffer(destination);
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
        command.PrepareCaptures(2, 0, 1);
        command.Capture(nativeSource);
        command.Capture(nativeDestination);
        command.List->EmitRaytracingAccelerationStructurePostbuildInfo(&native, 1, &address);
    }

    private static AccelerationStructurePrebuildMode RequireAccelerationStructureBuildSupport(
        RayTracing capability,
        AccelerationStructureBuildOptions options)
    {
        AccelerationStructurePrebuildMode mode =
            RequireValidAccelerationStructureBuildOptions(options);
        if ((options & (AccelerationStructureBuildOptions.AllowUpdate |
                        AccelerationStructureBuildOptions.PerformUpdate)) != 0 &&
            !capability.AccelerationStructureUpdate)
        {
            throw new NotSupportedException(
                "Acceleration-structure update is unavailable.");
        }
        if ((options & AccelerationStructureBuildOptions.AllowCompaction) != 0 &&
            !capability.Compaction)
        {
            throw new NotSupportedException(
                "Acceleration-structure compaction is unavailable.");
        }
        return mode;
    }

    internal static AccelerationStructurePrebuildMode RequireValidAccelerationStructureBuildOptions(
        AccelerationStructureBuildOptions options)
    {
        const AccelerationStructureBuildOptions supported =
            AccelerationStructureBuildOptions.AllowUpdate |
            AccelerationStructureBuildOptions.AllowCompaction |
            AccelerationStructureBuildOptions.PreferFastTrace |
            AccelerationStructureBuildOptions.PreferFastBuild |
            AccelerationStructureBuildOptions.MinimizeMemory |
            AccelerationStructureBuildOptions.PerformUpdate;
        if ((options & ~supported) != 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if ((options & AccelerationStructureBuildOptions.PerformUpdate) != 0 &&
            (options & AccelerationStructureBuildOptions.AllowUpdate) == 0)
        {
            throw new ArgumentException(
                "PerformUpdate requires AllowUpdate.",
                nameof(options));
        }
        if ((options & AccelerationStructureBuildOptions.PreferFastTrace) != 0 &&
            (options & AccelerationStructureBuildOptions.PreferFastBuild) != 0)
        {
            throw new ArgumentException(
                "PreferFastTrace and PreferFastBuild are mutually exclusive.",
                nameof(options));
        }
        return new AccelerationStructurePrebuildMode(
            (options & AccelerationStructureBuildOptions.AllowUpdate) != 0,
            (options & AccelerationStructureBuildOptions.PerformUpdate) != 0);
    }

    internal readonly struct AccelerationStructurePrebuildMode
    {
        internal AccelerationStructurePrebuildMode(bool allowUpdate, bool performUpdate)
        {
            AllowUpdate = allowUpdate;
            PerformUpdate = performUpdate;
        }

        internal bool AllowUpdate { get; }

        internal bool PerformUpdate { get; }
    }

    private BuildRaytracingAccelerationStructureInputs CreateBuildInputs(
        D3D12Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries,
        Span<RaytracingGeometryDesc> nativeGeometries)
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
            PopulateTopLevelBuildInputs(device, geometries, ref result);
            return result;
        }

        if (nativeGeometries.Length != geometries.Length)
        {
            throw new ArgumentException(
                "Bottom-level build scratch must match the geometry count.",
                nameof(nativeGeometries));
        }
        for (int index = 0; index < geometries.Length; index++)
            nativeGeometries[index] = CreateNativeGeometry(device, geometries[index]);
        result.NumDescs = checked((uint)nativeGeometries.Length);
        return result;
    }

    private void PopulateTopLevelBuildInputs(
        D3D12Device device,
        ReadOnlySpan<AccelerationStructureGeometry> geometries,
        ref BuildRaytracingAccelerationStructureInputs result)
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
    }

    private RaytracingGeometryDesc CreateNativeGeometry(
        D3D12Device device,
        in AccelerationStructureGeometry geometry)
    {
        if (geometry.Count == 0)
        {
            throw new ArgumentException(
                "A bottom-level geometry description is invalid.",
                "geometries");
        }
        RaytracingGeometryDesc result = new()
        {
            Type = geometry.Type switch
            {
                AccelerationStructureGeometryType.Triangles => RaytracingGeometryType.Triangles,
                AccelerationStructureGeometryType.AxisAlignedBoundingBoxes =>
                    RaytracingGeometryType.ProceduralPrimitiveAabbs,
                _ => throw new ArgumentOutOfRangeException("geometries"),
            },
            Flags = ToNativeGeometryFlags(geometry.Options),
        };
        if (geometry.Type == AccelerationStructureGeometryType.Triangles)
            result.Triangles = CreateTriangleGeometry(device, geometry);
        else
            result.AABBs = CreateAabbGeometry(device, geometry);
        return result;
    }

    private RaytracingGeometryTrianglesDesc CreateTriangleGeometry(
        D3D12Device device,
        in AccelerationStructureGeometry geometry)
    {
        (uint positionSize, uint componentAlignment) =
            GetRayTracingVertexLayout(geometry.PrimaryFormat);
        if (geometry.PrimaryStride < positionSize ||
            geometry.PrimaryStride % componentAlignment != 0)
        {
            throw new ArgumentException(
                "A triangle vertex format or stride is invalid.",
                "geometries");
        }
        D3D12Buffer vertices = ResolveInputRegion(
            device,
            geometry.Primary,
            checked((ulong)geometry.Count * geometry.PrimaryStride),
            componentAlignment,
            "geometries",
            out BufferRange vertexRange);
        RaytracingGeometryTrianglesDesc result = new()
        {
            VertexFormat = FormatMappings.ToDxgi(geometry.PrimaryFormat),
            VertexCount = geometry.Count,
            VertexBuffer = new GpuVirtualAddressAndStride(
                vertices.Native->GetGPUVirtualAddress() + vertexRange.Offset,
                geometry.PrimaryStride),
            IndexFormat = NativeFormat.FormatUnknown,
        };
        if (geometry.Secondary.Buffer is not null)
            PopulateTriangleIndices(device, geometry, ref result);
        if (geometry.Transform.Buffer is not null)
        {
            D3D12Buffer transform = ResolveInputRegion(
                device,
                geometry.Transform,
                48,
                16,
                "geometries",
                out BufferRange transformRange);
            result.Transform3x4 = transform.Native->GetGPUVirtualAddress() + transformRange.Offset;
        }
        return result;
    }

    private void PopulateTriangleIndices(
        D3D12Device device,
        in AccelerationStructureGeometry geometry,
        ref RaytracingGeometryTrianglesDesc triangles)
    {
        uint indexSize = geometry.IndexType switch
        {
            IndexType.UInt16 => 2,
            IndexType.UInt32 => 4,
            _ => throw new ArgumentOutOfRangeException("geometries"),
        };
        D3D12Buffer indices = ResolveInputRegion(
            device,
            geometry.Secondary,
            indexSize,
            indexSize,
            "geometries",
            out BufferRange indexRange);
        if (indexRange.Size % indexSize != 0 || indexRange.Size / indexSize > uint.MaxValue)
            throw new ArgumentException("The triangle index range is invalid.", "geometries");
        triangles.IndexFormat = geometry.IndexType switch
        {
            IndexType.UInt16 => NativeFormat.FormatR16Uint,
            IndexType.UInt32 => NativeFormat.FormatR32Uint,
            _ => throw new ArgumentOutOfRangeException("geometries"),
        };
        triangles.IndexCount = checked((uint)(indexRange.Size / indexSize));
        triangles.IndexBuffer = indices.Native->GetGPUVirtualAddress() + indexRange.Offset;
    }

    private RaytracingGeometryAabbsDesc CreateAabbGeometry(
        D3D12Device device,
        in AccelerationStructureGeometry geometry)
    {
        if (geometry.PrimaryStride < 24 || geometry.PrimaryStride % 8 != 0)
        {
            throw new ArgumentException(
                "An AABB stride must be at least 24 bytes and 8-byte aligned.",
                "geometries");
        }
        D3D12Buffer aabbs = ResolveInputRegion(
            device,
            geometry.Primary,
            checked((ulong)geometry.Count * geometry.PrimaryStride),
            8,
            "geometries",
            out BufferRange range);
        return new RaytracingGeometryAabbsDesc(
            geometry.Count,
            new GpuVirtualAddressAndStride(
                aabbs.Native->GetGPUVirtualAddress() + range.Offset,
                geometry.PrimaryStride));
    }

    private D3D12Buffer ResolveInputRegion(
        D3D12Device device,
        in BufferRegion region,
        ulong minimumSize,
        ulong alignment,
        string parameter,
        out BufferRange range)
    {
        D3D12Buffer buffer = RequireBuffer(region.Buffer);
        if (!ReferenceEquals(buffer.Device, device))
        {
            throw new ArgumentException(
                "An acceleration-structure input Buffer belongs to another Device.",
                parameter);
        }
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

    private void CaptureGeometryResources(
        D3D12CommandContext command,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
        foreach (ref readonly AccelerationStructureGeometry geometry in geometries)
        {
            command.Capture(RequireBuffer(geometry.Primary.Buffer));
            if (geometry.Secondary.Buffer is not null)
                command.Capture(RequireBuffer(geometry.Secondary.Buffer));
            if (geometry.Transform.Buffer is not null)
                command.Capture(RequireBuffer(geometry.Transform.Buffer));
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
        D3D12RootSignatureState global,
        IReadOnlyList<D3D12RootSignatureState> localRoots,
        CompiledProgramLibrary library,
        IReadOnlyList<D3D12RayTracingExport> exports,
        IReadOnlyList<RayTracingHitGroupState> hitGroups,
        in RayTracingPipelineDesc desc)
    {
        uint nodeMask = desc.NodeMask;
        uint maximumRecursionDepth = desc.MaximumRecursionDepth;
        uint maximumPayloadSize = desc.MaximumPayloadSize;
        uint maximumAttributeSize = desc.MaximumAttributeSize;
        RayTracingPipelineOptions options = desc.Options;
        return CreateCanonicalPipelineKey(
            device,
            4,
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
                writer.Write(checked((uint)localRoots.Count));
                foreach (D3D12RootSignatureState root in localRoots)
                    WriteCanonicalBytes(writer, root.Serialized);
            },
            writer =>
            {
                writer.Write(nodeMask);
                writer.Write(checked((uint)exports.Count));
                foreach (D3D12RayTracingExport value in exports)
                {
                    writer.Write((byte)value.Type);
                    WriteCanonicalString(writer, value.Name);
                }
                writer.Write(checked((uint)hitGroups.Count));
                foreach (RayTracingHitGroupState value in hitGroups)
                {
                    WriteCanonicalString(writer, value.Name);
                    WriteCanonicalString(writer, value.ClosestHit ?? string.Empty);
                    WriteCanonicalString(writer, value.AnyHit ?? string.Empty);
                    WriteCanonicalString(writer, value.Intersection ?? string.Empty);
                    writer.Write((int)value.Type);
                }
                writer.Write(maximumRecursionDepth);
                writer.Write(maximumPayloadSize);
                writer.Write(maximumAttributeSize);
                writer.Write((byte)options);
            });
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
    }

    private sealed class D3D12AccelerationStructure : AccelerationStructure
    {
        private readonly D3D12Device _device;
        private readonly D3D12Buffer _storage;
        private readonly NativeLease _storageLifetime;

        internal D3D12AccelerationStructure(
            D3D12Device device,
            D3D12Buffer storage,
            in AccelerationStructureInfo info,
            string? label)
            : base(device, info, label)
        {
            _device = device;
            _storage = storage;
            _storageLifetime = storage.NativeLifetime;
            _storageLifetime.Retain();
        }

        internal ulong Address =>
            _storage.Native->GetGPUVirtualAddress() + Info.StorageRange.Offset;
        internal D3D12Buffer Storage => _storage;
        internal NativeLease NativeLifetime => _storageLifetime;

        internal override void Release(bool fromParent)
        {
            _storageLifetime.Release();
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
            VariableLayoutReflection[] layouts,
            D3D12RootSignatureState localRoot,
            RayRecordType type,
            byte[] identifier)
        {
            Name = name;
            EntryPoint = entryPoint;
            Layouts = layouts;
            LocalRoot = localRoot;
            Type = type;
            Identifier = identifier;
        }

        internal string Name { get; }
        internal EntryPointReflection EntryPoint { get; }
        internal VariableLayoutReflection[] Layouts { get; }
        internal D3D12RootSignatureState LocalRoot { get; }
        internal RayRecordType Type { get; }
        internal byte[] Identifier { get; }
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
            NativeLease native,
            D3D12RootSignatureState root,
            D3D12RootSignatureState[] localRoots,
            NativeLease[] additionalLeases,
            RetainedSlangProgram program,
            Dictionary<EntryPointReflection, D3D12RayTracingExport> entries,
            Dictionary<string, D3D12RayTracingExport> hitGroups,
            string? label)
            : base(
                device,
                native,
                root,
                localRoots,
                additionalLeases,
                program,
                PipelineType.RayTracing,
                label)
        {
            _properties = additionalLeases[0];
            _entries = entries;
            _hitGroups = hitGroups;
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

    }

    private sealed class D3D12RayTracingShaderTable : RayTracingShaderTable
    {
        private readonly D3D12Device _device;
        private RetainedSlangProgram? _program;
        private readonly NativeLease _pipelineState;

        internal D3D12RayTracingShaderTable(
            D3D12Device device,
            D3D12RayTracingPipeline pipeline,
            in RayTracingShaderTableDesc description)
            : base(device, description)
        {
            _device = device;
            Pipeline = pipeline;
            RetainedSlangProgram? program = null;
            NativeLease? pipelineState = null;
            try
            {
                program = pipeline.RetainProgramReference();
                pipelineState = pipeline.RetainNativeState();
                _program = program;
                program = null;
                _pipelineState = pipelineState;
            }
            catch
            {
                pipelineState?.Release();
                program?.Dispose();
                throw;
            }
        }

        internal D3D12RayTracingPipeline Pipeline { get; }

        internal override void Release(bool fromParent)
        {
            _pipelineState.Release();
            Interlocked.Exchange(ref _program, null)?.Dispose();
            _device.UnregisterChild(this);
        }
    }

    private readonly record struct D3D12RecordedRayRecord(
        D3D12RayTracingExport Export,
        uint ParameterBlockOffset,
        uint ParameterBlockCount);

    private sealed class D3D12RecordedRayTable
    {
        private D3D12RecordedRayRecord[] _rayGeneration = [];
        private D3D12RecordedRayRecord[] _miss = [];
        private D3D12RecordedRayRecord[] _hit = [];
        private D3D12RecordedRayRecord[] _callable = [];
        private RayTracingLocalParameterBlock[] _parameterBlocks = [];
        private ResourceBinding[] _resources = [];
        private byte[] _ordinaryData = [];
        private int _rayGenerationCount;
        private int _missCount;
        private int _hitCount;
        private int _callableCount;
        private int _parameterBlockCount;
        private int _resourceCount;
        private int _ordinaryDataCount;
        private D3D12RayDispatchData _dispatchData;
        private bool _hasDispatchData;

        internal ReadOnlySpan<D3D12RecordedRayRecord> RayGeneration =>
            _rayGeneration.AsSpan(0, _rayGenerationCount);
        internal ReadOnlySpan<D3D12RecordedRayRecord> Miss => _miss.AsSpan(0, _missCount);
        internal ReadOnlySpan<D3D12RecordedRayRecord> Hit => _hit.AsSpan(0, _hitCount);
        internal ReadOnlySpan<D3D12RecordedRayRecord> Callable =>
            _callable.AsSpan(0, _callableCount);
        internal ReadOnlySpan<RayTracingLocalParameterBlock> ParameterBlocks =>
            _parameterBlocks.AsSpan(0, _parameterBlockCount);
        internal ReadOnlySpan<ResourceBinding> Resources => _resources.AsSpan(0, _resourceCount);
        internal ReadOnlySpan<byte> OrdinaryData => _ordinaryData.AsSpan(0, _ordinaryDataCount);

        internal void RecordUpdate(
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

            PrepareCapacity(
                update.ParameterBlocks.Length,
                update.Resources.Length,
                update.OrdinaryData.Length,
                update.RayGeneration.Length,
                update.Miss.Length,
                update.Hit.Length,
                update.Callable.Length);
            update.ParameterBlocks.CopyTo(_parameterBlocks);
            update.Resources.CopyTo(_resources);
            update.OrdinaryData.CopyTo(_ordinaryData);
            _parameterBlockCount = update.ParameterBlocks.Length;
            _resourceCount = update.Resources.Length;
            _ordinaryDataCount = update.OrdinaryData.Length;
            _rayGenerationCount = CaptureRecords(
                table,
                update.RayGeneration,
                RayRecordType.RayGeneration,
                _parameterBlocks.AsSpan(0, _parameterBlockCount),
                _resources.AsSpan(0, _resourceCount),
                _ordinaryData.AsSpan(0, _ordinaryDataCount),
                _rayGeneration);
            _missCount = CaptureRecords(
                table,
                update.Miss,
                RayRecordType.Miss,
                _parameterBlocks.AsSpan(0, _parameterBlockCount),
                _resources.AsSpan(0, _resourceCount),
                _ordinaryData.AsSpan(0, _ordinaryDataCount),
                _miss);
            _hitCount = CaptureRecords(
                table,
                update.Hit,
                RayRecordType.Hit,
                _parameterBlocks.AsSpan(0, _parameterBlockCount),
                _resources.AsSpan(0, _resourceCount),
                _ordinaryData.AsSpan(0, _ordinaryDataCount),
                _hit);
            _callableCount = CaptureRecords(
                table,
                update.Callable,
                RayRecordType.Callable,
                _parameterBlocks.AsSpan(0, _parameterBlockCount),
                _resources.AsSpan(0, _resourceCount),
                _ordinaryData.AsSpan(0, _ordinaryDataCount),
                _callable);
            _hasDispatchData = false;
        }

        internal void PrepareCapacity(
            int parameterBlockCount,
            int resourceCount,
            int ordinaryDataCount,
            int rayGenerationCount,
            int missCount,
            int hitCount,
            int callableCount)
        {
            EnsureCapacity(ref _parameterBlocks, parameterBlockCount);
            EnsureCapacity(ref _resources, resourceCount);
            EnsureCapacity(ref _ordinaryData, ordinaryDataCount);
            EnsureCapacity(ref _rayGeneration, rayGenerationCount);
            EnsureCapacity(ref _miss, missCount);
            EnsureCapacity(ref _hit, hitCount);
            EnsureCapacity(ref _callable, callableCount);
        }

        internal D3D12RayDispatchData GetDispatchData(
            D3D12CommandContext command,
            D3D12RayTracingShaderTable table)
        {
            D3D12CommandSlot slot = command.Recording;
            if (_hasDispatchData &&
                _dispatchData.DescriptorArenaVersion == slot.DescriptorArenaVersion)
            {
                return _dispatchData;
            }

            D3D12RayDispatchData dispatchData = BuildDispatchData(command, table);
            _dispatchData = dispatchData;
            _hasDispatchData = true;
            return dispatchData;
        }

        private static int CaptureRecords(
            D3D12RayTracingShaderTable table,
            ReadOnlySpan<RayTracingShaderRecord> records,
            RayRecordType type,
            ReadOnlySpan<RayTracingLocalParameterBlock> parameterBlocks,
            ReadOnlySpan<ResourceBinding> resources,
            ReadOnlySpan<byte> ordinaryData,
            D3D12RecordedRayRecord[] destination)
        {
            for (int index = 0; index < records.Length; index++)
            {
                ref readonly RayTracingShaderRecord record = ref records[index];
                D3D12RayTracingExport export = type == RayRecordType.Hit
                    ? table.Pipeline.GetHitGroup(record.HitGroupName!)
                    : table.Pipeline.GetEntry(record.EntryPoint, type);

                ValidateSlice(
                    record.ParameterBlockOffset,
                    record.ParameterBlockCount,
                    parameterBlocks.Length,
                    nameof(records));
                ReadOnlySpan<RayTracingLocalParameterBlock> blocks = parameterBlocks.Slice(
                    checked((int)record.ParameterBlockOffset),
                    checked((int)record.ParameterBlockCount));
                if (blocks.Length != export.Layouts.Length)
                {
                    throw new ArgumentException(
                        "A ray-tracing shader record must provide exactly one parameter block for each exact Slang local layout associated with its export.",
                        nameof(records));
                }
                for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
                {
                    ref readonly RayTracingLocalParameterBlock block = ref blocks[blockIndex];
                    bool expected = false;
                    foreach (VariableLayoutReflection layout in export.Layouts)
                        expected |= block.Layout == layout;
                    if (!expected)
                    {
                        throw new ArgumentException(
                            "A ray-tracing shader record contains a parameter block that is not an exact Slang local layout of its export.",
                            nameof(records));
                    }
                    for (int prior = 0; prior < blockIndex; prior++)
                    {
                        if (blocks[prior].Layout == block.Layout)
                        {
                            throw new ArgumentException(
                                "A ray-tracing shader record repeats the same Slang local parameter block.",
                                nameof(records));
                        }
                    }
                    ValidateSlice(
                        block.ResourceOffset,
                        block.ResourceCount,
                        resources.Length,
                        nameof(records));
                    ValidateSlice(
                        block.OrdinaryDataOffset,
                        block.OrdinaryDataSize,
                        ordinaryData.Length,
                        nameof(records));
                    ReadOnlySpan<ResourceBinding> bindings = resources.Slice(
                        checked((int)block.ResourceOffset),
                        checked((int)block.ResourceCount));
                    ReadOnlySpan<byte> data = ordinaryData.Slice(
                        checked((int)block.OrdinaryDataOffset),
                        checked((int)block.OrdinaryDataSize));
                    RequireNativeParameterBindings(
                        block.Layout,
                        export.LocalRoot.GetBlock(block.Layout),
                        bindings,
                        data);
                }
                destination[index] = new D3D12RecordedRayRecord(
                    export,
                    record.ParameterBlockOffset,
                    record.ParameterBlockCount);
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
            Array.Clear(_parameterBlocks, 0, _parameterBlockCount);
            Array.Clear(_resources, 0, _resourceCount);
            _rayGenerationCount = 0;
            _missCount = 0;
            _hitCount = 0;
            _callableCount = 0;
            _parameterBlockCount = 0;
            _resourceCount = 0;
            _ordinaryDataCount = 0;
            _dispatchData = default;
            _hasDispatchData = false;
        }

        private static void EnsureCapacity<T>(ref T[] values, int capacity)
        {
            if (values.Length < capacity)
                Array.Resize(ref values, capacity);
        }

        private D3D12RayDispatchData BuildDispatchData(
            D3D12CommandContext command,
            D3D12RayTracingShaderTable table)
        {
            uint resourceCount = 0;
            uint samplerCount = 0;
            ulong ordinarySize = 0;
            CountAllDispatchDataRequirements(
                ref resourceCount,
                ref samplerCount,
                ref ordinarySize);

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
                throw new InvalidOperationException("Ray dispatch data cannot be empty.");

            D3D12OrdinaryDataReservation storage = command.ReserveTransientOrdinaryData(totalSize);
            Span<byte> destination = storage.CommitSpan(checked((int)totalSize), clear: true);
            ulong baseAddress = storage.Address;
            uint resourceCursor = resourceBase;
            uint samplerCursor = samplerBase;
            ulong ordinaryCursor = ordinaryOffset;
            WriteRayRecordCategory(
                command,
                RayGeneration,
                rayGenerationOffset,
                stride,
                destination,
                totalSize,
                baseAddress,
                ref resourceCursor,
                ref samplerCursor,
                ref ordinaryCursor);
            WriteRayRecordCategory(
                command,
                Miss,
                missOffset,
                stride,
                destination,
                totalSize,
                baseAddress,
                ref resourceCursor,
                ref samplerCursor,
                ref ordinaryCursor);
            WriteRayRecordCategory(
                command,
                Hit,
                hitOffset,
                stride,
                destination,
                totalSize,
                baseAddress,
                ref resourceCursor,
                ref samplerCursor,
                ref ordinaryCursor);
            WriteRayRecordCategory(
                command,
                Callable,
                callableOffset,
                stride,
                destination,
                totalSize,
                baseAddress,
                ref resourceCursor,
                ref samplerCursor,
                ref ordinaryCursor);
            Silk.NET.Direct3D12.DispatchRaysDesc native = new()
            {
                RayGenerationShaderRecord = new GpuVirtualAddressRange(
                    baseAddress + rayGenerationOffset,
                    stride),
                MissShaderTable = CreateRayDispatchRange(
                    baseAddress,
                    stride,
                    Miss.Length,
                    missOffset),
                HitGroupTable = CreateRayDispatchRange(
                    baseAddress,
                    stride,
                    Hit.Length,
                    hitOffset),
                CallableShaderTable = CreateRayDispatchRange(
                    baseAddress,
                    stride,
                    Callable.Length,
                    callableOffset),
                Width = 1,
                Height = 1,
                Depth = 1,
            };
            return new D3D12RayDispatchData(
                command.Recording.DescriptorArenaVersion,
                native);
        }

        private void CountAllDispatchDataRequirements(
            ref uint resourceCount,
            ref uint samplerCount,
            ref ulong ordinarySize)
        {
            CountDispatchDataRequirements(
                RayGeneration,
                ref resourceCount,
                ref samplerCount,
                ref ordinarySize);
            CountDispatchDataRequirements(
                Miss,
                ref resourceCount,
                ref samplerCount,
                ref ordinarySize);
            CountDispatchDataRequirements(
                Hit,
                ref resourceCount,
                ref samplerCount,
                ref ordinarySize);
            CountDispatchDataRequirements(
                Callable,
                ref resourceCount,
                ref samplerCount,
                ref ordinarySize);
        }

        private void CountDispatchDataRequirements(
            ReadOnlySpan<D3D12RecordedRayRecord> records,
            ref uint resourceCount,
            ref uint samplerCount,
            ref ulong ordinarySize)
        {
            foreach (D3D12RecordedRayRecord record in records)
            {
                ReadOnlySpan<RayTracingLocalParameterBlock> blocks = ParameterBlocks.Slice(
                    checked((int)record.ParameterBlockOffset),
                    checked((int)record.ParameterBlockCount));
                foreach (ref readonly RayTracingLocalParameterBlock block in blocks)
                {
                    NativeParameterBinding layout = record.Export.LocalRoot.GetBlock(block.Layout);
                    resourceCount = checked(
                        resourceCount + (layout.ResourceTable?.DescriptorCount ?? 0));
                    samplerCount = checked(
                        samplerCount + (layout.SamplerTable?.DescriptorCount ?? 0));
                    if (layout.OrdinaryRoot is OrdinaryRootBinding ordinary &&
                        !ordinary.UsesRootConstants)
                    {
                        ordinarySize = checked(
                            ordinarySize + AlignUp(block.OrdinaryDataSize, 256));
                    }
                }
            }
        }

        private void WriteRayRecordCategory(
            D3D12CommandContext command,
            ReadOnlySpan<D3D12RecordedRayRecord> records,
            ulong categoryOffset,
            ulong stride,
            Span<byte> destination,
            ulong totalSize,
            ulong baseAddress,
            ref uint resourceCursor,
            ref uint samplerCursor,
            ref ulong ordinaryCursor)
        {
            for (int index = 0; index < records.Length; index++)
            {
                D3D12RecordedRayRecord record = records[index];
                Span<byte> target = destination.Slice(
                    checked((int)(categoryOffset + checked((ulong)index * stride))),
                    checked((int)stride));
                record.Export.Identifier.CopyTo(target);
                if (record.ParameterBlockCount == 0)
                    continue;
                WriteDefaultRayRootTables(command, record, target);
                ReadOnlySpan<RayTracingLocalParameterBlock> blocks = ParameterBlocks.Slice(
                    checked((int)record.ParameterBlockOffset),
                    checked((int)record.ParameterBlockCount));
                foreach (ref readonly RayTracingLocalParameterBlock block in blocks)
                {
                    WriteRayParameterBlock(
                        command,
                        record,
                        block,
                        target,
                        destination,
                        totalSize,
                        baseAddress,
                        ref resourceCursor,
                        ref samplerCursor,
                        ref ordinaryCursor);
                }
            }
        }

        private static void WriteDefaultRayRootTables(
            D3D12CommandContext command,
            in D3D12RecordedRayRecord record,
            Span<byte> target)
        {
            foreach (DefaultRootTable defaultTable in record.Export.LocalRoot.DefaultTables)
            {
                GpuDescriptorHandle handle = command.Recording.GetGpuHandle(
                    defaultTable.Heap,
                    0);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                    target.Slice(checked(32 + (int)defaultTable.RootArgumentOffset)),
                    handle.Ptr);
            }
        }

        private void WriteRayParameterBlock(
            D3D12CommandContext command,
            in D3D12RecordedRayRecord record,
            in RayTracingLocalParameterBlock block,
            Span<byte> target,
            Span<byte> destination,
            ulong totalSize,
            ulong baseAddress,
            ref uint resourceCursor,
            ref uint samplerCursor,
            ref ulong ordinaryCursor)
        {
            NativeParameterBinding layout = record.Export.LocalRoot.GetBlock(block.Layout);
            ReadOnlySpan<ResourceBinding> bindings = Resources.Slice(
                checked((int)block.ResourceOffset),
                checked((int)block.ResourceCount));
            uint recordResourceBase = resourceCursor;
            uint recordSamplerBase = samplerCursor;
            CopyRayRecordDescriptors(
                command,
                layout,
                bindings,
                ref resourceCursor,
                ref samplerCursor);
            WriteRayRecordTableHandles(
                command,
                layout,
                target,
                recordResourceBase,
                recordSamplerBase);
            if (layout.OrdinaryRoot is not OrdinaryRootBinding ordinaryRoot)
                return;
            ReadOnlySpan<byte> data = OrdinaryData.Slice(
                checked((int)block.OrdinaryDataOffset),
                checked((int)block.OrdinaryDataSize));
            WriteRayOrdinaryData(
                ordinaryRoot,
                data,
                target,
                destination,
                totalSize,
                baseAddress,
                ref ordinaryCursor);
        }

        private static void CopyRayRecordDescriptors(
            D3D12CommandContext command,
            NativeParameterBinding layout,
            ReadOnlySpan<ResourceBinding> bindings,
            ref uint resourceCursor,
            ref uint samplerCursor)
        {
            if (bindings.Length != layout.Slots.Length)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "The DXR local-record binding count does not match its Slang-reflected descriptor shape.");
            }
            for (int ordinal = 0; ordinal < bindings.Length; ordinal++)
            {
                ref readonly ResourceBinding binding = ref bindings[ordinal];
                ref readonly DescriptorSlotDesc slot = ref layout.Slots[ordinal];
                ParameterHeap heap = slot.Type == ResourceBindingType.Sampler
                    ? ParameterHeap.Sampler
                    : ParameterHeap.Resource;
                command.CopyTransientDescriptor(
                    heap,
                    heap == ParameterHeap.Resource ? resourceCursor++ : samplerCursor++,
                    binding,
                    slot);
            }
        }

        private static void WriteRayRecordTableHandles(
            D3D12CommandContext command,
            NativeParameterBinding layout,
            Span<byte> target,
            uint resourceBase,
            uint samplerBase)
        {
            if (layout.ResourceTable is D3D12BoundedTable resourceTable)
            {
                GpuDescriptorHandle handle = command.Recording.GetGpuHandle(
                    ParameterHeap.Resource,
                    resourceBase);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                    target.Slice(checked(32 + (int)resourceTable.RootArgumentOffset)),
                    handle.Ptr);
            }
            if (layout.SamplerTable is D3D12BoundedTable samplerTable)
            {
                GpuDescriptorHandle handle = command.Recording.GetGpuHandle(
                    ParameterHeap.Sampler,
                    samplerBase);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                    target.Slice(checked(32 + (int)samplerTable.RootArgumentOffset)),
                    handle.Ptr);
            }
        }

        private static void WriteRayOrdinaryData(
            in OrdinaryRootBinding ordinaryRoot,
            ReadOnlySpan<byte> data,
            Span<byte> target,
            Span<byte> destination,
            ulong totalSize,
            ulong baseAddress,
            ref ulong ordinaryCursor)
        {
            if (ordinaryRoot.UsesRootConstants)
            {
                Span<byte> constants = target.Slice(
                    checked(32 + (int)ordinaryRoot.RootArgumentOffset),
                    checked((int)ordinaryRoot.ConstantCount * sizeof(uint)));
                constants.Clear();
                data.CopyTo(constants);
                return;
            }
            ordinaryCursor = AlignUp(ordinaryCursor, 256);
            data.CopyTo(destination.Slice(
                checked((int)ordinaryCursor),
                checked((int)(totalSize - ordinaryCursor))));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                target.Slice(checked(32 + (int)ordinaryRoot.RootArgumentOffset)),
                baseAddress + ordinaryCursor);
            ordinaryCursor = checked(ordinaryCursor + AlignUp((ulong)data.Length, 256));
        }

        private static GpuVirtualAddressRangeAndStride CreateRayDispatchRange(
            ulong baseAddress,
            ulong stride,
            int count,
            ulong offset) =>
            count == 0
                ? default
                : new GpuVirtualAddressRangeAndStride(
                    baseAddress + offset,
                    checked((ulong)count * stride),
                    stride);
    }

    private readonly record struct D3D12RayDispatchData(
        ulong DescriptorArenaVersion,
        Silk.NET.Direct3D12.DispatchRaysDesc Description);

    private sealed partial class D3D12CommandSlot
    {
        private RaytracingGeometryDesc[] _accelerationStructureGeometryDescriptions = [];
        private readonly Dictionary<D3D12RayTracingShaderTable, D3D12RecordedRayTable>
            _recordedRayTables = new(ReferenceEqualityComparer.Instance);
        private readonly List<D3D12RecordedRayTable> _recordedRayTablePool = [];
        private int _recordedRayTableCount;

        internal void PrepareRecordedRayTable(
            int tableCount,
            int poolCount,
            int parameterBlockCount,
            int resourceCount,
            int ordinaryDataByteCount,
            int rayGenerationCount,
            int missCount,
            int hitCount,
            int callableCount)
        {
            if (tableCount != 0)
            {
                _recordedRayTables.EnsureCapacity(
                    checked(_recordedRayTables.Count + tableCount));
            }
            int poolRequired = checked(_recordedRayTableCount + poolCount);
            if (poolRequired > _recordedRayTablePool.Count)
            {
                _recordedRayTablePool.EnsureCapacity(poolRequired);
                while (_recordedRayTablePool.Count < poolRequired)
                    _recordedRayTablePool.Add(new D3D12RecordedRayTable());
            }
            if (poolCount != 0)
            {
                _recordedRayTablePool[_recordedRayTableCount].PrepareCapacity(
                    parameterBlockCount,
                    resourceCount,
                    ordinaryDataByteCount,
                    rayGenerationCount,
                    missCount,
                    hitCount,
                    callableCount);
            }
        }

        internal void PrepareAccelerationStructureGeometries(int count)
        {
            if (count > _accelerationStructureGeometryDescriptions.Length)
                Array.Resize(ref _accelerationStructureGeometryDescriptions, count);
        }

        internal Span<RaytracingGeometryDesc> ReserveAccelerationStructureGeometryDescriptions(
            int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (_accelerationStructureGeometryDescriptions.Length < count)
            {
                int doubled = _accelerationStructureGeometryDescriptions.Length <= int.MaxValue / 2
                    ? _accelerationStructureGeometryDescriptions.Length * 2
                    : int.MaxValue;
                Array.Resize(
                    ref _accelerationStructureGeometryDescriptions,
                    Math.Max(count, Math.Max(4, doubled)));
            }
            return _accelerationStructureGeometryDescriptions.AsSpan(0, count);
        }

        internal D3D12RecordedRayTable RecordRayTableUpdate(
            D3D12RayTracingShaderTable table,
            in RayTracingShaderTableUpdate update)
        {
            _recordedRayTables.EnsureCapacity(checked(_recordedRayTables.Count + 1));
            D3D12RecordedRayTable recordedTable;
            if (_recordedRayTableCount < _recordedRayTablePool.Count)
            {
                recordedTable = _recordedRayTablePool[_recordedRayTableCount];
            }
            else
            {
                _recordedRayTablePool.EnsureCapacity(checked(_recordedRayTablePool.Count + 1));
                recordedTable = new D3D12RecordedRayTable();
                _recordedRayTablePool.Add(recordedTable);
            }
            try
            {
                recordedTable.RecordUpdate(table, update);
            }
            catch
            {
                recordedTable.Reset();
                throw;
            }
            _recordedRayTableCount++;
            _recordedRayTables[table] = recordedTable;
            return recordedTable;
        }

        internal D3D12RecordedRayTable GetRecordedRayTable(
            D3D12RayTracingShaderTable table) =>
            _recordedRayTables.TryGetValue(table, out D3D12RecordedRayTable? recordedTable)
                ? recordedTable
                : throw new InvalidOperationException(
                    "UpdateRayTracingShaderTable must precede dispatch in this recording.");

        internal void ClearRecordedRayTables()
        {
            for (int index = 0; index < _recordedRayTableCount; index++)
                _recordedRayTablePool[index].Reset();
            _recordedRayTableCount = 0;
            _recordedRayTables.Clear();
        }
    }

    private sealed partial class D3D12CommandContext
    {
        internal Span<RaytracingGeometryDesc> ReserveAccelerationStructureGeometryDescriptions(
            int count) => Recording.ReserveAccelerationStructureGeometryDescriptions(count);

        internal D3D12RecordedRayTable RecordRayTableUpdate(
            D3D12RayTracingShaderTable table,
            in RayTracingShaderTableUpdate update) =>
            Recording.RecordRayTableUpdate(table, update);

        internal D3D12RayDispatchData GetRayDispatchData(
            D3D12RayTracingShaderTable table) =>
            Recording.GetRecordedRayTable(table).GetDispatchData(this, table);
    }

    private sealed class D3D12AccelerationStructureSrv : AccelerationStructureSrv, INativeDescriptor
    {
        private readonly ViewReferences _references;

        internal D3D12AccelerationStructureSrv(
            D3D12Device device,
            D3D12AccelerationStructure structure,
            in AccelerationStructureSrvDesc description,
            DescriptorLease descriptor)
            : base(device, description)
        {
            NativeDescriptor = descriptor;
            _references = new ViewReferences(device, descriptor, structure.NativeLifetime);
        }

        public DescriptorLease NativeDescriptor { get; }

        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed partial class D3D12CommandContext
    {
        internal void Capture(D3D12AccelerationStructure value)
        {
            RequireResourceVisible(
                value.Storage.Info.VisibleNodeMask,
                nameof(value));
            Recording.Capture(value.NativeLifetime);
        }

        internal void Capture(AccelerationStructureSrv value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12AccelerationStructure structure =
                RequireD3D12.AccelerationStructure(value.Resource);
            D3D12CommandSlot slot = Recording;
            slot.Capture(descriptor.NativeDescriptor, structure.NativeLifetime);
        }
    }

    private static partial class RequireD3D12
    {
        internal static D3D12RayTracingPipeline RayTracingPipeline(Pipeline value) =>
            value as D3D12RayTracingPipeline ??
            throw new ArgumentException(
                "The Pipeline is not a Direct3D 12 ray-tracing pipeline.",
                nameof(value));

        internal static D3D12AccelerationStructure AccelerationStructure(
            AccelerationStructure value) =>
            value as D3D12AccelerationStructure ??
            throw new ArgumentException(
                "The AccelerationStructure was not created by the Direct3D 12 backend.",
                nameof(value));

        internal static D3D12RayTracingShaderTable RayTracingShaderTable(
            RayTracingShaderTable value) =>
            value as D3D12RayTracingShaderTable ??
            throw new ArgumentException(
                "The RayTracingShaderTable was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
