namespace SomeEngine.Graphics.Null;

internal sealed class HeapRecord
{
    public required HeapDesc Desc { get; init; }
    public required byte[] Storage { get; init; }
    public required PhysicalAllocationId AllocationId { get; init; }
}

internal sealed class BufferRecord
{
    public required BufferDesc Desc { get; init; }
    public required MemoryType MemoryType { get; init; }
    public required PhysicalAllocationInfo Allocation { get; init; }
    public required byte[] Storage { get; init; }
    public required int BaseOffset { get; init; }
    public HeapHandle Heap { get; init; }
    public ResourceState State { get; set; }
    public ResidencyPriority Priority { get; set; } = ResidencyPriority.Normal;
    public bool Resident { get; set; } = true;
    public bool IsMapped { get; set; }

    public Span<byte> Bytes => Storage.AsSpan(BaseOffset, checked((int)Desc.Size));
}

internal sealed class TextureRecord
{
    public required TextureDesc Desc { get; init; }
    public required MemoryType MemoryType { get; init; }
    public required PhysicalAllocationInfo Allocation { get; init; }
    public required byte[] Storage { get; init; }
    public required int BaseOffset { get; init; }
    public required ResourceState[] States { get; init; }
    public HeapHandle Heap { get; init; }
    public ResidencyPriority Priority { get; set; } = ResidencyPriority.Normal;
    public bool Resident { get; set; } = true;

    public Span<byte> Bytes => Storage.AsSpan(BaseOffset, checked((int)TextureLayout.GetByteSize(Desc)));
}

internal sealed record TextureViewRecord(TextureViewDesc Desc);
internal sealed record BufferViewRecord(BufferViewDesc Desc);
internal sealed record SamplerRecord(SamplerDesc Desc);
internal sealed record BindGroupLayoutRecord(BindingDesc[] Bindings);
internal sealed record BindGroupRecord(BindGroupLayoutHandle Layout, BindingWrite[] Writes, string? Name);
internal sealed record ShaderRecord(ShaderDesc Desc);
internal sealed record PipelineLayoutRecord(BindGroupLayoutHandle[] Groups, PushConstantRange[] PushConstants, string? Name);

internal enum PipelineKind : byte
{
    Raster,
    Compute,
}

internal sealed record PipelineRecord(
    PipelineKind Kind,
    PipelineLayoutHandle Layout,
    ShaderHandle FirstShader,
    ShaderHandle SecondShader,
    RasterPipelineDesc Raster,
    ComputePipelineDesc Compute,
    string? Name,
    PipelineStatus Status,
    PipelineCacheKey CacheKey);

internal readonly record struct PipelineCacheIdentity(
    PipelineKind Kind,
    PipelineLayoutHandle Layout,
    ShaderArtifactKey FirstShader,
    ShaderArtifactKey SecondShader);

internal sealed class QueryPoolRecord
{
    public required QueryPoolDesc Desc { get; init; }
    public required byte[][] Values { get; init; }
    public required bool[] Ready { get; init; }
}

internal sealed class SwapchainRecord
{
    public required SwapchainDesc Desc { get; set; }
    public required TextureHandle[] Images { get; set; }
    public int AcquiredImage { get; set; } = -1;
    public uint NextImage { get; set; }
}

internal sealed class BindlessTableRecord
{
    public required BindlessTableDesc Desc { get; init; }
    public required uint[] Generations { get; init; }
    public required bool[] Allocated { get; init; }
    public required bool[] HasValue { get; init; }
    public required BindingWrite[] Values { get; init; }
}

internal sealed class CommandReferences
{
    public HashSet<HeapHandle> Heaps { get; } = [];
    public HashSet<BufferHandle> Buffers { get; } = [];
    public HashSet<TextureHandle> Textures { get; } = [];
    public HashSet<TextureViewHandle> TextureViews { get; } = [];
    public HashSet<BufferViewHandle> BufferViews { get; } = [];
    public HashSet<SamplerHandle> Samplers { get; } = [];
    public HashSet<BindGroupLayoutHandle> BindGroupLayouts { get; } = [];
    public HashSet<BindGroupHandle> BindGroups { get; } = [];
    public HashSet<ShaderHandle> Shaders { get; } = [];
    public HashSet<PipelineLayoutHandle> PipelineLayouts { get; } = [];
    public HashSet<PipelineHandle> Pipelines { get; } = [];
    public HashSet<QueryPoolHandle> QueryPools { get; } = [];
}

internal sealed class CommandListRecord
{
    public required QueueType Queue { get; init; }
    public required RecordedCommand[] Commands { get; init; }
    public required CommandReferences References { get; init; }
    public required string? Name { get; init; }
    public bool ReferencesPinned { get; set; }
}
