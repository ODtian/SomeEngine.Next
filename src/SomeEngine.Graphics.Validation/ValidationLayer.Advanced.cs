using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    public Buffer CreateReservedBuffer(Device device, in BufferDesc desc)
    {
        RequireCapability<SparseResources>(device);
        var state = new ResourceValidationState(buffer: true);
        BufferDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Buffer? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateReservedBuffer(device, createDesc);
                state.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, state);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Texture CreateReservedTexture(Device device, in TextureDesc desc)
    {
        RequireCapability<SparseResources>(device);
        var state = new ResourceValidationState(buffer: false);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            Texture? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateReservedTexture(device, desc);
                state.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, state);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
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
        var state = new ResourceValidationState(buffer: false);
        SamplerFeedbackTextureDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.SampledTexture);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            SamplerFeedbackTexture? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateSamplerFeedbackTexture(device, createDesc);
                state.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, state);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
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
        TextureUavDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(texture);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            SamplerFeedbackUav? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateSamplerFeedbackUav(device, texture, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public void ClearSamplerFeedback(
        CommandContext context,
        SamplerFeedbackUav feedback)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RequireCapability<SamplerFeedback>(context.Device);
        RequireOnDevice(context.Device, feedback, "Sampler feedback UAV");
        PrepareCommandDependency(state, feedback);
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
        PrepareCommandDependencies(state, feedback, destination);
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
        PrepareCommandDependencies(state, feedback, destination);
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
        var state = new ResourceValidationState(buffer: true);
        BufferRange createRange = storageRange;
        var objectInfo = new ValidationObjectInfo(storage);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            AccelerationStructure? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateAccelerationStructure(
                    device,
                    storage,
                    createRange,
                    type,
                    label);
                state.Bind(result);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _resourceStates.Add(result, state);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _resourceStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public AccelerationStructureSrv CreateAccelerationStructureSrv(
        Device device,
        in AccelerationStructureSrvDesc desc)
    {
        RequireCapability<RayTracing>(device);
        RequireOnDevice(device, desc.AccelerationStructure, "AccelerationStructure");
        AccelerationStructureSrvDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.AccelerationStructure);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            AccelerationStructureSrv? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateAccelerationStructureSrv(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
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
        PipelineBindingValidationState pipelineBindings = ReflectPipelineBindings(
            desc.Program,
            ReadOnlySpan<SlangShaderSharp.EntryPointReflection>.Empty);
        pipelineBindings.AddStaticSamplers(desc.StaticSamplers);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _pipelineBindingStates.EnsureAdditionalCapacity();
            _rayTracingPipelines.EnsureAdditionalCapacity();
            Pipeline? result = null;
            bool objectAdded = false;
            bool bindingsAdded = false;
            bool validationAdded = false;
            try
            {
                result = Backend.CreateRayTracingPipeline(device, desc, cache);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _pipelineBindingStates.Add(result, pipelineBindings);
                bindingsAdded = true;
                _rayTracingPipelines.Add(result, validation);
                validationAdded = true;
                return result;
            }
            catch
            {
                if (validationAdded)
                    _rayTracingPipelines.Remove(result!);
                if (bindingsAdded)
                    _pipelineBindingStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Task<Pipeline> CreateRayTracingPipelineAsync(
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
        PipelineBindingValidationState bindings = ReflectPipelineBindings(
            desc.Program,
            ReadOnlySpan<SlangShaderSharp.EntryPointReflection>.Empty);
        bindings.AddStaticSamplers(desc.StaticSamplers);
        var objectInfo = new ValidationObjectInfo(device);
        Task<Pipeline> creation = Backend.CreateRayTracingPipelineAsync(device, desc, cache);
        return RegisterRayTracingPipelineAsync(
            creation,
            objectInfo,
            bindings,
            validation);
    }

    private async Task<Pipeline> RegisterRayTracingPipelineAsync(
        Task<Pipeline> creation,
        ValidationObjectInfo objectInfo,
        PipelineBindingValidationState bindings,
        RayTracingPipelineValidationState validation)
    {
        Pipeline result = await creation.ConfigureAwait(false);
        bool objectAdded = false;
        bool bindingsAdded = false;
        try
        {
            lock (_gate)
            {
                _ = Backend;
                _objects.EnsureAdditionalCapacity();
                _pipelineBindingStates.EnsureAdditionalCapacity();
                _rayTracingPipelines.EnsureAdditionalCapacity();
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _pipelineBindingStates.Add(result, bindings);
                bindingsAdded = true;
                _rayTracingPipelines.Add(result, validation);
            }
            return result;
        }
        catch
        {
            lock (_gate)
            {
                _rayTracingPipelines.Remove(result);
                if (bindingsAdded)
                    _pipelineBindingStates.Remove(result);
                if (objectAdded)
                    _objects.Remove(result);
            }
            result.Dispose();
            throw;
        }
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
        var tableState = new RayTracingTableValidationState(validatedPipeline);
        RayTracingShaderTableDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(desc.Pipeline);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _rayTracingTables.EnsureAdditionalCapacity();
            RayTracingShaderTable? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateRayTracingShaderTable(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _rayTracingTables.Add(result, tableState);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _rayTracingTables.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
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
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            PrepareCommandDependencyCore(state, desc.Destination, ref capacity);
            PrepareCommandDependencyCore(state, desc.Scratch, ref capacity);
            if (desc.Source is not null)
                PrepareCommandDependencyCore(state, desc.Source, ref capacity);
            foreach (AccelerationStructureGeometry geometry in desc.Geometries)
            {
                PrepareCommandDependencyCore(state, geometry.Primary.Buffer, ref capacity);
                if (geometry.Secondary.Buffer is not null)
                    PrepareCommandDependencyCore(state, geometry.Secondary.Buffer, ref capacity);
                if (geometry.Transform.Buffer is not null)
                    PrepareCommandDependencyCore(state, geometry.Transform.Buffer, ref capacity);
            }
            ReserveCommandMutation(state, capacity);
        }
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
        PrepareCommandDependencies(state, destination, source);
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
        PrepareCommandDependencies(state, destination.Buffer, source);
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
        PrepareCommandDependencies(state, destination, source.Buffer);
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
        PrepareCommandDependencies(state, source, destination);
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
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            PrepareCommandDependencyCore(state, table, ref capacity);
            foreach (ref readonly ResourceBinding binding in update.Resources)
            {
                if (binding.Value is GraphicsObject dependency)
                    PrepareCommandDependencyCore(state, dependency, ref capacity);
            }
            ReserveCommandMutation(state, capacity);
        }
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
        PrepareCommandDependency(state, desc.ShaderTable);
        Backend.DispatchRays(context, desc);
        RecordCommandDependency(state, desc.ShaderTable);
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
        PrepareCommandDependency(state, arguments.Buffer);
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
        if (texture is not null)
            PrepareCommandDependency(state, texture);
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
        if (desc.NodeMask == 0)
            Reject("WorkGraphs", "A Work Graph Pipeline requires a non-zero node mask.");
        PipelineBindingValidationState pipelineBindings = ReflectPipelineBindings(
            desc.Program,
            ReadOnlySpan<SlangShaderSharp.EntryPointReflection>.Empty,
            useConservativeWorkGraphEntries: true);
        pipelineBindings.AddStaticSamplers(desc.StaticSamplers);
        HashSet<EntryPointReflection> reflectedEntries = CollectReflectedWorkGraphEntries(
            pipelineBindings.Reflection,
            capability.MaximumNodeCount);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _pipelineBindingStates.EnsureAdditionalCapacity();
            _workGraphPipelines.EnsureAdditionalCapacity();
            Pipeline? result = null;
            bool objectAdded = false;
            bool bindingsAdded = false;
            bool graphAdded = false;
            try
            {
                result = Backend.CreateWorkGraphPipeline(device, desc, cache);
                WorkGraphEntryPointInfo[] materializedEntries =
                    ReadMaterializedWorkGraphEntries(result);
                ValidateMaterializedWorkGraphEntries(
                    capability,
                    reflectedEntries,
                    materializedEntries);
                var graphState = new WorkGraphPipelineValidationState(materializedEntries);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _pipelineBindingStates.Add(result, pipelineBindings);
                bindingsAdded = true;
                _workGraphPipelines.Add(result, graphState);
                graphAdded = true;
                return result;
            }
            catch
            {
                if (graphAdded)
                    _workGraphPipelines.Remove(result!);
                if (bindingsAdded)
                    _pipelineBindingStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Task<Pipeline> CreateWorkGraphPipelineAsync(
        Device device,
        in WorkGraphPipelineDesc desc,
        PipelineCache? cache = null)
    {
        WorkGraphs capability = RequireCapability<WorkGraphs>(device);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        if (desc.NodeMask == 0)
            Reject("WorkGraphs", "A Work Graph Pipeline requires a non-zero node mask.");
        PipelineBindingValidationState bindings = ReflectPipelineBindings(
            desc.Program,
            ReadOnlySpan<SlangShaderSharp.EntryPointReflection>.Empty,
            useConservativeWorkGraphEntries: true);
        bindings.AddStaticSamplers(desc.StaticSamplers);
        HashSet<EntryPointReflection> reflectedEntries = CollectReflectedWorkGraphEntries(
            bindings.Reflection,
            capability.MaximumNodeCount);
        var objectInfo = new ValidationObjectInfo(device);
        Task<Pipeline> creation = Backend.CreateWorkGraphPipelineAsync(device, desc, cache);
        return RegisterWorkGraphPipelineAsync(
            creation,
            capability,
            reflectedEntries,
            objectInfo,
            bindings);
    }

    private async Task<Pipeline> RegisterWorkGraphPipelineAsync(
        Task<Pipeline> creation,
        WorkGraphs capability,
        HashSet<EntryPointReflection> reflectedEntries,
        ValidationObjectInfo objectInfo,
        PipelineBindingValidationState bindings)
    {
        Pipeline result = await creation.ConfigureAwait(false);
        bool objectAdded = false;
        bool bindingsAdded = false;
        try
        {
            WorkGraphEntryPointInfo[] materializedEntries =
                ReadMaterializedWorkGraphEntries(result);
            ValidateMaterializedWorkGraphEntries(
                capability,
                reflectedEntries,
                materializedEntries);
            var graphState = new WorkGraphPipelineValidationState(materializedEntries);
            lock (_gate)
            {
                _ = Backend;
                _objects.EnsureAdditionalCapacity();
                _pipelineBindingStates.EnsureAdditionalCapacity();
                _workGraphPipelines.EnsureAdditionalCapacity();
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _pipelineBindingStates.Add(result, bindings);
                bindingsAdded = true;
                _workGraphPipelines.Add(result, graphState);
            }
            return result;
        }
        catch
        {
            lock (_gate)
            {
                _workGraphPipelines.Remove(result);
                if (bindingsAdded)
                    _pipelineBindingStates.Remove(result);
                if (objectAdded)
                    _objects.Remove(result);
            }
            result.Dispose();
            throw;
        }
    }

    private HashSet<EntryPointReflection> CollectReflectedWorkGraphEntries(
        ShaderReflection reflection,
        uint maximumNodeCount)
    {
        var reflectedEntries = new HashSet<EntryPointReflection>();
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            EntryPointReflection entry = reflection.GetEntryPointByIndex(index);
            if (entry.Stage is not (SlangStage.Dispatch or SlangStage.Node))
                continue;
            if (!reflectedEntries.Add(entry))
                Reject("WorkGraphs", "Slang exposes a duplicate Work Graph entry point.");
        }
        if (reflectedEntries.Count == 0 || (uint)reflectedEntries.Count > maximumNodeCount)
        {
            Reject(
                "WorkGraphs",
                "The linked Slang program exposes no valid Work Graph nodes or exceeds the Device limit.");
        }
        return reflectedEntries;
    }

    private WorkGraphEntryPointInfo[] ReadMaterializedWorkGraphEntries(Pipeline pipeline)
    {
        _ = Backend.TryGetWorkGraphEntryPoints(pipeline, [], out int materializedEntryCount);
        var materializedEntries = new WorkGraphEntryPointInfo[materializedEntryCount];
        if (!Backend.TryGetWorkGraphEntryPoints(
                pipeline,
                materializedEntries,
                out int confirmedEntryCount) ||
            confirmedEntryCount != materializedEntryCount)
        {
            Reject(
                "WorkGraphs",
                "The materialized Work Graph entry-point count changed during creation.");
        }
        return materializedEntries;
    }

    private void ValidateMaterializedWorkGraphEntries(
        WorkGraphs capability,
        HashSet<EntryPointReflection> reflectedEntries,
        ReadOnlySpan<WorkGraphEntryPointInfo> materializedEntries)
    {
        if (materializedEntries.IsEmpty ||
            (uint)materializedEntries.Length > capability.MaximumNodeCount)
        {
            Reject("WorkGraphs", "The backend returned an invalid Work Graph entry-point count.");
        }
        var materializedIdentities = new HashSet<EntryPointReflection>();
        foreach (ref readonly WorkGraphEntryPointInfo entry in materializedEntries)
        {
            if (!WorkGraphValidation.IsEntryPointLayoutValid(
                    capability.MaximumInputRecordSize,
                    entry.RecordSize,
                    entry.RecordAlignment))
            {
                Reject(
                    "WorkGraphs",
                    "The backend returned an invalid Work Graph entry-point layout.");
            }
            if (!reflectedEntries.Contains(entry.EntryPoint) ||
                !materializedIdentities.Add(entry.EntryPoint))
            {
                Reject(
                    "WorkGraphs",
                    "The backend returned a Work Graph entry identity not authored by Slang.");
            }
        }
    }

    internal static string GetEffectiveStateObjectEntryPointName(
        SlangShaderSharp.EntryPointReflection entryPoint) =>
        WorkGraphValidation.GetEffectiveEntryPointName(entryPoint);

    internal static string GetEffectiveStateObjectEntryPointName(
        string name,
        string? nameOverride) =>
        WorkGraphValidation.GetEffectiveEntryPointName(name, nameOverride);

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

    public bool TryGetWorkGraphEntryPoints(
        Pipeline pipeline,
        Span<WorkGraphEntryPointInfo> destination,
        out int requiredCount)
    {
        Require(pipeline);
        if (pipeline.Type != PipelineType.WorkGraph ||
            !_workGraphPipelines.TryGetValue(pipeline, out _))
        {
            requiredCount = 0;
            Reject(
                "WorkGraphs",
                "The Pipeline is not a Work Graph Pipeline.",
                pipeline.Label);
            return false;
        }
        return Backend.TryGetWorkGraphEntryPoints(pipeline, destination, out requiredCount);
    }

    public void BindWorkGraph(
        CommandContext context,
        Pipeline pipeline,
        in BufferRegion? backingMemory,
        WorkGraphInitialization initialization)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RequireOnDevice(context.Device, pipeline, "Work-graph Pipeline");
        if (pipeline.Type != PipelineType.WorkGraph)
            Reject("Commands", "BindWorkGraph requires a WorkGraph Pipeline.", pipeline.Label);
        if (!_workGraphPipelines.TryGetValue(
                pipeline,
                out WorkGraphPipelineValidationState? pipelineState))
        {
            Reject("Ownership", "Work Graph Pipeline was not created through this Validation Layer.");
        }
        if (!Enum.IsDefined(initialization))
            Reject("WorkGraphs", "WorkGraphInitialization is invalid.");
        _ = RequireCapability<WorkGraphs>(context.Device);
        bool backingIsUsed = false;
        if (backingMemory is BufferRegion suppliedBacking)
        {
            RequireOnDevice(context.Device, suppliedBacking.Buffer, "Work-graph backing Buffer");
            WorkGraphMemoryRequirements requirements =
                Backend.GetWorkGraphMemoryRequirements(pipeline);
            BufferRange suppliedRange = suppliedBacking.Range.Resolve(suppliedBacking.Buffer.Info.Size);
            backingIsUsed = requirements.NormalizeBackingSize(suppliedRange.Size) != 0;
        }
        if (backingIsUsed && backingMemory is BufferRegion preparedBacking)
            PrepareCommandDependencies(state, pipeline, preparedBacking.Buffer);
        else
            PrepareCommandDependency(state, pipeline);
        Backend.BindWorkGraph(
            context,
            pipeline,
            backingMemory,
            initialization);
        RecordCommandDependency(state, pipeline);
        if (backingIsUsed && backingMemory is BufferRegion retainedBacking)
            RecordCommandDependency(state, retainedBacking.Buffer);
        lock (state)
        {
            state.Pipeline = pipeline;
            state.PipelineType = PipelineType.WorkGraph;
            state.WorkGraphBound = true;
        }
    }

    public void DispatchWorkGraph(CommandContext context, in WorkGraphDispatchDesc desc)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        WorkGraphs capability = RequireCapability<WorkGraphs>(context.Device);
        RequirePipeline(state, context, PipelineType.WorkGraph, "DispatchWorkGraph");
        lock (state)
        {
            if (!state.WorkGraphBound)
                Reject("Commands", "DispatchWorkGraph requires BindWorkGraph.", context.Label);
        }
        Pipeline selected;
        lock (state)
            selected = state.Pipeline!;
        if (!_workGraphPipelines.TryGetValue(
                selected,
                out WorkGraphPipelineValidationState? pipelineState))
        {
            Reject("Ownership", "The current Work Graph Pipeline has no validation state.", context.Label);
        }

        switch (desc.Mode)
        {
            case WorkGraphDispatchInputMode.NodeCpu:
                if (!capability.CpuInput)
                    Reject("WorkGraphs", "CPU Work Graph input is unavailable on this Device.");
                ValidateCpuNodeInput(
                    pipelineState!,
                    desc.EntryPoint,
                    desc.RecordCount,
                    desc.RecordStride,
                    0,
                    desc.Records.Length,
                    "Work-graph CPU input");
                break;
            case WorkGraphDispatchInputMode.NodeGpu:
                if (!capability.GpuInput)
                    Reject("WorkGraphs", "GPU Work Graph input is unavailable on this Device.");
                ValidateGpuNodeInput(
                    context.Device,
                    pipelineState!,
                    desc.EntryPoint,
                    desc.GpuRecords,
                    desc.RecordCount,
                    desc.RecordStride,
                    "Work-graph GPU input");
                PrepareCommandDependency(state, desc.GpuRecords.Buffer);
                break;
            case WorkGraphDispatchInputMode.MultiNodeCpu:
                if (!capability.CpuInput)
                    Reject("WorkGraphs", "CPU Work Graph input is unavailable on this Device.");
                ValidateMultiNodeCpu(
                    pipelineState!,
                    desc.CpuNodeInputs,
                    desc.Records.Length,
                    capability.MaximumNodeCount);
                break;
            case WorkGraphDispatchInputMode.MultiNodeGpu:
                if (!capability.GpuInput)
                    Reject("WorkGraphs", "GPU Work Graph input is unavailable on this Device.");
                ValidateAndPrepareMultiNodeGpu(
                    state,
                    context.Device,
                    pipelineState!,
                    desc.GpuNodeInputs,
                    capability.MaximumNodeCount);
                break;
            default:
                Reject("WorkGraphs", "WorkGraphDispatchInputMode is invalid.");
                break;
        }

        Backend.DispatchWorkGraph(context, desc);
        if (desc.Mode == WorkGraphDispatchInputMode.NodeGpu)
        {
            RecordCommandDependency(state, desc.GpuRecords.Buffer);
        }
        else if (desc.Mode == WorkGraphDispatchInputMode.MultiNodeGpu)
        {
            lock (state)
            {
                foreach (ref readonly WorkGraphGpuNodeInput input in desc.GpuNodeInputs)
                    RecordCommandDependencyCore(state, input.Records.Buffer);
            }
        }
    }

    private void ValidateMultiNodeCpu(
            WorkGraphPipelineValidationState graph,
            ReadOnlySpan<WorkGraphCpuNodeInput> inputs,
            int packetLength,
            uint maximumNodeCount)
        {
            if (inputs.IsEmpty || (uint)inputs.Length > maximumNodeCount)
                Reject("WorkGraphs", "A multi-node CPU dispatch has an invalid node-input count.");
            for (int index = 0; index < inputs.Length; index++)
            {
                ref readonly WorkGraphCpuNodeInput input = ref inputs[index];
                RequireUniqueCpuEntry(inputs, index, input.EntryPoint);
                ValidateCpuNodeInput(
                    graph,
                    input.EntryPoint,
                    input.RecordCount,
                    input.RecordStride,
                    input.RecordOffset,
                    packetLength,
                    "Multi-node CPU Work Graph input");
            }
        }

    private void ValidateAndPrepareMultiNodeGpu(
            ContextValidationState validationState,
            Device device,
            WorkGraphPipelineValidationState graph,
            ReadOnlySpan<WorkGraphGpuNodeInput> inputs,
            uint maximumNodeCount)
        {
            if (inputs.IsEmpty || (uint)inputs.Length > maximumNodeCount)
                Reject("WorkGraphs", "A multi-node GPU dispatch has an invalid node-input count.");
            for (int index = 0; index < inputs.Length; index++)
            {
                ref readonly WorkGraphGpuNodeInput input = ref inputs[index];
                RequireUniqueGpuEntry(inputs, index, input.EntryPoint);
                ValidateGpuNodeInput(
                    device,
                    graph,
                    input.EntryPoint,
                    input.Records,
                    input.RecordCount,
                    input.RecordStride,
                    "Multi-node GPU Work Graph input");
            }
            lock (validationState)
            {
                CommandMutationCapacity capacity = default;
                foreach (ref readonly WorkGraphGpuNodeInput input in inputs)
                    PrepareCommandDependencyCore(validationState, input.Records.Buffer, ref capacity);
                ReserveCommandMutation(validationState, capacity);
            }
        }

    private void ValidateCpuNodeInput(
            WorkGraphPipelineValidationState graph,
            EntryPointReflection entryPoint,
            uint recordCount,
            uint recordStride,
            uint recordOffset,
            int packetLength,
            string role)
        {
            WorkGraphEntryPointInfo entry = RequireWorkGraphEntry(graph, entryPoint, role);
            ValidateWorkGraphRecordLayout(entry, recordCount, recordStride, out ulong requiredBytes);
            if (recordOffset > (uint)packetLength ||
                requiredBytes > (ulong)packetLength - recordOffset)
            {
                Reject("WorkGraphs", $"{role} is outside its CPU record packet.");
            }
        }

    private void ValidateGpuNodeInput(
            Device device,
            WorkGraphPipelineValidationState graph,
            EntryPointReflection entryPoint,
            in BufferRegion records,
            uint recordCount,
            uint recordStride,
            string role)
        {
            WorkGraphEntryPointInfo entry = RequireWorkGraphEntry(graph, entryPoint, role);
            ValidateWorkGraphRecordLayout(entry, recordCount, recordStride, out ulong requiredBytes);
            RequireOnDevice(device, records.Buffer, role + " Buffer");
            BufferRange range = records.Range.Resolve(records.Buffer.Info.Size);
            if ((records.Buffer.Info.Usages & BufferUsages.ShaderRead) == 0 ||
                range.Size < requiredBytes)
            {
                Reject("WorkGraphs", $"{role} Buffer range is incompatible.");
            }
        }

    private WorkGraphEntryPointInfo RequireWorkGraphEntry(
            WorkGraphPipelineValidationState graph,
            EntryPointReflection entryPoint,
            string role)
        {
            try
            {
                return graph.GetEntryPoint(entryPoint);
            }
            catch (ArgumentException exception)
            {
                Reject("WorkGraphs", $"{role}: {exception.Message}");
                throw;
            }
        }

    private void ValidateWorkGraphRecordLayout(
            in WorkGraphEntryPointInfo entry,
            uint recordCount,
            uint recordStride,
            out ulong requiredBytes)
        {
            if (recordCount == 0)
            {
                Reject("WorkGraphs", "A Work Graph dispatch requires at least one record.");
            }
            if (entry.RecordSize == 0)
            {
                if (recordStride != 0)
                    Reject("WorkGraphs", "An empty Work Graph record requires zero stride.");
                requiredBytes = 0;
                return;
            }
            if (recordStride != 0 &&
                (recordStride < entry.RecordSize ||
                 recordStride % entry.RecordAlignment != 0 ||
                 recordStride % 4 != 0))
            {
                Reject("WorkGraphs", "A Work Graph record stride is incompatible with Slang/native layout.");
            }
            ulong effectiveStride = recordStride == 0 ? entry.RecordSize : recordStride;
            requiredBytes = checked(
                checked((ulong)(recordCount - 1) * effectiveStride) + entry.RecordSize);
        }

    private void RequireUniqueCpuEntry(
            ReadOnlySpan<WorkGraphCpuNodeInput> inputs,
            int index,
            EntryPointReflection entryPoint)
        {
            if (entryPoint == EntryPointReflection.Null)
                Reject("WorkGraphs", "A Work Graph node input requires a Slang entry point.");
            for (int prior = 0; prior < index; prior++)
            {
                if (inputs[prior].EntryPoint == entryPoint)
                    Reject("WorkGraphs", "A multi-node Work Graph dispatch repeats an entry point.");
            }
        }

    private void RequireUniqueGpuEntry(
            ReadOnlySpan<WorkGraphGpuNodeInput> inputs,
            int index,
            EntryPointReflection entryPoint)
        {
            if (entryPoint == EntryPointReflection.Null)
                Reject("WorkGraphs", "A Work Graph node input requires a Slang entry point.");
            for (int prior = 0; prior < index; prior++)
            {
                if (inputs[prior].EntryPoint == entryPoint)
                    Reject("WorkGraphs", "A multi-node Work Graph dispatch repeats an entry point.");
            }
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
            if (!capability.Supports(argument.Type))
            {
                Reject(
                    "IndirectCommands",
                    $"Indirect argument {argument.Type} is unavailable on this Device.");
            }
        }
        PipelineType actionPipelineType = ClassifyIndirectAction(desc.Arguments);
        var state = new IndirectLayoutValidationState(actionPipelineType, desc.Pipeline);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _indirectLayouts.EnsureAdditionalCapacity();
            IndirectCommandLayout? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateIndirectCommandLayout(device, desc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _indirectLayouts.Add(result, state);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _indirectLayouts.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
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
            if (layoutState.Pipeline is not null &&
                !ReferenceEquals(state.Pipeline, layoutState.Pipeline))
            {
                Reject(
                    "Commands",
                    "ExecuteIndirect requires the exact Pipeline captured by its layout.",
                    context.Label);
            }
            if (layoutState.ActionPipelineType == PipelineType.WorkGraph &&
                !state.WorkGraphBound)
            {
                Reject(
                    "Commands",
                    "Indirect Work Graph dispatch requires BindWorkGraph.",
                    context.Label);
            }
        }
        RequireOnDevice(context.Device, arguments.Buffer, "Indirect arguments");
        if (count is { } countRegion)
            RequireOnDevice(context.Device, countRegion.Buffer, "Indirect count Buffer");
        if (count is { } preparedCount)
        {
            lock (state)
            {
                CommandMutationCapacity capacity = default;
                PrepareCommandDependencyCore(state, layout, ref capacity);
                PrepareCommandDependencyCore(state, arguments.Buffer, ref capacity);
                PrepareCommandDependencyCore(state, preparedCount.Buffer, ref capacity);
                ReserveCommandMutation(state, capacity);
            }
        }
        else
        {
            lock (state)
            {
                CommandMutationCapacity capacity = default;
                PrepareCommandDependencyCore(state, layout, ref capacity);
                PrepareCommandDependencyCore(state, arguments.Buffer, ref capacity);
                ReserveCommandMutation(state, capacity);
            }
        }
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
            var layouts = new HashSet<SlangShaderSharp.VariableLayoutReflection>();
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
                CreateRayExportValidationState(
                    RayExportValidationType.Hit,
                    [.. layouts]));

            void AddHitMember(
                SlangShaderSharp.EntryPointReflection entry,
                SlangShaderSharp.SlangStage expectedStage)
            {
                if (entry == SlangShaderSharp.EntryPointReflection.Null)
                    return;
                ValidateEntry(entry, expectedStage);
                if (!hasMember)
                {
                    hasMember = true;
                }
                if (entry.VarLayout != SlangShaderSharp.VariableLayoutReflection.Null)
                    layouts.Add(entry.VarLayout);
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
                    CreateRayExportValidationState(
                        type,
                        entry.VarLayout == SlangShaderSharp.VariableLayoutReflection.Null
                            ? []
                            : [entry.VarLayout]));
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
        SlangShaderSharp.VariableLayoutReflection[] layouts)
    {
        return new RayExportValidationState(type, layouts);
    }

    private void ValidateRayTracingTableUpdate(
        RayTracingShaderTable table,
        RayTracingPipelineValidationState pipeline,
        in RayTracingShaderTableUpdate update)
    {
        RayTracingShaderTableDesc description = table.Description;
        if (!_pipelineBindingStates.TryGetValue(
                description.Pipeline, out PipelineBindingValidationState? pipelineBindings))
            Reject("Ownership", "The shader table Pipeline has no binding validation state.", table.Label);
        ValidateRayTracingRecordCounts(description, update, table.Label);
        ValidateRayTracingRecords(
            pipeline,
            pipelineBindings,
            update.RayGeneration,
            RayExportValidationType.RayGeneration,
            hitRecords: false,
            update);
        ValidateRayTracingRecords(
            pipeline,
            pipelineBindings,
            update.Miss,
            RayExportValidationType.Miss,
            hitRecords: false,
            update);
        ValidateRayTracingRecords(
            pipeline,
            pipelineBindings,
            update.Hit,
            RayExportValidationType.Hit,
            hitRecords: true,
            update);
        ValidateRayTracingRecords(
            pipeline,
            pipelineBindings,
            update.Callable,
            RayExportValidationType.Callable,
            hitRecords: false,
            update);
    }

    private void ValidateRayTracingRecordCounts(
        in RayTracingShaderTableDesc description,
        in RayTracingShaderTableUpdate update,
        string? tableLabel)
    {
        if ((uint)update.RayGeneration.Length != description.RayGenerationRecordCount ||
            (uint)update.Miss.Length != description.MissRecordCount ||
            (uint)update.Hit.Length != description.HitRecordCount ||
            (uint)update.Callable.Length != description.CallableRecordCount)
        {
            Reject(
                "RayTracing",
                "Every shader-table update category must match its declared record count.",
                tableLabel);
        }
    }

    private void ValidateRayTracingRecords(
        RayTracingPipelineValidationState pipeline,
        PipelineBindingValidationState? pipelineBindings,
        ReadOnlySpan<RayTracingShaderRecord> records,
        RayExportValidationType expectedType,
        bool hitRecords,
        in RayTracingShaderTableUpdate update)
    {
        foreach (ref readonly RayTracingShaderRecord record in records)
        {
            RayExportValidationState export = ResolveRayTracingExport(
                pipeline,
                record,
                expectedType,
                hitRecords);
            ValidateRayTracingParameterBlocks(export, record, update, pipelineBindings);
        }
    }

    private RayExportValidationState ResolveRayTracingExport(
        RayTracingPipelineValidationState pipeline,
        in RayTracingShaderRecord record,
        RayExportValidationType expectedType,
        bool hitRecord)
    {
        if (hitRecord)
        {
            if (record.EntryPoint != SlangShaderSharp.EntryPointReflection.Null ||
                record.HitGroupName is null)
            {
                Reject("RayTracing", "A hit record names an incompatible hit-group export.");
            }
            if (!pipeline.HitGroups.TryGetValue(
                    record.HitGroupName!,
                    out RayExportValidationState? hitExport))
            {
                Reject("RayTracing", "A hit record names an incompatible hit-group export.");
            }
            return hitExport!;
        }

        if (record.HitGroupName is not null)
            Reject("RayTracing", "A shader record names an incompatible Pipeline export.");
        if (!pipeline.Entries.TryGetValue(
                record.EntryPoint,
                out RayExportValidationState? export) ||
            export.Type != expectedType)
        {
            Reject("RayTracing", "A shader record names an incompatible Pipeline export.");
        }
        return export!;
    }

    private void ValidateRayTracingParameterBlocks(
        RayExportValidationState export,
        in RayTracingShaderRecord record,
        in RayTracingShaderTableUpdate update,
        PipelineBindingValidationState? pipelineBindings)
    {
        if (record.ParameterBlockOffset > (uint)update.ParameterBlocks.Length ||
            record.ParameterBlockCount >
                (uint)update.ParameterBlocks.Length - record.ParameterBlockOffset)
        {
            Reject(
                "RayTracing",
                "A shader-record parameter-block slice is outside its update packet.");
        }
        ReadOnlySpan<RayTracingLocalParameterBlock> blocks = update.ParameterBlocks.Slice(
            checked((int)record.ParameterBlockOffset),
            checked((int)record.ParameterBlockCount));
        if (blocks.Length != export.Layouts.Length)
        {
            Reject(
                "RayTracing",
                "A shader record must provide exactly one parameter block for each exact Slang local layout of its export.");
        }
        for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            ref readonly RayTracingLocalParameterBlock block = ref blocks[blockIndex];
            bool expectedLayout = false;
            foreach (SlangShaderSharp.VariableLayoutReflection layout in export.Layouts)
                expectedLayout |= block.Layout == layout;
            if (!expectedLayout)
            {
                Reject(
                    "RayTracing",
                    "A shader-record parameter block is not an exact Slang local layout of its export.");
            }
            for (int prior = 0; prior < blockIndex; prior++)
            {
                if (blocks[prior].Layout == block.Layout)
                {
                    Reject(
                        "RayTracing",
                        "A shader record repeats the same Slang local parameter block.");
                }
            }
            ValidateRayTracingParameterBlockPayload(block, update, pipelineBindings);
        }
    }

    private void ValidateRayTracingParameterBlockPayload(
        in RayTracingLocalParameterBlock block,
        in RayTracingShaderTableUpdate update,
        PipelineBindingValidationState? pipelineBindings)
    {
        if (block.ResourceOffset > (uint)update.Resources.Length ||
            block.ResourceCount > (uint)update.Resources.Length - block.ResourceOffset ||
            block.OrdinaryDataOffset > (uint)update.OrdinaryData.Length ||
            block.OrdinaryDataSize >
                (uint)update.OrdinaryData.Length - block.OrdinaryDataOffset)
        {
            Reject(
                "RayTracing",
                "A shader-record parameter-block payload slice is outside its update packet.");
        }
        ReadOnlySpan<ResourceBinding> resources = update.Resources.Slice(
            checked((int)block.ResourceOffset),
            checked((int)block.ResourceCount));
        ReadOnlySpan<byte> ordinaryData = update.OrdinaryData.Slice(
            checked((int)block.OrdinaryDataOffset),
            checked((int)block.OrdinaryDataSize));
        if (DiagnoseParameterBindings(
                block.Layout,
                resources,
                ordinaryData,
                pipelineBindings) is string diagnostic)
        {
            Reject("RayTracing", diagnostic);
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
        ValidateAccelerationStructureBuildOptions(capability, type, options);
        if (geometries.IsEmpty)
            Reject("RayTracing", "An acceleration-structure build requires geometry.");

        if (type == AccelerationStructureType.TopLevel)
        {
            ValidateTopLevelGeometry(capability, geometries);
            return;
        }
        ValidateBottomLevelGeometries(capability, geometries);
    }

    private void ValidateAccelerationStructureBuildOptions(
        RayTracing capability,
        AccelerationStructureType type,
        AccelerationStructureBuildOptions options)
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
    }

    private void ValidateTopLevelGeometry(
        RayTracing capability,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
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
    }

    private void ValidateBottomLevelGeometries(
        RayTracing capability,
        ReadOnlySpan<AccelerationStructureGeometry> geometries)
    {
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
            ulong geometryPrimitiveCount =
                geometry.Type == AccelerationStructureGeometryType.Triangles
                    ? CountTrianglePrimitives(capability, geometry)
                    : ValidateAabbGeometry(geometry);
            primitiveCount = checked(primitiveCount + geometryPrimitiveCount);
            if (primitiveCount > capability.MaximumPrimitivesPerBottomLevel)
                Reject("RayTracing", "The bottom-level build exceeds the Device primitive limit.");
        }
    }

    private ulong CountTrianglePrimitives(
        RayTracing capability,
        in AccelerationStructureGeometry geometry)
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
            return geometry.Count / 3u;

        uint indexSize = geometry.IndexType switch
        {
            IndexType.UInt16 => 2,
            IndexType.UInt32 => 4,
            _ => 0,
        };
        if (indexSize == 0)
            Reject("RayTracing", "The triangle index type is invalid.");
        BufferRange indices = geometry.Secondary.Range.Resolve(geometry.Secondary.Buffer.Info.Size);
        if (indices.Size % indexSize != 0 || indices.Size / indexSize > uint.MaxValue)
            Reject("RayTracing", "The triangle index range is invalid.");
        return indices.Size / indexSize / 3u;
    }

    private ulong ValidateAabbGeometry(in AccelerationStructureGeometry geometry)
    {
        if (geometry.PrimaryStride < 24 || geometry.PrimaryStride % 8 != 0)
            Reject("RayTracing", "An AABB stride must be at least 24 bytes and 8-byte aligned.");
        return geometry.Count;
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
