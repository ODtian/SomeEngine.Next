namespace SomeEngine.Graphics.Null;

internal sealed partial class CommandContext
{
    public void ResetQueryPool(QueryPoolHandle pool, uint firstQuery, uint queryCount)
    {
        EnterRecording();
        RequireOutsideRendering(nameof(ResetQueryPool));
        _device.ValidateQueryRangeForRecording(pool, firstQuery, queryCount);
        if (_activeQueries.Any(active => active.Pool == pool &&
            active.Index >= firstQuery && active.Index < checked(firstQuery + queryCount)))
            throw _device.ValidationError("An active query cannot be reset.");
        _references.QueryPools.Add(pool);
        _commands.Add(new ResetQueryPoolCommand(pool, firstQuery, queryCount));
    }

    public void BeginQuery(QueryPoolHandle pool, uint queryIndex)
    {
        EnterRecording();
        QueryType type = _device.GetQueryTypeForRecording(pool, queryIndex);
        if (type == QueryType.Timestamp)
            throw _device.ValidationError("Timestamp queries are written, not begun.");
        if (type == QueryType.Occlusion && !_rendering)
            throw _device.ValidationError("Occlusion queries require an open rendering scope.");
        if (type == QueryType.PipelineStatistics && _pipelineKind is null)
            throw _device.ValidationError("Pipeline-statistics queries require a bound pipeline.");
        if (!_activeQueries.Add((pool, queryIndex)))
            throw _device.ValidationError("The query is already active in this command context.");
        _references.QueryPools.Add(pool);
        _commands.Add(new BeginQueryCommand(pool, queryIndex));
    }

    public void EndQuery(QueryPoolHandle pool, uint queryIndex)
    {
        EnterRecording();
        _ = _device.GetQueryTypeForRecording(pool, queryIndex);
        if (!_activeQueries.Remove((pool, queryIndex)))
            throw _device.ValidationError("EndQuery requires a matching BeginQuery in this command context.");
        _references.QueryPools.Add(pool);
        _commands.Add(new EndQueryCommand(pool, queryIndex));
    }

    public void WriteTimestamp(QueryPoolHandle pool, uint queryIndex)
    {
        EnterRecording();
        if (Queue == QueueType.Copy || _rendering)
            throw _device.ValidationError("WriteTimestamp requires a graphics/compute queue outside rendering.");
        if (_device.GetQueryTypeForRecording(pool, queryIndex) != QueryType.Timestamp)
            throw _device.ValidationError("WriteTimestamp requires a timestamp query pool.");
        _references.QueryPools.Add(pool);
        _commands.Add(new WriteTimestampCommand(pool, queryIndex));
    }

    public void ResolveQueryPool(
        QueryPoolHandle pool,
        uint firstQuery,
        uint queryCount,
        BufferHandle destination,
        ulong destinationOffset,
        ulong destinationStride = 0)
    {
        EnterRecording();
        RequireOutsideRendering(nameof(ResolveQueryPool));
        ulong normalizedStride = _device.ValidateQueryResolveForRecording(
            pool, firstQuery, queryCount, destination, destinationOffset, destinationStride);
        if (_activeQueries.Any(active => active.Pool == pool &&
            active.Index >= firstQuery && active.Index < checked(firstQuery + queryCount)))
            throw _device.ValidationError("An active query cannot be resolved.");
        _references.QueryPools.Add(pool);
        _references.Buffers.Add(destination);
        _commands.Add(new ResolveQueryPoolCommand(
            pool, firstQuery, queryCount, destination, destinationOffset, normalizedStride));
    }

    public void PushDebugGroup(string name)
    {
        EnterRecording();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _debugDepth++;
        _commands.Add(new PushDebugGroupCommand(name));
    }

    public void PopDebugGroup()
    {
        EnterRecording();
        if (_debugDepth == 0) throw _device.ValidationError("No debug group is open.");
        _debugDepth--;
        _commands.Add(new PopDebugGroupCommand());
    }

    public void InsertDebugMarker(string name)
    {
        EnterRecording();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _commands.Add(new InsertDebugMarkerCommand(name));
    }

    public CommandListHandle Finish()
    {
        EnterRecording();
        if (_rendering) throw _device.ValidationError("Finish rejected an unclosed rendering scope.");
        if (_debugDepth != 0) throw _device.ValidationError("Finish rejected unclosed debug groups.");
        if (_activeQueries.Count != 0) throw _device.ValidationError("Finish rejected unclosed queries.");
        if (_finished) throw _device.ValidationError("A command context can be finished only once.");
        _finished = true;
        return _device.PublishCommandList(Queue, _commands.ToArray(), _references, _name);
    }

    public void Dispose()
    {
        if (_disposed) return;
        BindOrValidateThread();
        _disposed = true;
        if (!_finished) _commands.Clear();
    }

    private void EnterRecording()
    {
        _device.EnsureAvailableForContext();
        BindOrValidateThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw _device.ValidationError("The command context has already been finished.");
    }

    private void BindOrValidateThread()
    {
        int current = Environment.CurrentManagedThreadId;
        int owner = Volatile.Read(ref _ownerThreadId);
        if (owner == 0)
        {
            owner = Interlocked.CompareExchange(ref _ownerThreadId, current, 0);
            if (owner == 0) owner = current;
        }
        if (owner != current)
        {
            throw _device.ValidationError($"Command context is owned by managed thread {owner}, not {current}.");
        }
    }

    private void RequireOutsideRendering(string operation)
    {
        if (_rendering) throw _device.ValidationError($"{operation} is not permitted inside a rendering scope.");
    }

    private void AddResourceReference(ResourceHandle resource)
    {
        switch (resource.Kind)
        {
            case ResourceKind.Buffer:
                _references.Buffers.Add(new BufferHandle(resource.Domain, resource.Slot, resource.Generation));
                break;
            case ResourceKind.Texture:
                _references.Textures.Add(new TextureHandle(resource.Domain, resource.Slot, resource.Generation));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(resource));
        }
    }

    private void AddIndirectReferences(
        BufferHandle argumentBuffer,
        ulong argumentOffset,
        uint maxCommandCount,
        uint commandStride,
        uint argumentSize,
        BufferHandle countBuffer,
        ulong countBufferOffset)
    {
        _device.ValidateIndirectForRecording(
            argumentBuffer,
            argumentOffset,
            maxCommandCount,
            commandStride,
            argumentSize,
            countBuffer,
            countBufferOffset);
        _references.Buffers.Add(argumentBuffer);
        if (countBuffer != default) _references.Buffers.Add(countBuffer);
    }
}
