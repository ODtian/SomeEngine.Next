using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer<TBackend>
{
    public DescriptorTable CreateDescriptorTable(
        Device device,
        ReadOnlySpan<ResourceBindingType> slotTypes,
        string? label = null)
    {
        RequireDevice(device);
        return Track(Backend.CreateDescriptorTable(device, slotTypes, label), device);
    }

    public uint GetDescriptorIndex(DescriptorTable table, uint slot)
    {
        Require(table);
        return Backend.GetDescriptorIndex(table, slot);
    }

    public void WriteDescriptor(DescriptorTable table, uint slot, in ResourceBinding value)
    {
        Require(table);
        if (value.Value is DeviceResource resource)
        {
            Require(resource);
            RequireSameDevice(table.Device, resource.Device, "Descriptor value");
        }
        Backend.WriteDescriptor(table, slot, value);
    }

    public PersistentParameterBindings CreatePersistentParameterBindings(
        Device device,
        in ParameterBlockBindings bindings,
        string? label = null)
    {
        RequireDevice(device);
        GraphicsObject[] dependencies = ValidateBindings(
            device,
            bindings,
            out ValidationParameterBlockLayout reflectedLayout);
        PersistentParameterBindings result = Track(
            Backend.CreatePersistentParameterBindings(device, bindings, label),
            device);
        _persistentBindingStates.Add(
            result,
            new BindingValidationState(reflectedLayout, dependencies));
        return result;
    }

    public void UpdatePersistentParameterBindings(
        PersistentParameterBindings destination,
        in ParameterBlockBindings bindings)
    {
        Require(destination);
        if (!_persistentBindingStates.TryGetValue(destination, out BindingValidationState? state))
        {
            Reject(
                "Ownership",
                "PersistentParameterBindings was not created through this Validation Layer.",
                destination.Label);
        }
        GraphicsObject[] dependencies = ValidateBindings(
            destination.Device,
            bindings,
            state!.Layout);
        Backend.UpdatePersistentParameterBindings(destination, bindings);
        state.Dependencies = dependencies;
    }

    public void PublishDescriptors(Device device)
    {
        RequireDevice(device);
        Backend.PublishDescriptors(device);
    }

    public PipelineCache CreatePipelineCache(Device device, in PipelineCacheDesc desc)
    {
        RequireDevice(device);
        return Track(Backend.CreatePipelineCache(device, desc), device);
    }

    public bool TryGetPipelineCacheData(
        PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount)
    {
        Require(cache);
        return Backend.TryGetPipelineCacheData(cache, destination, out requiredByteCount);
    }

    public void MergePipelineCaches(
        PipelineCache destination,
        ReadOnlySpan<PipelineCache> sources)
    {
        Require(destination);
        foreach (PipelineCache source in sources)
        {
            Require(source);
            RequireSameDevice(destination.Device, source.Device, "PipelineCache");
        }
        Backend.MergePipelineCaches(destination, sources);
    }

    public Pipeline CreateGraphicsPipeline(
        Device device,
        in GraphicsPipelineDesc desc,
        PipelineCache? cache = null)
    {
        RequireDevice(device);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        EntryPointReflection[] entries = [desc.Vertex, desc.Pixel];
        PipelineBindingValidationState bindings =
            ReflectPipelineBindings(desc.Program, entries);
        Pipeline result = Track(Backend.CreateGraphicsPipeline(device, desc, cache), device);
        _pipelineBindingStates.Add(result, bindings);
        return result;
    }

    public Pipeline CreateComputePipeline(
        Device device,
        in ComputePipelineDesc desc,
        PipelineCache? cache = null)
    {
        RequireDevice(device);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        EntryPointReflection[] entries = [desc.Compute];
        PipelineBindingValidationState bindings =
            ReflectPipelineBindings(desc.Program, entries);
        Pipeline result = Track(Backend.CreateComputePipeline(device, desc, cache), device);
        _pipelineBindingStates.Add(result, bindings);
        return result;
    }

    public Pipeline CreateMeshPipeline(
        Device device,
        in MeshPipelineDesc desc,
        PipelineCache? cache = null)
    {
        RequireCapability<MeshShaders>(device);
        if (cache is not null)
            RequireOnDevice(device, cache, "PipelineCache");
        List<EntryPointReflection> entries = [desc.Mesh];
        if (desc.Amplification != EntryPointReflection.Null)
            entries.Add(desc.Amplification);
        if (desc.Pixel != EntryPointReflection.Null)
            entries.Add(desc.Pixel);
        PipelineBindingValidationState bindings =
            ReflectPipelineBindings(desc.Program, CollectionsMarshal.AsSpan(entries));
        Pipeline result = Track(Backend.CreateMeshPipeline(device, desc, cache), device);
        _pipelineBindingStates.Add(result, bindings);
        return result;
    }

    private GraphicsObject[] ValidateBindings(
        Device device,
        in ParameterBlockBindings bindings,
        out ValidationParameterBlockLayout reflectedLayout)
    {
        if (bindings.Layout == SlangShaderSharp.VariableLayoutReflection.Null)
            Reject("Bindings", "ParameterBlockBindings.Layout is null.");
        try
        {
            reflectedLayout = ValidationParameterBlockLayout.Reflect(bindings.Layout);
        }
        catch (Exception exception) when (exception is ArgumentException or
            GraphicsException or OverflowException)
        {
            Reject("Bindings", exception.Message);
            throw;
        }
        return ValidateBindings(device, bindings, reflectedLayout);
    }

    private GraphicsObject[] ValidateBindings(
        Device device,
        in ParameterBlockBindings bindings,
        ValidationParameterBlockLayout reflectedLayout)
    {
        if (bindings.Layout != reflectedLayout.Layout)
        {
            Reject(
                "Bindings",
                "The parameter layout cannot change during a complete binding replacement.");
        }
        if (reflectedLayout.Diagnose(
                bindings.Resources,
                bindings.OrdinaryData) is string diagnostic)
            Reject("Bindings", diagnostic);

        var dependencies = new HashSet<GraphicsObject>(ReferenceEqualityComparer.Instance);
        foreach (ref readonly ResourceBinding binding in bindings.Resources)
        {
            if (binding.Value is not DeviceResource resource)
                continue;
            Require(resource);
            RequireSameDevice(device, resource.Device, "ResourceBinding");
            GraphicsObject? current = resource;
            while (current is not null && current is not Device)
            {
                dependencies.Add(current);
                current = _objects.TryGetValue(current, out ValidationObjectInfo? info)
                    ? info.Parent
                    : null;
            }
        }
        return dependencies.ToArray();
    }

    private PipelineBindingValidationState ReflectPipelineBindings(
        IComponentType program,
        ReadOnlySpan<EntryPointReflection> entries)
    {
        ArgumentNullException.ThrowIfNull(program);
        ISlangBlob? diagnostics = null;
        try
        {
            ShaderReflection reflection = program.GetLayout(0, out diagnostics);
            if (reflection == ShaderReflection.Null)
            {
                Reject(
                    "Bindings",
                    "Slang did not expose the selected linked program layout.");
            }

            var result = new PipelineBindingValidationState();
            VariableLayoutReflection global = reflection.GetGlobalParamsVarLayout()
                ?? VariableLayoutReflection.Null;
            result.Add(global);
            foreach (EntryPointReflection entry in entries)
                result.Add(entry.VarLayout);
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or OverflowException)
        {
            Reject("Bindings", exception.Message);
            throw;
        }
        finally
        {
            if ((object?)diagnostics is ComObject wrapper)
                wrapper.FinalRelease();
        }
    }
}
