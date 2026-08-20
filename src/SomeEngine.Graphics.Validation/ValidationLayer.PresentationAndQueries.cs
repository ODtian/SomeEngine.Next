namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    public Swapchain CreateSwapchain(Device device, in SwapchainDesc desc)
    {
        RequireDevice(device);
        lock (_gate)
        {
            if (!_surfaces.Contains(desc.Surface))
                Reject("Ownership", "Swapchain Surface was not created through this Validation Layer.");
        }
        if (desc.Surface.IsDisposed)
            Reject("Lifetime", "Swapchain Surface is disposed.", desc.Surface.Label);
        SwapchainDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            Swapchain? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateSwapchain(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image)
    {
        _ = Timeouts.ToMilliseconds(options.Timeout, nameof(options));
        if (options.PreserveContents)
        {
            throw new NotSupportedException(
                "The selected presentation backend does not advertise preserved back-buffer contents.");
        }
        Require(swapchain);
        var imageInfo = new ValidationObjectInfo(swapchain);
        var imageState = new ResourceValidationState(buffer: false);
        SwapchainAcquireStatus status;
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _resourceStates.EnsureAdditionalCapacity();
            status = Backend.Acquire(swapchain, options, out image);
            if (status == SwapchainAcquireStatus.Success &&
                !_objects.TryGetValue(image.Texture, out _))
            {
                bool objectAdded = false;
                bool stateAdded = false;
                try
                {
                    imageState.Bind(image.Texture);
                    _objects.Add(image.Texture, imageInfo);
                    objectAdded = true;
                    _resourceStates.Add(image.Texture, imageState);
                    stateAdded = true;
                }
                catch
                {
                    if (stateAdded)
                        _resourceStates.Remove(image.Texture);
                    if (objectAdded)
                        _objects.Remove(image.Texture);
                    throw;
                }
            }
        }
        if (status == SwapchainAcquireStatus.Success)
            ResetAcquiredTextureState(image);
        return status;
    }

    public PresentStatus Present(Queue queue, in SwapchainImage image)
    {
        RequireQueue(queue);
        Swapchain swapchain = image.Swapchain;
        Require(swapchain);
        RequireSameDevice(queue.Device, swapchain.Device, "SwapchainImage");
        RequirePresentationQueue(queue, swapchain);
        ValidatePresentTextureState(queue, image.Texture);
        return Backend.Present(queue, image);
    }

    private void RequirePresentationQueue(Queue queue, Swapchain swapchain)
    {
        Queue presentationQueue = Backend.GetQueue(
            swapchain.Device,
            QueueType.Graphics,
            0);
        if (!ReferenceEquals(queue, presentationQueue))
        {
            Reject(
                "Presentation",
                "SwapchainImage submission and presentation require the Graphics Queue that owns the native swapchain.",
                swapchain.Label);
        }
    }

    public ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config)
    {
        Require(swapchain);
        return Backend.Reconfigure(swapchain, config);
    }

    public QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc)
    {
        RequireDevice(device);
        DeviceValidationState deviceState = _deviceStates.GetValue(
            device,
            static _ => throw new InvalidOperationException(
                "The Device has no Validation node metadata."));
        uint nodeIndex = deviceState.ResolveNodeIndex(desc.NodeIndex, nameof(desc));
        QueryPoolDesc createDesc = desc with { NodeIndex = nodeIndex };
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            QueryPool? result = null;
            bool objectAdded = false;
            try
            {
                result = Backend.CreateQueryPool(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                return result;
            }
            catch
            {
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public void BeginQuery(CommandContext context, QueryPool pool, uint queryIndex)
    {
        ContextValidationState state = RequireRecording(context);
        RequireOnDevice(context.Device, pool, "QueryPool");
        ValidateQueryPool(context, pool, queryIndex, QueryValidationEventType.Begin);
        QuerySlot slot = new(pool, queryIndex);
        lock (state)
        {
            QueryLocalPhase phase = GetLocalQueryPhase(state, slot);
            if (phase is QueryLocalPhase.Active or QueryLocalPhase.Ready)
                Reject("Queries", "Query index is already active or has an unresolved result.", pool.Label);
            CommandMutationCapacity capacity = new() { QueryEvents = 1, QueryPhases = 1 };
            PrepareCommandDependencyCore(state, pool, ref capacity);
            ReserveCommandMutation(state, capacity);
            Backend.BeginQuery(context, pool, queryIndex);
            RecordQueryEvent(state, slot, QueryValidationEventType.Begin, QueryLocalPhase.Active);
            RecordCommandDependencyCore(state, pool);
        }
    }

    public void EndQuery(CommandContext context, QueryPool pool, uint queryIndex)
    {
        ContextValidationState state = RequireRecording(context);
        RequireOnDevice(context.Device, pool, "QueryPool");
        ValidateQueryPool(context, pool, queryIndex, QueryValidationEventType.End);
        QuerySlot slot = new(pool, queryIndex);
        lock (state)
        {
            QueryLocalPhase phase = GetLocalQueryPhase(state, slot);
            if (phase is QueryLocalPhase.Ready or QueryLocalPhase.Resolved)
                Reject("Queries", "EndQuery has no matching active query in execution order.", pool.Label);
            CommandMutationCapacity capacity = new() { QueryEvents = 1, QueryPhases = 1 };
            PrepareCommandDependencyCore(state, pool, ref capacity);
            ReserveCommandMutation(state, capacity);
            Backend.EndQuery(context, pool, queryIndex);
            RecordQueryEvent(state, slot, QueryValidationEventType.End, QueryLocalPhase.Ready);
            RecordCommandDependencyCore(state, pool);
        }
    }

    public void WriteTimestamp(CommandContext context, QueryPool pool, uint queryIndex)
    {
        RequireOutsideRendering(context);
        ContextValidationState state = GetContextState(context);
        RequireOnDevice(context.Device, pool, "QueryPool");
        ValidateQueryPool(context, pool, queryIndex, QueryValidationEventType.Write);
        QuerySlot slot = new(pool, queryIndex);
        lock (state)
        {
            QueryLocalPhase phase = GetLocalQueryPhase(state, slot);
            if (phase is QueryLocalPhase.Active or QueryLocalPhase.Ready)
                Reject("Queries", "Timestamp query index has an unresolved earlier result.", pool.Label);
            CommandMutationCapacity capacity = new() { QueryEvents = 1, QueryPhases = 1 };
            PrepareCommandDependencyCore(state, pool, ref capacity);
            ReserveCommandMutation(state, capacity);
            Backend.WriteTimestamp(context, pool, queryIndex);
            RecordQueryEvent(state, slot, QueryValidationEventType.Write, QueryLocalPhase.Ready);
            RecordCommandDependencyCore(state, pool);
        }
    }

    public void ResolveQueries(
        CommandContext context,
        QueryPool pool,
        uint firstQuery,
        uint queryCount,
        Buffer destination,
        in BufferRange destinationRange)
    {
        RequireOutsideRendering(context);
        ContextValidationState state = GetContextState(context);
        RequireOnDevice(context.Device, pool, "QueryPool");
        RequireOnDevice(context.Device, destination, "Query destination");
        if (queryCount == 0 ||
            firstQuery >= pool.Description.Count ||
            queryCount > pool.Description.Count - firstQuery)
            Reject("Queries", "ResolveQueries range is outside the QueryPool.", pool.Label);
        lock (state)
        {
            for (uint offset = 0; offset < queryCount; offset++)
            {
                QuerySlot slot = new(pool, checked(firstQuery + offset));
                if (GetLocalQueryPhase(state, slot) == QueryLocalPhase.Active)
                    Reject("Queries", "ResolveQueries cannot resolve an active query.", pool.Label);
            }

            CommandMutationCapacity capacity = new()
            {
                QueryEvents = checked((int)queryCount),
                QueryPhases = checked((int)queryCount),
            };
            PrepareCommandDependencyCore(state, pool, ref capacity);
            PrepareCommandDependencyCore(state, destination, ref capacity);
            ReserveCommandMutation(state, capacity);

            Backend.ResolveQueries(
                context,
                pool,
                firstQuery,
                queryCount,
                destination,
                destinationRange);

            RecordCommandDependencyCore(state, pool);
            RecordCommandDependencyCore(state, destination);

            for (uint offset = 0; offset < queryCount; offset++)
            {
                QuerySlot slot = new(pool, checked(firstQuery + offset));
                RecordQueryEvent(
                    state,
                    slot,
                    QueryValidationEventType.Resolve,
                    QueryLocalPhase.Resolved);
            }
        }
    }

    private void ValidateQueryPool(
        CommandContext context,
        QueryPool pool,
        uint queryIndex,
        QueryValidationEventType operation)
    {
        if (queryIndex >= pool.Description.Count)
            Reject("Queries", "Query index is outside the QueryPool.", pool.Label);
        if (pool.Description.QueueType != context.QueueType)
            Reject("Queries", "QueryPool belongs to another Queue family.", pool.Label);
        if (operation is QueryValidationEventType.Begin or QueryValidationEventType.End)
        {
            if (context.QueueType != QueueType.Graphics || context.Bundle)
            {
                Reject(
                    "Queries",
                    "BeginQuery/EndQuery require a non-bundle Graphics CommandContext.",
                    context.Label);
            }
            if (pool.Description.Type == QueryType.Timestamp)
                Reject("Queries", "Timestamp queries use WriteTimestamp, not BeginQuery/EndQuery.", pool.Label);
        }
        else if (operation == QueryValidationEventType.Write &&
                 pool.Description.Type != QueryType.Timestamp)
        {
            Reject("Queries", "WriteTimestamp requires a Timestamp QueryPool.", pool.Label);
        }
    }

    private static QueryLocalPhase GetLocalQueryPhase(
        ContextValidationState state,
        in QuerySlot slot) =>
        state.QueryPhases.TryGetValue(slot, out QueryLocalPhase phase)
            ? phase
            : QueryLocalPhase.Unknown;

    private static void RecordQueryEvent(
        ContextValidationState state,
        in QuerySlot slot,
        QueryValidationEventType type,
        QueryLocalPhase phase)
    {
        state.QueryEvents.Add(new QueryValidationEvent(slot, type));
        state.QueryPhases[slot] = phase;
    }
}
