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
        string? label = null
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        _ = nativeDevice.RequireCapability<RayTracing>(nameof(CreateAccelerationStructure));
        D3D12Buffer nativeStorage = RequireBuffer(storage);
        BufferRange range = storageRange.Expand(nativeStorage.Info.Size);
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

    public AccelerationStructureSrv CreateAccelerationStructureSrv(Device device, in AccelerationStructureSrvDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        _ = nativeDevice.RequireCapability<RayTracing>(nameof(CreateAccelerationStructureSrv));
        D3D12AccelerationStructure structure = RequireAccelerationStructure(desc.AccelerationStructure);
        DescriptorAllocation descriptor = nativeDevice.GetResourceDescriptors(nativeDevice.ResolveResourceHomeNodeIndex(structure.Storage.Info.CreationNodeMask)).Allocate();
        D3D12AccelerationStructureSrv? result = null;
        try
        {
            WriteAccelerationStructureSrv(nativeDevice, structure, descriptor.Cpu);
            result = new D3D12AccelerationStructureSrv(nativeDevice, structure, desc, descriptor);
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
        ReadOnlySpan<AccelerationStructureGeometry> geometries
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        RayTracing capability = nativeDevice.RequireCapability<RayTracing>(nameof(GetAccelerationStructureBuildInfo));
        AccelerationStructurePrebuildMode mode = RequireAccelerationStructureBuildSupport(capability, options);
        RaytracingGeometryDesc[] nativeGeometries = type == AccelerationStructureType.BottomLevel ? new RaytracingGeometryDesc[geometries.Length] : [];
        BuildRaytracingAccelerationStructureInputs inputs = CreateBuildInputs(nativeDevice, type, options, geometries, nativeGeometries);
        RaytracingAccelerationStructurePrebuildInfo info = default;
        fixed (RaytracingGeometryDesc* geometryPointer = nativeGeometries)
        {
            if (nativeGeometries.Length != 0)
                inputs.PGeometryDescs = geometryPointer;
            DeviceQueryTrace.GetRaytracingAccelerationStructurePrebuildInfo(nativeDevice.Native, &inputs, &info);
        }

        _ = RequireValidAccelerationStructurePrebuildInfo(info, mode);
        return new AccelerationStructureBuildInfo(info.ResultDataMaxSizeInBytes, 256, info.ScratchDataSizeInBytes, 256, info.UpdateScratchDataSizeInBytes, 256);
    }

    public Pipeline CreateRayTracingPipeline(Device device, in RayTracingPipelineDesc desc, PipelineCache? cache = null) => CreateRayTracingPipeline(device, desc, cache, null);
    private Pipeline CreateRayTracingPipeline(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache,
        SlangShaderData? capturedSlangCode
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12PipelineCache? nativeCache = GetPipelineCache(nativeDevice, cache);
        StaticSamplerBinding[] staticSamplers = desc.StaticSamplers.ToArray();
        ShaderReflection reflection = desc.Reflection;
        int localRootCapacity = checked(desc.RayGeneration.Length + desc.Miss.Length + desc.Callable.Length + desc.HitGroups.Length);
        var shaderExports = new Dictionary<string, EntryPointReflection>(localRootCapacity, StringComparer.Ordinal);
        var records = new List<D3D12RayTracingExport>(localRootCapacity);
        var localRoots = new List<D3D12RootSignatureState>(localRootCapacity);
        var localRootArray = new D3D12RootSignatureState[localRootCapacity];
        var identifiers = new byte[localRootCapacity][];
        for (int index = 0; index < identifiers.Length; index++)
            identifiers[index] = new byte[32];
        var entryExports = new Dictionary<EntryPointReflection, D3D12RayTracingExport>(checked(desc.RayGeneration.Length + desc.Miss.Length + desc.Callable.Length));
        var hitGroupExports = new Dictionary<string, D3D12RayTracingExport>(desc.HitGroups.Length, StringComparer.Ordinal);

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

                records,
                localRoots,
                identifiers,
                hitGroups
            );
            EntryPointReflection[] allEntries = GetOrderedRayTracingEntries(reflection, shaderExports.Values);
            CompiledShaderLibrary library = CompileShaderLibrary(desc.Code, desc.CodeBlob, reflection, allEntries);
            D3D12RootSignatureState global = CompileRayTracingGlobalRootSignature(nativeDevice, reflection, allEntries, staticSamplers);
            globalToRelease = global;
            localRoots.CopyTo(localRootArray);
            return MaterializeRayTracingPipeline(
                nativeDevice,
                nativeCache,
                desc,
                global,
                localRootArray,
                library,
                allEntries,
                records,
                hitGroups,
                entryExports,
                hitGroupExports,
                capturedSlangCode,
                ref rootsTransferred
            );
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

    private void AddAllRayTracingExports(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        in RayTracingPipelineDesc desc,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers,
        Dictionary<string, EntryPointReflection> shaderExports,

        List<D3D12RayTracingExport> records,
        List<D3D12RootSignatureState> localRoots,
        byte[][] identifiers,
        List<RayTracingHitGroupState> hitGroups
    )
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

            records,
            localRoots,
            identifiers
        );
        AddRayTracingEntries(
            nativeDevice,
            reflection,
            desc.Miss,
            SlangStage.Miss,
            RayRecordType.Miss,
            "miss",
            staticSamplers,
            shaderExports,

            records,
            localRoots,
            identifiers
        );
        AddRayTracingEntries(
            nativeDevice,
            reflection,
            desc.Callable,
            SlangStage.Callable,
            RayRecordType.Callable,
            "callable",
            staticSamplers,
            shaderExports,

            records,
            localRoots,
            identifiers
        );
        AddRayTracingHitGroups(
            nativeDevice,
            reflection,
            desc.HitGroups,
            staticSamplers,
            shaderExports,

            records,
            localRoots,
            identifiers,
            hitGroups
        );
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

        List<D3D12RayTracingExport> records,
        List<D3D12RootSignatureState> localRoots,
        byte[][] identifiers
    )
    {
        foreach (EntryPointReflection entry in entries)
        {
            string name = AddRayTracingShader(reflection, entry, stage, role, shaderExports, records);
            D3D12RootSignatureState local = CompileTrackedRayTracingLocalRoot(nativeDevice, reflection, [entry], staticSamplers, localRoots);
            records.Add(new D3D12RayTracingExport(
                name,
                entry,
                entry.VarLayout == VariableLayoutReflection.Null ? [] : [entry.VarLayout],
                local,
                type,
                identifiers[records.Count]
            ));
        }
    }

    private void AddRayTracingHitGroups(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        ReadOnlySpan<RayTracingHitGroup> descriptions,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers,
        Dictionary<string, EntryPointReflection> shaderExports,

        List<D3D12RayTracingExport> records,
        List<D3D12RootSignatureState> localRoots,
        byte[][] identifiers,
        List<RayTracingHitGroupState> hitGroups
    )
    {
        foreach (ref readonly RayTracingHitGroup hitGroup in descriptions)
        {
            var members = new List<EntryPointReflection>(3);
            string? closest = AddRayTracingHitMember(
                reflection,
                hitGroup.ClosestHit,
                SlangStage.ClosestHit,
                "closest-hit",
                members,
                shaderExports,

                records
            );
            string? anyHit = AddRayTracingHitMember(reflection, hitGroup.AnyHit, SlangStage.AnyHit, "any-hit", members, shaderExports, records);
            string? intersection = AddRayTracingHitMember(
                reflection,
                hitGroup.Intersection,
                SlangStage.Intersection,
                "intersection",
                members,
                shaderExports,

                records
            );
            VariableLayoutReflection[] layouts = members.Select(static member => member.VarLayout).Where(static layout => layout != VariableLayoutReflection.Null).Distinct().ToArray();
            D3D12RootSignatureState local = CompileTrackedRayTracingLocalRoot(nativeDevice, reflection, CollectionsMarshal.AsSpan(members), staticSamplers, localRoots);
            records.Add(new D3D12RayTracingExport(hitGroup.Name, EntryPointReflection.Null, layouts, local, RayRecordType.Hit, identifiers[records.Count]));
            hitGroups.Add(new RayTracingHitGroupState(
                hitGroup.Name,
                closest,
                anyHit,
                intersection,
                intersection is null ? HitGroupType.Triangles : HitGroupType.ProceduralPrimitive
            ));
        }
    }

    private string? AddRayTracingHitMember(
        ShaderReflection reflection,
        EntryPointReflection entry,
        SlangStage stage,
        string role,
        List<EntryPointReflection> members,
        Dictionary<string, EntryPointReflection> shaderExports,

        List<D3D12RayTracingExport> records
    )
    {
        if (entry == EntryPointReflection.Null)
            return null;
        members.Add(entry);
        return AddRayTracingShader(reflection, entry, stage, role, shaderExports, records);
    }

    private string AddRayTracingShader(
        ShaderReflection reflection,
        EntryPointReflection entry,
        SlangStage stage,
        string role,
        Dictionary<string, EntryPointReflection> shaderExports,

        List<D3D12RayTracingExport> records
    )
    {
        string name = ValidateStateObjectEntryPoint(reflection, entry, [stage], role);
        shaderExports.TryGetValue(name, out EntryPointReflection existing);
        if (existing == entry)
            return name;
        shaderExports[name] = entry;
        return name;
    }

    private D3D12RootSignatureState CompileTrackedRayTracingLocalRoot(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        ReadOnlySpan<EntryPointReflection> entries,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers,
        List<D3D12RootSignatureState> localRoots
    )
    {
        D3D12RootSignatureState root = D3D12RootSignatureBuilder.CompileLocal(this, nativeDevice, reflection, entries, staticSamplers, PipelineType.RayTracing);
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

    private static EntryPointReflection[] GetOrderedRayTracingEntries(ShaderReflection reflection, IEnumerable<EntryPointReflection> selected)
    {
        HashSet<EntryPointReflection> selectedEntries = [..selected];
        List<EntryPointReflection> orderedEntries = [];
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            EntryPointReflection entry = reflection.GetEntryPointByIndex(index);
            if (selectedEntries.Contains(entry))
                orderedEntries.Add(entry);
        }

        return[..orderedEntries];
    }

    private D3D12RootSignatureState CompileRayTracingGlobalRootSignature(
        D3D12Device nativeDevice,
        ShaderReflection reflection,
        ReadOnlySpan<EntryPointReflection> entries,
        ReadOnlySpan<StaticSamplerBinding> staticSamplers
    )
    {
        return D3D12RootSignatureBuilder.CompileGlobal(this, nativeDevice, reflection, staticSamplers, PipelineType.RayTracing);
    }

    private D3D12RayTracingPipeline MaterializeRayTracingPipeline(
        D3D12Device nativeDevice,
        D3D12PipelineCache? nativeCache,
        in RayTracingPipelineDesc desc,
        D3D12RootSignatureState global,
        D3D12RootSignatureState[] localRoots,
        CompiledShaderLibrary library,
        EntryPointReflection[] allEntries,
        IReadOnlyList<D3D12RayTracingExport> records,
        IReadOnlyList<RayTracingHitGroupState> hitGroups,
        Dictionary<EntryPointReflection, D3D12RayTracingExport> entryExports,
        Dictionary<string, D3D12RayTracingExport> hitGroupExports,
        SlangShaderData? capturedSlangCode,
        ref bool rootsTransferred
    )
    {
        ID3D12StateObject* stateObject = null;
        ID3D12StateObjectProperties* properties = null;
        NativeResourceData? nativeState = null;
        NativeResourceData? propertiesData = null;
        SlangShaderData? retainedCode = null;
        D3D12RayTracingPipeline? result = null;
        try
        {
            stateObject = CreateNativeRayTracingStateObject(nativeDevice, global, library.Code, allEntries, records, hitGroups, desc);
            SetNativeName(stateObject, desc.Label ?? "Ray Tracing State Object");
            properties = QueryRayTracingStateObjectProperties(nativeDevice, stateObject);
            ReadRayTracingShaderIdentifiers(properties, records);
            PopulateRayTracingExportMaps(records, entryExports, hitGroupExports);
            propertiesData = new NativeResourceData((IUnknown*)properties, ownsReference: true);
            properties = null;
            NativeResourceData[] rootDependencies = CreateRayTracingRootDependencies(global, localRoots);
            nativeState = new NativeResourceData((IUnknown*)stateObject, ownsReference: true, rootDependencies);
            stateObject = null;
            retainedCode = capturedSlangCode ?? desc.SlangCode;
            NativeResourceData[] additionalData = [propertiesData.Value];
            result = new D3D12RayTracingPipeline(
                nativeDevice,
                nativeState.Value,
                global,
                localRoots,
                additionalData,
                retainedCode,
                entryExports,
                hitGroupExports,
                desc.Label
            );
            rootsTransferred = true;
            nativeState = null;
            propertiesData = null;
            retainedCode = null;
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            CleanupFailedRayTracingPipeline(result, nativeState, propertiesData, stateObject, properties);
            throw;
        }
    }

    private ID3D12StateObject* CreateNativeRayTracingStateObject(
        D3D12Device nativeDevice,
        D3D12RootSignatureState global,
        byte[] code,
        EntryPointReflection[] allEntries,
        IReadOnlyList<D3D12RayTracingExport> records,
        IReadOnlyList<RayTracingHitGroupState> hitGroups,
        in RayTracingPipelineDesc desc
    )
    {
        using NativeStateObjectArena arena = new();
        int subobjectCount = checked(1 + 4 + hitGroups.Count + (records.Count * 2));
        StateSubobject* subobjects = arena.Allocate<StateSubobject>(subobjectCount);
        int ordinal = AddRayTracingLibrary(arena, code, allEntries, subobjects);
        GlobalRootSignature* globalDescription = arena.Allocate<GlobalRootSignature>();
        globalDescription->PGlobalRootSignature = global.Native;
        subobjects[ordinal++] = new StateSubobject(StateSubobjectType.GlobalRootSignature, globalDescription);
        ordinal = AddNativeRayTracingHitGroups(arena, hitGroups, subobjects, ordinal);
        ordinal = AddRayTracingLocalRootAssociations(arena, records, subobjects, ordinal);
        ordinal = AddRayTracingConfiguration(arena, desc, subobjects, ordinal);
        StateObjectDesc native = new(StateObjectType.RaytracingPipeline, checked((uint)subobjectCount), subobjects);
        ID3D12StateObject* stateObject = null;
        Guid iid = ID3D12StateObject.Guid;
        int stateObjectResult = DevicePipelineTrace.CreateStateObject(nativeDevice.Native, &native, &iid, (void**)&stateObject);
        try
        {
            ThrowIfFailed(
                nativeDevice,
                stateObjectResult,
                NativeOperationType.PipelineCreation,
                "ID3D12Device5::CreateStateObject(ray tracing)"
            );
            return stateObject;
        }
        catch
        {
            if (stateObject is not null)
                _ = PipelineTrace.Release(stateObject);
            throw;
        }
    }

    private static int AddRayTracingLibrary(
        NativeStateObjectArena arena,
        byte[] code,
        EntryPointReflection[] allEntries,
        StateSubobject* subobjects
    )
    {
        ExportDesc* exports = arena.Allocate<ExportDesc>(allEntries.Length);
        for (int index = 0; index < allEntries.Length; index++)
        {
            string name = GetEntryPointName(allEntries[index]);
            exports[index] = new ExportDesc(arena.String(name), null, ExportFlags.None);
        }

        DxilLibraryDesc* libraryDescription = arena.Allocate<DxilLibraryDesc>();
        *libraryDescription = new DxilLibraryDesc(new ShaderBytecode(arena.Bytes(code), (nuint)code.Length), checked((uint)allEntries.Length), exports);
        subobjects[0] = new StateSubobject(StateSubobjectType.DxilLibrary, libraryDescription);
        return 1;
    }

    private static int AddNativeRayTracingHitGroups(
        NativeStateObjectArena arena,
        IReadOnlyList<RayTracingHitGroupState> hitGroups,
        StateSubobject* subobjects,
        int ordinal
    )
    {
        foreach (RayTracingHitGroupState hitGroup in hitGroups)
        {
            HitGroupDesc* nativeHitGroup = arena.Allocate<HitGroupDesc>();
            nativeHitGroup->HitGroupExport = arena.String(hitGroup.Name);
            nativeHitGroup->Type = hitGroup.Type;
            nativeHitGroup->ClosestHitShaderImport = hitGroup.ClosestHit is null ? null : arena.String(hitGroup.ClosestHit);
            nativeHitGroup->AnyHitShaderImport = hitGroup.AnyHit is null ? null : arena.String(hitGroup.AnyHit);
            nativeHitGroup->IntersectionShaderImport = hitGroup.Intersection is null ? null : arena.String(hitGroup.Intersection);
            subobjects[ordinal++] = new StateSubobject(StateSubobjectType.HitGroup, nativeHitGroup);
        }

        return ordinal;
    }

    private static int AddRayTracingLocalRootAssociations(
        NativeStateObjectArena arena,
        IReadOnlyList<D3D12RayTracingExport> records,
        StateSubobject* subobjects,
        int ordinal
    )
    {
        foreach (D3D12RayTracingExport record in records)
        {
            LocalRootSignature* localDescription = arena.Allocate<LocalRootSignature>();
            localDescription->PLocalRootSignature = record.LocalRoot.Native;
            int localOrdinal = ordinal++;
            subobjects[localOrdinal] = new StateSubobject(StateSubobjectType.LocalRootSignature, localDescription);
            char** associatedExports = (char**)arena.Allocate<nint>();
            *associatedExports = arena.String(record.Name);
            SubobjectToExportsAssociation* association = arena.Allocate<SubobjectToExportsAssociation>();
            association->PSubobjectToAssociate = subobjects + localOrdinal;
            association->NumExports = 1;
            association->PExports = associatedExports;
            subobjects[ordinal++] = new StateSubobject(StateSubobjectType.SubobjectToExportsAssociation, association);
        }

        return ordinal;
    }

    private static int AddRayTracingConfiguration(
        NativeStateObjectArena arena,
        in RayTracingPipelineDesc desc,
        StateSubobject* subobjects,
        int ordinal
    )
    {
        RaytracingShaderConfig* shaderConfig = arena.Allocate<RaytracingShaderConfig>();
        shaderConfig->MaxPayloadSizeInBytes = desc.MaximumPayloadSize;
        shaderConfig->MaxAttributeSizeInBytes = desc.MaximumAttributeSize;
        subobjects[ordinal++] = new StateSubobject(StateSubobjectType.RaytracingShaderConfig, shaderConfig);
        RaytracingPipelineConfig1* pipelineConfig = arena.Allocate<RaytracingPipelineConfig1>();
        pipelineConfig->MaxTraceRecursionDepth = desc.MaximumRecursionDepth;
        pipelineConfig->Flags = ToNativePipelineFlags(desc.Options);
        subobjects[ordinal++] = new StateSubobject(StateSubobjectType.RaytracingPipelineConfig1, pipelineConfig);
        uint* nodeMask = arena.Allocate<uint>();
        *nodeMask = desc.NodeMask;
        subobjects[ordinal++] = new StateSubobject(StateSubobjectType.NodeMask, nodeMask);
        return ordinal;
    }

    private static ID3D12StateObjectProperties* QueryRayTracingStateObjectProperties(D3D12Device nativeDevice, ID3D12StateObject* stateObject)
    {
        ID3D12StateObjectProperties* properties = null;
        Guid iid = ID3D12StateObjectProperties.Guid;
        int result = PipelineTrace.QueryInterface(stateObject, &iid, (void**)&properties);
        try
        {
            ThrowIfFailed(
                nativeDevice,
                result,
                NativeOperationType.PipelineCreation,
                "ID3D12StateObject::QueryInterface(ID3D12StateObjectProperties)"
            );
            return properties;
        }
        catch
        {
            if (properties is not null)
                _ = PipelineTrace.Release(properties);
            throw;
        }
    }

    private static void ReadRayTracingShaderIdentifiers(ID3D12StateObjectProperties* properties, IReadOnlyList<D3D12RayTracingExport> records)
    {
        foreach (D3D12RayTracingExport record in records)
        {
            void* identifier = PipelineTrace.GetShaderIdentifier(properties, record.Name);
            if (identifier is null)
            {
                throw new GraphicsException(GraphicsError.PipelineCreation, $"D3D12 did not materialize shader identifier '{record.Name}'.");
            }

            new ReadOnlySpan<byte>(identifier, 32).CopyTo(record.Identifier);
        }
    }

    private static void PopulateRayTracingExportMaps(
        IReadOnlyList<D3D12RayTracingExport> records,
        Dictionary<EntryPointReflection, D3D12RayTracingExport> entryExports,
        Dictionary<string, D3D12RayTracingExport> hitGroupExports
    )
    {
        foreach (D3D12RayTracingExport record in records)
        {
            if (record.EntryPoint != EntryPointReflection.Null)
                entryExports.Add(record.EntryPoint, record);
            if (record.Type == RayRecordType.Hit)
                hitGroupExports.Add(record.Name, record);
        }
    }

    private static NativeResourceData[] CreateRayTracingRootDependencies(D3D12RootSignatureState global, IReadOnlyList<D3D12RootSignatureState> localRoots)
    {
        var dependencies = new NativeResourceData[checked(localRoots.Count + 1)];
        dependencies[0] = global.NativeData;
        for (int index = 0; index < localRoots.Count; index++)
            dependencies[index + 1] = localRoots[index].NativeData;
        return dependencies;
    }

    private static void CleanupFailedRayTracingPipeline(
        D3D12RayTracingPipeline? result,
        NativeResourceData? nativeState,
        NativeResourceData? propertiesData,
        ID3D12StateObject* stateObject,
        ID3D12StateObjectProperties* properties
    )
    {
        if (result is not null)
        {
            result.Dispose();
            return;
        }

        nativeState?.Release();
        propertiesData?.Release();
        if (properties is not null)
            _ = PipelineTrace.Release(properties);
        if (stateObject is not null)
            _ = PipelineTrace.Release(stateObject);
    }

    public RayTracingShaderTable CreateRayTracingShaderTable(Device device, in RayTracingShaderTableDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        RayTracing capability = nativeDevice.RequireCapability<RayTracing>(nameof(CreateRayTracingShaderTable));
        D3D12RayTracingPipeline pipeline = RequireRayTracingPipeline(desc.Pipeline);
        NativeMemoryChecks.Range(desc.MaximumRecordSize, 0, pipeline.MaximumLocalRecordSize);
        _ = GetRayTableSize(desc);
        D3D12RayTracingShaderTable result = new(nativeDevice, pipeline, desc);
        nativeDevice.RegisterChild(result);
        return result;
    }

    public void UpdateRayTracingShaderTable(CommandContext context, RayTracingShaderTable table, in RayTracingShaderTableUpdate update)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        RayTracing capability = command.NativeDevice.RequireCapability<RayTracing>(nameof(UpdateRayTracingShaderTable));
        D3D12RayTracingShaderTable nativeTable = RequireRayTracingShaderTable(table);
        int resourceCount = update.Resources.Length;
        int recordCount = checked(update.RayGeneration.Length + update.Miss.Length + update.Hit.Length + update.Callable.Length);
        command.PrepareSwapchainUses(resourceCount);
        command.PrepareOrdinaryData(checked(GetRayTableSize(nativeTable.Description) + (ulong)recordCount * 256UL + (ulong)update.OrdinaryData.Length));
        command.PrepareDescriptors(checked((uint)resourceCount), checked((uint)resourceCount));
        command.PrepareTransientObjects(checked((checked((uint)resourceCount) != 0 ? 1 : 0) + (checked((uint)resourceCount) != 0 ? 1 : 0)));
        command.PrepareRecordedRayTable(
            1,
            1,
            update.ParameterBlocks.Length,
            resourceCount,
            update.OrdinaryData.Length,
            update.RayGeneration.Length,
            update.Miss.Length,
            update.Hit.Length,
            update.Callable.Length
        );
        D3D12RecordedRayTable recordedTable = command.RecordRayTableUpdate(nativeTable, update);
        foreach (ResourceBinding binding in recordedTable.Resources)
            _ = recordedTable.GetDispatchData(command, nativeTable);
    }

    public void DispatchRays(CommandContext context, in DispatchRaysDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        RayTracing capability = command.NativeDevice.RequireCapability<RayTracing>(nameof(DispatchRays));
        D3D12RayTracingShaderTable table = RequireRayTracingShaderTable(desc.ShaderTable);
        D3D12RayDispatchData dispatchData = command.GetRayDispatchData(table);
        Silk.NET.Direct3D12.DispatchRaysDesc native = dispatchData.Description;
        native.Width = desc.Width;
        native.Height = desc.Height;
        native.Depth = desc.Depth;
        CommandExecutionTrace.DispatchRays(command, command.List, &native);
    }

    private static ulong GetRayTableSize(in RayTracingShaderTableDesc desc)
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
        return checked((value + alignment - 1) & ~(alignment - 1));
    }

    public void BuildAccelerationStructure(CommandContext context, in AccelerationStructureBuildDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        RayTracing capability = command.NativeDevice.RequireCapability<RayTracing>(nameof(BuildAccelerationStructure));
        AccelerationStructurePrebuildMode mode = RequireAccelerationStructureBuildSupport(capability, desc.Options);
        D3D12AccelerationStructure destination = RequireAccelerationStructure(desc.Destination);
        D3D12AccelerationStructure? source = desc.Source is null ? null : RequireAccelerationStructure(desc.Source);
        D3D12Buffer scratch = RequireBuffer(desc.Scratch);
        BufferRange scratchRange = desc.ScratchRange.Expand(scratch.Info.Size);
        int geometryCount = desc.Type == AccelerationStructureType.BottomLevel ? desc.Geometries.Length : 0;
        int retainedCount = checked(3 + geometryCount * 3);
        command.PrepareAccelerationStructureGeometries(geometryCount);
        Span<RaytracingGeometryDesc> nativeGeometries = command.ReserveAccelerationStructureGeometryDescriptions(geometryCount);
        BuildRaytracingAccelerationStructureInputs inputs = CreateBuildInputs(command.NativeDevice, desc.Type, desc.Options, desc.Geometries, nativeGeometries);
        RaytracingAccelerationStructurePrebuildInfo requirements = default;
        fixed (RaytracingGeometryDesc* geometryPointer = nativeGeometries)
        {
            if (nativeGeometries.Length != 0)
                inputs.PGeometryDescs = geometryPointer;
            DeviceQueryTrace.GetRaytracingAccelerationStructurePrebuildInfo(command.NativeDevice.Native, &inputs, &requirements);
            ulong requiredScratch = RequireValidAccelerationStructurePrebuildInfo(requirements, mode);
            BuildRaytracingAccelerationStructureDesc native = new()
            {
                DestAccelerationStructureData = destination.Address,
                Inputs = inputs,
                SourceAccelerationStructureData = source?.Address ?? 0,
                ScratchAccelerationStructureData = ResourceTrace.GetGPUVirtualAddress(scratch.Native) + scratchRange.Offset,
            };
            CaptureGeometryResources(command, desc.Geometries);
            CommandExecutionTrace.BuildRaytracingAccelerationStructure(command, command.List, &native, 0, null);
        }
    }

    internal static ulong RequireValidAccelerationStructurePrebuildInfo(in RaytracingAccelerationStructurePrebuildInfo info, AccelerationStructurePrebuildMode mode)
    {
        ulong requiredScratch = mode.PerformUpdate ? info.UpdateScratchDataSizeInBytes : info.ScratchDataSizeInBytes;
        if (info.ResultDataMaxSizeInBytes == 0 || (!mode.AllowUpdate && info.UpdateScratchDataSizeInBytes != 0))
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "D3D12 returned invalid acceleration-structure prebuild requirements: " + $"result={info.ResultDataMaxSizeInBytes}, " + $"buildScratch={info.ScratchDataSizeInBytes}, " + $"updateScratch={info.UpdateScratchDataSizeInBytes}, " + $"allowUpdate={mode.AllowUpdate}, performUpdate={mode.PerformUpdate}."
            );
        }

        return requiredScratch;
    }

    public void CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        RayTracing capability = command.NativeDevice.RequireCapability<RayTracing>(nameof(CopyAccelerationStructure));
        D3D12AccelerationStructure nativeDestination = RequireAccelerationStructure(destination);
        D3D12AccelerationStructure nativeSource = RequireAccelerationStructure(source);
        CommandTransferTrace.CopyRaytracingAccelerationStructure(command, command.List, nativeDestination.Address, nativeSource.Address, ToNativeCopyMode(type));
    }

    public void SerializeAccelerationStructure(CommandContext context, in BufferRegion destination, AccelerationStructure source)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        RayTracing capability = command.NativeDevice.RequireCapability<RayTracing>(nameof(SerializeAccelerationStructure));
        D3D12Buffer nativeDestination = RequireBuffer(destination.Buffer);
        D3D12AccelerationStructure nativeSource = RequireAccelerationStructure(source);
        BufferRange destinationRange = destination.Range.Expand(nativeDestination.Info.Size);
        CommandTransferTrace.CopyRaytracingAccelerationStructure(
            command,
            command.List,
            ResourceTrace.GetGPUVirtualAddress(nativeDestination.Native) + destinationRange.Offset,
            nativeSource.Address,
            RaytracingAccelerationStructureCopyMode.Serialize
        );
    }

    public void DeserializeAccelerationStructure(CommandContext context, AccelerationStructure destination, in BufferRegion source)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        RayTracing capability = command.NativeDevice.RequireCapability<RayTracing>(nameof(DeserializeAccelerationStructure));
        D3D12AccelerationStructure nativeDestination = RequireAccelerationStructure(destination);
        D3D12Buffer nativeSource = RequireBuffer(source.Buffer);
        BufferRange sourceRange = source.Range.Expand(nativeSource.Info.Size);
        CommandTransferTrace.CopyRaytracingAccelerationStructure(
            command,
            command.List,
            nativeDestination.Address,
            ResourceTrace.GetGPUVirtualAddress(nativeSource.Native) + sourceRange.Offset,
            RaytracingAccelerationStructureCopyMode.Deserialize
        );
    }

    public void EmitAccelerationStructurePostBuildInfo(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        Buffer destination,
        ulong destinationOffset
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        RayTracing capability = command.NativeDevice.RequireCapability<RayTracing>(nameof(EmitAccelerationStructurePostBuildInfo));
        D3D12AccelerationStructure nativeSource = RequireAccelerationStructure(source);
        D3D12Buffer nativeDestination = RequireBuffer(destination);
        ulong resultSize = type == AccelerationStructurePostBuildInfoType.SerializationSize ? 2UL * sizeof(ulong) : sizeof(ulong);
        RaytracingAccelerationStructurePostbuildInfoDesc native = new()
        {
            DestBuffer = ResourceTrace.GetGPUVirtualAddress(nativeDestination.Native) + destinationOffset,
            InfoType = ToNativePostBuildInfo(type),
        };
        ulong address = nativeSource.Address;
        CommandExecutionTrace.EmitRaytracingAccelerationStructurePostbuildInfo(command, command.List, &native, 1, &address);
    }

    private static AccelerationStructurePrebuildMode RequireAccelerationStructureBuildSupport(RayTracing capability, AccelerationStructureBuildOptions options)
    {
        AccelerationStructurePrebuildMode mode = RequireValidAccelerationStructureBuildOptions(options);
        return mode;
    }

    internal static AccelerationStructurePrebuildMode RequireValidAccelerationStructureBuildOptions(AccelerationStructureBuildOptions options)
    {
        return new AccelerationStructurePrebuildMode(
            (options & AccelerationStructureBuildOptions.AllowUpdate) != 0,
            (options & AccelerationStructureBuildOptions.PerformUpdate) != 0
        );
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
        Span<RaytracingGeometryDesc> nativeGeometries
    )
    {
        BuildRaytracingAccelerationStructureInputs result = new()
        {
            Type = type == AccelerationStructureType.BottomLevel ? RaytracingAccelerationStructureType.BottomLevel : RaytracingAccelerationStructureType.TopLevel,
            Flags = ToNativeBuildFlags(options),
            DescsLayout = ElementsLayout.Array,
        };
        if (type == AccelerationStructureType.TopLevel)
        {
            PopulateTopLevelBuildInputs(device, geometries, ref result);
            return result;
        }

        for (int index = 0; index < geometries.Length; index++)
            nativeGeometries[index] = CreateNativeGeometry(device, geometries[index]);
        result.NumDescs = checked((uint)nativeGeometries.Length);
        return result;
    }

    private void PopulateTopLevelBuildInputs(
        D3D12Device device,
        ReadOnlySpan<AccelerationStructureGeometry> geometries,
        ref BuildRaytracingAccelerationStructureInputs result
    )
    {
        ref readonly AccelerationStructureGeometry geometry = ref geometries[0];
        D3D12Buffer instances = ResolveInputRegion(device, geometry.Primary, checked((ulong)geometry.Count * 64), 16, nameof(geometries), out BufferRange range);
        result.NumDescs = geometry.Count;
        result.InstanceDescs = ResourceTrace.GetGPUVirtualAddress(instances.Native) + range.Offset;
    }

    private RaytracingGeometryDesc CreateNativeGeometry(D3D12Device device, in AccelerationStructureGeometry geometry)
    {
        RaytracingGeometryDesc result = new()
        {
            Type = geometry.Type switch
            {
                AccelerationStructureGeometryType.Triangles => RaytracingGeometryType.Triangles,
                AccelerationStructureGeometryType.AxisAlignedBoundingBoxes => RaytracingGeometryType.ProceduralPrimitiveAabbs,
                _ => default!,
            },
            Flags = ToNativeGeometryFlags(geometry.Options),
        };
        if (geometry.Type == AccelerationStructureGeometryType.Triangles)
            result.Triangles = CreateTriangleGeometry(device, geometry);
        else
            result.AABBs = CreateAabbGeometry(device, geometry);
        return result;
    }

    private RaytracingGeometryTrianglesDesc CreateTriangleGeometry(D3D12Device device, in AccelerationStructureGeometry geometry)
    {
        (uint positionSize, uint componentAlignment) = GetRayTracingVertexLayout(geometry.PrimaryFormat);
        D3D12Buffer vertices = ResolveInputRegion(
            device,
            geometry.Primary,
            checked((ulong)geometry.Count * geometry.PrimaryStride),
            componentAlignment,
            "geometries",
            out BufferRange vertexRange
        );
        RaytracingGeometryTrianglesDesc result = new()
        {
            VertexFormat = FormatMappings.ToDxgi(geometry.PrimaryFormat),
            VertexCount = geometry.Count,
            VertexBuffer = new GpuVirtualAddressAndStride(ResourceTrace.GetGPUVirtualAddress(vertices.Native) + vertexRange.Offset, geometry.PrimaryStride),
            IndexFormat = NativeFormat.FormatUnknown,
        };
        if (geometry.Secondary.Buffer is not null)
            PopulateTriangleIndices(device, geometry, ref result);
        if (geometry.Transform.Buffer is not null)
        {
            D3D12Buffer transform = ResolveInputRegion(device, geometry.Transform, 48, 16, "geometries", out BufferRange transformRange);
            result.Transform3x4 = ResourceTrace.GetGPUVirtualAddress(transform.Native) + transformRange.Offset;
        }

        return result;
    }

    private void PopulateTriangleIndices(
        D3D12Device device,
        in AccelerationStructureGeometry geometry,
        ref RaytracingGeometryTrianglesDesc triangles
    )
    {
        uint indexSize = geometry.IndexType switch
        {
            IndexType.UInt16 => 2,
            IndexType.UInt32 => 4,
            _ => default!,
        };
        D3D12Buffer indices = ResolveInputRegion(device, geometry.Secondary, indexSize, indexSize, "geometries", out BufferRange indexRange);
        triangles.IndexFormat = geometry.IndexType switch
        {
            IndexType.UInt16 => NativeFormat.FormatR16Uint,
            IndexType.UInt32 => NativeFormat.FormatR32Uint,
            _ => default!,
        };
        triangles.IndexCount = checked((uint)(indexRange.Size / indexSize));
        triangles.IndexBuffer = ResourceTrace.GetGPUVirtualAddress(indices.Native) + indexRange.Offset;
    }

    private RaytracingGeometryAabbsDesc CreateAabbGeometry(D3D12Device device, in AccelerationStructureGeometry geometry)
    {
        D3D12Buffer aabbs = ResolveInputRegion(
            device,
            geometry.Primary,
            checked((ulong)geometry.Count * geometry.PrimaryStride),
            8,
            "geometries",
            out BufferRange range
        );
        return new RaytracingGeometryAabbsDesc(
            geometry.Count,
            new GpuVirtualAddressAndStride(ResourceTrace.GetGPUVirtualAddress(aabbs.Native) + range.Offset, geometry.PrimaryStride)
        );
    }

    private D3D12Buffer ResolveInputRegion(
        D3D12Device device,
        in BufferRegion region,
        ulong minimumSize,
        ulong alignment,
        string parameter,
        out BufferRange range
    )
    {
        D3D12Buffer buffer = RequireBuffer(region.Buffer);
        range = region.Range.Expand(buffer.Info.Size);
        return buffer;
    }

    private void CaptureGeometryResources(D3D12CommandContext command, ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
        foreach (ref readonly AccelerationStructureGeometry geometry in geometries)
        {
        }
    }

    private static (uint PositionSize, uint ComponentAlignment) GetRayTracingVertexLayout(Format format) => format switch
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
        _ => default!,
    };
    private static RaytracingAccelerationStructureBuildFlags ToNativeBuildFlags(AccelerationStructureBuildOptions value)
    {
        RaytracingAccelerationStructureBuildFlags result = RaytracingAccelerationStructureBuildFlags.None;
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

    private static RaytracingPipelineFlags ToNativePipelineFlags(RayTracingPipelineOptions value)
    {
        RaytracingPipelineFlags result = RaytracingPipelineFlags.None;
        if ((value & RayTracingPipelineOptions.SkipTriangles) != 0)
            result |= RaytracingPipelineFlags.SkipTriangles;
        if ((value & RayTracingPipelineOptions.SkipProceduralPrimitives) != 0)
            result |= RaytracingPipelineFlags.SkipProceduralPrimitives;
        return result;
    }

    private static RaytracingGeometryFlags ToNativeGeometryFlags(AccelerationStructureGeometryOptions value)
    {
        RaytracingGeometryFlags result = RaytracingGeometryFlags.None;
        if ((value & AccelerationStructureGeometryOptions.Opaque) != 0)
            result |= RaytracingGeometryFlags.Opaque;
        if ((value & AccelerationStructureGeometryOptions.NoRepeatedAnyHit) != 0)
            result |= RaytracingGeometryFlags.NoDuplicateAnyhitInvocation;
        return result;
    }

    private static RaytracingAccelerationStructureCopyMode ToNativeCopyMode(AccelerationStructureCopyType value) => value switch
    {
        AccelerationStructureCopyType.Clone => RaytracingAccelerationStructureCopyMode.Clone,
        AccelerationStructureCopyType.Compact => RaytracingAccelerationStructureCopyMode.Compact,
        _ => default!,
    };
    private static RaytracingAccelerationStructurePostbuildInfoType ToNativePostBuildInfo(AccelerationStructurePostBuildInfoType value) => value switch
    {
        AccelerationStructurePostBuildInfoType.CompactedSize => RaytracingAccelerationStructurePostbuildInfoType.CompactedSize,
        AccelerationStructurePostBuildInfoType.SerializationSize => RaytracingAccelerationStructurePostbuildInfoType.Serialization,
        AccelerationStructurePostBuildInfoType.CurrentSize => RaytracingAccelerationStructurePostbuildInfoType.CurrentSize,
        _ => default!,
    };
    private static void WriteAccelerationStructureSrv(D3D12Device device, D3D12AccelerationStructure structure, CpuDescriptorHandle destination)
    {
        ShaderResourceViewDesc native = new()
        {
            Format = NativeFormat.FormatUnknown,
            ViewDimension = SrvDimension.RaytracingAccelerationStructure,
            Shader4ComponentMapping = 5768,
        };
        native.RaytracingAccelerationStructure = new RaytracingAccelerationStructureSrv(structure.Address);
        DeviceResourceTrace.CreateShaderResourceView(device.Native, null, &native, destination);
    }

    private static void RegisterAccelerationStructureView(D3D12Device device, D3D12AccelerationStructure structure, GraphicsObject view)
    {
        device.RegisterChild(view);
    }

    private sealed class D3D12AccelerationStructure : AccelerationStructure
    {
        private readonly D3D12Device _device;
        private readonly D3D12Buffer _storage;
        private readonly NativeResourceData _storageLifetime;
        internal D3D12AccelerationStructure(D3D12Device device, D3D12Buffer storage, in AccelerationStructureInfo info, string? label) : base(device, info, label)
        {
            _device = device;
            _storage = storage;
            _storageLifetime = storage.NativeData;
            _storageLifetime.Retain();
        }

        internal ulong Address => ResourceTrace.GetGPUVirtualAddress(_storage.Native) + Info.StorageRange.Offset;
        internal D3D12Buffer Storage => _storage;
        internal NativeResourceData NativeData => _storageLifetime;

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
            byte[] identifier
        )
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

    private sealed record RayTracingHitGroupState(string Name, string? ClosestHit, string? AnyHit, string? Intersection, HitGroupType Type);
    private sealed class D3D12RayTracingPipeline : D3D12Pipeline
    {
        private readonly NativeResourceData _properties;
        private readonly Dictionary<EntryPointReflection, D3D12RayTracingExport> _entries;
        private readonly Dictionary<string, D3D12RayTracingExport> _hitGroups;
        internal D3D12RayTracingPipeline(
            D3D12Device device,
            NativeResourceData native,
            D3D12RootSignatureState root,
            D3D12RootSignatureState[] localRoots,
            NativeResourceData[] additionalData,
            SlangShaderData slangCode,
            Dictionary<EntryPointReflection, D3D12RayTracingExport> entries,
            Dictionary<string, D3D12RayTracingExport> hitGroups,
            string? label
        ) : base(device, native, root, localRoots, additionalData, slangCode, PipelineType.RayTracing, label)
        {
            _properties = additionalData[0];
            _entries = entries;
            _hitGroups = hitGroups;
        }

        internal ID3D12StateObject* Native => (ID3D12StateObject*)NativeObject;
        internal uint MaximumLocalRecordSize => _entries.Values.Concat(_hitGroups.Values).Select(static value => checked(32u + value.LocalRoot.RootArgumentSize)).DefaultIfEmpty(32u).Max();

        internal bool HasExportType(RayRecordType type) => _entries.Values.Any(value => value.Type == type) || _hitGroups.Values.Any(value => value.Type == type);
        internal D3D12RayTracingExport GetEntry(EntryPointReflection entryPoint, RayRecordType type)
        {
            _entries.TryGetValue(entryPoint, out D3D12RayTracingExport? value);
            return value!;
        }

        internal D3D12RayTracingExport GetHitGroup(string name)
        {
            _hitGroups.TryGetValue(name, out D3D12RayTracingExport? value);
            return value!;
        }
    }

    private sealed class D3D12RayTracingShaderTable : RayTracingShaderTable
    {
        private readonly D3D12Device _device;
        private SlangShaderData? _slangCode;
        private readonly NativeResourceData _pipelineState;
        internal D3D12RayTracingShaderTable(D3D12Device device, D3D12RayTracingPipeline pipeline, in RayTracingShaderTableDesc description) : base(device, description)
        {
            _device = device;
            Pipeline = pipeline;
            SlangShaderData? slangCode = null;
            NativeResourceData? pipelineState = null;
            try
            {
                slangCode = pipeline.GetSlangCodeReference();
                pipelineState = pipeline.RetainNativeState();
                _slangCode = slangCode;
                slangCode = null;
                _pipelineState = pipelineState.Value;
            }
            catch
            {
                pipelineState?.Release();
                throw;
            }
        }

        internal D3D12RayTracingPipeline Pipeline { get; }

        internal override void Release(bool fromParent)
        {
            _pipelineState.Release();
            _ = Interlocked.Exchange(ref _slangCode, null);
            _device.UnregisterChild(this);
        }
    }

    private readonly record struct D3D12RecordedRayRecord(D3D12RayTracingExport Export, uint ParameterBlockOffset, uint ParameterBlockCount);
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
        internal ReadOnlySpan<D3D12RecordedRayRecord> RayGeneration => _rayGeneration.AsSpan(0, _rayGenerationCount);
        internal ReadOnlySpan<D3D12RecordedRayRecord> Miss => _miss.AsSpan(0, _missCount);
        internal ReadOnlySpan<D3D12RecordedRayRecord> Hit => _hit.AsSpan(0, _hitCount);
        internal ReadOnlySpan<D3D12RecordedRayRecord> Callable => _callable.AsSpan(0, _callableCount);
        internal ReadOnlySpan<RayTracingLocalParameterBlock> ParameterBlocks => _parameterBlocks.AsSpan(0, _parameterBlockCount);
        internal ReadOnlySpan<ResourceBinding> Resources => _resources.AsSpan(0, _resourceCount);
        internal ReadOnlySpan<byte> OrdinaryData => _ordinaryData.AsSpan(0, _ordinaryDataCount);

        internal void RecordUpdate(D3D12RayTracingShaderTable table, in RayTracingShaderTableUpdate update)
        {
            RayTracingShaderTableDesc description = table.Description;
            PrepareCapacity(
                update.ParameterBlocks.Length,
                update.Resources.Length,
                update.OrdinaryData.Length,
                update.RayGeneration.Length,
                update.Miss.Length,
                update.Hit.Length,
                update.Callable.Length
            );
            update.ParameterBlocks.CopyTo(_parameterBlocks);
            update.Resources.CopyTo(_resources);
            update.OrdinaryData.CopyTo(_ordinaryData);
            _parameterBlockCount = update.ParameterBlocks.Length;
            _resourceCount = update.Resources.Length;
            _ordinaryDataCount = update.OrdinaryData.Length;
            _rayGenerationCount = CaptureRecords(table, update.RayGeneration, RayRecordType.RayGeneration, _rayGeneration);
            _missCount = CaptureRecords(table, update.Miss, RayRecordType.Miss, _miss);
            _hitCount = CaptureRecords(table, update.Hit, RayRecordType.Hit, _hit);
            _callableCount = CaptureRecords(table, update.Callable, RayRecordType.Callable, _callable);
            _hasDispatchData = false;
        }

        internal void PrepareCapacity(
            int parameterBlockCount,
            int resourceCount,
            int ordinaryDataCount,
            int rayGenerationCount,
            int missCount,
            int hitCount,
            int callableCount
        )
        {
            EnsureCapacity(ref _parameterBlocks, parameterBlockCount);
            EnsureCapacity(ref _resources, resourceCount);
            EnsureCapacity(ref _ordinaryData, ordinaryDataCount);
            EnsureCapacity(ref _rayGeneration, rayGenerationCount);
            EnsureCapacity(ref _miss, missCount);
            EnsureCapacity(ref _hit, hitCount);
            EnsureCapacity(ref _callable, callableCount);
        }

        internal D3D12RayDispatchData GetDispatchData(D3D12CommandContext command, D3D12RayTracingShaderTable table)
        {
            D3D12CommandBuffer entry = command.Recording;
            if (_hasDispatchData && _dispatchData.DescriptorArenaVersion == entry.DescriptorArenaVersion)
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
            D3D12RecordedRayRecord[] destination
        )
        {
            for (int index = 0; index < records.Length; index++)
            {
                ref readonly RayTracingShaderRecord record = ref records[index];
                D3D12RayTracingExport export = type == RayRecordType.Hit ? table.Pipeline.GetHitGroup(record.HitGroupName!) : table.Pipeline.GetEntry(record.EntryPoint, type);
                destination[index] = new D3D12RecordedRayRecord(export, record.ParameterBlockOffset, record.ParameterBlockCount);
            }

            return records.Length;
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

        private D3D12RayDispatchData BuildDispatchData(D3D12CommandContext command, D3D12RayTracingShaderTable table)
        {
            uint resourceCount = 0;
            uint samplerCount = 0;
            ulong ordinarySize = 0;
            CountAllDispatchDataRequirements(ref resourceCount, ref samplerCount, ref ordinarySize);
            (uint resourceBase, uint samplerBase) = command.AllocateTransientDescriptorPair(resourceCount, samplerCount);
            ulong stride = table.Description.MaximumRecordSize;
            ulong rayGenerationOffset = 0;
            ulong missOffset = AlignUp(checked(rayGenerationOffset + checked((ulong)RayGeneration.Length * stride)), 64);
            ulong hitOffset = AlignUp(checked(missOffset + checked((ulong)Miss.Length * stride)), 64);
            ulong callableOffset = AlignUp(checked(hitOffset + checked((ulong)Hit.Length * stride)), 64);
            ulong tableEnd = checked(callableOffset + checked((ulong)Callable.Length * stride));
            ulong ordinaryStart = AlignUp(tableEnd, 256);
            ulong totalSize = checked(ordinaryStart + ordinarySize);
            D3D12OrdinaryDataReservation storage = command.ReserveTransientOrdinaryData(totalSize);
            Span<byte> destination = storage.CommitSpan(checked((int)totalSize), clear: true);
            ulong baseAddress = storage.Address;
            uint resourceOffset = resourceBase;
            uint samplerOffset = samplerBase;
            ulong ordinaryOffset = ordinaryStart;
            WriteRayRecordCategory(
                command,
                RayGeneration,
                rayGenerationOffset,
                stride,
                destination,
                totalSize,
                baseAddress,
                ref resourceOffset,
                ref samplerOffset,
                ref ordinaryOffset
            );
            WriteRayRecordCategory(
                command,
                Miss,
                missOffset,
                stride,
                destination,
                totalSize,
                baseAddress,
                ref resourceOffset,
                ref samplerOffset,
                ref ordinaryOffset
            );
            WriteRayRecordCategory(
                command,
                Hit,
                hitOffset,
                stride,
                destination,
                totalSize,
                baseAddress,
                ref resourceOffset,
                ref samplerOffset,
                ref ordinaryOffset
            );
            WriteRayRecordCategory(
                command,
                Callable,
                callableOffset,
                stride,
                destination,
                totalSize,
                baseAddress,
                ref resourceOffset,
                ref samplerOffset,
                ref ordinaryOffset
            );
            Silk.NET.Direct3D12.DispatchRaysDesc native = new()
            {
                RayGenerationShaderRecord = new GpuVirtualAddressRange(baseAddress + rayGenerationOffset, stride),
                MissShaderTable = CreateRayDispatchRange(baseAddress, stride, Miss.Length, missOffset),
                HitGroupTable = CreateRayDispatchRange(baseAddress, stride, Hit.Length, hitOffset),
                CallableShaderTable = CreateRayDispatchRange(baseAddress, stride, Callable.Length, callableOffset),
                Width = 1,
                Height = 1,
                Depth = 1,
            };
            return new D3D12RayDispatchData(command.Recording.DescriptorArenaVersion, native);
        }

        private void CountAllDispatchDataRequirements(ref uint resourceCount, ref uint samplerCount, ref ulong ordinarySize)
        {
            CountDispatchDataRequirements(RayGeneration, ref resourceCount, ref samplerCount, ref ordinarySize);
            CountDispatchDataRequirements(Miss, ref resourceCount, ref samplerCount, ref ordinarySize);
            CountDispatchDataRequirements(Hit, ref resourceCount, ref samplerCount, ref ordinarySize);
            CountDispatchDataRequirements(Callable, ref resourceCount, ref samplerCount, ref ordinarySize);
        }

        private void CountDispatchDataRequirements(
            ReadOnlySpan<D3D12RecordedRayRecord> records,
            ref uint resourceCount,
            ref uint samplerCount,
            ref ulong ordinarySize
        )
        {
            foreach (D3D12RecordedRayRecord record in records)
            {
                ReadOnlySpan<RayTracingLocalParameterBlock> blocks = ParameterBlocks.Slice(checked((int)record.ParameterBlockOffset), checked((int)record.ParameterBlockCount));
                foreach (ref readonly RayTracingLocalParameterBlock block in blocks)
                {
                    NativeParameterBinding layout = record.Export.LocalRoot.GetBlock(block.Layout);
                    resourceCount = checked(resourceCount + (layout.ResourceTable?.DescriptorCount ?? 0));
                    samplerCount = checked(samplerCount + (layout.SamplerTable?.DescriptorCount ?? 0));
                    if (layout.OrdinaryRoot is OrdinaryRootBinding ordinary && !ordinary.UsesRootConstants)
                    {
                        ordinarySize = checked(ordinarySize + AlignUp(block.OrdinaryDataSize, 256));
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
            ref uint resourceOffset,
            ref uint samplerOffset,
            ref ulong ordinaryOffset
        )
        {
            for (int index = 0; index < records.Length; index++)
            {
                D3D12RecordedRayRecord record = records[index];
                Span<byte> target = destination.Slice(checked((int)(categoryOffset + checked((ulong)index * stride))), checked((int)stride));
                record.Export.Identifier.CopyTo(target);
                if (record.ParameterBlockCount == 0)
                    continue;
                WriteDefaultRayRootTables(command, record, target);
                ReadOnlySpan<RayTracingLocalParameterBlock> blocks = ParameterBlocks.Slice(checked((int)record.ParameterBlockOffset), checked((int)record.ParameterBlockCount));
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
                        ref resourceOffset,
                        ref samplerOffset,
                        ref ordinaryOffset
                    );
                }
            }
        }

        private static void WriteDefaultRayRootTables(D3D12CommandContext command, in D3D12RecordedRayRecord record, Span<byte> target)
        {
            foreach (DefaultRootTable defaultTable in record.Export.LocalRoot.DefaultTables)
            {
                GpuDescriptorHandle handle = command.Recording.GetGpuHandle(defaultTable.Heap, 0);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(checked(32 + (int)defaultTable.RootArgumentOffset)), handle.Ptr);
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
            ref uint resourceOffset,
            ref uint samplerOffset,
            ref ulong ordinaryOffset
        )
        {
            NativeParameterBinding layout = record.Export.LocalRoot.GetBlock(block.Layout);
            ReadOnlySpan<ResourceBinding> bindings = Resources.Slice(checked((int)block.ResourceOffset), checked((int)block.ResourceCount));
            uint recordResourceBase = resourceOffset;
            uint recordSamplerBase = samplerOffset;
            CopyRayRecordDescriptors(command, layout, bindings, ref resourceOffset, ref samplerOffset);
            WriteRayRecordTableHandles(command, layout, target, recordResourceBase, recordSamplerBase);
            if (layout.OrdinaryRoot is not OrdinaryRootBinding ordinaryRoot)
                return;
            ReadOnlySpan<byte> data = OrdinaryData.Slice(checked((int)block.OrdinaryDataOffset), checked((int)block.OrdinaryDataSize));
            WriteRayOrdinaryData(ordinaryRoot, data, target, destination, totalSize, baseAddress, ref ordinaryOffset);
        }

        private static void CopyRayRecordDescriptors(
            D3D12CommandContext command,
            NativeParameterBinding layout,
            ReadOnlySpan<ResourceBinding> bindings,
            ref uint resourceOffset,
            ref uint samplerOffset
        )
        {
            command.CopyTransientDescriptors(resourceOffset, samplerOffset, bindings, layout.Entries);
            foreach (ref readonly DescriptorEntryDesc entry in layout.Entries.AsSpan())
            {
                if (entry.Type == ResourceBindingType.Sampler)
                    samplerOffset++;
                else
                    resourceOffset++;
            }
        }

        private static void WriteRayRecordTableHandles(
            D3D12CommandContext command,
            NativeParameterBinding layout,
            Span<byte> target,
            uint resourceBase,
            uint samplerBase
        )
        {
            if (layout.ResourceTable is D3D12BoundedTable resourceTable)
            {
                GpuDescriptorHandle handle = command.Recording.GetGpuHandle(ParameterHeap.Resource, resourceBase);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(checked(32 + (int)resourceTable.RootArgumentOffset)), handle.Ptr);
            }

            if (layout.SamplerTable is D3D12BoundedTable samplerTable)
            {
                GpuDescriptorHandle handle = command.Recording.GetGpuHandle(ParameterHeap.Sampler, samplerBase);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(checked(32 + (int)samplerTable.RootArgumentOffset)), handle.Ptr);
            }
        }

        private static void WriteRayOrdinaryData(
            in OrdinaryRootBinding ordinaryRoot,
            ReadOnlySpan<byte> data,
            Span<byte> target,
            Span<byte> destination,
            ulong totalSize,
            ulong baseAddress,
            ref ulong ordinaryOffset
        )
        {
            if (ordinaryRoot.UsesRootConstants)
            {
                Span<byte> constants = target.Slice(checked(32 + (int)ordinaryRoot.RootArgumentOffset), checked((int)ordinaryRoot.ConstantCount * sizeof(uint)));
                constants.Clear();
                data.CopyTo(constants);
                return;
            }

            ordinaryOffset = AlignUp(ordinaryOffset, 256);
            data.CopyTo(destination.Slice(checked((int)ordinaryOffset), checked((int)(totalSize - ordinaryOffset))));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(target.Slice(checked(32 + (int)ordinaryRoot.RootArgumentOffset)), baseAddress + ordinaryOffset);
            ordinaryOffset = checked(ordinaryOffset + AlignUp((ulong)data.Length, 256));
        }

        private static GpuVirtualAddressRangeAndStride CreateRayDispatchRange(ulong baseAddress, ulong stride, int count, ulong offset) => count == 0 ? default : new GpuVirtualAddressRangeAndStride(baseAddress + offset, checked((ulong)count * stride), stride);
    }

    private readonly record struct D3D12RayDispatchData(ulong DescriptorArenaVersion, Silk.NET.Direct3D12.DispatchRaysDesc Description);
    private sealed partial class D3D12CommandBuffer
    {
        private RaytracingGeometryDesc[] _accelerationStructureGeometryDescriptions = [];
        private readonly Dictionary<D3D12RayTracingShaderTable, D3D12RecordedRayTable> _recordedRayTables = new(ReferenceEqualityComparer.Instance);
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
            int callableCount
        )
        {
            if (tableCount != 0)
            {
                _recordedRayTables.EnsureCapacity(checked(_recordedRayTables.Count + tableCount));
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
                _recordedRayTablePool[_recordedRayTableCount].PrepareCapacity(parameterBlockCount, resourceCount, ordinaryDataByteCount, rayGenerationCount, missCount, hitCount, callableCount);
            }
        }

        internal void PrepareAccelerationStructureGeometries(int count)
        {
            if (count > _accelerationStructureGeometryDescriptions.Length)
                Array.Resize(ref _accelerationStructureGeometryDescriptions, count);
        }

        internal Span<RaytracingGeometryDesc> ReserveAccelerationStructureGeometryDescriptions(int count)
        {
            if (_accelerationStructureGeometryDescriptions.Length < count)
            {
                int doubled = _accelerationStructureGeometryDescriptions.Length <= int.MaxValue / 2 ? _accelerationStructureGeometryDescriptions.Length * 2 : int.MaxValue;
                Array.Resize(ref _accelerationStructureGeometryDescriptions, Math.Max(count, Math.Max(4, doubled)));
            }

            return _accelerationStructureGeometryDescriptions.AsSpan(0, count);
        }

        internal D3D12RecordedRayTable RecordRayTableUpdate(D3D12RayTracingShaderTable table, in RayTracingShaderTableUpdate update)
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

        internal D3D12RecordedRayTable GetRecordedRayTable(D3D12RayTracingShaderTable table) => _recordedRayTables[table];
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
        internal Span<RaytracingGeometryDesc> ReserveAccelerationStructureGeometryDescriptions(int count) => Recording.ReserveAccelerationStructureGeometryDescriptions(count);
        internal D3D12RecordedRayTable RecordRayTableUpdate(D3D12RayTracingShaderTable table, in RayTracingShaderTableUpdate update) => Recording.RecordRayTableUpdate(table, update);
        internal D3D12RayDispatchData GetRayDispatchData(D3D12RayTracingShaderTable table) => Recording.GetRecordedRayTable(table).GetDispatchData(this, table);
    }

    private sealed class D3D12AccelerationStructureSrv : AccelerationStructureSrv, INativeDescriptor
    {
        private readonly ViewReferences _references;
        internal D3D12AccelerationStructureSrv(
            D3D12Device device,
            D3D12AccelerationStructure structure,
            in AccelerationStructureSrvDesc description,
            DescriptorAllocation descriptor
        ) : base(device, description)
        {
            NativeDescriptor = descriptor;
            _references = new ViewReferences(device, descriptor, structure.NativeData);
        }

        public DescriptorAllocation NativeDescriptor { get; }

        internal override void Release(bool fromParent) => _references.Release(this);
    }

    private sealed partial class D3D12CommandContext
    {
    }

    private static partial class RequireD3D12
    {
        internal static D3D12RayTracingPipeline RayTracingPipeline(Pipeline value) => (D3D12RayTracingPipeline)value;
        internal static D3D12AccelerationStructure AccelerationStructure(AccelerationStructure value) => (D3D12AccelerationStructure)value;
        internal static D3D12RayTracingShaderTable RayTracingShaderTable(RayTracingShaderTable value) => (D3D12RayTracingShaderTable)value;
    }
}
