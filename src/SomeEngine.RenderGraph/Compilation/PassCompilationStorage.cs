using System.Numerics;

namespace SomeEngine.RenderGraph;

public sealed partial class RenderGraph
{
    internal static int PassLookupCapacity(int count)
    {
        if (count == 0) return 0;
        int capacity = 4;
        int required = checked(count * 2);
        while (capacity < required) capacity = checked(capacity * 2);
        return capacity;
    }

    internal void MaterializePassCompilationStorage(
        int passOrdinal,
        in PassData pass,
        int descriptorOffset,
        int pushConstantOffset,
        int accessBucketOffset,
        int bindlessBucketOffset,
        int queryBucketOffset)
    {
        ref PassData compilation = ref Passes[passOrdinal];
        _ = descriptorOffset;
        _ = pushConstantOffset;
        compilation.DescriptorOffset = 0;
        compilation.DescriptorCount = 0;
        compilation.PushConstantOffset = 0;
        compilation.PushConstantCount = 0;
        compilation.AccessBucketOffset = accessBucketOffset;
        compilation.AccessBucketCount = PassLookupCapacity(pass.AccessCount);
        compilation.BindlessAccessBucketOffset = bindlessBucketOffset;
        compilation.BindlessAccessBucketCount = PassLookupCapacity(pass.BindlessAccessCount);
        compilation.QueryBucketOffset = queryBucketOffset;
        compilation.QueryBucketCount = PassLookupCapacity(pass.QueryAccessCount);

        BuildPassAccessIndex(pass, compilation);
        BuildPassBindlessAccessIndex(pass, compilation);
        BuildPassQueryIndex(pass, compilation);
    }

    internal bool ContainsPassAccess(int pass, in PassInputData key)
    {
        ref readonly PassData compilation = ref Passes[pass];
        if (compilation.AccessBucketCount == 0) return false;
        ReadOnlySpan<int> buckets = AccessIndexBuckets.ReadOnlySpan.Slice(
            compilation.AccessBucketOffset,
            compilation.AccessBucketCount);
        int mask = compilation.AccessBucketCount - 1;
        int bucket = HashAccess(in key) & mask;
        while (buckets[bucket] >= 0)
        {
            ref readonly PassInputData candidate =
                ref PassInputs[compilation.AccessOffset + buckets[bucket]];
            if (GraphAccessEquals(candidate, key)) return true;
            bucket = (bucket + 1) & mask;
        }
        return false;
    }

    internal bool TryGetPassBindlessAccess(
        int pass,
        DescriptorTableHandle table,
        GraphBindingType type,
        int view,
        out int ordinal)
    {
        ref readonly PassData compilation = ref Passes[pass];
        if (compilation.BindlessAccessBucketCount == 0)
        {
            ordinal = -1;
            return false;
        }
        ReadOnlySpan<int> buckets = BindlessAccessIndexBuckets.ReadOnlySpan.Slice(
            compilation.BindlessAccessBucketOffset,
            compilation.BindlessAccessBucketCount);
        int mask = compilation.BindlessAccessBucketCount - 1;
        if (table.Graph != GraphSerial)
            throw new ArgumentException(
                "The bindless table locator belongs to another graph invocation.",
                nameof(table));
        int tableOrdinal = table.Ordinal;
        _ = GetDescriptorTable(tableOrdinal);
        int bucket = HashBindlessAccess(tableOrdinal, type, view) & mask;
        while (buckets[bucket] >= 0)
        {
            int candidate = buckets[bucket];
            int access = checked(compilation.BindlessAccessOffset + candidate);
            if (GetBindlessAccessTable(access) == tableOrdinal &&
                GetBindlessAccessType(access) == type &&
                GetBindlessAccessView(access) == view)
            {
                ordinal = candidate;
                return true;
            }
            bucket = (bucket + 1) & mask;
        }
        ordinal = -1;
        return false;
    }

    internal bool ContainsPassQuery(int pass, int query)
    {
        ref readonly PassData compilation = ref Passes[pass];
        if (query < 0 || compilation.QueryBucketCount == 0) return false;
        ReadOnlySpan<int> buckets = QueryIndexBuckets.ReadOnlySpan.Slice(
            compilation.QueryBucketOffset,
            compilation.QueryBucketCount);
        int mask = compilation.QueryBucketCount - 1;
        int bucket = HashQuery(query) & mask;
        while (buckets[bucket] >= 0)
        {
            if (PassQueries[compilation.QueryAccessOffset + buckets[bucket]] == query)
                return true;
            bucket = (bucket + 1) & mask;
        }
        return false;
    }

    private void BuildPassAccessIndex(in PassData pass, in PassData compilation)
    {
        if (compilation.AccessBucketCount == 0) return;
        Span<int> buckets = AccessIndexBuckets.Span.Slice(
            compilation.AccessBucketOffset,
            compilation.AccessBucketCount);
        buckets.Fill(-1);
        int mask = compilation.AccessBucketCount - 1;
        ReadOnlySpan<PassInputData> accesses = GetPassAccesses(pass);
        for (int ordinal = 0; ordinal < accesses.Length; ordinal++)
        {
            ref readonly PassInputData access = ref accesses[ordinal];
            int bucket = HashAccess(in access) & mask;
            while (buckets[bucket] >= 0)
            {
                if (GraphAccessEquals(accesses[buckets[bucket]], access))
                    throw new InvalidOperationException("A pass contains duplicate canonical access rows.");
                bucket = (bucket + 1) & mask;
            }
            buckets[bucket] = ordinal;
        }
    }

    private void BuildPassBindlessAccessIndex(in PassData pass, in PassData compilation)
    {
        if (compilation.BindlessAccessBucketCount == 0) return;
        Span<int> buckets = BindlessAccessIndexBuckets.Span.Slice(
            compilation.BindlessAccessBucketOffset,
            compilation.BindlessAccessBucketCount);
        buckets.Fill(-1);
        int mask = compilation.BindlessAccessBucketCount - 1;
        for (int ordinal = 0; ordinal < pass.BindlessAccessCount; ordinal++)
        {
            int access = checked(pass.BindlessAccessOffset + ordinal);
            int table = GetBindlessAccessTable(access);
            GraphBindingType type = GetBindlessAccessType(access);
            int view = GetBindlessAccessView(access);
            int bucket = HashBindlessAccess(table, type, view) & mask;
            while (buckets[bucket] >= 0)
            {
                int existing = checked(
                    pass.BindlessAccessOffset + buckets[bucket]);
                if (GetBindlessAccessTable(existing) == table &&
                    GetBindlessAccessType(existing) == type &&
                    GetBindlessAccessView(existing) == view)
                {
                    throw new InvalidOperationException(
                        "A pass contains duplicate bindless access rows.");
                }
                bucket = (bucket + 1) & mask;
            }
            buckets[bucket] = ordinal;
        }
    }

    private void BuildPassQueryIndex(in PassData pass, in PassData compilation)
    {
        if (compilation.QueryBucketCount == 0) return;
        Span<int> buckets = QueryIndexBuckets.Span.Slice(
            compilation.QueryBucketOffset,
            compilation.QueryBucketCount);
        buckets.Fill(-1);
        int mask = compilation.QueryBucketCount - 1;
        ReadOnlySpan<int> queries = GetPassQueries(pass);
        for (int ordinal = 0; ordinal < queries.Length; ordinal++)
        {
            int query = queries[ordinal];
            int bucket = HashQuery(query) & mask;
            while (buckets[bucket] >= 0) bucket = (bucket + 1) & mask;
            buckets[bucket] = ordinal;
        }
    }

    private static bool GraphAccessEquals(in PassInputData left, in PassInputData right)
    {
        if (left.Flags != right.Flags ||
            left.Resource != right.Resource ||
            left.View != right.View)
        {
            return false;
        }
        return left.IsBuffer
            ? left.State == right.State && left.BufferRange == right.BufferRange
            : left.State == right.State && left.TextureRange == right.TextureRange;
    }

    private static int HashAccess(in PassInputData access)
    {
        uint hash = 2166136261u;
        Add(ref hash, (uint)access.Flags);
        Add(ref hash, unchecked((uint)access.Resource));
        Add(ref hash, unchecked((uint)access.View));
        if (access.IsBuffer)
        {
            Add(ref hash, (uint)access.State);
            Add(ref hash, unchecked((uint)access.BufferRange.Offset));
            Add(ref hash, unchecked((uint)(access.BufferRange.Offset >> 32)));
            Add(ref hash, unchecked((uint)access.BufferRange.Size));
            Add(ref hash, unchecked((uint)(access.BufferRange.Size >> 32)));
        }
        else
        {
            Add(ref hash, (uint)access.State);
            Add(ref hash, unchecked((uint)access.TextureRange.GetHashCode()));
        }
        return unchecked((int)(hash ^ (hash >> 16)));
    }

    private static void Add(ref uint hash, uint value)
    {
        hash ^= value;
        hash *= 16777619u;
    }

    private static int HashBindlessAccess(int table, GraphBindingType type, int view)
    {
        uint value = unchecked((uint)table) * 0x9E37_79B9u;
        value = BitOperations.RotateLeft(value ^ (uint)type, 13) * 0x85EB_CA6Bu;
        value = BitOperations.RotateLeft(value ^ unchecked((uint)view), 11) * 0xC2B2_AE35u;
        return unchecked((int)(value ^ (value >> 16)));
    }

    private static int HashQuery(int query)
    {
        uint value = unchecked((uint)query) * 0x9E37_79B9u;
        return unchecked((int)(value ^ (value >> 16)));
    }
}

public sealed partial class RenderGraph
{
    internal PassInputData CreateAccessKey(
        BufferHandle buffer,
        GraphResourceUsage state,
        GraphAccess flags,
        BufferRange? requestedRange)
    {
        int resource = ResolveBuffer(buffer);
        ulong size = GetBufferDescription(resource).Size;
        BufferRange range = AccessNormalizer.NormalizeBuffer(
            size,
            requestedRange ?? new BufferRange(0, size));
        ValidateBufferEffect(flags, state);
        return new PassInputData(
            resource,
            -1,
            flags,
            state,
            range);
    }

    internal PassInputData CreateAccessKey(
        TextureHandle textureHandle,
        GraphResourceUsage state,
        GraphAccess flags,
        TextureSubresourceRange? requestedRange)
    {
        int resource = ResolveTexture(textureHandle);
        if (state is GraphResourceUsage.RenderTarget or
            GraphResourceUsage.DepthRead or
            GraphResourceUsage.DepthWrite)
        {
            throw new ArgumentException(
                "Rendering attachments require SetRenderAttachment or SetRenderAttachmentDepth.",
                nameof(state));
        }
        GraphTextureDescription texture = GetTextureDescription(resource);
        TextureSubresourceRange range = AccessNormalizer.NormalizeTexture(
            texture,
            requestedRange ?? new TextureSubresourceRange(
                0,
                checked((uint)texture.MipLevels),
                0,
                checked((uint)texture.ArrayLayers),
                GraphFormat.AllowedAspects(texture.Format)));
        ValidateTextureEffect(flags, state);
        return new PassInputData(
            resource,
            -1,
            flags,
            state,
            range);
    }

    internal PassInputData CreateAccessKey(
        BufferViewHandle handle,
        GraphAccess flags)
    {
        int view = ValidateBufferView(handle);
        GraphBindingType type = _bufferViewTypes[view];
        GraphResourceUsage use = type switch
        {
            GraphBindingType.ConstantBuffer => GraphResourceUsage.VertexOrConstantBuffer,
            GraphBindingType.ReadOnlyBuffer => GraphResourceUsage.ShaderResource,
            GraphBindingType.StorageBuffer => GraphResourceUsage.UnorderedAccess,
            _ => throw new ArgumentException(
                "The buffer view type cannot be used by a pass.",
                nameof(handle)),
        };
        ValidateViewEffect(flags, type, nameof(handle));
        return new PassInputData(
            _bufferViewResources[view],
            view,
            flags,
            use,
            _bufferViewRanges[view]);
    }

    internal PassInputData CreateAccessKey(
        TextureViewHandle handle,
        GraphAccess flags)
    {
        int view = ValidateTextureView(handle);
        GraphTextureViewUsage usage = _textureViewUsages[view];
        GraphBindingType type;
        GraphResourceUsage use;
        if ((flags & GraphAccess.ReadWrite) == GraphAccess.Read &&
            (usage & GraphTextureViewUsage.ShaderResource) != 0)
        {
            type = GraphBindingType.SampledTexture;
            use = GraphResourceUsage.ShaderResource;
        }
        else if ((usage & GraphTextureViewUsage.Storage) != 0)
        {
            type = GraphBindingType.StorageTexture;
            use = GraphResourceUsage.UnorderedAccess;
        }
        else
        {
            throw new ArgumentException(
                "The texture view usage cannot satisfy this access.",
                nameof(handle));
        }
        ValidateViewEffect(flags, type, nameof(handle));
        return new PassInputData(
            _textureViewResources[view],
            view,
            flags,
            use,
            _textureViewRanges[view]);
    }

    internal PassInputData CreateAccelerationStructureAccessKey(
        AccelerationStructureHandle accelerationStructure)
    {
        int ordinal = ValidateAccelerationStructure(accelerationStructure);
        return new PassInputData(
            _accelerationStructureBuffers[ordinal],
            ordinal,
            GraphAccess.Read,
            GraphResourceUsage.AccelerationStructure,
            _accelerationStructureRanges[ordinal]);
    }

    internal int GetPassDescriptorCount(int pass)
    {
        _ = pass;
        return 0;
    }

    internal int GetPassPushConstantCount(int pass)
    {
        _ = pass;
        return 0;
    }
}
