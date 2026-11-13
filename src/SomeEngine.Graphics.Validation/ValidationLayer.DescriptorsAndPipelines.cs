using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;
public sealed partial class ValidationLayer
{
    public DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<DescriptorEntryDesc> entries,
        string? label = null,
        uint nodeIndex = uint.MaxValue,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireDevice(device);
        DescriptorEntries.Validate(entries);
        DeviceValidationState deviceState = _deviceStates.GetValue(device, static _ => throw new InvalidOperationException("The Device has no Validation node metadata."));
        uint resolvedNodeIndex = deviceState.ResolveNodeIndex(nodeIndex, nameof(nodeIndex));
        DescriptorEntryDesc[] createSlots = entries.ToArray();
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            DescriptorTable? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateDescriptorTable(device, createSlots, label, resolvedNodeIndex, cancellationToken);
                _objects.Add(result, objectInfo);
                _descriptorWrites.Add(result, createSlots.Select(entry => entry.Type != ResourceBindingType.Sampler).ToArray());
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

    public DescriptorIndex GetDescriptorIndex(DescriptorTable table, uint entry)
    {
        Require(table);
        if (entry >= table.Count)
            throw new ArgumentOutOfRangeException(nameof(entry));
        return Backend.GetDescriptorIndex(table, entry);
    }

    public void WriteDescriptor(DescriptorTable table, uint entry, in ResourceBinding value)
    {
        Require(table);
        if (entry >= table.Count)
            throw new ArgumentOutOfRangeException(nameof(entry));
        if (value.Type != table.GetEntryDesc(entry).Type)
            throw new ArgumentException("The descriptor value must match the table entry type.", nameof(value));
        if (value.IsNull && value.Type == ResourceBindingType.Sampler)
            throw new ArgumentException("A Sampler descriptor cannot be null.", nameof(value));
        ValidateNullDescriptor(table.Device, value);
        if (value.Value is DeviceResource resource)
        {
            Require(resource);
            RequireSameDevice(table.Device, resource.Device, "Descriptor value");
        }

        Backend.WriteDescriptor(table, entry, value);
        _descriptorWrites.GetValue(table, static _ => throw new InvalidOperationException("Descriptor validation state is unavailable."))[entry] = true;
    }

    public PersistentParameterBindings CreatePersistentParameterBindings(Device device, Pipeline pipeline, in ParameterBlockBindings bindings, string? label = null)
    {
        RequireDevice(device);
        Require(pipeline);
        RequireSameDevice(device, pipeline.Device, "Pipeline");
        if (!_pipelineBindingStates.TryGetValue(pipeline, out PipelineBindingValidationState? pipelineBindings))
            Reject("Ownership", "Pipeline was not created through this Validation Layer.", label);
        if (!pipelineBindings!.Contains(bindings.Layout))
            RejectArgument("Bindings", "The Slang parameter layout is not part of the supplied Pipeline.", nameof(bindings), label);
        if (DiagnoseParameterBindings(bindings.Layout, bindings.Resources, bindings.OrdinaryData, pipelineBindings)is string createDiagnostic)
            RejectArgument("Bindings", createDiagnostic, nameof(bindings), label);
        GraphicsObject[] dependencies = CollectBindingDependencies(device, bindings.Resources);
        VariableLayoutReflection reflectedLayout = bindings.Layout;
        var state = new BindingValidationState(reflectedLayout, pipeline, pipelineBindings, dependencies);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _persistentBindingStates.EnsureAdditionalCapacity();
            PersistentParameterBindings? result = null;
            bool objectAdded = false;
            bool bindingAdded = false;
            try
            {
                result = Backend.CreatePersistentParameterBindings(device, pipeline, bindings, label);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _persistentBindingStates.Add(result, state);
                bindingAdded = true;
                return result;
            }
            catch (Exception exception)when (exception is ArgumentException or GraphicsException or OverflowException)
            {
                if (bindingAdded)
                    _persistentBindingStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                Report(ValidationMessageType.Error, "Bindings", exception.Message, label);
                throw;
            }
            catch
            {
                if (bindingAdded)
                    _persistentBindingStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public void UpdatePersistentParameterBindings(PersistentParameterBindings destination, in ParameterBlockBindings bindings)
    {
        Require(destination);
        if (!_persistentBindingStates.TryGetValue(destination, out BindingValidationState? state))
        {
            Reject("Ownership", "PersistentParameterBindings was not created through this Validation Layer.", destination.Label);
        }

        GraphicsObject[] dependencies = ValidateBindings(destination.Device, bindings, state!.Layout, state.Validation);
        Backend.UpdatePersistentParameterBindings(destination, bindings);
        state.Dependencies = dependencies;
    }

    public void PublishDescriptors(Device device, uint nodeIndex = uint.MaxValue, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireDevice(device);
        DeviceValidationState deviceState = _deviceStates.GetValue(device, static _ => throw new InvalidOperationException("The Device has no Validation node metadata."));
        uint resolvedNodeIndex = deviceState.ResolveNodeIndex(nodeIndex, nameof(nodeIndex));
        if (_descriptorWrites.AnyEntry((table, written) => ReferenceEquals(table.Device, device) && table.NodeIndex == resolvedNodeIndex && written.Any(value => !value)))
            Reject("Descriptors", "Sampler descriptor entries require values before publication.");
        Backend.PublishDescriptors(device, resolvedNodeIndex, cancellationToken);
    }

    public PipelineCache CreatePipelineCache(Device device, in PipelineCacheDesc desc, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireDevice(device);
        if (desc.MaximumEntryCount < 0)
        {
            Report(ValidationMessageType.Error, "PipelineCache", "PipelineCacheDesc.MaximumEntryCount must not be negative.", desc.Label);
            throw new ArgumentOutOfRangeException(
                nameof(PipelineCacheDesc.MaximumEntryCount),
                desc.MaximumEntryCount,
                "The maximum pipeline-cache entry count must not be negative."
            );
        }

        if (desc.MaximumByteCount < 0 ||
            desc.MaximumByteCount is > 0 and < PipelineCacheEnvelope.EmptyEnvelopeByteCount)
        {
            Report(ValidationMessageType.Error, "PipelineCache", "PipelineCacheDesc.MaximumByteCount must not be negative.", desc.Label);
            throw new ArgumentOutOfRangeException(
                nameof(PipelineCacheDesc.MaximumByteCount),
                desc.MaximumByteCount,
                "The maximum serialized pipeline-cache byte count must not be negative."
            );
        }

        if (desc.MaximumDecodedByteCount < 0)
        {
            Report(
                ValidationMessageType.Error,
                "PipelineCache",
                "PipelineCacheDesc.MaximumDecodedByteCount must not be negative.",
                desc.Label
            );
            throw new ArgumentOutOfRangeException(
                nameof(PipelineCacheDesc.MaximumDecodedByteCount),
                desc.MaximumDecodedByteCount,
                "The maximum decoded pipeline-cache byte count must not be negative."
            );
        }

        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            PipelineCache? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreatePipelineCache(device, desc, cancellationToken);
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

    public bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(cache);
        return Backend.TryGetPipelineCacheData(cache, destination, out requiredByteCount, cancellationToken);
    }

    public void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Require(destination);
        foreach (PipelineCache source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(source);
            RequireSameDevice(destination.Device, source.Device, "PipelineCache");
        }

        Backend.MergePipelineCaches(destination, sources, cancellationToken);
    }

    public Pipeline CreateGraphicsPipeline(Device device, in GraphicsPipelineDesc desc, PipelineCache? cache = null)
    {
        RequireDevice(device);
        ValidateGraphicsDescription(device, desc);
        ValidateDepthBounds(device, desc.DepthStencil, desc.DynamicStates);
        ValidateAttachmentFormats(device, desc.Attachments, desc.Blend);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        ValidateDynamicStates(device, desc.DynamicStates);
        EntryPointReflection[] entries = [desc.Vertex, desc.Pixel];
        PipelineBindingValidationState bindings = ReflectPipelineBindings(desc.Reflection, entries);
        bindings.AddStaticSamplers(device, desc.StaticSamplers);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _pipelineBindingStates.EnsureAdditionalCapacity();
            Pipeline? result = null;
            bool objectAdded = false;
            bool bindingsAdded = false;
            try
            {
                result = Backend.CreateGraphicsPipeline(device, desc, cache);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _pipelineBindingStates.Add(result, bindings);
                bindingsAdded = true;
                return result;
            }
            catch
            {
                if (bindingsAdded)
                    _pipelineBindingStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Task<Pipeline> CreateGraphicsPipelineAsync(Device device, in GraphicsPipelineDesc desc, PipelineCache? cache = null)
    {
        RequireDevice(device);
        ValidateGraphicsDescription(device, desc);
        ValidateDepthBounds(device, desc.DepthStencil, desc.DynamicStates);
        ValidateAttachmentFormats(device, desc.Attachments, desc.Blend);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        ValidateDynamicStates(device, desc.DynamicStates);
        EntryPointReflection[] entries = [desc.Vertex, desc.Pixel];
        PipelineBindingValidationState bindings = ReflectPipelineBindings(desc.Reflection, entries);
        bindings.AddStaticSamplers(device, desc.StaticSamplers);
        var objectInfo = new ValidationObjectInfo(device);
        Task<Pipeline> creation = Backend.CreateGraphicsPipelineAsync(device, desc, cache);
        return RegisterPipelineAsync(creation, objectInfo, bindings);
    }

    public Pipeline CreateComputePipeline(Device device, in ComputePipelineDesc desc, PipelineCache? cache = null)
    {
        RequireDevice(device);
        ValidateComputeDescription(desc);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        EntryPointReflection[] entries = [desc.Compute];
        PipelineBindingValidationState bindings = ReflectPipelineBindings(desc.Reflection, entries);
        bindings.AddStaticSamplers(device, desc.StaticSamplers.Span);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _pipelineBindingStates.EnsureAdditionalCapacity();
            Pipeline? result = null;
            bool objectAdded = false;
            bool bindingsAdded = false;
            try
            {
                result = Backend.CreateComputePipeline(device, desc, cache);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _pipelineBindingStates.Add(result, bindings);
                bindingsAdded = true;
                return result;
            }
            catch
            {
                if (bindingsAdded)
                    _pipelineBindingStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Task<Pipeline> CreateComputePipelineAsync(Device device, in ComputePipelineDesc desc, PipelineCache? cache = null)
    {
        RequireDevice(device);
        ValidateComputeDescription(desc);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        EntryPointReflection[] entries = [desc.Compute];
        PipelineBindingValidationState bindings = ReflectPipelineBindings(desc.Reflection, entries);
        bindings.AddStaticSamplers(device, desc.StaticSamplers.Span);
        var objectInfo = new ValidationObjectInfo(device);
        Task<Pipeline> creation = Backend.CreateComputePipelineAsync(device, desc, cache);
        return RegisterPipelineAsync(creation, objectInfo, bindings);
    }

    public Pipeline CreateMeshPipeline(Device device, in MeshPipelineDesc desc, PipelineCache? cache = null)
    {
        RequireCapability<MeshShaders>(device);
        ValidateMeshDescription(device, desc);
        ValidateDepthBounds(device, desc.DepthStencil, desc.DynamicStates);
        ValidateAttachmentFormats(device, desc.Attachments, desc.Blend);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        ValidateDynamicStates(device, desc.DynamicStates);
        List<EntryPointReflection> entries = [desc.Mesh];
        if (desc.Amplification != EntryPointReflection.Null)
            entries.Add(desc.Amplification);
        if (desc.Pixel != EntryPointReflection.Null)
            entries.Add(desc.Pixel);
        PipelineBindingValidationState bindings = ReflectPipelineBindings(desc.Reflection, CollectionsMarshal.AsSpan(entries));
        bindings.AddStaticSamplers(device, desc.StaticSamplers);
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _pipelineBindingStates.EnsureAdditionalCapacity();
            Pipeline? result = null;
            bool objectAdded = false;
            bool bindingsAdded = false;
            try
            {
                result = Backend.CreateMeshPipeline(device, desc, cache);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _pipelineBindingStates.Add(result, bindings);
                bindingsAdded = true;
                return result;
            }
            catch
            {
                if (bindingsAdded)
                    _pipelineBindingStates.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public Task<Pipeline> CreateMeshPipelineAsync(Device device, in MeshPipelineDesc desc, PipelineCache? cache = null)
    {
        RequireCapability<MeshShaders>(device);
        ValidateMeshDescription(device, desc);
        ValidateDepthBounds(device, desc.DepthStencil, desc.DynamicStates);
        ValidateAttachmentFormats(device, desc.Attachments, desc.Blend);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        ValidateDynamicStates(device, desc.DynamicStates);
        List<EntryPointReflection> entries = [desc.Mesh];
        if (desc.Amplification != EntryPointReflection.Null)
            entries.Add(desc.Amplification);
        if (desc.Pixel != EntryPointReflection.Null)
            entries.Add(desc.Pixel);
        PipelineBindingValidationState bindings = ReflectPipelineBindings(desc.Reflection, CollectionsMarshal.AsSpan(entries));
        bindings.AddStaticSamplers(device, desc.StaticSamplers);
        var objectInfo = new ValidationObjectInfo(device);
        Task<Pipeline> creation = Backend.CreateMeshPipelineAsync(device, desc, cache);
        return RegisterPipelineAsync(creation, objectInfo, bindings);
    }

    private async Task<Pipeline> RegisterPipelineAsync(Task<Pipeline> creation, ValidationObjectInfo objectInfo, PipelineBindingValidationState bindings)
    {
        Pipeline result = await creation.ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _ = Backend;
                _objects.EnsureAdditionalCapacity();
                _pipelineBindingStates.EnsureAdditionalCapacity();
                _objects.Add(result, objectInfo);
                try
                {
                    _pipelineBindingStates.Add(result, bindings);
                }
                catch
                {
                    _objects.Remove(result);
                    throw;
                }
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private void ValidateDynamicStates(Device device, DynamicStates requested)
    {
        DynamicStates unsupported = requested & ~device.Capabilities.SupportedDynamicStates;
        if (unsupported != DynamicStates.None)
        {
            Reject("Capabilities", $"Dynamic Pipeline state {unsupported} is unavailable on this Device.");
        }
    }

    private GraphicsObject[] ValidateBindings(Device device, in ParameterBlockBindings bindings, out VariableLayoutReflection reflectedLayout)
    {
        if (bindings.Layout == SlangShaderSharp.VariableLayoutReflection.Null)
            Reject("Bindings", "ParameterBlockBindings.Layout is null.");
        try
        {
            reflectedLayout = bindings.Layout;
            _ = GetParameterContentsLayout(bindings.Layout.TypeLayout);
        }
        catch (Exception exception)when (exception is ArgumentException or GraphicsException or OverflowException)
        {
            Reject("Bindings", exception.Message);
            throw;
        }

        return ValidateBindings(device, bindings, reflectedLayout);
    }

    private GraphicsObject[] ValidateBindings(
        Device device,
        in ParameterBlockBindings bindings,
        VariableLayoutReflection reflectedLayout,
        PipelineBindingValidationState? pipeline = null
    )
    {
        if (bindings.Layout != reflectedLayout)
        {
            Reject("Bindings", "The parameter layout cannot change during a complete binding replacement.");
        }

        if (DiagnoseParameterBindings(reflectedLayout, bindings.Resources, bindings.OrdinaryData, pipeline)is string diagnostic)
            Reject("Bindings", diagnostic);
        return CollectBindingDependencies(device, bindings.Resources);
    }

    private GraphicsObject[] CollectBindingDependencies(Device device, ReadOnlySpan<ResourceBinding> resources)
    {
        var dependencies = new HashSet<GraphicsObject>(ReferenceEqualityComparer.Instance);
        foreach (ref readonly ResourceBinding binding in resources)
        {
            ValidateNullDescriptor(device, binding);
            if (binding.Value is not DeviceResource resource)
                continue;
            Require(resource);
            RequireSameDevice(device, resource.Device, "ResourceBinding");
            if (resource is Buffer buffer)
            {
                ValidateConstantBufferRange(device, buffer, binding.BufferRange);
            }

            GraphicsObject? current = resource;
            while (current is not null && current is not Device)
            {
                dependencies.Add(current);
                current = _objects.TryGetValue(current, out ValidationObjectInfo? info) ? info.Parent : null;
            }
        }

        return dependencies.ToArray();
    }

    private PipelineBindingValidationState ReflectPipelineBindings(
        ShaderReflection reflection,
        ReadOnlySpan<EntryPointReflection> entries,
        bool useConservativeWorkGraphEntries = false
    )
    {
        try
        {
            if (reflection == ShaderReflection.Null)
                throw new ArgumentException("Pipeline Slang reflection is null.", nameof(reflection));
            var result = new PipelineBindingValidationState(reflection);
            VariableLayoutReflection global = reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
            result.Add(global);
            if (useConservativeWorkGraphEntries)
            {
                for (uint index = 0; index < reflection.EntryPointCount; index++)
                {
                    EntryPointReflection entry = reflection.GetEntryPointByIndex(index);
                    if (entry.Stage is SlangStage.Dispatch or SlangStage.Node)
                        result.Add(entry.VarLayout);
                }
            }
            else
                foreach (EntryPointReflection entry in entries)
                    result.Add(entry.VarLayout);
            return result;
        }
        catch (Exception exception)when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            Reject("Bindings", exception.Message);
            throw;
        }
    }
}
