namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private void CommitPassState(int pass, QueueCompletion completion)
    {
        foreach (int accessIndex in _passAccesses[pass])
        {
            FrameResourceAccess access = _accesses[accessIndex];
            switch (access.TargetKind)
            {
                case GraphAccessTargetKind.Buffer:
                    CommitBuffer(access, completion);
                    break;
                case GraphAccessTargetKind.Texture:
                    CommitTexture(access, completion);
                    break;
                case GraphAccessTargetKind.QueryPool:
                    CommitQueryPool(access, completion);
                    break;
                case GraphAccessTargetKind.RayTracingShaderTable:
                    CommitShaderTable(access, completion);
                    break;
            }
        }
    }

    private void CommitBuffer(in FrameResourceAccess access, QueueCompletion completion)
    {
        FrameBuffer frameBuffer = _buffers[access.ResourceIndex];
        BufferBoundaryState[] current = frameBuffer.Lifetime == RenderGraphResourceLifetime.Persistent
            ? frameBuffer.Definition?.BoundaryStates ?? frameBuffer.EntryBoundaryStates ?? []
            : frameBuffer.EntryBoundaryStates ?? [];
        ResourceContentState contents = access.ResultContents ?? access.Mode switch
        {
            GraphAccessMode.Read => ResourceContentState.Defined,
            GraphAccessMode.ReadWrite => ResourceContentState.Defined,
            GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete => ResourceContentState.Defined,
            _ => FindBufferContents(current, access.BufferRange),
        };
        var endpoint = new BufferBoundaryState(
            access.BufferRange,
            access.Sync,
            access.Access,
            contents,
            _passes[access.PassIndex].Queue,
            completion);
        BufferBoundaryState[] boundaryStates = UpdateBufferBoundaryStates(
            current,
            endpoint,
            access.Mode == GraphAccessMode.Read);
        if (frameBuffer.Lifetime == RenderGraphResourceLifetime.Persistent &&
            frameBuffer.Definition is { } resource)
        {
            resource.BoundaryStates = boundaryStates;
        }
        else
        {
            frameBuffer.EntryBoundaryStates = boundaryStates;
            _buffers[access.ResourceIndex] = frameBuffer;
            if (frameBuffer.Lifetime == RenderGraphResourceLifetime.PerFrame &&
                frameBuffer.Resource is not null)
            {
                _frame.Slot.TransientResources?.StoreCachedBufferStates(
                    frameBuffer.Identity,
                    frameBuffer.Resource,
                    boundaryStates);
            }
        }
    }

    private void CommitTexture(in FrameResourceAccess access, QueueCompletion completion)
    {
        FrameTexture frameTexture = _textures[access.ResourceIndex];
        TextureBoundaryState[] current = frameTexture.Lifetime == RenderGraphResourceLifetime.Persistent
            ? frameTexture.Definition?.BoundaryStates ?? frameTexture.EntryBoundaryStates ?? []
            : frameTexture.EntryBoundaryStates ?? [];
        ResourceContentState contents = access.ResultContents ?? access.Mode switch
        {
            GraphAccessMode.Read => ResourceContentState.Defined,
            GraphAccessMode.ReadWrite => ResourceContentState.Defined,
            GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete => ResourceContentState.Defined,
            _ => FindTextureContents(current, access.TextureRange),
        };
        var endpoint = new TextureBoundaryState(
            access.TextureRange,
            access.Sync,
            access.Access,
            access.TextureLayout,
            contents,
            _passes[access.PassIndex].Queue,
            completion);
        TextureBoundaryState[] boundaryStates = UpdateTextureBoundaryStates(
            current,
            endpoint,
            access.Mode == GraphAccessMode.Read);
        if (frameTexture.Lifetime == RenderGraphResourceLifetime.Persistent &&
            frameTexture.Definition is { } resource)
        {
            resource.BoundaryStates = boundaryStates;
        }
        else
        {
            frameTexture.EntryBoundaryStates = boundaryStates;
            _textures[access.ResourceIndex] = frameTexture;
            if (frameTexture.Lifetime == RenderGraphResourceLifetime.PerFrame &&
                frameTexture.Resource is not null)
            {
                _frame.Slot.TransientResources?.StoreCachedTextureStates(
                    frameTexture.Identity,
                    frameTexture.Resource,
                    boundaryStates);
            }
        }
    }

    private void CommitQueryPool(in FrameResourceAccess access, QueueCompletion completion)
    {
        FrameQueryPool framePool = _queryPools[access.ResourceIndex];
        QueryBoundaryState[] current = framePool.Definition.BoundaryStates;
        ResourceContentState contents = access.ResultContents ?? access.Mode switch
        {
            GraphAccessMode.Read => FindQueryContents(current, access.QueryRange),
            GraphAccessMode.ReadWrite => ResourceContentState.Defined,
            GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete =>
                ResourceContentState.Defined,
            _ => FindQueryContents(current, access.QueryRange),
        };
        var endpoint = new QueryBoundaryState(
            access.QueryRange,
            contents,
            _passes[access.PassIndex].Queue,
            completion);
        QueryBoundaryState[] boundaryStates = UpdateQueryEndpoints(
            current,
            endpoint,
            access.Mode == GraphAccessMode.Read);
        framePool.Definition.BoundaryStates = boundaryStates;
        framePool.EntryBoundaryStates = boundaryStates;
        _queryPools[access.ResourceIndex] = framePool;
    }

    private void CommitShaderTable(in FrameResourceAccess access, QueueCompletion completion)
    {
        FrameRayTracingShaderTable frameTable = _shaderTables[access.ResourceIndex];
        RayTracingShaderTableBoundaryState[] current = frameTable.Definition.BoundaryStates;
        ResourceContentState contents = access.ResultContents ?? access.Mode switch
        {
            GraphAccessMode.Read => ResolveShaderTableContents(current),
            GraphAccessMode.ReadWrite => ResourceContentState.Defined,
            GraphAccessMode.Write when access.Coverage == WriteCoverage.Complete =>
                ResourceContentState.Defined,
            _ => ResolveShaderTableContents(current),
        };
        var endpoint = new RayTracingShaderTableBoundaryState(
            contents,
            _passes[access.PassIndex].Queue,
            completion);
        RayTracingShaderTableBoundaryState[] boundaryStates = UpdateShaderTableEndpoints(
            current,
            endpoint,
            access.Mode == GraphAccessMode.Read);
        frameTable.Definition.BoundaryStates = boundaryStates;
        frameTable.EntryBoundaryStates = boundaryStates;
        _shaderTables[access.ResourceIndex] = frameTable;
    }

    private static BufferBoundaryState[] UpdateBufferBoundaryStates(
        BufferBoundaryState[] current,
        in BufferBoundaryState update,
        bool reader)
    {
        if (current.Length == 1 && current[0].Range == update.Range)
        {
            BufferBoundaryState existing = current[0];
            bool distinctQueueReaders = reader &&
                !ResourceAccessRules.Writes(existing.Access) &&
                existing.Queue is not null &&
                update.Queue is not null &&
                !ReferenceEquals(existing.Queue, update.Queue);
            if (!distinctQueueReaders)
            {
                current[0] = update;
                return current;
            }
        }

        var result = new List<BufferBoundaryState>(current.Length + 2);
        ulong updateStart = update.Range.Offset;
        ulong updateEnd = update.Range.Offset + update.Range.Size;
        foreach (BufferBoundaryState existing in current)
        {
            BufferRange range = existing.Range;
            ulong start = range.Offset;
            ulong end = range.Offset + range.Size;
            if (end <= updateStart || start >= updateEnd)
            {
                result.Add(existing);
                continue;
            }
            bool existingReader = !ResourceAccessRules.Writes(existing.Access);
            if (reader && existingReader && existing.Queue is not null && update.Queue is not null)
            {
                if (!ReferenceEquals(existing.Queue, update.Queue)) result.Add(existing);
                continue;
            }
            if (start < updateStart)
                result.Add(existing with { Range = new BufferRange(start, updateStart - start) });
            if (end > updateEnd)
                result.Add(existing with { Range = new BufferRange(updateEnd, end - updateEnd) });
        }
        result.Add(update);
        result.Sort(static (left, right) => left.Range.Offset.CompareTo(right.Range.Offset));
        return result.ToArray();
    }

    private static QueryBoundaryState[] UpdateQueryEndpoints(
        QueryBoundaryState[] current,
        in QueryBoundaryState update,
        bool reader)
    {
        var result = new List<QueryBoundaryState>(current.Length + 2);
        uint updateStart = update.Range.FirstQuery;
        uint updateEnd = checked(updateStart + update.Range.QueryCount);
        foreach (QueryBoundaryState existing in current)
        {
            uint start = existing.Range.FirstQuery;
            uint end = checked(start + existing.Range.QueryCount);
            if (end <= updateStart || start >= updateEnd)
            {
                result.Add(existing);
                continue;
            }
            if (reader && !ReferenceEquals(existing.Queue, update.Queue))
            {
                result.Add(existing);
                continue;
            }
            if (start < updateStart)
            {
                result.Add(existing with
                {
                    Range = new QueryRange(start, updateStart - start),
                });
            }
            if (end > updateEnd)
            {
                result.Add(existing with
                {
                    Range = new QueryRange(updateEnd, end - updateEnd),
                });
            }
        }
        result.Add(update);
        result.Sort(static (left, right) =>
            left.Range.FirstQuery.CompareTo(right.Range.FirstQuery));
        return result.ToArray();
    }

    private static RayTracingShaderTableBoundaryState[] UpdateShaderTableEndpoints(
        RayTracingShaderTableBoundaryState[] current,
        in RayTracingShaderTableBoundaryState update,
        bool reader)
    {
        if (!reader) return [update];
        var result = new List<RayTracingShaderTableBoundaryState>(current.Length + 1);
        foreach (RayTracingShaderTableBoundaryState existing in current)
        {
            if (!ReferenceEquals(existing.Queue, update.Queue))
                result.Add(existing);
        }
        result.Add(update);
        return result.ToArray();
    }

    private static TextureBoundaryState[] UpdateTextureBoundaryStates(
        TextureBoundaryState[] current,
        in TextureBoundaryState update,
        bool reader)
    {
        var result = new List<TextureBoundaryState>(current.Length + 8);
        foreach (TextureBoundaryState existing in current)
        {
            if (!Overlaps(existing.Range, update.Range))
            {
                result.Add(existing);
                continue;
            }

            bool existingReader = !ResourceAccessRules.Writes(existing.Access);
            if (reader && existingReader && !ReferenceEquals(existing.Queue, update.Queue))
            {
                // Distinct Queue readers coexist on the overlapping subresources.
                result.Add(existing);
                continue;
            }

            Subtract(existing, update.Range, result);
        }
        result.Add(update);
        result.Sort(static (left, right) =>
        {
            int aspect = left.Range.Aspects.CompareTo(right.Range.Aspects);
            if (aspect != 0) return aspect;
            int layer = left.Range.FirstArrayLayer.CompareTo(right.Range.FirstArrayLayer);
            return layer != 0
                ? layer
                : left.Range.FirstMipLevel.CompareTo(right.Range.FirstMipLevel);
        });
        return result.ToArray();
    }

    private static void Subtract(
        in TextureBoundaryState existing,
        in TextureSubresourceRange removed,
        List<TextureBoundaryState> destination)
    {
        TextureBoundaryState endpoint = existing;
        TextureSubresourceRange source = existing.Range;
        TextureAspects untouchedAspects = source.Aspects & ~removed.Aspects;
        AddRange(source.FirstMipLevel, source.MipLevelCount,
            source.FirstArrayLayer, source.ArrayLayerCount, untouchedAspects);

        TextureAspects overlappingAspects = source.Aspects & removed.Aspects;
        if (overlappingAspects == TextureAspects.None) return;

        uint sourceMipEnd = checked(source.FirstMipLevel + source.MipLevelCount);
        uint sourceLayerEnd = checked(source.FirstArrayLayer + source.ArrayLayerCount);
        uint removedMipStart = Math.Max(source.FirstMipLevel, removed.FirstMipLevel);
        uint removedMipEnd = Math.Min(sourceMipEnd,
            checked(removed.FirstMipLevel + removed.MipLevelCount));
        uint removedLayerStart = Math.Max(source.FirstArrayLayer, removed.FirstArrayLayer);
        uint removedLayerEnd = Math.Min(sourceLayerEnd,
            checked(removed.FirstArrayLayer + removed.ArrayLayerCount));

        AddRange(source.FirstMipLevel, source.MipLevelCount,
            source.FirstArrayLayer, removedLayerStart - source.FirstArrayLayer,
            overlappingAspects);
        AddRange(source.FirstMipLevel, source.MipLevelCount,
            removedLayerEnd, sourceLayerEnd - removedLayerEnd,
            overlappingAspects);
        AddRange(source.FirstMipLevel, removedMipStart - source.FirstMipLevel,
            removedLayerStart, removedLayerEnd - removedLayerStart,
            overlappingAspects);
        AddRange(removedMipEnd, sourceMipEnd - removedMipEnd,
            removedLayerStart, removedLayerEnd - removedLayerStart,
            overlappingAspects);
        return;

        void AddRange(
            uint firstMip,
            uint mipCount,
            uint firstLayer,
            uint layerCount,
            TextureAspects aspects)
        {
            if (mipCount == 0 || layerCount == 0 || aspects == TextureAspects.None) return;
            destination.Add(endpoint with
            {
                Range = new TextureSubresourceRange(
                    firstMip,
                    mipCount,
                    firstLayer,
                    layerCount,
                    aspects),
            });
        }
    }

    private static ResourceContentState FindQueryContents(
        QueryBoundaryState[] boundaryStates,
        in QueryRange range)
    {
        uint cursor = range.FirstQuery;
        uint end = checked(range.FirstQuery + range.QueryCount);
        while (cursor < end)
        {
            uint coveredUntil = cursor;
            ResourceContentState contents = ResourceContentState.Undefined;
            foreach (QueryBoundaryState endpoint in boundaryStates)
            {
                uint endpointStart = endpoint.Range.FirstQuery;
                uint endpointEnd = checked(
                    endpoint.Range.FirstQuery + endpoint.Range.QueryCount);
                if (endpointStart > cursor || endpointEnd <= cursor) continue;
                if (endpoint.Contents != ResourceContentState.Defined)
                    return ResourceContentState.Undefined;
                if (endpointEnd > coveredUntil)
                {
                    coveredUntil = endpointEnd;
                    contents = endpoint.Contents;
                }
            }
            if (contents != ResourceContentState.Defined || coveredUntil == cursor)
                return ResourceContentState.Undefined;
            cursor = Math.Min(coveredUntil, end);
        }
        return ResourceContentState.Defined;
    }

    private static ResourceContentState FindBufferContents(
        BufferBoundaryState[] boundaryStates,
        in BufferRange range)
    {
        foreach (BufferBoundaryState endpoint in boundaryStates)
            if (Contains(endpoint.Range, range)) return endpoint.Contents;
        return ResourceContentState.Undefined;
    }

    private static ResourceContentState FindTextureContents(
        TextureBoundaryState[] boundaryStates,
        in TextureSubresourceRange range)
    {
        foreach (TextureBoundaryState endpoint in boundaryStates)
            if (Contains(endpoint.Range, range)) return endpoint.Contents;
        return ResourceContentState.Undefined;
    }

    private static bool Overlaps(
        in TextureSubresourceRange left,
        in TextureSubresourceRange right)
    {
        bool mip = left.FirstMipLevel < right.FirstMipLevel + right.MipLevelCount &&
                   right.FirstMipLevel < left.FirstMipLevel + left.MipLevelCount;
        bool layer = left.FirstArrayLayer < right.FirstArrayLayer + right.ArrayLayerCount &&
                     right.FirstArrayLayer < left.FirstArrayLayer + left.ArrayLayerCount;
        return mip && layer && (left.Aspects & right.Aspects) != 0;
    }
}

