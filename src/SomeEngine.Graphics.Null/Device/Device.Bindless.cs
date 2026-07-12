namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    public BindlessTableHandle CreateBindlessTable(in BindlessTableDesc desc)
    {
        EnsureCoordinatorThread();
        RequireBindless();
        desc.Validate();
        if (desc.Capacity > Capabilities.Limits.MaxDescriptorArrayLength)
            throw new ArgumentOutOfRangeException(nameof(desc));
        lock (_gate)
        {
            EnsureNotDisposed();
            int capacity = checked((int)desc.Capacity);
            (uint slot, uint generation) = _bindlessTables.Allocate(new BindlessTableRecord
            {
                Desc = desc,
                Generations = new uint[capacity],
                Allocated = new bool[capacity],
                HasValue = new bool[capacity],
                Values = new BindingWrite[capacity],
            });
            return new BindlessTableHandle(_domain, slot, generation);
        }
    }

    public void DestroyBindlessTable(BindlessTableHandle table)
    {
        EnsureCoordinatorThread();
        RequireBindless();
        lock (_gate)
        {
            EnsureNotDisposed();
            BindlessTableRecord record = RequireBindlessTable(table);
            for (int index = 0; index < record.Values.Length; index++)
            {
                if (record.HasValue[index]) ReleaseBindlessValue(record.Desc.Kind, record.Values[index]);
            }
            _bindlessTables.Destroy(table.Domain, table.Slot, table.Generation);
        }
    }

    public BindlessSlot AllocateBindlessSlot(BindlessTableHandle table)
    {
        EnsureCoordinatorThread();
        RequireBindless();
        lock (_gate)
        {
            EnsureNotDisposed();
            BindlessTableRecord record = RequireBindlessTable(table);
            for (int index = 0; index < record.Allocated.Length; index++)
            {
                if (record.Allocated[index]) continue;
                record.Allocated[index] = true;
                if (record.Generations[index] == 0) record.Generations[index] = 1;
                return new BindlessSlot(table, checked((uint)index), record.Generations[index]);
            }
            throw new InvalidOperationException("The bindless table has no free slots.");
        }
    }

    public void FreeBindlessSlot(in BindlessSlot slot)
    {
        EnsureCoordinatorThread();
        RequireBindless();
        lock (_gate)
        {
            EnsureNotDisposed();
            (BindlessTableRecord record, int index) = RequireBindlessSlot(slot);
            if (record.HasValue[index]) ReleaseBindlessValue(record.Desc.Kind, record.Values[index]);
            record.Allocated[index] = false;
            record.HasValue[index] = false;
            record.Values[index] = default;
            uint generation = unchecked(record.Generations[index] + 1);
            record.Generations[index] = generation == 0 ? 1 : generation;
        }
    }

    public void WriteBindlessTexture(in BindlessSlot slot, TextureViewHandle view)
    {
        EnsureCoordinatorThread();
        RequireBindless();
        lock (_gate)
        {
            EnsureNotDisposed();
            (BindlessTableRecord record, int index) = RequireBindlessSlot(slot);
            if (record.Desc.Kind is not (BindingKind.SampledTexture or BindingKind.StorageTexture))
                throw ValidationError("The bindless table does not contain texture descriptors.");
            TextureViewRecord textureView = RequireTextureView(view);
            TextureViewUsage usage = record.Desc.Kind == BindingKind.SampledTexture
                ? TextureViewUsage.ShaderResource
                : TextureViewUsage.Storage;
            if (!textureView.Desc.Usage.HasFlag(usage))
                throw ValidationError($"The texture view lacks {usage} usage.");
            ReplaceBindlessValue(record, index, BindingWrite.Texture(0, view));
            _textureViews.AddChild(view.Domain, view.Slot, view.Generation);
        }
    }

    public void WriteBindlessBuffer(in BindlessSlot slot, BufferViewHandle view)
    {
        EnsureCoordinatorThread();
        RequireBindless();
        lock (_gate)
        {
            EnsureNotDisposed();
            (BindlessTableRecord record, int index) = RequireBindlessSlot(slot);
            if (record.Desc.Kind is not (BindingKind.ConstantBuffer or BindingKind.ReadOnlyBuffer or BindingKind.StorageBuffer))
                throw ValidationError("The bindless table does not contain buffer descriptors.");
            BufferViewRecord bufferView = RequireBufferView(view);
            if (bufferView.Desc.Kind != record.Desc.Kind)
                throw ValidationError("The buffer view kind does not match the bindless table.");
            ReplaceBindlessValue(record, index, BindingWrite.Buffer(0, view));
            _bufferViews.AddChild(view.Domain, view.Slot, view.Generation);
        }
    }

    public void WriteBindlessSampler(in BindlessSlot slot, SamplerHandle sampler)
    {
        EnsureCoordinatorThread();
        RequireBindless();
        lock (_gate)
        {
            EnsureNotDisposed();
            (BindlessTableRecord record, int index) = RequireBindlessSlot(slot);
            if (record.Desc.Kind != BindingKind.Sampler)
                throw ValidationError("The bindless table does not contain sampler descriptors.");
            _ = RequireSampler(sampler);
            ReplaceBindlessValue(record, index, BindingWrite.SamplerValue(0, sampler));
            _samplers.AddChild(sampler.Domain, sampler.Slot, sampler.Generation);
        }
    }

    private void RequireBindless()
    {
        if (!Capabilities.SupportsBindless)
            throw UnsupportedError("Bindless descriptors are not supported by this device profile.");
    }

    private BindlessTableRecord RequireBindlessTable(BindlessTableHandle handle) =>
        _bindlessTables.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;

    private (BindlessTableRecord Record, int Index) RequireBindlessSlot(in BindlessSlot slot)
    {
        BindlessTableRecord record = RequireBindlessTable(slot.Table);
        if (slot.Index >= (uint)record.Allocated.Length)
            throw new ArgumentException("The bindless slot index is outside its table.", nameof(slot));
        int index = checked((int)slot.Index);
        if (!record.Allocated[index] || record.Generations[index] != slot.Generation)
            throw new ArgumentException("The bindless slot is stale or free.", nameof(slot));
        return (record, index);
    }

    private void ReplaceBindlessValue(BindlessTableRecord record, int index, BindingWrite value)
    {
        if (record.HasValue[index]) ReleaseBindlessValue(record.Desc.Kind, record.Values[index]);
        record.Values[index] = value;
        record.HasValue[index] = true;
    }

    private void ReleaseBindlessValue(BindingKind kind, BindingWrite value)
    {
        switch (kind)
        {
            case BindingKind.SampledTexture:
            case BindingKind.StorageTexture:
                _textureViews.ReleaseChild(value.TextureView.Domain, value.TextureView.Slot, value.TextureView.Generation);
                break;
            case BindingKind.ConstantBuffer:
            case BindingKind.ReadOnlyBuffer:
            case BindingKind.StorageBuffer:
                _bufferViews.ReleaseChild(value.BufferView.Domain, value.BufferView.Slot, value.BufferView.Generation);
                break;
            case BindingKind.Sampler:
                _samplers.ReleaseChild(value.Sampler.Domain, value.Sampler.Slot, value.Sampler.Generation);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
