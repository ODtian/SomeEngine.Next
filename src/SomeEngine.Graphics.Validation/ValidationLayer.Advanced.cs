namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer<TBackend>
{
    public Buffer CreateReservedBuffer(Device device, in BufferDesc desc)
    {
        RequireCapability<SparseResources>(device);
        return Track(Backend.CreateReservedBuffer(device, desc), device);
    }

    public Texture CreateReservedTexture(Device device, in TextureDesc desc)
    {
        RequireCapability<SparseResources>(device);
        return Track(Backend.CreateReservedTexture(device, desc), device);
    }

    public SparseResourceInfo GetSparseResourceInfo(Resource resource)
    {
        Require(resource);
        RequireCapability<SparseResources>(resource.Device);
        return Backend.GetSparseResourceInfo(resource);
    }

    public QueueCompletion UpdateSparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingDesc> mappings)
    {
        RequireQueue(queue);
        RequireCapability<SparseResources>(queue.Device);
        foreach (SparseMappingDesc mapping in mappings)
        {
            RequireOnDevice(queue.Device, mapping.Resource, "Sparse resource");
            if (mapping.Heap is not null)
                RequireOnDevice(queue.Device, mapping.Heap, "Sparse Heap");
        }
        return Backend.UpdateSparseMappings(queue, mappings);
    }

    public QueueCompletion CopySparseMappings(
        Queue queue,
        ReadOnlySpan<SparseMappingCopyDesc> copies)
    {
        RequireQueue(queue);
        RequireCapability<SparseResources>(queue.Device);
        foreach (SparseMappingCopyDesc copy in copies)
        {
            RequireOnDevice(queue.Device, copy.Source, "Sparse mapping source");
            RequireOnDevice(queue.Device, copy.Destination, "Sparse mapping destination");
        }
        return Backend.CopySparseMappings(queue, copies);
    }

    public ResidencyInfo GetResidencyInfo(Device device)
    {
        RequireCapability<Residency>(device);
        return Backend.GetResidencyInfo(device);
    }

    public ResidencyResource GetResidencyResource(Heap heap)
    {
        Require(heap);
        RequireCapability<Residency>(heap.Device);
        return Backend.GetResidencyResource(heap);
    }

    public ResidencyResource GetResidencyResource(Resource resource)
    {
        Require(resource);
        RequireCapability<Residency>(resource.Device);
        return Backend.GetResidencyResource(resource);
    }

    public ResidencyResource GetResidencyResource(QueryPool pool)
    {
        Require(pool);
        RequireCapability<Residency>(pool.Device);
        return Backend.GetResidencyResource(pool);
    }

    public ResidencyResource GetResidencyResource(DescriptorTable table)
    {
        Require(table);
        RequireCapability<Residency>(table.Device);
        return Backend.GetResidencyResource(table);
    }

    public QueueCompletion EnqueueMakeResident(
        Queue queue,
        ReadOnlySpan<ResidencyResource> resources)
    {
        RequireQueue(queue);
        RequireCapability<Residency>(queue.Device);
        foreach (ResidencyResource resource in resources)
        {
            if (resource.IsDefault || !ReferenceEquals(resource.Device, queue.Device))
                Reject("Residency", "ResidencyResource is invalid or belongs to another Device.");
        }
        return Backend.EnqueueMakeResident(queue, resources);
    }

    public void Evict(Device device, ReadOnlySpan<ResidencyResource> resources)
    {
        RequireCapability<Residency>(device);
        foreach (ResidencyResource resource in resources)
        {
            if (resource.IsDefault || !ReferenceEquals(resource.Device, device))
                Reject("Residency", "ResidencyResource is invalid or belongs to another Device.");
        }
        Backend.Evict(device, resources);
    }

    public SamplerFeedbackTexture CreateSamplerFeedbackTexture(
        Device device,
        in SamplerFeedbackTextureDesc desc)
    {
        SamplerFeedback capability = RequireCapability<SamplerFeedback>(device);
        RequireOnDevice(device, desc.SampledTexture, "Sampled Texture");
        TextureInfo sampled = desc.SampledTexture.Info;
        if (sampled.Dimension != TextureDimension.Texture2D ||
            sampled.SampleCount != 1 ||
            (sampled.Usages & TextureUsages.Sampled) == 0)
        {
            Reject(
                "SamplerFeedback",
                "Sampler feedback requires a single-sampled Texture2D with Sampled usage.",
                desc.Label);
        }
        if (!capability.SupportedFormats.Contains(sampled.Format))
            Reject("SamplerFeedback", $"Format {sampled.Format} does not support sampler feedback.");
        if (!Enum.IsDefined(desc.Type) ||
            desc.MipRegionWidth < capability.MinimumMipRegionWidth ||
            desc.MipRegionHeight < capability.MinimumMipRegionHeight ||
            desc.MipRegionWidth > sampled.Width / 2 ||
            desc.MipRegionHeight > sampled.Height / 2 ||
            !IsPowerOfTwo(desc.MipRegionWidth) ||
            !IsPowerOfTwo(desc.MipRegionHeight))
        {
            Reject(
                "SamplerFeedback",
                "Sampler-feedback mip-region dimensions must be supported powers of two.",
                desc.Label);
        }
        return Track(Backend.CreateSamplerFeedbackTexture(device, desc), desc.SampledTexture);
    }

    public SamplerFeedbackUav CreateSamplerFeedbackUav(
        Device device,
        SamplerFeedbackTexture texture,
        in TextureUavDesc desc)
    {
        RequireCapability<SamplerFeedback>(device);
        RequireOnDevice(device, texture, "Sampler feedback Texture");
        RequireOnDevice(device, desc.Texture, "Sampler feedback UAV Texture");
        if (!ReferenceEquals(texture, desc.Texture))
            Reject(
                "SamplerFeedback",
                "SamplerFeedbackUav must describe the supplied feedback Texture.",
                desc.Label);
        TextureInfo info = texture.Info;
        TextureSubresourceRange range = desc.Range;
        TextureViewDimension expectedDimension = info.ArrayLayerCount == 1
            ? TextureViewDimension.Texture2D
            : TextureViewDimension.Texture2DArray;
        if (desc.Format != Format.R8UInt ||
            desc.Dimension != expectedDimension ||
            range.FirstMipLevel != 0 ||
            range.MipLevelCount != info.MipLevelCount ||
            range.FirstArrayLayer != 0 ||
            range.ArrayLayerCount != info.ArrayLayerCount ||
            range.Aspects is not (TextureAspects.Color or TextureAspects.Plane0))
        {
            Reject(
                "SamplerFeedback",
                "A sampler-feedback UAV must describe the complete feedback Texture.",
                desc.Label);
        }
        return Track(Backend.CreateSamplerFeedbackUav(device, texture, desc), texture);
    }

    public void ClearSamplerFeedback(
        CommandContext context,
        SamplerFeedbackUav feedback)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RequireCapability<SamplerFeedback>(context.Device);
        RequireOnDevice(context.Device, feedback, "Sampler feedback UAV");
        Backend.ClearSamplerFeedback(context, feedback);
        RecordCommandDependency(state, feedback);
    }

    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Buffer destination,
        in BufferRange destinationRange)
    {
        ContextValidationState state = RequireGraphicsOutsideRendering(context);
        RequireCapability<SamplerFeedback>(context.Device);
        RequireOnDevice(context.Device, feedback, "Sampler feedback Texture");
        RequireOnDevice(context.Device, destination, "Sampler feedback destination");
        Backend.ResolveSamplerFeedback(context, feedback, destination, destinationRange);
        RecordCommandDependency(state, feedback);
        RecordCommandDependency(state, destination);
    }

    public void ResolveSamplerFeedback(
        CommandContext context,
        SamplerFeedbackTexture feedback,
        Texture destination,
        in TextureSubresourceRange destinationRange)
    {
        ContextValidationState state = RequireGraphicsOutsideRendering(context);
        RequireCapability<SamplerFeedback>(context.Device);
        RequireOnDevice(context.Device, feedback, "Sampler feedback Texture");
        RequireOnDevice(context.Device, destination, "Sampler feedback destination");
        Backend.ResolveSamplerFeedback(context, feedback, destination, destinationRange);
        RecordCommandDependency(state, feedback);
        RecordCommandDependency(state, destination);
    }

    public AccelerationStructure CreateAccelerationStructure(
        Device device,
        Buffer storage,
        in BufferRange storageRange,
        AccelerationStructureType type,
        string? label = null)
    {
        RequireCapability<RayTracing>(device);
        RequireOnDevice(device, storage, "Acceleration-structure storage");
        return Track(
            Backend.CreateAccelerationStructure(device, storage, storageRange, type, label),
            storage);
    }

    public AccelerationStructureSrv CreateAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc)
    {
        RequireCapability<RayTracing>(device);
        RequireOnDevice(device, desc.AccelerationStructure, "AccelerationStructure");
        return Track(Backend.CreateAccelerationStructureSrv(device, desc), desc.AccelerationStructure);
    }

    public BindlessAccelerationStructureSrv CreateBindlessAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc)
    {
        RequireCapability<RayTracing>(device);
        RequireOnDevice(device, desc.AccelerationStructure, "AccelerationStructure");
        return Track(
            Backend.CreateBindlessAccelerationStructureSrv(device, desc),
            desc.AccelerationStructure);
    }

    public AccelerationStructureBuildInfo GetAccelerationStructureBuildInfo(
        Device device,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
        RayTracing capability = RequireCapability<RayTracing>(device);
        foreach (AccelerationStructureGeometry geometry in geometries)
        {
            RequireOnDevice(device, geometry.Primary.Buffer, "Acceleration-structure input");
            if (geometry.Secondary.Buffer is not null)
                RequireOnDevice(device, geometry.Secondary.Buffer, "Acceleration-structure input");
            if (geometry.Transform.Buffer is not null)
                RequireOnDevice(device, geometry.Transform.Buffer, "Acceleration-structure transform");
        }
        ValidateAccelerationStructureBuild(capability, type, options, geometries);
        return Backend.GetAccelerationStructureBuildInfo(device, type, options, geometries);
    }

    public Pipeline CreateRayTracingPipeline(
        Device device,
        in RayTracingPipelineDesc desc,
        PipelineCache? cache = null)
    {
        RayTracing capability = RequireCapability<RayTracing>(device);
        if (!capability.PipelineRayTracing)
            throw new NotSupportedException("Pipeline ray tracing is unavailable.");
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        if (desc.MaximumRecursionDepth == 0 ||
            desc.MaximumRecursionDepth > capability.MaximumRecursionDepth ||
            desc.MaximumPayloadSize > capability.MaximumPayloadSize ||
            desc.MaximumAttributeSize > capability.MaximumAttributeSize ||
            desc.NodeMask == 0 ||
            (desc.NodeMask & ~device.EnabledNodeMask) != 0)
        {
            Reject("RayTracing", "The ray-tracing Pipeline exceeds an advertised Device limit.");
        }
        RayTracingPipelineValidationState validation = ValidateRayTracingPipeline(desc);
        Pipeline result = Track(Backend.CreateRayTracingPipeline(device, desc, cache), device);
        _pipelineBindingStates.Add(
            result,
            ReflectPipelineBindings(
                desc.Program,
                ReadOnlySpan<SlangShaderSharp.EntryPointReflection>.Empty));
        _rayTracingPipelines.Add(result, validation);
        return result;
    }

    public RayTracingShaderTable CreateRayTracingShaderTable(
        Device device,
        in RayTracingShaderTableDesc desc)
    {
        RayTracing capability = RequireCapability<RayTracing>(device);
        RequireOnDevice(device, desc.Pipeline, "Ray-tracing Pipeline");
        if (desc.Pipeline.Type != PipelineType.RayTracing)
        {
            Reject("RayTracing", "The Pipeline is not a ray-tracing Pipeline.", desc.Label);
        }
        if (!_rayTracingPipelines.TryGetValue(
                desc.Pipeline,
                out RayTracingPipelineValidationState? pipelineState))
        {
            Reject("Ownership", "Ray-tracing Pipeline was not created through this Validation Layer.");
        }
        RayTracingPipelineValidationState validatedPipeline = pipelineState!;
        if (desc.RayGenerationRecordCount != 1 ||
            desc.MaximumRecordSize < 32 ||
            desc.MaximumRecordSize > capability.MaximumShaderRecordStride ||
            desc.MaximumRecordSize % capability.ShaderRecordAlignment != 0)
        {
            Reject("RayTracing", "The shader-table record capacity is invalid.", desc.Label);
        }
        if ((desc.MissRecordCount != 0 &&
             !validatedPipeline.HasExport(RayExportValidationType.Miss)) ||
            (desc.HitRecordCount != 0 && validatedPipeline.HitGroups.Count == 0) ||
            (desc.CallableRecordCount != 0 &&
             !validatedPipeline.HasExport(RayExportValidationType.Callable)))
        {
            Reject("RayTracing", "A shader-table category has no compatible Pipeline export.");
        }
        RayTracingShaderTable result = Track(
            Backend.CreateRayTracingShaderTable(device, desc),
            desc.Pipeline);
        _rayTracingTables.Add(result, new RayTracingTableValidationState(validatedPipeline));
        return result;
    }

    public void BuildAccelerationStructure(
        CommandContext context,
        in AccelerationStructureBuildDesc desc)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RayTracing capability = RequireCapability<RayTracing>(context.Device);
        RequireOnDevice(context.Device, desc.Destination, "Acceleration-structure destination");
        RequireOnDevice(context.Device, desc.Scratch, "Acceleration-structure scratch Buffer");
        if (desc.Source is not null)
            RequireOnDevice(context.Device, desc.Source, "Acceleration-structure source");
        foreach (AccelerationStructureGeometry geometry in desc.Geometries)
        {
            RequireOnDevice(context.Device, geometry.Primary.Buffer, "Acceleration-structure input");
            if (geometry.Secondary.Buffer is not null)
                RequireOnDevice(context.Device, geometry.Secondary.Buffer, "Acceleration-structure input");
            if (geometry.Transform.Buffer is not null)
                RequireOnDevice(context.Device, geometry.Transform.Buffer, "Acceleration-structure transform");
        }
        ValidateAccelerationStructureBuild(capability, desc.Type, desc.Options, desc.Geometries);
        if (desc.Destination.Info.Type != desc.Type)
            Reject("RayTracing", "The destination acceleration-structure type does not match the build.");
        bool update = (desc.Options & AccelerationStructureBuildOptions.PerformUpdate) != 0;
        if (update && desc.Source is null)
            Reject("RayTracing", "An update build requires a source acceleration structure.");
        if (!update && desc.Source is not null)
            Reject("RayTracing", "A non-update build cannot name a source acceleration structure.");
        if (desc.Source is not null && desc.Source.Info.Type != desc.Type)
            Reject("RayTracing", "The update source type does not match the build.");
        Backend.BuildAccelerationStructure(context, desc);
        RecordCommandDependency(state, desc.Destination);
        RecordCommandDependency(state, desc.Scratch);
        if (desc.Source is not null)
            RecordCommandDependency(state, desc.Source);
        foreach (AccelerationStructureGeometry geometry in desc.Geometries)
        {
            RecordCommandDependency(state, geometry.Primary.Buffer);
            if (geometry.Secondary.Buffer is not null)
                RecordCommandDependency(state, geometry.Secondary.Buffer);
            if (geometry.Transform.Buffer is not null)
                RecordCommandDependency(state, geometry.Transform.Buffer);
        }
    }

    public void CopyAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        AccelerationStructure source,
        AccelerationStructureCopyType type)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RayTracing capability = RequireCapability<RayTracing>(context.Device);
        RequireOnDevice(context.Device, destination, "Acceleration-structure destination");
        RequireOnDevice(context.Device, source, "Acceleration-structure source");
        if (!Enum.IsDefined(type))
            Reject("RayTracing", "AccelerationStructureCopyType is invalid.");
        if (type == AccelerationStructureCopyType.Compact && !capability.Compaction)
            Reject("RayTracing", "Acceleration-structure compaction is unavailable.");
        if (destination.Info.Type != source.Info.Type)
            Reject("RayTracing", "Acceleration-structure copy types are incompatible.");
        if (type == AccelerationStructureCopyType.Clone &&
            destination.Info.Size < source.Info.Size)
        {
            Reject("RayTracing", "A clone destination cannot be smaller than its source.");
        }
        if (RangesOverlap(
                destination.Info.Storage,
                destination.Info.StorageRange,
                source.Info.Storage,
                source.Info.StorageRange))
        {
            Reject("RayTracing", "Acceleration-structure copy ranges cannot overlap.");
        }
        Backend.CopyAccelerationStructure(context, destination, source, type);
        RecordCommandDependency(state, destination);
        RecordCommandDependency(state, source);
    }

    public void SerializeAccelerationStructure(
        CommandContext context,
        in BufferRegion destination,
        AccelerationStructure source)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RayTracing capability = RequireCapability<RayTracing>(context.Device);
        if (!capability.Serialization)
            Reject("RayTracing", "Acceleration-structure serialization is unavailable.");
        RequireOnDevice(context.Device, destination.Buffer, "Serialization destination");
        RequireOnDevice(context.Device, source, "Acceleration-structure source");
        BufferRange resolvedDestination = destination.Range.Resolve(destination.Buffer.Info.Size);
        if (RangesOverlap(
                destination.Buffer,
                resolvedDestination,
                source.Info.Storage,
                source.Info.StorageRange))
        {
            Reject("RayTracing", "Serialized output cannot overlap its source.");
        }
        Backend.SerializeAccelerationStructure(context, destination, source);
        RecordCommandDependency(state, destination.Buffer);
        RecordCommandDependency(state, source);
    }

    public void DeserializeAccelerationStructure(
        CommandContext context,
        AccelerationStructure destination,
        in BufferRegion source)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RayTracing capability = RequireCapability<RayTracing>(context.Device);
        if (!capability.Serialization)
            Reject("RayTracing", "Acceleration-structure deserialization is unavailable.");
        RequireOnDevice(context.Device, destination, "Acceleration-structure destination");
        RequireOnDevice(context.Device, source.Buffer, "Serialization source");
        BufferRange resolvedSource = source.Range.Resolve(source.Buffer.Info.Size);
        if (RangesOverlap(
                destination.Info.Storage,
                destination.Info.StorageRange,
                source.Buffer,
                resolvedSource))
        {
            Reject("RayTracing", "Serialized input cannot overlap its destination.");
        }
        Backend.DeserializeAccelerationStructure(context, destination, source);
        RecordCommandDependency(state, destination);
        RecordCommandDependency(state, source.Buffer);
    }

    public void EmitAccelerationStructurePostBuildInfo(
        CommandContext context,
        AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type,
        Buffer destination,
        ulong destinationOffset)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RayTracing capability = RequireCapability<RayTracing>(context.Device);
        RequireOnDevice(context.Device, source, "Acceleration-structure source");
        RequireOnDevice(context.Device, destination, "Post-build-info destination");
        if (!Enum.IsDefined(type))
            Reject("RayTracing", "AccelerationStructurePostBuildInfoType is invalid.");
        if (type == AccelerationStructurePostBuildInfoType.CompactedSize && !capability.Compaction)
            Reject("RayTracing", "Acceleration-structure compaction is unavailable.");
        if (type == AccelerationStructurePostBuildInfoType.SerializationSize &&
            !capability.Serialization)
        {
            Reject("RayTracing", "Acceleration-structure serialization is unavailable.");
        }
        Backend.EmitAccelerationStructurePostBuildInfo(
            context,
            source,
            type,
            destination,
            destinationOffset);
        RecordCommandDependency(state, source);
        RecordCommandDependency(state, destination);
    }

    public void UpdateRayTracingShaderTable(
        CommandContext context,
        RayTracingShaderTable table,
        in RayTracingShaderTableUpdate update)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RequireCapability<RayTracing>(context.Device);
        RequireOnDevice(context.Device, table, "Ray-tracing shader table");
        if (!_rayTracingTables.TryGetValue(table, out RayTracingTableValidationState? tableState))
            Reject("Ownership", "RayTracingShaderTable was not created through this Validation Layer.");
        foreach (ref readonly ResourceBinding binding in update.Resources)
        {
            if (binding.Value is DeviceResource resource)
                RequireOnDevice(context.Device, resource, "Ray-tracing shader-table binding");
        }
        ValidateRayTracingTableUpdate(table, tableState!.Pipeline, update);
        Backend.UpdateRayTracingShaderTable(context, table, update);
        RecordCommandDependency(state, table);
        foreach (ref readonly ResourceBinding binding in update.Resources)
        {
            if (binding.Value is GraphicsObject dependency)
                RecordCommandDependency(state, dependency);
        }
    }

    public void DispatchRays(CommandContext context, in DispatchRaysDesc desc)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RayTracing capability = RequireCapability<RayTracing>(context.Device);
        RequirePipeline(state, context, PipelineType.RayTracing, "DispatchRays");
        RequireOnDevice(context.Device, desc.ShaderTable, "Ray-tracing shader table");
        RequireCurrentRayTracingTablePipeline(state, context, desc.ShaderTable);
        if (desc.Width == 0 || desc.Height == 0 || desc.Depth == 0 ||
            (ulong)desc.Width * desc.Height * desc.Depth >
                capability.MaximumRayGenerationShaderThreads)
        {
            Reject("RayTracing", "The ray-generation thread grid exceeds the Device limit.");
        }
        Backend.DispatchRays(context, desc);
        RecordCommandDependency(state, desc.ShaderTable);
    }

    public void DispatchRaysIndirect(
        CommandContext context,
        RayTracingShaderTable table,
        in BufferRegion arguments)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RayTracing capability = RequireCapability<RayTracing>(context.Device);
        if (!capability.IndirectDispatch)
            Reject("RayTracing", "Indirect ray dispatch is unavailable on this Device.");
        RequirePipeline(state, context, PipelineType.RayTracing, "DispatchRaysIndirect");
        RequireOnDevice(context.Device, table, "Ray-tracing shader table");
        RequireCurrentRayTracingTablePipeline(state, context, table);
        RequireOnDevice(context.Device, arguments.Buffer, "Indirect arguments");
        Backend.DispatchRaysIndirect(context, table, arguments);
        RecordCommandDependency(state, table);
        RecordCommandDependency(state, arguments.Buffer);
    }

    public void DispatchMesh(CommandContext context, in DispatchArguments arguments)
    {
        ContextValidationState state = RequireDraw(context);
        MeshShaders capability = RequireCapability<MeshShaders>(context.Device);
        RequirePipeline(state, context, PipelineType.Mesh, "DispatchMesh");
        if (arguments.X > capability.MaximumThreadGroupCountX ||
            arguments.Y > capability.MaximumThreadGroupCountY ||
            arguments.Z > capability.MaximumThreadGroupCountZ ||
            (ulong)arguments.X * arguments.Y * arguments.Z >
                capability.MaximumTotalThreadGroupCount)
        {
            Reject("MeshShaders", "DispatchMesh exceeds an advertised thread-group limit.");
        }
        Backend.DispatchMesh(context, arguments);
    }

    public void DispatchMeshIndirect(CommandContext context, in BufferRegion arguments)
    {
        ContextValidationState state = RequireDraw(context);
        MeshShaders capability = RequireCapability<MeshShaders>(context.Device);
        RequirePipeline(state, context, PipelineType.Mesh, "DispatchMeshIndirect");
        if (!capability.IndirectDispatch)
            Reject("MeshShaders", "Indirect mesh dispatch is unavailable on this Device.");
        RequireOnDevice(context.Device, arguments.Buffer, "Indirect arguments");
        Backend.DispatchMeshIndirect(context, arguments);
        RecordCommandDependency(state, arguments.Buffer);
    }

    public void SetShadingRate(
        CommandContext context,
        ShadingRate rate,
        ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner)
    {
        RequireGraphicsRecording(context);
        VariableRateShading capability = RequireCapability<VariableRateShading>(context.Device);
        if (!capability.Rates.Contains(rate))
            Reject("VariableRateShading", $"Shading rate {rate} is unavailable on this Device.");
        if (!capability.Combiners.Contains(primitiveCombiner) ||
            !capability.Combiners.Contains(imageCombiner))
        {
            Reject(
                "VariableRateShading",
                "A requested shading-rate combiner is unavailable on this Device.");
        }
        Backend.SetShadingRate(context, rate, primitiveCombiner, imageCombiner);
    }

    public void SetShadingRateImage(CommandContext context, Texture? texture)
    {
        ContextValidationState state = RequireGraphicsRecording(context);
        VariableRateShading capability = RequireCapability<VariableRateShading>(context.Device);
        if (!capability.ShadingRateImage)
            Reject("VariableRateShading", "Shading-rate images are unavailable on this Device.");
        if (texture is not null)
        {
            RequireOnDevice(context.Device, texture, "Shading-rate image");
            TextureInfo info = texture.Info;
            if (info.Dimension != TextureDimension.Texture2D ||
                info.Format != Format.R8UInt ||
                info.MipLevelCount != 1 ||
                info.ArrayLayerCount != 1 ||
                info.SampleCount != 1 ||
                (info.Usages & TextureUsages.ShadingRate) == 0)
            {
                Reject(
                    "VariableRateShading",
                    "A shading-rate image must be a single-mip, single-layer, non-MSAA " +
                    "R8UInt Texture2D with ShadingRate usage.",
                    texture.Label);
            }
        }
        Backend.SetShadingRateImage(context, texture);
        if (texture is not null)
            RecordCommandDependency(state, texture);
    }

    public Pipeline CreateWorkGraphPipeline(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null)
    {
        WorkGraphs capability = RequireCapability<WorkGraphs>(device);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        if (desc.MaximumInputRecordCount == 0 ||
            desc.MaximumInputRecordCount > capability.MaximumInputRecordCount ||
            desc.EntryPoints.Length > capability.MaximumNodeCount)
        {
            Reject("WorkGraphs", "The Work Graph pipeline exceeds an advertised Device limit.");
        }
        var entryIdentities = new HashSet<(string Name, uint NodeIndex)>();
        var entryMaximums = new uint[desc.EntryPoints.Length];
        for (int index = 0; index < desc.EntryPoints.Length; index++)
        {
            ref readonly WorkGraphEntryPointLayout entry = ref desc.EntryPoints[index];
            if (!entryIdentities.Add((entry.EntryPoint.Name, entry.NodeIndex)) ||
                entry.MaximumInputRecordCount == 0 ||
                entry.MaximumInputRecordCount > desc.MaximumInputRecordCount)
            {
                Reject("WorkGraphs", "A Work Graph entry-point layout is incompatible.");
            }
            entryMaximums[index] = entry.MaximumInputRecordCount;
        }
        var overridden = new HashSet<SlangShaderSharp.EntryPointReflection>();
        foreach (ref readonly WorkGraphNodeOverride value in desc.NodeOverrides)
        {
            if (!overridden.Add(value.EntryPoint) ||
                value.MaximumDispatchGridX == 0 ||
                value.MaximumDispatchGridY == 0 ||
                value.MaximumDispatchGridZ == 0 ||
                value.MaximumInputRecordCount == 0 ||
                value.MaximumInputRecordCount > desc.MaximumInputRecordCount)
            {
                Reject("WorkGraphs", "A Work Graph node override is incompatible.");
            }
        }
        Pipeline result = Track(Backend.CreateWorkGraphPipeline(device, desc, cache), device);
        _pipelineBindingStates.Add(
            result,
            ReflectPipelineBindings(
                desc.Program,
                ReadOnlySpan<SlangShaderSharp.EntryPointReflection>.Empty));
        _workGraphPipelines.Add(
            result,
            new WorkGraphPipelineValidationState(desc.MaximumInputRecordCount, entryMaximums));
        return result;
    }

    public WorkGraphMemoryRequirements GetWorkGraphMemoryRequirements(Pipeline pipeline)
    {
        Require(pipeline);
        if (pipeline.Type != PipelineType.WorkGraph ||
            !_workGraphPipelines.TryGetValue(pipeline, out _))
        {
            Reject("WorkGraphs", "The Pipeline is not a Work Graph Pipeline.", pipeline.Label);
        }
        return Backend.GetWorkGraphMemoryRequirements(pipeline);
    }

    public void SetWorkGraphProgram(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion backingMemory,
        WorkGraphInitialization initialization,
        uint maximumInputRecordCount)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RequireOnDevice(context.Device, pipeline, "Work-graph Pipeline");
        if (pipeline.Type != PipelineType.WorkGraph)
            Reject("Commands", "SetWorkGraphProgram requires a WorkGraph Pipeline.", pipeline.Label);
        if (!_workGraphPipelines.TryGetValue(
                pipeline,
                out WorkGraphPipelineValidationState? pipelineState))
        {
            Reject("Ownership", "Work Graph Pipeline was not created through this Validation Layer.");
        }
        if (!Enum.IsDefined(initialization))
            Reject("WorkGraphs", "WorkGraphInitialization is invalid.");
        WorkGraphs capability = RequireCapability<WorkGraphs>(context.Device);
        if (maximumInputRecordCount == 0 ||
            maximumInputRecordCount > pipelineState!.MaximumInputRecordCount ||
            maximumInputRecordCount > capability.MaximumInputRecordCount)
        {
            Reject("WorkGraphs", "The Work Graph input-record count exceeds its declared limit.");
        }
        RequireOnDevice(context.Device, backingMemory.Buffer, "Work-graph backing Buffer");
        Backend.SetWorkGraphProgram(
            context,
            pipeline,
            backingMemory,
            initialization,
            maximumInputRecordCount);
        RecordCommandDependency(state, pipeline);
        RecordCommandDependency(state, backingMemory.Buffer);
        lock (state)
        {
            state.Pipeline = pipeline;
            state.PipelineType = PipelineType.WorkGraph;
            state.PipelineSignature = pipeline.Signature;
            state.PipelineSignatureSet = true;
            state.WorkGraphProgram = true;
        }
    }

    public void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        WorkGraphs capability = RequireCapability<WorkGraphs>(context.Device);
        RequirePipeline(state, context, PipelineType.WorkGraph, "DispatchWorkGraph");
        lock (state)
        {
            if (!state.WorkGraphProgram)
                Reject("Commands", "DispatchWorkGraph requires SetWorkGraphProgram.", context.Label);
        }
        Pipeline selected;
        lock (state)
            selected = state.Pipeline!;
        if (!_workGraphPipelines.TryGetValue(
                selected,
                out WorkGraphPipelineValidationState? pipelineState) ||
            desc.EntryPointIndex >= pipelineState.EntryMaximumInputRecordCounts.Length)
        {
            Reject("WorkGraphs", "The Work Graph entry-point index is invalid.", context.Label);
        }
        uint entryMaximum = pipelineState!.EntryMaximumInputRecordCounts[desc.EntryPointIndex];
        if (desc.RecordCount == 0 ||
            desc.RecordCount > entryMaximum ||
            desc.RecordCount > pipelineState.MaximumInputRecordCount ||
            desc.RecordCount > capability.MaximumInputRecordCount)
        {
            Reject("WorkGraphs", "The Work Graph record count exceeds its declared limit.");
        }
        if (desc.UsesGpuRecords && !capability.GpuInput)
            Reject("WorkGraphs", "GPU Work Graph input is unavailable on this Device.");
        if (!desc.UsesGpuRecords && !capability.CpuInput)
            Reject("WorkGraphs", "CPU Work Graph input is unavailable on this Device.");
        if (desc.UsesGpuRecords)
            RequireOnDevice(context.Device, desc.GpuRecords.Buffer, "Work-graph input Buffer");
        Backend.DispatchWorkGraph(context, desc);
        if (desc.UsesGpuRecords)
            RecordCommandDependency(state, desc.GpuRecords.Buffer);
    }

    public IndirectCommandLayout CreateIndirectCommandLayout(
        Device device,
        in IndirectCommandLayoutDesc desc)
    {
        IndirectCommands capability = RequireCapability<IndirectCommands>(device);
        if (desc.Pipeline is not null)
            RequireOnDevice(device, desc.Pipeline, "Indirect Pipeline");
        if (desc.Stride == 0 ||
            desc.Stride > capability.MaximumStride ||
            desc.Stride % capability.ArgumentBufferAlignment != 0)
        {
            Reject("IndirectCommands", "The indirect command stride exceeds the advertised limits.");
        }
        foreach (ref readonly IndirectArgumentDesc argument in desc.Arguments)
        {
            IndirectArgumentTypes required = ToIndirectArgumentSupport(argument.Type);
            if ((capability.ArgumentTypes & required) == 0)
            {
                Reject(
                    "IndirectCommands",
                    $"Indirect argument {argument.Type} is unavailable on this Device.");
            }
        }
        IndirectCommandLayout result = Track(
            Backend.CreateIndirectCommandLayout(device, desc),
            device);
        PipelineType actionPipelineType = ClassifyIndirectAction(desc.Arguments);
        _indirectLayouts.Add(
            result,
            new IndirectLayoutValidationState(
                actionPipelineType,
                desc.Pipeline?.Signature ?? default,
                desc.Pipeline is not null));
        return result;
    }

    public void ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count = null)
    {
        IndirectCommands capability = RequireCapability<IndirectCommands>(context.Device);
        if (maximumCommandCount == 0 || maximumCommandCount > capability.MaximumCommandCount)
        {
            Reject(
                "IndirectCommands",
                "The indirect command count exceeds the advertised limit.");
        }
        RequireOnDevice(context.Device, layout, "Indirect command layout");
        if (!_indirectLayouts.TryGetValue(layout, out IndirectLayoutValidationState? layoutState))
        {
            Reject(
                "Ownership",
                "IndirectCommandLayout was not created through this Validation Layer.",
                layout.Label);
        }
        ContextValidationState state = layoutState!.ActionPipelineType is
            PipelineType.Graphics or PipelineType.Mesh
                ? RequireDraw(context)
                : RequireComputeOutsideRendering(context);
        RequirePipeline(
            state,
            context,
            layoutState.ActionPipelineType,
            "ExecuteIndirect");
        lock (state)
        {
            if (layoutState.PipelineSignatureSet &&
                (!state.PipelineSignatureSet ||
                 state.PipelineSignature != layoutState.PipelineSignature))
            {
                Reject(
                    "Commands",
                    "ExecuteIndirect requires the Pipeline signature captured by its layout.",
                    context.Label);
            }
            if (layoutState.ActionPipelineType == PipelineType.WorkGraph &&
                !state.WorkGraphProgram)
            {
                Reject(
                    "Commands",
                    "Indirect Work Graph dispatch requires SetWorkGraphProgram.",
                    context.Label);
            }
        }
        RequireOnDevice(context.Device, arguments.Buffer, "Indirect arguments");
        if (count is { } countRegion)
            RequireOnDevice(context.Device, countRegion.Buffer, "Indirect count Buffer");
        Backend.ExecuteIndirect(context, layout, arguments, maximumCommandCount, count);
        RecordCommandDependency(state, layout);
        RecordCommandDependency(state, arguments.Buffer);
        if (count is { } recordedCount)
            RecordCommandDependency(state, recordedCount.Buffer);
    }

    private static PipelineType ClassifyIndirectAction(
        ReadOnlySpan<IndirectArgumentDesc> arguments)
    {
        foreach (ref readonly IndirectArgumentDesc argument in arguments)
        {
            switch (argument.Type)
            {
                case IndirectArgumentType.Draw:
                case IndirectArgumentType.DrawIndexed:
                    return PipelineType.Graphics;
                case IndirectArgumentType.Dispatch:
                    return PipelineType.Compute;
                case IndirectArgumentType.DispatchMesh:
                    return PipelineType.Mesh;
                case IndirectArgumentType.DispatchRays:
                    return PipelineType.RayTracing;
                case IndirectArgumentType.WorkGraph:
                    return PipelineType.WorkGraph;
            }
        }
        throw new InvalidOperationException(
            "The backend accepted an indirect layout without an action argument.");
    }

    private RayTracingPipelineValidationState ValidateRayTracingPipeline(
        in RayTracingPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc.Program);
        if (desc.RayGeneration.IsEmpty)
            Reject("RayTracing", "A ray-tracing Pipeline requires a ray-generation export.");
        const RayTracingPipelineOptions supportedOptions =
            RayTracingPipelineOptions.SkipTriangles |
            RayTracingPipelineOptions.SkipProceduralPrimitives;
        if ((desc.Options & ~supportedOptions) != 0)
            Reject("RayTracing", "RayTracingPipelineOptions contains an unknown value.");

        var result = new RayTracingPipelineValidationState();
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddEntries(
            desc.RayGeneration,
            SlangShaderSharp.SlangStage.RayGeneration,
            RayExportValidationType.RayGeneration);
        AddEntries(
            desc.Miss,
            SlangShaderSharp.SlangStage.Miss,
            RayExportValidationType.Miss);
        AddEntries(
            desc.Callable,
            SlangShaderSharp.SlangStage.Callable,
            RayExportValidationType.Callable);

        foreach (ref readonly RayTracingHitGroup hitGroup in desc.HitGroups)
        {
            if (string.IsNullOrWhiteSpace(hitGroup.Name) || !names.Add(hitGroup.Name))
                Reject("RayTracing", "Ray-tracing state-object export names must be unique.");
            SlangShaderSharp.VariableLayoutReflection layout =
                SlangShaderSharp.VariableLayoutReflection.Null;
            bool hasMember = false;
            AddHitMember(
                hitGroup.ClosestHit,
                SlangShaderSharp.SlangStage.ClosestHit);
            AddHitMember(hitGroup.AnyHit, SlangShaderSharp.SlangStage.AnyHit);
            AddHitMember(
                hitGroup.Intersection,
                SlangShaderSharp.SlangStage.Intersection);
            if (!hasMember)
                Reject("RayTracing", "A ray-tracing hit group requires at least one shader.");
            result.HitGroups.Add(
                hitGroup.Name,
                CreateRayExportValidationState(RayExportValidationType.Hit, layout));

            void AddHitMember(
                SlangShaderSharp.EntryPointReflection entry,
                SlangShaderSharp.SlangStage expectedStage)
            {
                if (entry == SlangShaderSharp.EntryPointReflection.Null)
                    return;
                ValidateEntry(entry, expectedStage);
                if (!hasMember)
                {
                    layout = entry.VarLayout;
                    hasMember = true;
                }
                else if (entry.VarLayout != layout)
                {
                    Reject(
                        "RayTracing",
                        "Every shader in a hit group must expose the same local parameter layout.");
                }
            }
        }
        return result;

        void AddEntries(
            ReadOnlySpan<SlangShaderSharp.EntryPointReflection> entries,
            SlangShaderSharp.SlangStage expectedStage,
            RayExportValidationType type)
        {
            foreach (SlangShaderSharp.EntryPointReflection entry in entries)
            {
                ValidateEntry(entry, expectedStage);
                result.Entries.TryAdd(
                    entry,
                    CreateRayExportValidationState(type, entry.VarLayout));
            }
        }

        void ValidateEntry(
            SlangShaderSharp.EntryPointReflection entry,
            SlangShaderSharp.SlangStage expectedStage)
        {
            if (entry == SlangShaderSharp.EntryPointReflection.Null ||
                entry.Stage != expectedStage ||
                string.IsNullOrWhiteSpace(entry.Name) ||
                !names.Add(entry.Name))
            {
                Reject(
                    "RayTracing",
                    $"A ray-tracing export is not a unique {expectedStage} Slang entry point.");
            }
        }
    }

    private RayExportValidationState CreateRayExportValidationState(
        RayExportValidationType type,
        SlangShaderSharp.VariableLayoutReflection layout)
    {
        ValidationParameterBlockLayout? reflectedLayout = layout ==
            SlangShaderSharp.VariableLayoutReflection.Null
                ? null
                : ValidationParameterBlockLayout.Reflect(layout);
        return new RayExportValidationState(type, layout, reflectedLayout);
    }

    private void ValidateRayTracingTableUpdate(
        RayTracingShaderTable table,
        RayTracingPipelineValidationState pipeline,
        in RayTracingShaderTableUpdate update)
    {
        RayTracingShaderTableDesc description = table.Description;
        if ((uint)update.RayGeneration.Length != description.RayGenerationRecordCount ||
            (uint)update.Miss.Length != description.MissRecordCount ||
            (uint)update.Hit.Length != description.HitRecordCount ||
            (uint)update.Callable.Length != description.CallableRecordCount)
        {
            Reject(
                "RayTracing",
                "Every shader-table update category must match its declared record count.",
                table.Label);
        }
        ValidateRecords(
            update.RayGeneration,
            RayExportValidationType.RayGeneration,
            false,
            update.Resources,
            update.OrdinaryData);
        ValidateRecords(
            update.Miss,
            RayExportValidationType.Miss,
            false,
            update.Resources,
            update.OrdinaryData);
        ValidateRecords(
            update.Hit,
            RayExportValidationType.Hit,
            true,
            update.Resources,
            update.OrdinaryData);
        ValidateRecords(
            update.Callable,
            RayExportValidationType.Callable,
            false,
            update.Resources,
            update.OrdinaryData);

        void ValidateRecords(
            ReadOnlySpan<RayTracingShaderRecord> records,
            RayExportValidationType expectedType,
            bool hitRecords,
            ReadOnlySpan<ResourceBinding> allResources,
            ReadOnlySpan<byte> allOrdinaryData)
        {
            foreach (ref readonly RayTracingShaderRecord record in records)
            {
                RayExportValidationState export;
                if (hitRecords)
                {
                    if (record.EntryPoint != SlangShaderSharp.EntryPointReflection.Null ||
                        record.HitGroupName is null)
                    {
                        Reject("RayTracing", "A hit record names an incompatible hit-group export.");
                    }
                    if (!pipeline.HitGroups.TryGetValue(
                            record.HitGroupName!,
                            out RayExportValidationState? found))
                    {
                        Reject("RayTracing", "A hit record names an incompatible hit-group export.");
                    }
                    export = found!;
                }
                else
                {
                    if (record.HitGroupName is not null)
                    {
                        Reject("RayTracing", "A shader record names an incompatible Pipeline export.");
                    }
                    if (!pipeline.Entries.TryGetValue(
                            record.EntryPoint,
                            out RayExportValidationState? found) ||
                        found.Type != expectedType)
                    {
                        Reject("RayTracing", "A shader record names an incompatible Pipeline export.");
                    }
                    export = found!;
                }
                if (record.Layout != export.Layout)
                {
                    Reject(
                        "RayTracing",
                        "A shader record must use the exact local parameter layout of its export.");
                }
                if (record.ResourceOffset > (uint)allResources.Length ||
                    record.ResourceCount > (uint)allResources.Length - record.ResourceOffset ||
                    record.OrdinaryDataOffset > (uint)allOrdinaryData.Length ||
                    record.OrdinaryDataSize >
                        (uint)allOrdinaryData.Length - record.OrdinaryDataOffset)
                {
                    Reject("RayTracing", "A shader-record payload slice is outside its update packet.");
                }
                ReadOnlySpan<ResourceBinding> resources = allResources.Slice(
                    checked((int)record.ResourceOffset),
                    checked((int)record.ResourceCount));
                ReadOnlySpan<byte> ordinaryData = allOrdinaryData.Slice(
                    checked((int)record.OrdinaryDataOffset),
                    checked((int)record.OrdinaryDataSize));
                if (export.ParameterLayout is null)
                {
                    if (!resources.IsEmpty || !ordinaryData.IsEmpty)
                    {
                        Reject(
                            "RayTracing",
                            "An export without local parameters requires an empty record payload.");
                    }
                }
                else if (export.ParameterLayout.Diagnose(
                        resources,
                        ordinaryData) is string diagnostic)
                {
                    Reject("RayTracing", diagnostic);
                }
            }
        }
    }

    private void RequireCurrentRayTracingTablePipeline(
        ContextValidationState state,
        CommandContext context,
        RayTracingShaderTable table)
    {
        if (!_rayTracingTables.TryGetValue(table, out _))
            Reject("Ownership", "RayTracingShaderTable was not created through this Validation Layer.");
        lock (state)
        {
            if (!ReferenceEquals(state.Pipeline, table.Description.Pipeline))
            {
                Reject(
                    "RayTracing",
                    "The shader table's ray-tracing Pipeline must be the selected Pipeline.",
                    context.Label);
            }
        }
    }

    private void ValidateAccelerationStructureBuild(
        RayTracing capability,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
        const AccelerationStructureBuildOptions supported =
            AccelerationStructureBuildOptions.AllowUpdate |
            AccelerationStructureBuildOptions.AllowCompaction |
            AccelerationStructureBuildOptions.PreferFastTrace |
            AccelerationStructureBuildOptions.PreferFastBuild |
            AccelerationStructureBuildOptions.MinimizeMemory |
            AccelerationStructureBuildOptions.PerformUpdate;
        if (!Enum.IsDefined(type) || (options & ~supported) != 0)
            Reject("RayTracing", "The acceleration-structure build type or options are invalid.");
        if ((options & AccelerationStructureBuildOptions.AllowUpdate) != 0 &&
            !capability.AccelerationStructureUpdate)
        {
            Reject("RayTracing", "Acceleration-structure updates are unavailable.");
        }
        if ((options & AccelerationStructureBuildOptions.PerformUpdate) != 0 &&
            (!capability.AccelerationStructureUpdate ||
             (options & AccelerationStructureBuildOptions.AllowUpdate) == 0))
        {
            Reject("RayTracing", "PerformUpdate requires supported AllowUpdate content.");
        }
        if ((options & AccelerationStructureBuildOptions.AllowCompaction) != 0 &&
            !capability.Compaction)
        {
            Reject("RayTracing", "Acceleration-structure compaction is unavailable.");
        }
        if ((options & AccelerationStructureBuildOptions.PreferFastTrace) != 0 &&
            (options & AccelerationStructureBuildOptions.PreferFastBuild) != 0)
        {
            Reject("RayTracing", "PreferFastTrace and PreferFastBuild are mutually exclusive.");
        }
        if (geometries.IsEmpty)
            Reject("RayTracing", "An acceleration-structure build requires geometry.");

        if (type == AccelerationStructureType.TopLevel)
        {
            if (geometries.Length != 1 ||
                geometries[0].Type != AccelerationStructureGeometryType.Instances ||
                geometries[0].Count == 0 ||
                geometries[0].Count > capability.MaximumInstancesPerTopLevel)
            {
                Reject(
                    "RayTracing",
                    "A top-level build requires one legal Instances geometry within the Device limit.");
            }
            return;
        }

        if ((uint)geometries.Length > capability.MaximumGeometriesPerBottomLevel)
            Reject("RayTracing", "The bottom-level build exceeds the Device geometry limit.");
        ulong primitiveCount = 0;
        const AccelerationStructureGeometryOptions supportedGeometryOptions =
            AccelerationStructureGeometryOptions.Opaque |
            AccelerationStructureGeometryOptions.NoDuplicateAnyHitInvocation;
        foreach (ref readonly AccelerationStructureGeometry geometry in geometries)
        {
            if (geometry.Count == 0 ||
                geometry.Type == AccelerationStructureGeometryType.Instances ||
                !Enum.IsDefined(geometry.Type) ||
                (geometry.Options & ~supportedGeometryOptions) != 0)
            {
                Reject("RayTracing", "A bottom-level geometry description is incompatible.");
            }
            if (geometry.Type == AccelerationStructureGeometryType.Triangles)
            {
                if (!TryGetRayTracingVertexLayout(
                        geometry.PrimaryFormat,
                        capability.Tier,
                        out uint positionSize,
                        out uint alignment) ||
                    geometry.PrimaryStride < positionSize ||
                    geometry.PrimaryStride % alignment != 0)
                {
                    Reject("RayTracing", "A triangle vertex format or stride is incompatible.");
                }
                if (geometry.Secondary.Buffer is null)
                {
                    primitiveCount = checked(primitiveCount + geometry.Count / 3u);
                }
                else
                {
                    uint indexSize = geometry.IndexType switch
                    {
                        IndexType.UInt16 => 2,
                        IndexType.UInt32 => 4,
                        _ => 0,
                    };
                    if (indexSize == 0)
                        Reject("RayTracing", "The triangle index type is invalid.");
                    BufferRange indices = geometry.Secondary.Range.Resolve(
                        geometry.Secondary.Buffer.Info.Size);
                    if (indices.Size % indexSize != 0 || indices.Size / indexSize > uint.MaxValue)
                        Reject("RayTracing", "The triangle index range is invalid.");
                    primitiveCount = checked(primitiveCount + indices.Size / indexSize / 3u);
                }
            }
            else
            {
                if (geometry.PrimaryStride < 24 || geometry.PrimaryStride % 8 != 0)
                    Reject("RayTracing", "An AABB stride must be at least 24 bytes and 8-byte aligned.");
                primitiveCount = checked(primitiveCount + geometry.Count);
            }
            if (primitiveCount > capability.MaximumPrimitivesPerBottomLevel)
                Reject("RayTracing", "The bottom-level build exceeds the Device primitive limit.");
        }
    }

    private static bool TryGetRayTracingVertexLayout(
        Format format,
        RayTracingTier tier,
        out uint positionSize,
        out uint alignment)
    {
        (positionSize, alignment) = format switch
        {
            Format.R32G32Float => (8u, 4u),
            Format.R32G32B32Float => (12u, 4u),
            Format.R16G16Float or Format.R16G16SNorm => (4u, 2u),
            Format.R16G16B16A16Float or Format.R16G16B16A16SNorm => (6u, 2u),
            Format.R16G16UNorm when tier >= RayTracingTier.Tier1_1 => (4u, 2u),
            Format.R16G16B16A16UNorm when tier >= RayTracingTier.Tier1_1 => (6u, 2u),
            Format.R10G10B10A2UNorm when tier >= RayTracingTier.Tier1_1 => (4u, 4u),
            Format.R8G8UNorm or Format.R8G8SNorm when tier >= RayTracingTier.Tier1_1 => (2u, 1u),
            Format.R8G8B8A8UNorm or Format.R8G8B8A8SNorm
                when tier >= RayTracingTier.Tier1_1 => (3u, 1u),
            _ => (0u, 0u),
        };
        return alignment != 0;
    }

    private static bool RangesOverlap(
        Buffer leftBuffer,
        in BufferRange left,
        Buffer rightBuffer,
        in BufferRange right) =>
        ReferenceEquals(leftBuffer, rightBuffer) &&
        left.Offset < right.Offset + right.Size &&
        right.Offset < left.Offset + left.Size;

    private static IndirectArgumentTypes ToIndirectArgumentSupport(
        IndirectArgumentType value) => value switch
    {
        IndirectArgumentType.Draw => IndirectArgumentTypes.Draw,
        IndirectArgumentType.DrawIndexed => IndirectArgumentTypes.DrawIndexed,
        IndirectArgumentType.Dispatch => IndirectArgumentTypes.Dispatch,
        IndirectArgumentType.DispatchMesh => IndirectArgumentTypes.DispatchMesh,
        IndirectArgumentType.DispatchRays => IndirectArgumentTypes.DispatchRays,
        IndirectArgumentType.WorkGraph => IndirectArgumentTypes.WorkGraph,
        IndirectArgumentType.VertexBuffer => IndirectArgumentTypes.VertexBuffer,
        IndirectArgumentType.IndexBuffer => IndirectArgumentTypes.IndexBuffer,
        IndirectArgumentType.Constants => IndirectArgumentTypes.Constants,
        IndirectArgumentType.ConstantBuffer => IndirectArgumentTypes.ConstantBuffer,
        IndirectArgumentType.ShaderResource => IndirectArgumentTypes.ShaderResource,
        IndirectArgumentType.UnorderedAccess => IndirectArgumentTypes.UnorderedAccess,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static bool IsPowerOfTwo(uint value) =>
        value != 0 && (value & (value - 1)) == 0;

    private TCapability RequireCapability<TCapability>(Device device)
        where TCapability : DeviceCapability
    {
        RequireDevice(device);
        if (!Backend.TryGetCapability(device, out TCapability? capability) || capability is null)
            throw new NotSupportedException($"{typeof(TCapability).Name} is unavailable on this Device.");
        return capability;
    }
}
