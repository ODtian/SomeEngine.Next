namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer<TBackend>
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
        return Track(Backend.CreateSwapchain(device, desc), device);
    }

    public SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image)
    {
        Require(swapchain);
        SwapchainAcquireStatus status = Backend.Acquire(swapchain, options, out image);
        if (status == SwapchainAcquireStatus.Success)
        {
            if (!ReferenceEquals(swapchain, image.Swapchain))
                Reject("Presentation", "Acquire returned an image for another Swapchain.", swapchain.Label);
            TrackIfAbsent(image.Texture, swapchain);
            ResetAcquiredTextureState(image);
        }
        return status;
    }

    public PresentStatus Present(Queue queue, in SwapchainImage image)
    {
        RequireQueue(queue);
        Swapchain swapchain = image.Swapchain;
        Require(swapchain);
        RequireSameDevice(queue.Device, swapchain.Device, "SwapchainImage");
        ValidatePresentTextureState(queue, image.Texture);
        return Backend.Present(queue, image);
    }

    public ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config)
    {
        Require(swapchain);
        return Backend.Reconfigure(swapchain, config);
    }

    public QueryPool CreateQueryPool(Device device, in QueryPoolDesc desc)
    {
        RequireDevice(device);
        return Track(Backend.CreateQueryPool(device, desc), device);
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
