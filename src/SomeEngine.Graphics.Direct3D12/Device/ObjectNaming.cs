using Vortice.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    public void SetName(HeapHandle heap, string? name) =>
        SetNativeName(_heaps.Get(heap.Domain, heap.Slot, heap.Generation, "heap"), name, static value => value.Heap);

    public void SetName(BufferHandle buffer, string? name) =>
        SetNativeName(GetBuffer(buffer), name, static value => value.Resource);

    public void SetName(TextureHandle texture, string? name) =>
        SetNativeName(GetTexture(texture), name, static value => value.Resource);

    public void SetName(TextureViewHandle view, string? name) =>
        SetLogicalName(_textureViews.Get(view.Domain, view.Slot, view.Generation, "texture view"), name);

    public void SetName(BufferViewHandle view, string? name) =>
        SetLogicalName(_bufferViews.Get(view.Domain, view.Slot, view.Generation, "buffer view"), name);

    public void SetName(SamplerHandle sampler, string? name) =>
        SetLogicalName(_samplers.Get(sampler.Domain, sampler.Slot, sampler.Generation, "sampler"), name);

    public void SetName(BindGroupLayoutHandle layout, string? name) =>
        SetLogicalName(_bindGroupLayouts.Get(layout.Domain, layout.Slot, layout.Generation, "bind-group layout"), name);

    public void SetName(BindGroupHandle group, string? name) =>
        SetLogicalName(_bindGroups.Get(group.Domain, group.Slot, group.Generation, "bind group"), name);

    public void SetName(ShaderHandle shader, string? name) =>
        SetLogicalName(_shaders.Get(shader.Domain, shader.Slot, shader.Generation, "shader"), name);

    public void SetName(PipelineLayoutHandle layout, string? name) =>
        SetNativeName(GetPipelineLayout(layout), name, static value => value.RootSignature);

    public void SetName(PipelineHandle pipeline, string? name) =>
        SetNativeName(GetPipeline(pipeline), name, static value => value.PipelineState);

    public void SetName(CommandListHandle commandList, string? name)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        ValidateObjectName(name);
        RecordedCommand command = _commands.Get(
            commandList.Domain, commandList.Slot, commandList.Generation, "command list");
        command.Allocation.Name = name;
        command.Allocation.List.Name = name ?? string.Empty;
        command.Allocation.Allocator.Name = name is null ? string.Empty : $"{name}.allocator";
    }

    public void SetName(QueryPoolHandle pool, string? name) =>
        SetNativeName(GetQueryPool(pool), name, static value => value.Heap);

    public void SetName(SwapchainHandle swapchain, string? name)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        ValidateObjectName(name);
        NativeSwapchain native = GetSwapchain(swapchain);
        native.LogicalName = name;
        native.Swapchain.DebugName = name ?? string.Empty;
    }

    public void SetName(BindlessTableHandle table, string? name) =>
        throw new NotSupportedException(
            "No bindless-table handle can exist while the optional bindless profile is disabled.");

    private void SetLogicalName(NativeLifetime value, string? name)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        ValidateObjectName(name);
        value.SetLogicalName(name);
    }

    private void SetNativeName<T>(T value, string? name, Func<T, ID3D12Object> native)
        where T : NativeLifetime
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        ApplyObjectName(value, native(value), name);
    }

    private static void ApplyObjectName(NativeLifetime value, ID3D12Object native, string? name)
    {
        ValidateObjectName(name);
        value.SetLogicalName(name);
        native.Name = name ?? string.Empty;
    }

    private static void ApplyLogicalName(NativeLifetime value, string? name)
    {
        ValidateObjectName(name);
        value.SetLogicalName(name);
    }

    private static void ValidateObjectName(string? name)
    {
        if (name is not null) ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }
}
