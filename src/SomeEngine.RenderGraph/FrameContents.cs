namespace SomeEngine.RenderGraph;

internal sealed partial class FrameRecording
{
    private void ValidateFrameContents()
    {
        foreach (var resource in _resourceAccessLists.Keys)
        {
            List<int> accesses = CollectResourceAccesses(resource.Kind, resource.Index, enabledOnly: false);
            FilterLiveAccesses(accesses);
            accesses.RemoveAll(access => _passes[_accesses[access].PassIndex].SectionBoundary);
            if (accesses.Count == 0) continue;
            SortAccessesBySchedule(accesses);
            switch (resource.Kind)
            {
                case GraphAccessTargetKind.Buffer: ValidateBufferContents(_buffers[resource.Index], accesses); break;
                case GraphAccessTargetKind.Texture: ValidateTextureContents(_textures[resource.Index], accesses); break;
                case GraphAccessTargetKind.QueryPool: ValidateQueryContents(_queryPools[resource.Index], accesses); break;
                case GraphAccessTargetKind.RayTracingShaderTable:
                    if (_shaderTables[resource.Index].Contents != ResourceContentState.Defined &&
                        _accesses[accesses[0]].Mode != GraphAccessMode.Write)
                        Undefined(accesses[0], _shaderTables[resource.Index].Resource.Label);
                    break;
            }
        }
    }

    private void ValidateBufferContents(FrameBuffer buffer, List<int> accesses)
    {
        int coordinates = BuildBufferCoordinates(buffer, accesses);
        int segments = coordinates - 1;
        EnsureCapacity(ref _contentsScratch, segments);
        EnsureCapacity(ref _bufferContentScopeScratch, segments);
        Array.Clear(_contentsScratch, 0, segments);
        Array.Clear(_bufferContentScopeScratch, 0, segments);
        if (buffer.UploadData is not null)
            Array.Fill(_contentsScratch, ResourceContentState.Defined, 0, segments);
        else InitializeBufferContents(buffer, _bufferCoordinateScratch.AsSpan(0, coordinates),
            _contentsScratch.AsSpan(0, segments), _bufferContentScopeScratch.AsSpan(0, segments));
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            foreach (BufferRange range in access.BufferRanges)
            {
                int first = BinarySearch(_bufferCoordinateScratch.AsSpan(0, coordinates), range.Offset);
                int end = BinarySearch(_bufferCoordinateScratch.AsSpan(0, coordinates), range.Offset + range.Size);
                for (int segment = first; segment < end; segment++)
                {
                    if (access.Mode != GraphAccessMode.Write &&
                        !HasRequiredBufferContents(_contentsScratch[segment], _bufferContentScopeScratch[segment], access.BufferContentScope))
                        Undefined(accessIndex, buffer.Description.Label);
                    Apply(access, ref _contentsScratch[segment]);
                    if (_contentsScratch[segment] == ResourceContentState.Defined && access.Mode != GraphAccessMode.Read)
                        _bufferContentScopeScratch[segment] = access.BufferContentScope;
                }
            }
        }
    }

    private void ValidateTextureContents(FrameTexture texture, List<int> accesses)
    {
        int cells = BuildTextureCoordinates(texture, accesses);
        EnsureCapacity(ref _contentsScratch, cells);
        Array.Clear(_contentsScratch, 0, cells);
        InitializeTextureContents(texture, _contentsScratch.AsSpan(0, cells));
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            foreach (int cell in TextureRegions(texture, access.TextureRange))
            {
                if (access.Mode != GraphAccessMode.Write && _contentsScratch[cell] != ResourceContentState.Defined)
                    Undefined(accessIndex, texture.Label);
                Apply(access, ref _contentsScratch[cell]);
            }
        }
    }

    private void ValidateQueryContents(FrameQueryPool pool, List<int> accesses)
    {
        int coordinates = BuildQueryCoordinates(pool, accesses);
        EnsureCapacity(ref _contentsScratch, coordinates - 1);
        Array.Clear(_contentsScratch, 0, coordinates - 1);
        InitializeQueryContents(pool, _queryCoordinateScratch.AsSpan(0, coordinates), _contentsScratch.AsSpan(0, coordinates - 1));
        foreach (int accessIndex in accesses)
        {
            FrameResourceAccess access = _accesses[accessIndex];
            int first = BinarySearch(_queryCoordinateScratch.AsSpan(0, coordinates), access.QueryRange.FirstQuery);
            int end = BinarySearch(_queryCoordinateScratch.AsSpan(0, coordinates), access.QueryRange.FirstQuery + access.QueryRange.QueryCount);
            for (int segment = first; segment < end; segment++)
            {
                if (access.Mode != GraphAccessMode.Write && _contentsScratch[segment] != ResourceContentState.Defined)
                    Undefined(accessIndex, pool.Resource.Label);
                Apply(access, ref _contentsScratch[segment]);
            }
        }
    }

    private static void Apply(in FrameResourceAccess access, ref ResourceContentState contents)
    {
        if (access.Mode == GraphAccessMode.Read) return;
        if (access.ResultContents is { } result) contents = result;
        else if (access.Mode == GraphAccessMode.ReadWrite || access.Coverage == WriteCoverage.Complete)
            contents = ResourceContentState.Defined;
    }

    private void Undefined(int accessIndex, string? label) => throw new UndefinedResourceReadException(
        $"Pass '{_passes[_accesses[accessIndex].PassIndex].Label}' reads undefined resource '{label}'.");
}
