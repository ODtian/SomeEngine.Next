namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    private readonly Dictionary<PipelineCacheEntryKey, PipelineHandle> _pipelineCache = [];
    private long _pipelineCacheHits;
    private long _pipelineCacheMisses;
    private long _pipelineCacheInvalidations;

    public PipelineStatus GetPipelineStatus(PipelineHandle pipeline)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        _ = GetPipeline(pipeline);
        return PipelineStatus.Ready;
    }

    public PipelineCacheStats GetPipelineCacheStats()
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        return new PipelineCacheStats(
            _pipelineCache.Count,
            _pipelineCacheHits,
            _pipelineCacheMisses,
            _pipelineCacheInvalidations);
    }

    public void InvalidatePipelineCache(PipelineCacheKey key)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (!key.IsValid) return;
        PipelineCacheEntryKey[] matching = _pipelineCache.Keys
            .Where(entry => entry.Key == key)
            .ToArray();
        foreach (PipelineCacheEntryKey entry in matching) _pipelineCache.Remove(entry);
        bool invalidated = matching.Length != 0;
        invalidated |= _nativePipelineLibrary.Invalidate(key);
        if (invalidated) _pipelineCacheInvalidations++;
    }

    public void InvalidateAllPipelines()
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        _pipelineCacheInvalidations += Math.Max(_pipelineCache.Count, _nativePipelineLibrary.EntryCount);
        _pipelineCache.Clear();
        _nativePipelineLibrary.Reset();
    }

    private bool TryGetCachedPipeline(PipelineCacheKey key, PipelineType type, out PipelineHandle pipeline)
    {
        if (!key.IsValid)
        {
            pipeline = default;
            return false;
        }
        if (_pipelineCache.TryGetValue(new PipelineCacheEntryKey(key, type), out pipeline))
        {
            // Validate that the generational handle is still live. DestroyPipeline eagerly
            // removes reverse mappings, so a stale entry is an invariant violation.
            _ = GetPipeline(pipeline);
            _pipelineCacheHits++;
            return true;
        }
        return false;
    }

    private void RegisterCachedPipeline(PipelineCacheKey key, PipelineType type, PipelineHandle pipeline)
    {
        if (key.IsValid) _pipelineCache.Add(new PipelineCacheEntryKey(key, type), pipeline);
    }

    private void RemoveCachedPipeline(PipelineHandle pipeline)
    {
        PipelineCacheEntryKey[] keys = _pipelineCache
            .Where(pair => pair.Value == pipeline)
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (PipelineCacheEntryKey key in keys) _pipelineCache.Remove(key);
    }

    private readonly record struct PipelineCacheEntryKey(PipelineCacheKey Key, PipelineType Type);
}
