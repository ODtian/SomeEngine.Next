namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    public BindGroupLayoutHandle CreateBindGroupLayout(ReadOnlySpan<BindingDesc> bindings)
    {
        EnsureCoordinatorThread();
        BindingDesc[] frozen = bindings.ToArray();
        ValidateBindingLayout(frozen);
        lock (_gate)
        {
            EnsureNotDisposed();
            (uint slot, uint generation) = _bindGroupLayouts.Allocate(new BindGroupLayoutRecord(frozen));
            return new BindGroupLayoutHandle(_domain, slot, generation);
        }
    }

    public BindGroupHandle CreateBindGroup(BindGroupLayoutHandle layout, ReadOnlySpan<BindingWrite> writes, string? name = null)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            CommandReferences dependencies = new();
            BindingWrite[] frozen = ValidateAndFreezeBindingWritesCore(layout, writes, dependencies);
            (uint slot, uint generation) = _bindGroups.Allocate(new BindGroupRecord(layout, frozen, name));
            _bindGroupLayouts.AddChild(layout.Domain, layout.Slot, layout.Generation);
            AddBindingChildren(dependencies);
            return new BindGroupHandle(_domain, slot, generation);
        }
    }

    public void DestroyBindGroupLayout(BindGroupLayoutHandle layout)
    {
        EnsureCoordinatorThread();
        lock (_gate) { EnsureNotDisposed(); _bindGroupLayouts.Destroy(layout.Domain, layout.Slot, layout.Generation); }
    }

    public void DestroyBindGroup(BindGroupHandle group)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            BindGroupRecord record = RequireBindGroup(group);
            _bindGroups.Destroy(group.Domain, group.Slot, group.Generation);
            _bindGroupLayouts.ReleaseChild(record.Layout.Domain, record.Layout.Slot, record.Layout.Generation);
            ReleaseBindingChildren(record.Writes);
        }
    }

    public ShaderHandle CreateShader(in ShaderDesc desc)
    {
        EnsureCoordinatorThread();
        ValidateShaderDesc(desc);
        ShaderInterface frozenInterface = new(
            desc.Interface.Bindings.ToArray(),
            desc.Interface.PushConstants.ToArray(),
            desc.Interface.LayoutHash);
        ShaderDesc frozen = desc with
        {
            Bytecode = desc.Bytecode.ToArray(),
            Interface = frozenInterface,
        };
        lock (_gate)
        {
            EnsureNotDisposed();
            (uint slot, uint generation) = _shaders.Allocate(new ShaderRecord(frozen));
            return new ShaderHandle(_domain, slot, generation);
        }
    }

    public PipelineLayoutHandle CreatePipelineLayout(in PipelineLayoutDesc desc)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            BindGroupLayoutHandle[] groups = desc.Groups.ToArray();
            int descriptorTableCount = 0;
            foreach (BindGroupLayoutHandle group in groups)
            {
                descriptorTableCount = checked(
                    descriptorTableCount + RequireBindGroupLayout(group).Bindings.Length);
            }
            PushConstantRange[] pushConstants = desc.PushConstants.ToArray();
            ValidatePushConstants(pushConstants, descriptorTableCount);
            (uint slot, uint generation) = _pipelineLayouts.Allocate(new PipelineLayoutRecord(groups, pushConstants, desc.Name));
            foreach (BindGroupLayoutHandle group in groups.Distinct())
            {
                _bindGroupLayouts.AddChild(group.Domain, group.Slot, group.Generation);
            }
            return new PipelineLayoutHandle(_domain, slot, generation);
        }
    }

    public PipelineHandle CreateRasterPipeline(in RasterPipelineDesc desc)
    {
        EnsureCoordinatorThread();
        if (desc.SampleCount <= 0) throw new ArgumentOutOfRangeException(nameof(desc));
        lock (_gate)
        {
            EnsureNotDisposed();
            PipelineLayoutRecord layout = RequirePipelineLayout(desc.Layout);
            ShaderRecord vertex = RequireShader(desc.VertexShader);
            ShaderRecord pixel = RequireShader(desc.PixelShader);
            RequireShaderStage(vertex.Desc, ShaderStage.Vertex, nameof(desc.VertexShader));
            RequireShaderStage(pixel.Desc, ShaderStage.Pixel, nameof(desc.PixelShader));
            ValidateShaderInterface(layout, vertex.Desc);
            ValidateShaderInterface(layout, pixel.Desc);
            Format[] colorFormats = desc.ColorFormats.ToArray();
            if (colorFormats.Any(static format => format == Format.Unknown || TextureLayout.IsDepth(format)))
                throw new ArgumentException("Raster color formats must be concrete color formats.", nameof(desc));
            if (desc.DepthStencilFormat != Format.Unknown && !TextureLayout.IsDepth(desc.DepthStencilFormat))
                throw new ArgumentException("DepthStencilFormat must be a depth format.", nameof(desc));
            BlendAttachmentDesc[] blends = desc.BlendAttachments.ToArray();
            if (blends.Length != 0 && blends.Length != colorFormats.Length)
                throw new ArgumentException("Blend attachment count must match color format count.", nameof(desc));
            RasterPipelineDesc frozen = desc with
            {
                ColorFormats = colorFormats,
                VertexAttributes = desc.VertexAttributes.ToArray(),
                VertexBuffers = desc.VertexBuffers.ToArray(),
                BlendAttachments = blends,
            };
            (uint slot, uint generation) = _pipelines.Allocate(new PipelineRecord(
                PipelineKind.Raster,
                desc.Layout,
                desc.VertexShader,
                desc.PixelShader,
                frozen,
                default,
                desc.Name));
            _pipelineLayouts.AddChild(desc.Layout.Domain, desc.Layout.Slot, desc.Layout.Generation);
            _shaders.AddChild(desc.VertexShader.Domain, desc.VertexShader.Slot, desc.VertexShader.Generation);
            _shaders.AddChild(desc.PixelShader.Domain, desc.PixelShader.Slot, desc.PixelShader.Generation);
            return new PipelineHandle(_domain, slot, generation);
        }
    }

    public PipelineHandle CreateComputePipeline(in ComputePipelineDesc desc)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            PipelineLayoutRecord layout = RequirePipelineLayout(desc.Layout);
            ShaderRecord shader = RequireShader(desc.Shader);
            RequireShaderStage(shader.Desc, ShaderStage.Compute, nameof(desc.Shader));
            ValidateShaderInterface(layout, shader.Desc);
            (uint slot, uint generation) = _pipelines.Allocate(new PipelineRecord(
                PipelineKind.Compute,
                desc.Layout,
                desc.Shader,
                default,
                default,
                desc,
                desc.Name));
            _pipelineLayouts.AddChild(desc.Layout.Domain, desc.Layout.Slot, desc.Layout.Generation);
            _shaders.AddChild(desc.Shader.Domain, desc.Shader.Slot, desc.Shader.Generation);
            return new PipelineHandle(_domain, slot, generation);
        }
    }

    public PipelineMetadata GetPipelineMetadata(PipelineHandle pipeline)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            PipelineRecord record = RequirePipeline(pipeline);
            ShaderDesc first = RequireShader(record.FirstShader).Desc;
            if (record.Kind == PipelineKind.Compute)
            {
                return new PipelineMetadata(
                    PipelineType.Compute,
                    [new PipelineShaderIdentity(first.Key, first.Stage)]);
            }

            ShaderDesc second = RequireShader(record.SecondShader).Desc;
            return new PipelineMetadata(
                PipelineType.Raster,
                [
                    new PipelineShaderIdentity(first.Key, first.Stage),
                    new PipelineShaderIdentity(second.Key, second.Stage),
                ]);
        }
    }

    public void DestroyShader(ShaderHandle shader)
    {
        EnsureCoordinatorThread();
        lock (_gate) { EnsureNotDisposed(); _shaders.Destroy(shader.Domain, shader.Slot, shader.Generation); }
    }

    public void DestroyPipelineLayout(PipelineLayoutHandle layout)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            PipelineLayoutRecord record = RequirePipelineLayout(layout);
            _pipelineLayouts.Destroy(layout.Domain, layout.Slot, layout.Generation);
            foreach (BindGroupLayoutHandle group in record.Groups.Distinct())
            {
                _bindGroupLayouts.ReleaseChild(group.Domain, group.Slot, group.Generation);
            }
        }
    }

    public void DestroyPipeline(PipelineHandle pipeline)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            PipelineRecord record = RequirePipeline(pipeline);
            _pipelines.Destroy(pipeline.Domain, pipeline.Slot, pipeline.Generation);
            _pipelineLayouts.ReleaseChild(record.Layout.Domain, record.Layout.Slot, record.Layout.Generation);
            _shaders.ReleaseChild(record.FirstShader.Domain, record.FirstShader.Slot, record.FirstShader.Generation);
            if (record.SecondShader.IsValid)
            {
                _shaders.ReleaseChild(record.SecondShader.Domain, record.SecondShader.Slot, record.SecondShader.Generation);
            }
        }
    }

    internal PipelineRecord GetPipelineForRecording(PipelineHandle pipeline)
    {
        lock (_gate) { EnsureNotDisposed(); return RequirePipeline(pipeline); }
    }

    internal BindGroupRecord GetBindGroupForRecording(BindGroupHandle group)
    {
        lock (_gate) { EnsureNotDisposed(); return RequireBindGroup(group); }
    }

    internal BindingWrite[] ValidateAndFreezeBindingWrites(
        BindGroupLayoutHandle layout,
        ReadOnlySpan<BindingWrite> writes,
        CommandReferences references)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            return ValidateAndFreezeBindingWritesCore(layout, writes, references);
        }
    }

    internal void ValidatePipelineBindings(PipelineHandle pipeline, IReadOnlyDictionary<uint, BindGroupLayoutHandle> boundGroups)
    {
        lock (_gate)
        {
            PipelineRecord pipelineRecord = RequirePipeline(pipeline);
            PipelineLayoutRecord layout = RequirePipelineLayout(pipelineRecord.Layout);
            for (uint index = 0; index < (uint)layout.Groups.Length; index++)
            {
                if (!boundGroups.TryGetValue(index, out BindGroupLayoutHandle bound) || bound != layout.Groups[index])
                {
                    throw ValidationError($"Pipeline requires bind group layout {layout.Groups[index]} at index {index}.");
                }
            }
        }
    }

    internal void ValidatePushConstantsForRecording(
        PipelineHandle pipeline,
        PipelineLayoutHandle layoutHandle,
        ShaderStage stages,
        uint byteOffset,
        int byteLength)
    {
        lock (_gate)
        {
            if (!pipeline.IsValid) throw ValidationError("Push constants require a bound pipeline.");
            PipelineRecord pipelineRecord = RequirePipeline(pipeline);
            if (pipelineRecord.Layout != layoutHandle)
                throw ValidationError("Push-constant layout does not match the bound pipeline layout.");
            if (stages == 0 || (stages & ~(ShaderStage.Vertex | ShaderStage.Pixel | ShaderStage.Compute)) != 0)
                throw new ArgumentOutOfRangeException(nameof(stages));
            if (byteLength <= 0 || (byteOffset & 3) != 0 || (byteLength & 3) != 0)
                throw new ArgumentException("Push-constant writes must be non-empty and four-byte aligned.", nameof(byteLength));

            PipelineLayoutRecord layout = RequirePipelineLayout(layoutHandle);
            ulong end = checked((ulong)byteOffset + (uint)byteLength);
            foreach (PushConstantRange range in layout.PushConstants)
            {
                ulong rangeEnd = checked((ulong)range.Offset + range.Size);
                if (byteOffset >= range.Offset && end <= rangeEnd && (range.Visibility & stages) == stages) return;
            }
            throw ValidationError("Push-constant write is outside the declared layout range or stage visibility.");
        }
    }

    private BindingWrite[] ValidateAndFreezeBindingWritesCore(
        BindGroupLayoutHandle layoutHandle,
        ReadOnlySpan<BindingWrite> writes,
        CommandReferences? references)
    {
        BindGroupLayoutRecord layout = RequireBindGroupLayout(layoutHandle);
        BindingWrite[] frozen = writes.ToArray();
        HashSet<(uint Binding, uint Element)> seen = [];
        foreach (ref readonly BindingWrite write in frozen.AsSpan())
        {
            BindingDesc binding = FindBinding(layout, write.Binding);
            if (write.Element >= binding.Count) throw new ArgumentOutOfRangeException(nameof(writes));
            if (!seen.Add((write.Binding, write.Element))) throw new ArgumentException("Duplicate binding write.", nameof(writes));
            ValidateBindingValue(binding, write, references);
        }

        foreach (BindingDesc binding in layout.Bindings)
        {
            for (uint element = 0; element < binding.Count; element++)
            {
                if (!seen.Contains((binding.Binding, element)))
                    throw new ArgumentException($"Binding {binding.Binding}[{element}] was not supplied.", nameof(writes));
            }
        }
        return frozen;
    }

    private void ValidateBindingValue(BindingDesc binding, in BindingWrite write, CommandReferences? references)
    {
        switch (binding.Kind)
        {
            case BindingKind.SampledTexture:
            case BindingKind.StorageTexture:
                if (write.ValueKind != BindingValueKind.TextureView) throw new ArgumentException("A texture binding requires a texture view.");
                TextureViewRecord textureView = RequireTextureView(write.TextureView);
                TextureViewUsage requiredTextureUsage = binding.Kind == BindingKind.SampledTexture ? TextureViewUsage.ShaderResource : TextureViewUsage.Storage;
                if (!textureView.Desc.Usage.HasFlag(requiredTextureUsage)) throw ValidationError($"Texture view lacks {requiredTextureUsage} usage.");
                references?.TextureViews.Add(write.TextureView);
                break;
            case BindingKind.ConstantBuffer:
            case BindingKind.ReadOnlyBuffer:
            case BindingKind.StorageBuffer:
                if (write.ValueKind != BindingValueKind.BufferView) throw new ArgumentException("A buffer binding requires a buffer view.");
                BufferViewRecord bufferView = RequireBufferView(write.BufferView);
                if (bufferView.Desc.Kind != binding.Kind) throw ValidationError($"Buffer view kind {bufferView.Desc.Kind} does not match {binding.Kind}.");
                references?.BufferViews.Add(write.BufferView);
                break;
            case BindingKind.Sampler:
                if (write.ValueKind != BindingValueKind.Sampler) throw new ArgumentException("A sampler binding requires a sampler.");
                _ = RequireSampler(write.Sampler);
                references?.Samplers.Add(write.Sampler);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(binding));
        }
    }

    private void AddBindingChildren(CommandReferences dependencies)
    {
        foreach (TextureViewHandle view in dependencies.TextureViews)
        {
            _textureViews.AddChild(view.Domain, view.Slot, view.Generation);
        }
        foreach (BufferViewHandle view in dependencies.BufferViews)
        {
            _bufferViews.AddChild(view.Domain, view.Slot, view.Generation);
        }
        foreach (SamplerHandle sampler in dependencies.Samplers)
        {
            _samplers.AddChild(sampler.Domain, sampler.Slot, sampler.Generation);
        }
    }

    private void ReleaseBindingChildren(BindingWrite[] writes)
    {
        CommandReferences dependencies = new();
        foreach (ref readonly BindingWrite write in writes.AsSpan()) AddBindingReference(write, dependencies);
        foreach (TextureViewHandle view in dependencies.TextureViews)
        {
            _textureViews.ReleaseChild(view.Domain, view.Slot, view.Generation);
        }
        foreach (BufferViewHandle view in dependencies.BufferViews)
        {
            _bufferViews.ReleaseChild(view.Domain, view.Slot, view.Generation);
        }
        foreach (SamplerHandle sampler in dependencies.Samplers)
        {
            _samplers.ReleaseChild(sampler.Domain, sampler.Slot, sampler.Generation);
        }
    }

    private void ValidateShaderInterface(PipelineLayoutRecord layout, in ShaderDesc shader)
    {
        foreach (ShaderBinding reflected in shader.Interface.Bindings.Span)
        {
            if (reflected.Group >= (uint)layout.Groups.Length)
                throw ValidationError($"Shader binding group {reflected.Group} is outside the pipeline layout.");
            BindGroupLayoutRecord group = RequireBindGroupLayout(layout.Groups[checked((int)reflected.Group)]);
            BindingDesc binding = FindBinding(group, reflected.Binding);
            if (binding.Kind != reflected.Kind || binding.Count < reflected.Count || (binding.Visibility & shader.Stage) == 0)
                throw ValidationError($"Shader binding {reflected.Group}:{reflected.Binding} is incompatible with the pipeline layout.");
        }
    }

    private static BindingDesc FindBinding(BindGroupLayoutRecord layout, uint binding)
    {
        foreach (BindingDesc candidate in layout.Bindings)
        {
            if (candidate.Binding == binding) return candidate;
        }
        throw new ArgumentException($"Binding {binding} is absent from the bind group layout.");
    }

    private static void ValidateBindingLayout(BindingDesc[] bindings)
    {
        HashSet<uint> seen = [];
        foreach (BindingDesc binding in bindings)
        {
            if (binding.Count == 0 || binding.Visibility == 0 || !Enum.IsDefined(binding.Kind))
                throw new ArgumentException("Invalid bind group layout entry.", nameof(bindings));
            if (!seen.Add(binding.Binding)) throw new ArgumentException($"Duplicate binding {binding.Binding}.", nameof(bindings));
        }
    }

    private static void ValidatePushConstants(PushConstantRange[] ranges, int descriptorTableCount)
    {
        ulong rootDwords = checked((ulong)descriptorTableCount);
        for (int index = 0; index < ranges.Length; index++)
        {
            PushConstantRange range = ranges[index];
            if (range.Size == 0 || range.Visibility == 0 ||
                (range.Offset & 3) != 0 || (range.Size & 3) != 0 ||
                (range.Visibility & ~(ShaderStage.Vertex | ShaderStage.Pixel | ShaderStage.Compute)) != 0)
                throw new ArgumentException("Push-constant ranges must be non-empty, four-byte aligned, and use valid stages.", nameof(ranges));
            rootDwords = checked(rootDwords + range.Size / 4);
            ulong end = (ulong)range.Offset + range.Size;
            for (int other = 0; other < index; other++)
            {
                PushConstantRange previous = ranges[other];
                ulong previousEnd = (ulong)previous.Offset + previous.Size;
                if (range.Offset < previousEnd && previous.Offset < end)
                    throw new ArgumentException("Push constant ranges overlap.", nameof(ranges));
            }
        }
        if (rootDwords > 64)
            throw new ArgumentException("Descriptor tables and push constants exceed the D3D12-compatible 64-DWORD root-signature budget.", nameof(ranges));
    }

    private static void ValidateShaderDesc(in ShaderDesc desc)
    {
        if (!desc.Key.IsValid) throw new ArgumentException("Shader artifact identity is required.", nameof(desc));
        if (!Enum.IsDefined(desc.Format)) throw new ArgumentException("Shader binary format is invalid.", nameof(desc));
        if (desc.Stage is not (ShaderStage.Vertex or ShaderStage.Pixel or ShaderStage.Compute))
            throw new ArgumentException("A shader must have exactly one stage.", nameof(desc));
        ArgumentException.ThrowIfNullOrWhiteSpace(desc.EntryPoint);
    }

    private static void RequireShaderStage(in ShaderDesc shader, ShaderStage required, string parameter)
    {
        if (shader.Stage != required) throw new ArgumentException($"Shader stage must be {required}.", parameter);
    }

    private BindGroupLayoutRecord RequireBindGroupLayout(BindGroupLayoutHandle handle) => _bindGroupLayouts.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private BindGroupRecord RequireBindGroup(BindGroupHandle handle) => _bindGroups.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private ShaderRecord RequireShader(ShaderHandle handle) => _shaders.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private PipelineLayoutRecord RequirePipelineLayout(PipelineLayoutHandle handle) => _pipelineLayouts.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
    private PipelineRecord RequirePipeline(PipelineHandle handle) => _pipelines.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
}
