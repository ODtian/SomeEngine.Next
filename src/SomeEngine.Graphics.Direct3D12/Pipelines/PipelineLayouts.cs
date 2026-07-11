using Vortice.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    private PipelineLayoutHandle CreatePipelineLayoutCore(in PipelineLayoutDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeBindGroupLayout[] groups = desc.Groups.Span
            .ToArray()
            .Select(GetBindGroupLayout)
            .ToArray();
        PushConstantRange[] pushConstants = desc.PushConstants.ToArray();
        ValidatePushConstants(pushConstants);

        List<RootParameter1> parameters = [];
        List<NativeRootBinding> rootBindings = [];
        for (int groupIndex = 0; groupIndex < groups.Length; groupIndex++)
        {
            NativeBindGroupLayout group = groups[groupIndex];
            foreach (BindingDesc binding in group.Bindings.OrderBy(static value => value.Binding))
            {
                BindingSlot slot = group.Find(binding.Binding);
                DescriptorRange1 range = new(
                    DescriptorRangeType(binding.Kind),
                    binding.Count,
                    binding.Binding,
                    checked((uint)groupIndex),
                    0,
                    DescriptorRangeFlags.None);
                uint parameterIndex = checked((uint)parameters.Count);
                parameters.Add(new RootParameter1(
                    new RootDescriptorTable1([range]),
                    ShaderVisibility(binding.Visibility)));
                rootBindings.Add(new NativeRootBinding(
                    checked((uint)groupIndex),
                    binding.Binding,
                    parameterIndex,
                    slot.HeapType,
                    slot.DescriptorOffset,
                    checked((int)binding.Count)));
            }
        }

        List<NativeRootConstant> rootConstants = [];
        foreach (PushConstantRange range in pushConstants)
        {
            uint parameterIndex = checked((uint)parameters.Count);
            parameters.Add(new RootParameter1(
                new RootConstants(range.Register, range.Space, range.Size / 4),
                ShaderVisibility(range.Visibility)));
            rootConstants.Add(new NativeRootConstant(range, parameterIndex));
        }

        int dwordCost = checked(rootBindings.Count + pushConstants.Sum(static range => (int)(range.Size / 4)));
        if (dwordCost > 64)
            throw new ArgumentException($"The D3D12 root signature costs {dwordCost} DWORDs; the architectural limit is 64.", nameof(desc));

        RootSignatureDescription1 nativeDesc = new(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            parameters.ToArray(),
            Array.Empty<StaticSamplerDescription>());
        ID3D12RootSignature rootSignature;
        try
        {
            rootSignature = _native.Device.CreateRootSignature(in nativeDesc);
        }
        catch (Exception exception)
        {
            throw PipelineCreationFailure("root-signature", exception);
        }

        foreach (NativeBindGroupLayout group in groups) group.AddChild();
        try
        {
            NativePipelineLayout native = new(
                rootSignature,
                groups,
                rootBindings.ToArray(),
                rootConstants.ToArray());
            HandleKey key = _pipelineLayouts.Add(native);
            return new PipelineLayoutHandle(_domain, key.Slot, key.Generation);
        }
        catch
        {
            foreach (NativeBindGroupLayout group in groups) group.RemoveChild();
            rootSignature.Dispose();
            throw;
        }
    }

    private PipelineHandle CreateComputePipelineCore(in ComputePipelineDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativePipelineLayout layout = GetPipelineLayout(desc.Layout);
        NativeShader shader = _shaders.Get(desc.Shader.Domain, desc.Shader.Slot, desc.Shader.Generation, "compute shader");
        if (shader.Stage != ShaderStage.Compute)
            throw new ArgumentException("Shader does not reference a compute-stage artifact.", nameof(desc));
        ValidateShaderInterface(layout, shader, nameof(desc.Shader));

        ComputePipelineStateDescription nativeDesc = new()
        {
            RootSignature = layout.RootSignature,
            ComputeShader = shader.Bytecode,
            Flags = PipelineStateFlags.None,
        };
        ID3D12PipelineState pipelineState;
        try
        {
            pipelineState = _native.Device.CreateComputePipelineState(nativeDesc);
        }
        catch (Exception exception)
        {
            throw PipelineCreationFailure("compute pipeline", exception);
        }

        layout.AddPipeline();
        shader.AddPipeline();
        try
        {
            NativeComputePipeline native = new(pipelineState, layout, shader);
            HandleKey key = _pipelines.Add(native);
            return new PipelineHandle(_domain, key.Slot, key.Generation);
        }
        catch
        {
            shader.RemovePipeline();
            layout.RemovePipeline();
            pipelineState.Dispose();
            throw;
        }
    }

    internal NativePipelineLayout GetPipelineLayout(PipelineLayoutHandle handle) =>
        _pipelineLayouts.Get(handle.Domain, handle.Slot, handle.Generation, "pipeline layout");

    private void ValidateShaderInterface(NativePipelineLayout layout, NativeShader shader, string argument)
    {
        foreach (ShaderBinding reflected in shader.Interface.Bindings.Span)
        {
            if (reflected.Group >= (uint)layout.Groups.Length)
                throw new ArgumentException($"Shader '{argument}' references group {reflected.Group} outside the pipeline layout.", argument);
            NativeBindGroupLayout group = layout.Groups[checked((int)reflected.Group)];
            BindingSlot slot = group.Find(reflected.Binding);
            BindingDesc binding = slot.Description;
            if (binding.Kind != reflected.Kind || binding.Count < reflected.Count || (binding.Visibility & shader.Stage) == 0)
                throw new ArgumentException($"Shader binding {reflected.Group}:{reflected.Binding} is incompatible with the pipeline layout.", argument);
        }
        foreach (PushConstantRange reflected in shader.Interface.PushConstants.Span)
        {
            bool found = layout.Constants.Any(candidate =>
                candidate.Range.Offset == reflected.Offset &&
                candidate.Range.Size >= reflected.Size &&
                candidate.Range.Register == reflected.Register &&
                candidate.Range.Space == reflected.Space &&
                (candidate.Range.Visibility & shader.Stage) != 0);
            if (!found)
                throw new ArgumentException($"Shader push-constant range at byte {reflected.Offset} is incompatible with the pipeline layout.", argument);
        }
    }

    private static void ValidatePushConstants(PushConstantRange[] ranges)
    {
        for (int index = 0; index < ranges.Length; index++)
        {
            PushConstantRange range = ranges[index];
            if (range.Size == 0 || (range.Offset & 3) != 0 || (range.Size & 3) != 0 || range.Visibility == 0 ||
                (range.Visibility & ~(ShaderStage.Vertex | ShaderStage.Pixel | ShaderStage.Compute)) != 0)
                throw new ArgumentException("Push-constant ranges require non-zero 4-byte-aligned sizes, offsets, and valid visibility.", nameof(ranges));
            ulong end = checked((ulong)range.Offset + range.Size);
            for (int previousIndex = 0; previousIndex < index; previousIndex++)
            {
                PushConstantRange previous = ranges[previousIndex];
                ulong previousEnd = checked((ulong)previous.Offset + previous.Size);
                if (range.Offset < previousEnd && previous.Offset < end)
                    throw new ArgumentException("Push-constant byte ranges overlap.", nameof(ranges));
                if (range.Register == previous.Register && range.Space == previous.Space &&
                    (range.Visibility & previous.Visibility) != 0)
                    throw new ArgumentException("Push-constant ranges overlap the same register and shader visibility.", nameof(ranges));
            }
        }
    }

    private static DescriptorRangeType DescriptorRangeType(BindingKind kind) => kind switch
    {
        BindingKind.ConstantBuffer => Vortice.Direct3D12.DescriptorRangeType.ConstantBufferView,
        BindingKind.SampledTexture or BindingKind.ReadOnlyBuffer => Vortice.Direct3D12.DescriptorRangeType.ShaderResourceView,
        BindingKind.StorageTexture or BindingKind.StorageBuffer => Vortice.Direct3D12.DescriptorRangeType.UnorderedAccessView,
        BindingKind.Sampler => Vortice.Direct3D12.DescriptorRangeType.Sampler,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static Vortice.Direct3D12.ShaderVisibility ShaderVisibility(ShaderStage stages) => stages switch
    {
        ShaderStage.Vertex => Vortice.Direct3D12.ShaderVisibility.Vertex,
        ShaderStage.Pixel => Vortice.Direct3D12.ShaderVisibility.Pixel,
        _ => Vortice.Direct3D12.ShaderVisibility.All,
    };

    private InvalidOperationException PipelineCreationFailure(string kind, Exception exception)
    {
        GraphicsDiagnostic[] nativeDiagnostics = _native.DrainDiagnostics();
        foreach (GraphicsDiagnostic diagnostic in nativeDiagnostics) _diagnostics.Enqueue(diagnostic);
        string detail = nativeDiagnostics.Length == 0
            ? "The D3D12 information queue did not report a validation message."
            : string.Join(" | ", nativeDiagnostics.Select(static diagnostic => diagnostic.Message));
        return new InvalidOperationException($"D3D12 {kind} creation failed. {detail}", exception);
    }
}
