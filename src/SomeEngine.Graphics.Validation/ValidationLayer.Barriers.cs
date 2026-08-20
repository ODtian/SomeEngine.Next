namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private ResourceValidationEvent CreateTransitionEvent(
        Resource resource,
        TextureSubresourceRange? textureRange,
        PipelineSync syncBefore,
        PipelineSync syncAfter,
        ResourceAccess accessBefore,
        ResourceAccess accessAfter,
        TextureLayout? layoutBefore,
        TextureLayout? layoutAfter,
        BarrierPhase phase)
    {
        ValidateBarrierPhase(phase);
        return ResourceValidationEvent.Transition(
            ResolveBarrierRange(resource, textureRange, allowWholeTexture: false),
            syncBefore,
            syncAfter,
            accessBefore,
            accessAfter,
            layoutBefore,
            layoutAfter,
            phase);
    }

    private void ValidateBarrierPhase(BarrierPhase phase)
    {
        if (!Enum.IsDefined(phase))
            Reject("Barriers", $"Unknown barrier phase value {(byte)phase}.");
    }

    private ResourceValidationEvent CreateReleaseEvent(in QueueRelease barrier)
    {
        ValidateResourceBarrierShape(
            barrier.Resource,
            barrier.TextureRange,
            barrier.Layout,
            "QueueRelease");
        return ResourceValidationEvent.Release(
            ResolveBarrierRange(
                barrier.Resource,
                barrier.TextureRange,
                allowWholeTexture: false),
            barrier.Sync,
            barrier.Access,
            barrier.Layout,
            barrier.DestinationQueueType);
    }

    private ResourceValidationEvent CreateAcquireEvent(in QueueAcquire barrier)
    {
        ValidateResourceBarrierShape(
            barrier.Resource,
            barrier.TextureRange,
            barrier.Layout,
            "QueueAcquire");
        return ResourceValidationEvent.Acquire(
            ResolveBarrierRange(
                barrier.Resource,
                barrier.TextureRange,
                allowWholeTexture: false),
            barrier.SourceQueueType,
            barrier.Sync,
            barrier.Access,
            barrier.Layout);
    }

    private ResourceValidationEvent CreateAliasingEvent(in AliasingBarrier barrier)
    {
        var before = new ResourceBarrierRange[barrier.Before.Length];
        for (int index = 0; index < before.Length; index++)
        {
            AliasingResource resource = barrier.Before[index];
            ValidateAliasingResourceShape(resource);
            before[index] = ResolveBarrierRange(
                resource.Resource,
                resource.TextureRange,
                allowWholeTexture: true);
        }

        var after = new ResourceBarrierRange[barrier.After.Length];
        for (int index = 0; index < after.Length; index++)
        {
            AliasingResource resource = barrier.After[index];
            ValidateAliasingResourceShape(resource);
            after[index] = ResolveBarrierRange(
                resource.Resource,
                resource.TextureRange,
                allowWholeTexture: true);
        }

        return ResourceValidationEvent.Aliasing(before, after);
    }

    private void ValidateAliasingResourceShape(in AliasingResource resource)
    {
        if (resource.Resource is Buffer or AccelerationStructure &&
            resource.TextureRange is not null)
        {
            string kind = resource.Resource is AccelerationStructure
                ? "AccelerationStructure"
                : "Buffer";
            Reject(
                "Barriers",
                $"A {kind} aliasing entry cannot contain a Texture subresource range.",
                resource.Resource.Label);
        }
    }

    private void ValidateResourceBarrierShape(
        Resource resource,
        TextureSubresourceRange? textureRange,
        TextureLayout? layout,
        string operation)
    {
        if (resource is Buffer)
        {
            if (textureRange is not null)
            {
                Reject(
                    "Barriers",
                    $"A Buffer {operation} cannot contain a Texture subresource range.",
                    resource.Label);
            }
            if (layout is not null)
            {
                Reject(
                    "Barriers",
                    $"A Buffer {operation} cannot contain a Texture layout.",
                    resource.Label);
            }
            return;
        }

        if (resource is not Texture)
            Reject("Barriers", $"{operation} requires a Buffer or Texture.", resource.Label);
        if (textureRange is null)
            Reject("Barriers", $"A Texture {operation} requires an explicit range.", resource.Label);
        if (layout is null)
            Reject("Barriers", $"A Texture {operation} requires a layout.", resource.Label);
    }

    private ResourceBarrierRange ResolveBarrierRange(
        Resource resource,
        TextureSubresourceRange? textureRange,
        bool allowWholeTexture)
    {
        if (!_resourceStates.TryGetValue(resource, out ResourceValidationState? state))
        {
            Reject(
                "Ownership",
                "Barrier resource was not created through this Validation Layer.",
                resource.Label);
        }

        if (resource is Buffer or AccelerationStructure)
        {
            if (textureRange is not null)
            {
                string kind = resource is AccelerationStructure
                    ? "AccelerationStructure"
                    : "Buffer";
                Reject(
                    "Barriers",
                    $"A {kind} cannot use a Texture subresource range.",
                    resource.Label);
            }
            return state!.WholeBufferRange!;
        }

        if (resource is not Texture)
        {
            Reject(
                "Barriers",
                "Barrier resource must be a Buffer, Texture, or AccelerationStructure.",
                resource.Label);
        }
        var texture = (Texture)resource;

        TextureSubresourceRange range;
        if (textureRange is { } explicitRange)
        {
            range = explicitRange;
        }
        else
        {
            if (!allowWholeTexture)
                Reject("Barriers", "A Texture barrier requires an explicit range.", resource.Label);
            range = new TextureSubresourceRange(
                0,
                texture.Info.MipLevelCount,
                0,
                texture.Info.ArrayLayerCount,
                DefaultValidationAspects(texture.Info.Format));
        }

        return new ResourceBarrierRange(state!, ResolveTextureCells(texture, range));
    }

    private int[] ResolveTextureCells(Texture texture, in TextureSubresourceRange range)
    {
        TextureInfo info = texture.Info;
        if (range.MipLevelCount == 0 ||
            range.FirstMipLevel >= info.MipLevelCount ||
            range.MipLevelCount > info.MipLevelCount - range.FirstMipLevel ||
            range.ArrayLayerCount == 0 ||
            range.FirstArrayLayer >= info.ArrayLayerCount ||
            range.ArrayLayerCount > info.ArrayLayerCount - range.FirstArrayLayer)
        {
            Reject("Barriers", "Texture barrier range is outside the Texture.", texture.Label);
        }

        Span<uint> planes = stackalloc uint[2];
        int planeCount = ResolveValidationPlanes(info.Format, range.Aspects, planes, texture.Label);
        int count;
        try
        {
            count = checked((int)(
                (ulong)range.MipLevelCount *
                range.ArrayLayerCount *
                (uint)planeCount));
        }
        catch (OverflowException)
        {
            Reject("Barriers", "Texture barrier range is too large to validate.", texture.Label);
            throw;
        }

        var cells = new int[count];
        int destination = 0;
        for (int planeIndex = 0; planeIndex < planeCount; planeIndex++)
        for (uint layer = range.FirstArrayLayer;
             layer < range.FirstArrayLayer + range.ArrayLayerCount;
             layer++)
        for (uint mip = range.FirstMipLevel;
             mip < range.FirstMipLevel + range.MipLevelCount;
             mip++)
        {
            try
            {
                cells[destination++] = checked((int)(
                    mip +
                    layer * info.MipLevelCount +
                    planes[planeIndex] * info.MipLevelCount * info.ArrayLayerCount));
            }
            catch (OverflowException)
            {
                Reject("Barriers", "Texture subresource index is too large to validate.", texture.Label);
                throw;
            }
        }
        return cells;
    }

    private int ResolveValidationPlanes(
        Format format,
        TextureAspects aspects,
        Span<uint> destination,
        string? label)
    {
        bool depth = format is Format.D16UNorm or Format.D24UNormS8UInt or
            Format.D32Float or Format.D32FloatS8UInt;
        bool stencil = format is Format.D24UNormS8UInt or Format.D32FloatS8UInt;
        TextureAspects named = aspects &
            (TextureAspects.Color | TextureAspects.Depth | TextureAspects.Stencil);
        TextureAspects planes = aspects &
            (TextureAspects.Plane0 | TextureAspects.Plane1 | TextureAspects.Plane2);
        if (aspects == TextureAspects.None ||
            (named != TextureAspects.None && planes != TextureAspects.None))
        {
            Reject(
                "Barriers",
                "A Texture barrier must select either named aspects or plane aspects.",
                label);
        }

        if (!depth)
        {
            if (aspects is not (TextureAspects.Color or TextureAspects.Plane0))
                Reject("Barriers", $"Texture format {format} exposes only Color/Plane0.", label);
            destination[0] = 0;
            return 1;
        }

        TextureAspects allowed = named != TextureAspects.None
            ? TextureAspects.Depth | (stencil ? TextureAspects.Stencil : TextureAspects.None)
            : TextureAspects.Plane0 | (stencil ? TextureAspects.Plane1 : TextureAspects.None);
        if ((aspects & ~allowed) != 0)
            Reject("Barriers", $"Texture format {format} does not expose the selected aspect.", label);

        int count = 0;
        TextureAspects first = named != TextureAspects.None
            ? TextureAspects.Depth
            : TextureAspects.Plane0;
        TextureAspects second = named != TextureAspects.None
            ? TextureAspects.Stencil
            : TextureAspects.Plane1;
        if ((aspects & first) != 0)
            destination[count++] = 0;
        if ((aspects & second) != 0)
            destination[count++] = 1;
        return count;
    }

    private static TextureAspects DefaultValidationAspects(Format format) => format switch
    {
        Format.D16UNorm or Format.D32Float => TextureAspects.Depth,
        Format.D24UNormS8UInt or Format.D32FloatS8UInt =>
            TextureAspects.Depth | TextureAspects.Stencil,
        _ => TextureAspects.Color,
    };

    private void ValidateLocalResourceEvent(
        ContextValidationState context,
        ResourceValidationEvent validationEvent)
    {
        switch (validationEvent.Kind)
        {
            case ResourceValidationEventKind.Transition:
                ValidateLocalTransition(context, validationEvent);
                break;
            case ResourceValidationEventKind.Release:
                foreach (int cell in validationEvent.Range!.Cells)
                {
                    var key = new ResourceCellKey(validationEvent.Range.State, cell);
                    if (!context.ResourceStates.TryGetValue(key, out LocalResourceState state))
                        continue;
                    RequireLocalAvailable(validationEvent.Range, state);
                    RequireBeforeState(
                        validationEvent.Range,
                        state.Sync,
                        state.Access,
                        state.Layout,
                        validationEvent.SyncBefore,
                        validationEvent.AccessBefore,
                        validationEvent.LayoutBefore);
                }
                break;
            case ResourceValidationEventKind.Acquire:
                foreach (int cell in validationEvent.Range!.Cells)
                {
                    var key = new ResourceCellKey(validationEvent.Range.State, cell);
                    if (context.ResourceStates.ContainsKey(key))
                    {
                        Reject(
                            "Barriers",
                            "QueueAcquire must be the first local operation for its range; the matching release belongs to another Queue submission.",
                            validationEvent.Range.Resource.Label);
                    }
                }
                break;
            case ResourceValidationEventKind.Aliasing:
                foreach (ResourceBarrierRange range in validationEvent.BeforeRanges!)
                foreach (int cell in range.Cells)
                {
                    var key = new ResourceCellKey(range.State, cell);
                    if (!context.ResourceStates.TryGetValue(key, out LocalResourceState state))
                        continue;
                    if (state.Status == LocalResourceStatus.Released)
                    {
                        Reject(
                            "Barriers",
                            "An aliased range cannot be deactivated while a Queue handoff is pending.",
                            range.Resource.Label);
                    }
                    if (state.Split is not null)
                    {
                        Reject(
                            "Barriers",
                            "An aliased range cannot be deactivated while a split barrier is pending.",
                            range.Resource.Label);
                    }
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void ValidateLocalTransition(
        ContextValidationState context,
        in ResourceValidationEvent validationEvent)
    {
        ResourceBarrierRange range = validationEvent.Range!;
        PendingSplitState declared = PendingSplitState.FromEvent(validationEvent);
        foreach (int cell in range.Cells)
        {
            var key = new ResourceCellKey(range.State, cell);
            if (!context.ResourceStates.TryGetValue(key, out LocalResourceState state))
                continue;

            if (validationEvent.Phase == BarrierPhase.End)
            {
                RequireLocalStatusAvailable(range, state);
                if (state.Split is not PendingSplitState pending)
                {
                    Reject(
                        "Barriers",
                        "A split-barrier End has no matching Begin in this command stream.",
                        range.Resource.Label);
                    return;
                }
                RequireMatchingSplit(range, pending, declared);
                continue;
            }

            RequireLocalAvailable(range, state);
            RequireBeforeState(
                range,
                state.Sync,
                state.Access,
                state.Layout,
                validationEvent.SyncBefore,
                validationEvent.AccessBefore,
                validationEvent.LayoutBefore);
        }
    }

    private void ApplyLocalResourceEvent(
        ContextValidationState context,
        ResourceValidationEvent validationEvent)
    {
        switch (validationEvent.Kind)
        {
            case ResourceValidationEventKind.Transition:
                ApplyLocalTransition(context, validationEvent);
                break;
            case ResourceValidationEventKind.Acquire:
                SetLocalRange(
                    context,
                    validationEvent.Range!,
                    new LocalResourceState(
                        LocalResourceStatus.Available,
                        validationEvent.SyncAfter,
                        validationEvent.AccessAfter,
                        validationEvent.LayoutAfter));
                break;
            case ResourceValidationEventKind.Release:
                SetLocalRange(
                    context,
                    validationEvent.Range!,
                    new LocalResourceState(
                        LocalResourceStatus.Released,
                        PipelineSync.None,
                        ResourceAccess.NoAccess,
                        null));
                break;
            case ResourceValidationEventKind.Aliasing:
                foreach (ResourceBarrierRange range in validationEvent.BeforeRanges!)
                {
                    SetLocalRange(
                        context,
                        range,
                        new LocalResourceState(
                            LocalResourceStatus.Inactive,
                            PipelineSync.None,
                            ResourceAccess.NoAccess,
                            range.Resource is Texture ? TextureLayout.Undefined : null));
                }
                foreach (ResourceBarrierRange range in validationEvent.AfterRanges!)
                {
                    SetLocalRange(
                        context,
                        range,
                        new LocalResourceState(
                            LocalResourceStatus.Available,
                            PipelineSync.None,
                            ResourceAccess.NoAccess,
                            range.Resource is Texture ? TextureLayout.Undefined : null));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        if (validationEvent.Range is not null)
            RecordCommandDependencyCore(context, validationEvent.Range.Resource);
        if (validationEvent.BeforeRanges is not null)
        {
            foreach (ResourceBarrierRange range in validationEvent.BeforeRanges)
                RecordCommandDependencyCore(context, range.Resource);
        }
        if (validationEvent.AfterRanges is not null)
        {
            foreach (ResourceBarrierRange range in validationEvent.AfterRanges)
                RecordCommandDependencyCore(context, range.Resource);
        }
        context.ResourceEvents.Add(validationEvent);
    }

    private static void ApplyLocalTransition(
        ContextValidationState context,
        in ResourceValidationEvent validationEvent)
    {
        if (validationEvent.Phase == BarrierPhase.Begin)
        {
            SetLocalRange(
                context,
                validationEvent.Range!,
                new LocalResourceState(
                    LocalResourceStatus.Available,
                    validationEvent.SyncBefore,
                    validationEvent.AccessBefore,
                    validationEvent.LayoutBefore,
                    PendingSplitState.FromEvent(validationEvent)));
            return;
        }

        SetLocalRange(
            context,
            validationEvent.Range!,
            new LocalResourceState(
                LocalResourceStatus.Available,
                validationEvent.SyncAfter,
                validationEvent.AccessAfter,
                validationEvent.LayoutAfter));
    }

    private void PrepareLocalResourceEvent(
        ContextValidationState context,
        ResourceValidationEvent validationEvent)
    {
        CommandMutationCapacity capacity = new()
        {
            ResourceEvents = 1,
        };
        if (validationEvent.Range is not null)
            PrepareResourceRange(context, validationEvent.Range, ref capacity);
        if (validationEvent.BeforeRanges is not null)
        {
            foreach (ResourceBarrierRange range in validationEvent.BeforeRanges)
                PrepareResourceRange(context, range, ref capacity);
        }
        if (validationEvent.AfterRanges is not null)
        {
            foreach (ResourceBarrierRange range in validationEvent.AfterRanges)
                PrepareResourceRange(context, range, ref capacity);
        }
        ReserveCommandMutation(context, capacity);
    }

    private void PrepareResourceRange(
        ContextValidationState context,
        ResourceBarrierRange range,
        ref CommandMutationCapacity capacity)
    {
        capacity.ResourceStates = checked(capacity.ResourceStates + range.Cells.Length);
        PrepareCommandDependencyCore(context, range.Resource, ref capacity);
    }

    private static void SetLocalRange(
        ContextValidationState context,
        ResourceBarrierRange range,
        in LocalResourceState value)
    {
        foreach (int cell in range.Cells)
            context.ResourceStates[new ResourceCellKey(range.State, cell)] = value;
    }

    private void RequireLocalAvailable(
        ResourceBarrierRange range,
        in LocalResourceState state)
    {
        RequireLocalStatusAvailable(range, state);
        if (state.Split is not null)
        {
            Reject(
                "Barriers",
                "The resource range has a pending split barrier and only the matching End is legal.",
                range.Resource.Label);
        }
    }

    private void RequireLocalStatusAvailable(
        ResourceBarrierRange range,
        in LocalResourceState state)
    {
        if (state.Status == LocalResourceStatus.Inactive)
            Reject("Barriers", "The aliased resource range is inactive.", range.Resource.Label);
        if (state.Status == LocalResourceStatus.Released)
        {
            Reject(
                "Barriers",
                "The resource range was released and cannot be used before a matching acquire.",
                range.Resource.Label);
        }
    }

    private void RequireMatchingSplit(
        ResourceBarrierRange range,
        in PendingSplitState pending,
        in PendingSplitState declared)
    {
        if (pending == declared)
            return;
        Reject(
            "Barriers",
            "A split-barrier End must repeat the exact Begin transition.",
            range.Resource.Label);
    }

    private void RequireBeforeState(
        ResourceBarrierRange range,
        PipelineSync actualSync,
        ResourceAccess actualAccess,
        TextureLayout? actualLayout,
        PipelineSync declaredSync,
        ResourceAccess declaredAccess,
        TextureLayout? declaredLayout)
    {
        if (actualSync == declaredSync &&
            actualAccess == declaredAccess &&
            actualLayout == declaredLayout)
        {
            return;
        }

        Reject(
            "Barriers",
            $"Incorrect Before state. Tracked state is Sync={actualSync}, Access={actualAccess}, Layout={FormatLayout(actualLayout)}; " +
            $"the barrier declares Sync={declaredSync}, Access={declaredAccess}, Layout={FormatLayout(declaredLayout)}.",
            range.Resource.Label);
    }

    private ResourceSubmissionReservation? PlanResourceSubmission(
        Queue queue,
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        ReadOnlySpan<RecordedValidationState> recordings,
        ReadOnlySpan<TimelineSignal> timelineSignals,
        ResourceSubmissionReservation workspace)
    {
        bool hasEvents = false;
        foreach (RecordedValidationState recording in recordings)
            hasEvents |= recording.ResourceEvents.Count != 0;
        if (!hasEvents)
            return null;

            workspace.Clear();
            Dictionary<ResourceCellKey, ResourceCellState> changes = workspace.Changes;
            HashSet<ResourceValidationState> touched = workspace.States;
            List<PendingHandoff> newHandoffs = workspace.NewHandoffs;

            foreach (RecordedValidationState recording in recordings)
            foreach (ResourceValidationEvent validationEvent in recording.ResourceEvents)
            {
                AddTouchedResources(validationEvent, touched);
                ApplySubmittedResourceEvent(
                    queue,
                    completionWaits,
                    timelineWaits,
                    validationEvent,
                    changes,
                    newHandoffs);
            }

            foreach (ResourceValidationState state in touched)
            {
                if (state.SubmissionInProgress)
                {
                    Reject(
                        "Concurrency",
                        "The resource is already participating in a concurrent Queue submission.",
                        state.Resource.Label);
                }
            }

            Dictionary<ResourceValidationState, int> cellCapacityByState = workspace.CellCapacities;
            foreach (ResourceCellKey key in changes.Keys)
            {
                cellCapacityByState.TryGetValue(key.State, out int count);
                cellCapacityByState[key.State] = checked(count + 1);
            }
            TimelinePoint[] signals = timelineSignals.IsEmpty
                ? []
                : new TimelinePoint[timelineSignals.Length];
            for (int index = 0; index < timelineSignals.Length; index++)
            {
                TimelineSignal signal = timelineSignals[index];
                signals[index] = new TimelinePoint(signal.Timeline, signal.Value);
            }
            workspace.TimelineSignals = signals;
            return workspace;
    }

    private static void AddTouchedResources(
        ResourceValidationEvent validationEvent,
        HashSet<ResourceValidationState> destination)
    {
        if (validationEvent.Range is not null)
            destination.Add(validationEvent.Range.State);
        if (validationEvent.BeforeRanges is not null)
        {
            foreach (ResourceBarrierRange range in validationEvent.BeforeRanges)
                destination.Add(range.State);
        }
        if (validationEvent.AfterRanges is not null)
        {
            foreach (ResourceBarrierRange range in validationEvent.AfterRanges)
                destination.Add(range.State);
        }
    }

    private void ApplySubmittedResourceEvent(
        Queue queue,
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        ResourceValidationEvent validationEvent,
        Dictionary<ResourceCellKey, ResourceCellState> changes,
        List<PendingHandoff> newHandoffs)
    {
        switch (validationEvent.Kind)
        {
            case ResourceValidationEventKind.Transition:
                ApplySubmittedTransition(queue, validationEvent, changes);
                break;
            case ResourceValidationEventKind.Release:
                ApplySubmittedRelease(queue, validationEvent, changes, newHandoffs);
                break;
            case ResourceValidationEventKind.Acquire:
                ApplySubmittedAcquire(
                    queue,
                    completionWaits,
                    timelineWaits,
                    validationEvent,
                    changes);
                break;
            case ResourceValidationEventKind.Aliasing:
                ApplySubmittedAliasing(queue, validationEvent, changes);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void ApplySubmittedTransition(
        Queue queue,
        ResourceValidationEvent validationEvent,
        Dictionary<ResourceCellKey, ResourceCellState> changes)
    {
        ResourceBarrierRange range = validationEvent.Range!;
        PendingSplitState declared = PendingSplitState.FromEvent(validationEvent);
        foreach (int cell in range.Cells)
        {
            ResourceCellState current = GetSimulatedState(range.State, cell, changes);
            RequireQueueOwnership(queue, range, current);

            if (validationEvent.Phase == BarrierPhase.End)
            {
                if (current.Split is not PendingSplitState pending)
                {
                    Reject(
                        "Barriers",
                        "A split-barrier End has no matching submitted Begin.",
                        range.Resource.Label);
                    return;
                }
                RequireMatchingSplit(range, pending, declared);
            }
            else
            {
                RequireNoPendingSplit(range, current);
                RequireBeforeState(
                    range,
                    current.Sync,
                    current.Access,
                    current.Layout,
                    validationEvent.SyncBefore,
                    validationEvent.AccessBefore,
                    validationEvent.LayoutBefore);
            }

            if (validationEvent.Phase == BarrierPhase.Begin)
            {
                changes[new ResourceCellKey(range.State, cell)] = current with
                {
                    OwnerQueue = queue,
                    OwnerType = queue.Type,
                    Split = declared,
                };
                continue;
            }

            changes[new ResourceCellKey(range.State, cell)] = current with
            {
                Sync = validationEvent.SyncAfter,
                Access = validationEvent.AccessAfter,
                Layout = validationEvent.LayoutAfter,
                OwnerQueue = queue,
                OwnerType = queue.Type,
                Split = null,
            };
        }
    }

    private void ApplySubmittedRelease(
        Queue queue,
        ResourceValidationEvent validationEvent,
        Dictionary<ResourceCellKey, ResourceCellState> changes,
        List<PendingHandoff> newHandoffs)
    {
        ResourceBarrierRange range = validationEvent.Range!;
        foreach (int cell in range.Cells)
        {
            ResourceCellState current = GetSimulatedState(range.State, cell, changes);
            RequireQueueOwnership(queue, range, current);
            RequireNoPendingSplit(range, current);
            RequireBeforeState(
                range,
                current.Sync,
                current.Access,
                current.Layout,
                validationEvent.SyncBefore,
                validationEvent.AccessBefore,
                validationEvent.LayoutBefore);
        }

        var handoff = new PendingHandoff(
            range,
            queue,
            validationEvent.QueueType,
            validationEvent.SyncBefore,
            validationEvent.AccessBefore,
            validationEvent.LayoutBefore);
        newHandoffs.Add(handoff);
        foreach (int cell in range.Cells)
        {
            ResourceCellState current = GetSimulatedState(range.State, cell, changes);
            changes[new ResourceCellKey(range.State, cell)] = current with
            {
                Sync = PipelineSync.None,
                Access = ResourceAccess.NoAccess,
                Layout = range.Resource is Texture ? TextureLayout.General : null,
                OwnerQueue = queue,
                OwnerType = queue.Type,
                Handoff = handoff,
            };
        }
    }

    private void ApplySubmittedAcquire(
        Queue queue,
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        ResourceValidationEvent validationEvent,
        Dictionary<ResourceCellKey, ResourceCellState> changes)
    {
        ResourceBarrierRange range = validationEvent.Range!;
        PendingHandoff? handoff = null;
        foreach (int cell in range.Cells)
        {
            ResourceCellState current = GetSimulatedState(range.State, cell, changes);
            RequireNoPendingSplit(range, current);
            if (!current.Active || current.Handoff is null)
            {
                Reject(
                    "Barriers",
                    "QueueAcquire has no matching QueueRelease for the complete resource range.",
                    range.Resource.Label);
            }
            if (handoff is null)
                handoff = current.Handoff;
            else if (!ReferenceEquals(handoff, current.Handoff))
            {
                Reject(
                    "Barriers",
                    "QueueAcquire spans more than one pending QueueRelease.",
                    range.Resource.Label);
            }
        }

        PendingHandoff required = handoff!;
        if (!range.Cells.AsSpan().SequenceEqual(required.Range.Cells))
        {
            Reject(
                "Barriers",
                "QueueAcquire must name exactly the range named by QueueRelease.",
                range.Resource.Label);
        }
        if (validationEvent.QueueType != required.SourceQueue.Type)
        {
            Reject(
                "Barriers",
                $"QueueAcquire declares source {validationEvent.QueueType}, but QueueRelease executed on {required.SourceQueue.Type}.",
                range.Resource.Label);
        }
        if (queue.Type != required.DestinationQueueType)
        {
            Reject(
                "Barriers",
                $"QueueAcquire executes on {queue.Type}, but QueueRelease names destination {required.DestinationQueueType}.",
                range.Resource.Label);
        }
        if (ReferenceEquals(queue, required.SourceQueue))
        {
            Reject(
                "Barriers",
                "QueueRelease and QueueAcquire must execute on distinct Queues.",
                range.Resource.Label);
        }
        if (!IsHandoffOrdered(required, completionWaits, timelineWaits))
        {
            Reject(
                "Barriers",
                "QueueAcquire is missing the QueueCompletion or ExternalTimeline wait that orders its QueueRelease.",
                range.Resource.Label);
        }

        foreach (int cell in range.Cells)
        {
            ResourceCellState current = GetSimulatedState(range.State, cell, changes);
            changes[new ResourceCellKey(range.State, cell)] = current with
            {
                Sync = validationEvent.SyncAfter,
                Access = validationEvent.AccessAfter,
                Layout = validationEvent.LayoutAfter,
                OwnerQueue = queue,
                OwnerType = queue.Type,
                Handoff = null,
            };
        }
    }

    private void ApplySubmittedAliasing(
        Queue queue,
        ResourceValidationEvent validationEvent,
        Dictionary<ResourceCellKey, ResourceCellState> changes)
    {
        foreach (ResourceBarrierRange range in validationEvent.BeforeRanges!)
        foreach (int cell in range.Cells)
        {
            ResourceCellState current = GetSimulatedState(range.State, cell, changes);
            RequireQueueOwnership(queue, range, current);
            RequireNoPendingSplit(range, current);
            changes[new ResourceCellKey(range.State, cell)] = current with
            {
                Active = false,
                Sync = PipelineSync.None,
                Access = ResourceAccess.NoAccess,
                Layout = range.Resource is Texture ? TextureLayout.Undefined : null,
                OwnerQueue = queue,
                OwnerType = queue.Type,
            };
        }

        foreach (ResourceBarrierRange range in validationEvent.AfterRanges!)
        foreach (int cell in range.Cells)
        {
            changes[new ResourceCellKey(range.State, cell)] = new ResourceCellState(
                Active: true,
                Sync: PipelineSync.None,
                Access: ResourceAccess.NoAccess,
                Layout: range.Resource is Texture ? TextureLayout.Undefined : null,
                OwnerQueue: queue,
                OwnerType: queue.Type,
                Handoff: null);
        }
    }

    private ResourceCellState GetSimulatedState(
        ResourceValidationState state,
        int cell,
        Dictionary<ResourceCellKey, ResourceCellState> changes)
    {
        var key = new ResourceCellKey(state, cell);
        return changes.TryGetValue(key, out ResourceCellState value)
            ? value
            : state.GetCell(cell);
    }

    private void RequireQueueOwnership(
        Queue queue,
        ResourceBarrierRange range,
        in ResourceCellState state)
    {
        if (!state.Active)
            Reject("Barriers", "The aliased resource range is inactive.", range.Resource.Label);
        if (state.Handoff is not null)
        {
            Reject(
                "Barriers",
                "The resource range is between QueueRelease and QueueAcquire and cannot be accessed.",
                range.Resource.Label);
        }
        if (state.OwnerQueue is not null && !ReferenceEquals(state.OwnerQueue, queue))
        {
            Reject(
                "Barriers",
                "Resource use moved to another Queue without QueueRelease, QueueAcquire, and an ordering wait.",
                range.Resource.Label);
        }
        if (state.OwnerQueue is null && state.OwnerType is { } ownerType && ownerType != queue.Type)
        {
            Reject(
                "Barriers",
                $"Resource is initially owned by {ownerType}, not {queue.Type}; an explicit Queue handoff is required.",
                range.Resource.Label);
        }
    }

    private void RequireNoPendingSplit(
        ResourceBarrierRange range,
        in ResourceCellState state)
    {
        if (state.Split is null)
            return;
        Reject(
            "Barriers",
            "The resource range has a pending split barrier and only the matching End is legal.",
            range.Resource.Label);
    }

    private bool IsHandoffOrdered(
        PendingHandoff handoff,
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits)
    {
        if (!handoff.HasCompletion)
            return false;
        foreach (QueueCompletion wait in completionWaits)
        {
            if (ReferenceEquals(wait.Queue, handoff.SourceQueue) &&
                wait.Value >= handoff.Completion.Value)
            {
                return true;
            }
        }
        foreach (TimelinePoint wait in timelineWaits)
        foreach (TimelinePoint signal in handoff.TimelineSignals)
        {
            if (ReferenceEquals(wait.Timeline, signal.Timeline) && wait.Value >= signal.Value)
                return true;
        }
        return false;
    }

    private void CompleteResourceSubmission(
        ResourceSubmissionReservation? reservation,
        in QueueCompletion completion,
        bool commit)
    {
        if (reservation is null)
            return;

        if (commit)
        {
            foreach (PendingHandoff handoff in reservation.NewHandoffs)
            {
                handoff.Completion = completion;
                handoff.HasCompletion = true;
                handoff.TimelineSignals = reservation.TimelineSignals;
            }
            foreach ((ResourceCellKey key, ResourceCellState value) in reservation.Changes)
                key.State.SetCell(key.Cell, value);
        }

        foreach (ResourceValidationState state in reservation.States)
            state.SubmissionInProgress = false;
    }

    private void ResetAcquiredTextureState(in SwapchainImage image)
    {
        Texture texture = image.Texture;
        if (!_resourceStates.TryGetValue(texture, out ResourceValidationState? state))
        {
            Reject(
                "Ownership",
                "Acquired Texture was not created through this Validation Layer.",
                texture.Label);
        }

        lock (_gate)
        {
            state!.Reset(
                image.InitialSync,
                image.InitialAccess,
                image.InitialLayout,
                QueueType.Graphics);
        }
    }

    private void ValidatePresentTextureState(Queue queue, Texture texture)
    {
        ResourceBarrierRange range = ResolveBarrierRange(
            texture,
            null,
            allowWholeTexture: true);
        lock (_gate)
        {
            foreach (int cell in range.Cells)
            {
                ResourceCellState state = range.State.GetCell(cell);
                RequireQueueOwnership(queue, range, state);
                if (state.Sync != PipelineSync.None ||
                    state.Access != ResourceAccess.NoAccess ||
                    state.Layout != TextureLayout.Present)
                {
                    Reject(
                        "Presentation",
                        $"Present requires Sync=None, Access=NoAccess, Layout=Present; tracked state is " +
                        $"Sync={state.Sync}, Access={state.Access}, Layout={FormatLayout(state.Layout)}.",
                        texture.Label);
                }
            }
        }
    }

    private static string FormatLayout(TextureLayout? layout) =>
        layout?.ToString() ?? "n/a";

    private enum ResourceValidationEventKind : byte
    {
        Transition,
        Release,
        Acquire,
        Aliasing,
    }

    private enum LocalResourceStatus : byte
    {
        Available,
        Released,
        Inactive,
    }

    private readonly record struct LocalResourceState(
        LocalResourceStatus Status,
        PipelineSync Sync,
        ResourceAccess Access,
        TextureLayout? Layout,
        PendingSplitState? Split = null);

    private readonly record struct PendingSplitState(
        PipelineSync SyncBefore,
        PipelineSync SyncAfter,
        ResourceAccess AccessBefore,
        ResourceAccess AccessAfter,
        TextureLayout? LayoutBefore,
        TextureLayout? LayoutAfter)
    {
        internal static PendingSplitState FromEvent(in ResourceValidationEvent validationEvent) =>
            new(
                validationEvent.SyncBefore,
                validationEvent.SyncAfter,
                validationEvent.AccessBefore,
                validationEvent.AccessAfter,
                validationEvent.LayoutBefore,
                validationEvent.LayoutAfter);
    }

    private readonly record struct ResourceCellKey(ResourceValidationState State, int Cell);

    private readonly record struct ResourceCellState(
        bool Active,
        PipelineSync Sync,
        ResourceAccess Access,
        TextureLayout? Layout,
        Queue? OwnerQueue,
        QueueType? OwnerType,
        PendingHandoff? Handoff,
        PendingSplitState? Split = null);

    private sealed class ResourceValidationState
    {
        private readonly Dictionary<int, ResourceCellState> _cells = [];
        private ResourceCellState _initial;

        internal ResourceValidationState(bool buffer = false)
        {
            if (buffer)
                WholeBufferRange = new ResourceBarrierRange(this, [0]);
        }

        internal ResourceValidationState(Resource resource)
        {
            if (resource is Buffer)
                WholeBufferRange = new ResourceBarrierRange(this, [0]);
            Bind(resource);
        }

        internal void Bind(Resource resource)
        {
            Resource = resource;
            _initial = new ResourceCellState(
                Active: true,
                Sync: resource.InitialSync,
                Access: resource.InitialAccess,
                Layout: resource is Texture texture ? texture.InitialLayout : null,
                OwnerQueue: null,
                OwnerType: resource.InitialQueueType,
                Handoff: null);
        }

        internal Resource Resource { get; private set; } = null!;
        internal ResourceBarrierRange? WholeBufferRange { get; private set; }
        internal bool SubmissionInProgress;

        internal ResourceCellState GetCell(int cell) =>
            _cells.TryGetValue(cell, out ResourceCellState state) ? state : _initial;

        internal void SetCell(int cell, in ResourceCellState state) => _cells[cell] = state;

        internal void EnsureCellCapacity(int additionalCapacity) =>
            _cells.EnsureCapacity(checked(_cells.Count + additionalCapacity));

        internal void Reset(
            PipelineSync sync,
            ResourceAccess access,
            TextureLayout? layout,
            QueueType? ownerType)
        {
            _cells.Clear();
            _initial = new ResourceCellState(
                Active: true,
                Sync: sync,
                Access: access,
                Layout: layout,
                OwnerQueue: null,
                OwnerType: ownerType,
                Handoff: null);
        }
    }

    private sealed record ResourceBarrierRange(
        ResourceValidationState State,
        int[] Cells)
    {
        internal Resource Resource => State.Resource;
    }

    private readonly struct ResourceValidationEvent
    {
        private ResourceValidationEvent(ResourceValidationEventKind kind)
        {
            Kind = kind;
        }

        internal ResourceValidationEventKind Kind { get; }
        internal ResourceBarrierRange? Range { get; private init; }
        internal ResourceBarrierRange[]? BeforeRanges { get; private init; }
        internal ResourceBarrierRange[]? AfterRanges { get; private init; }
        internal PipelineSync SyncBefore { get; private init; }
        internal PipelineSync SyncAfter { get; private init; }
        internal ResourceAccess AccessBefore { get; private init; }
        internal ResourceAccess AccessAfter { get; private init; }
        internal TextureLayout? LayoutBefore { get; private init; }
        internal TextureLayout? LayoutAfter { get; private init; }
        internal QueueType QueueType { get; private init; }
        internal BarrierPhase Phase { get; private init; }

        internal static ResourceValidationEvent Transition(
            ResourceBarrierRange range,
            PipelineSync syncBefore,
            PipelineSync syncAfter,
            ResourceAccess accessBefore,
            ResourceAccess accessAfter,
            TextureLayout? layoutBefore,
            TextureLayout? layoutAfter,
            BarrierPhase phase) =>
            new(ResourceValidationEventKind.Transition)
            {
                Range = range,
                SyncBefore = syncBefore,
                SyncAfter = syncAfter,
                AccessBefore = accessBefore,
                AccessAfter = accessAfter,
                LayoutBefore = layoutBefore,
                LayoutAfter = layoutAfter,
                Phase = phase,
            };

        internal static ResourceValidationEvent Release(
            ResourceBarrierRange range,
            PipelineSync sync,
            ResourceAccess access,
            TextureLayout? layout,
            QueueType destinationQueueType) =>
            new(ResourceValidationEventKind.Release)
            {
                Range = range,
                SyncBefore = sync,
                AccessBefore = access,
                LayoutBefore = layout,
                QueueType = destinationQueueType,
            };

        internal static ResourceValidationEvent Acquire(
            ResourceBarrierRange range,
            QueueType sourceQueueType,
            PipelineSync sync,
            ResourceAccess access,
            TextureLayout? layout) =>
            new(ResourceValidationEventKind.Acquire)
            {
                Range = range,
                QueueType = sourceQueueType,
                SyncAfter = sync,
                AccessAfter = access,
                LayoutAfter = layout,
            };

        internal static ResourceValidationEvent Aliasing(
            ResourceBarrierRange[] before,
            ResourceBarrierRange[] after) =>
            new(ResourceValidationEventKind.Aliasing)
            {
                BeforeRanges = before,
                AfterRanges = after,
            };
    }

    private sealed class PendingHandoff
    {
        internal PendingHandoff(
            ResourceBarrierRange range,
            Queue sourceQueue,
            QueueType destinationQueueType,
            PipelineSync sync,
            ResourceAccess access,
            TextureLayout? layout)
        {
            Range = range;
            SourceQueue = sourceQueue;
            DestinationQueueType = destinationQueueType;
            Sync = sync;
            Access = access;
            Layout = layout;
        }

        internal ResourceBarrierRange Range { get; }
        internal Queue SourceQueue { get; }
        internal QueueType DestinationQueueType { get; }
        internal PipelineSync Sync { get; }
        internal ResourceAccess Access { get; }
        internal TextureLayout? Layout { get; }
        internal QueueCompletion Completion;
        internal bool HasCompletion;
        internal TimelinePoint[] TimelineSignals = [];
    }

    private sealed class ResourceSubmissionReservation
    {
        internal readonly Dictionary<ResourceCellKey, ResourceCellState> Changes = [];
        internal readonly HashSet<ResourceValidationState> States =
            new(ReferenceEqualityComparer.Instance);
        internal readonly Dictionary<ResourceValidationState, int> CellCapacities =
            new(ReferenceEqualityComparer.Instance);
        internal readonly List<PendingHandoff> NewHandoffs = [];
        internal TimelinePoint[] TimelineSignals = [];

        internal void Clear()
        {
            Changes.Clear();
            States.Clear();
            CellCapacities.Clear();
            NewHandoffs.Clear();
            TimelineSignals = [];
        }
    }
}
