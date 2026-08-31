namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    internal CommandContext CreateCommandContext(
        RhiDevice device,
        in CommandContextDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        if (desc.InitialSlotCount == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "InitialSlotCount must be positive.");
        if (desc.Bundle)
        {
            throw new NotSupportedException(
                "This Vulkan Device does not report secondary command-buffer bundle support.");
        }
        VulkanQueue queue = nativeDevice.GetQueue(desc.QueueType, desc.QueueIndex);
        var context = new VulkanCommandContext(nativeDevice, queue, desc);
        try
        {
            context.PrepareSlots(desc.InitialSlotCount);
            nativeDevice.RegisterChild(context);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    internal void Begin(CommandContext context, in CommandRecordingDesc desc = default) =>
        RequireCommandContext(context, nameof(context)).Begin(desc);

    internal RecordedCommands End(CommandContext context) =>
        RequireCommandContext(context, nameof(context)).EndCommands();

    internal RecordedBundle EndBundle(CommandContext context) =>
        throw new NotSupportedException(
            "This Vulkan Device does not report secondary command-buffer bundle support.");

    internal void Discard(CommandContext context) =>
        RequireCommandContext(context, nameof(context)).Discard();

    internal QueueCompletion Submit(RhiQueue queue, in QueueSubmitDesc desc)
    {
        VulkanQueue nativeQueue = RequireQueue(queue, nameof(queue));
        VulkanDevice device = (VulkanDevice)nativeQueue.Device;

        int completionWaitCount = desc.CompletionWaits.Length;
        int imageCount = desc.SwapchainImages.Length;
        int timelineWaitCount = desc.TimelineWaits.Length;
        int timelineSignalCount = desc.TimelineSignals.Length;
        int waitCount = checked(completionWaitCount + imageCount + timelineWaitCount);
        int signalCount = checked(imageCount + timelineSignalCount + 1);
        int commandCount = desc.Commands.Length;
        int timelineCount = checked(timelineWaitCount + timelineSignalCount);
        int referencedImageCapacity = CountReferencedSwapchainUses(
            nativeQueue,
            desc.Commands);
        VulkanQueueWork work = nativeQueue.TakeWork();
        int claimed = 0;
        int claimedImages = 0;
        int retainedTimelineCount = 0;
        try
        {
            work.EnsureCapacity(
                commandCount,
                imageCount,
                referencedImageCapacity,
                timelineCount,
                waitCount,
                signalCount);

            PrepareCompletionWaits(
                nativeQueue,
                desc.CompletionWaits,
                work.NativeWaits.AsSpan(0, completionWaitCount));
            work.ReferencedImageCount = CollectReferencedSwapchainUses(
                nativeQueue,
                desc.Commands,
                work.ReferencedImages);
            ValidateSwapchainImageSet(
                nativeQueue,
                desc.SwapchainImages,
                work.ReferencedImages.AsSpan(0, work.ReferencedImageCount),
                work.ImageMatches.AsSpan(0, work.ReferencedImageCount));
            claimed = ClaimCommands(
                nativeQueue,
                desc.Commands,
                work.NativeCommands.AsSpan(0, commandCount),
                work.CommandLeases.AsSpan(0, commandCount),
                work.CommandSequences.AsSpan(0, commandCount));
            work.CommandCount = claimed;
            claimedImages = ClaimSwapchainImages(
                nativeQueue,
                desc.SwapchainImages,
                work.NativeWaits.AsSpan(completionWaitCount, imageCount),
                work.NativeSignals.AsSpan(0, imageCount),
                work.ImageLeases.AsSpan(0, imageCount),
                work.ImageSequences.AsSpan(0, imageCount));
            work.ImageCount = claimedImages;
            retainedTimelineCount = PrepareTimelineSemaphores(
                nativeQueue,
                desc.TimelineWaits,
                desc.TimelineSignals,
                work.NativeWaits.AsSpan(completionWaitCount + imageCount, timelineWaitCount),
                work.NativeSignals.AsSpan(imageCount, timelineSignalCount),
                work.RetainedTimelines.AsSpan(0, timelineCount));
            work.TimelineCount = retainedTimelineCount;

            return SubmitPrepared(
                nativeQueue,
                device,
                ref work,
                commandCount,
                claimed,
                claimedImages,
                waitCount,
                signalCount,
                imageCount,
                timelineSignalCount);
        }
        catch
        {
            RestorePreparedSubmission(
                nativeQueue,
                device,
                work,
                claimed,
                claimedImages,
                retainedTimelineCount);
            throw;
        }
    }

    private QueueCompletion SubmitPrepared(
        VulkanQueue queue,
        VulkanDevice device,
        ref VulkanQueueWork work,
        int commandCount,
        int claimedCommandCount,
        int claimedImageCount,
        int waitCount,
        int signalCount,
        int imageCount,
        int timelineSignalCount)
    {
        lock (queue.Gate)
        {
            device.ThrowIfUnavailable();
            ulong completionValue = queue.ReserveCompletionValue();
            work.CompletionValue = completionValue;
            work.NativeSignals[imageCount + timelineSignalCount] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = queue.CompletionSemaphore,
                Value = completionValue,
                StageMask = PipelineStageFlags2.AllCommandsBit,
                DeviceIndex = 0,
            };
            Result result = QueueSubmitNative(
                queue,
                work,
                commandCount,
                waitCount,
                signalCount);
            switch (result)
            {
                case Result.Success:
                    work.NativeAccepted = true;
                    break;
                case Result.ErrorOutOfHostMemory:
                case Result.ErrorOutOfDeviceMemory:
                    device.ThrowIfDeviceCallFailed(result, "vkQueueSubmit2");
                    break;
                case Result.ErrorDeviceLost:
                    throw device.PublishDeviceLoss(result, "vkQueueSubmit2");
                default:
                    throw device.PublishInternalDeviceLoss(
                        $"vkQueueSubmit2 returned uncertain failure {result}.");
            }

            try
            {
                CommitSubmittedWork(
                    queue,
                    work,
                    completionValue,
                    claimedCommandCount,
                    claimedImageCount);
            }
            catch
            {
                work.MarkDeviceLostNoThrow();
                queue.AppendAcceptedNoThrow(work);
                work = null!;
                throw device.PublishInternalDeviceLoss(
                    "Submission state commit failed after native acceptance.");
            }

            queue.AppendAcceptedNoThrow(work);
            work = null!;
            return new QueueCompletion(queue, completionValue);
        }
    }

    private Result QueueSubmitNative(
        VulkanQueue queue,
        VulkanQueueWork work,
        int commandCount,
        int waitCount,
        int signalCount)
    {
        fixed (SemaphoreSubmitInfo* waitPointer = work.NativeWaits)
        fixed (SemaphoreSubmitInfo* signalPointer = work.NativeSignals)
        fixed (CommandBufferSubmitInfo* commandPointer = work.NativeCommands)
        {
            SubmitInfo2 submit = new()
            {
                SType = StructureType.SubmitInfo2,
                WaitSemaphoreInfoCount = checked((uint)waitCount),
                PWaitSemaphoreInfos = waitPointer,
                CommandBufferInfoCount = checked((uint)commandCount),
                PCommandBufferInfos = commandPointer,
                SignalSemaphoreInfoCount = checked((uint)signalCount),
                PSignalSemaphoreInfos = signalPointer,
            };
#if SOMEENGINE_TESTING
            FaultHooks.Before(VulkanCallPoint.QueueSubmit);
            if (FaultHooks.TryOverride(VulkanCallPoint.QueueSubmit, out Result injectedResult))
            {
                FaultHooks.After(VulkanCallPoint.QueueSubmit);
                return injectedResult;
            }
#endif
            Result result = Api.QueueSubmit2(queue.Native, 1, &submit, default);
#if SOMEENGINE_TESTING
            FaultHooks.After(VulkanCallPoint.QueueSubmit);
#endif
            return result;
        }
    }

    private static void CommitSubmittedWork(
        VulkanQueue queue,
        VulkanQueueWork work,
        ulong completionValue,
        int commandCount,
        int imageCount)
    {
        for (int index = 0; index < commandCount; index++)
            work.CommandLeases[index].MarkSubmitted(work.CommandSequences[index]);
        for (int index = 0; index < imageCount; index++)
            work.ImageLeases[index].MarkSubmission(queue, completionValue);
    }

    private static void RestorePreparedSubmission(
        VulkanQueue queue,
        VulkanDevice device,
        VulkanQueueWork? work,
        int commandCount,
        int imageCount,
        int timelineCount)
    {
        if (work is null || work.NativeAccepted)
            return;
        if (device.Status == DeviceStatus.Lost)
        {
            work.MarkDeviceLostNoThrow();
            work.ReleaseLostReferencesNoThrow();
            queue.ReturnWork(work);
            return;
        }
        for (int index = 0; index < commandCount; index++)
            work.CommandLeases[index].RestoreExecutable(work.CommandSequences[index]);
        for (int index = 0; index < imageCount; index++)
            work.ImageLeases[index].Restore(work.ImageSequences[index]);
        for (int index = timelineCount - 1; index >= 0; index--)
            work.RetainedTimelines[index].ReleaseNative();
        queue.ReturnWork(work);
    }

    private void PrepareCompletionWaits(
        VulkanQueue queue,
        ReadOnlySpan<QueueCompletion> source,
        Span<SemaphoreSubmitInfo> destination)
    {
        for (int index = 0; index < source.Length; index++)
        {
            QueueCompletion wait = source[index];
            VulkanQueue waitQueue = RequireQueue(wait.Queue, nameof(source));
            if (!ReferenceEquals(waitQueue.Device, queue.Device))
                throw new ArgumentException("A Queue completion wait must belong to the same Device.", nameof(source));
            destination[index] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = waitQueue.CompletionSemaphore,
                Value = wait.Value,
                StageMask = PipelineStageFlags2.AllCommandsBit,
                DeviceIndex = 0,
            };
        }
    }

    private static int CountReferencedSwapchainUses(
        VulkanQueue queue,
        ReadOnlySpan<RecordedCommands> commands)
    {
        int count = 0;
        foreach (ref readonly RecordedCommands recorded in commands)
        {
            VulkanRecordedCommandsLease lease = RequireRecordedLease(queue, recorded);
            count = checked(count + lease.GetSwapchainUseCount(recorded.Sequence));
        }
        return count;
    }

    private static int CollectReferencedSwapchainUses(
        VulkanQueue queue,
        ReadOnlySpan<RecordedCommands> commands,
        Span<VulkanSwapchainUse> destination)
    {
        int count = 0;
        foreach (ref readonly RecordedCommands recorded in commands)
        {
            VulkanRecordedCommandsLease lease = RequireRecordedLease(queue, recorded);
            int commandUseCount = lease.GetSwapchainUseCount(recorded.Sequence);
            for (int useIndex = 0; useIndex < commandUseCount; useIndex++)
            {
                VulkanSwapchainUse use = lease.GetSwapchainUse(recorded.Sequence, useIndex);
                bool duplicate = false;
                for (int existingIndex = 0; existingIndex < count; existingIndex++)
                {
                    ref readonly VulkanSwapchainUse existing = ref destination[existingIndex];
                    if (ReferenceEquals(existing.Lease, use.Lease) &&
                        existing.Sequence == use.Sequence)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                    destination[count++] = use;
            }
        }
        return count;
    }

    private static void ValidateSwapchainImageSet(
        VulkanQueue queue,
        ReadOnlySpan<SwapchainImage> supplied,
        ReadOnlySpan<VulkanSwapchainUse> referenced,
        Span<bool> matched)
    {
        if (supplied.Length != referenced.Length)
        {
            throw new ArgumentException(
                "SwapchainImages must exactly match the images used by RecordedCommands.",
                nameof(supplied));
        }
        matched.Clear();
        for (int suppliedIndex = 0; suppliedIndex < supplied.Length; suppliedIndex++)
        {
            SwapchainImage image = supplied[suppliedIndex];
            if (image.Lease is not VulkanSwapchainImageLease lease ||
                !ReferenceEquals(lease.Swapchain.Device, queue.Device))
            {
                throw new ArgumentException(
                    "A supplied SwapchainImage belongs to a different Vulkan Device.",
                    nameof(supplied));
            }

            int match = -1;
            for (int referencedIndex = 0; referencedIndex < referenced.Length; referencedIndex++)
            {
                if (matched[referencedIndex])
                    continue;
                ref readonly VulkanSwapchainUse expected = ref referenced[referencedIndex];
                if (ReferenceEquals(expected.Lease, lease) &&
                    expected.Sequence == image.Sequence &&
                    expected.Generation == lease.Swapchain.Info.Generation)
                {
                    match = referencedIndex;
                    break;
                }
            }
            if (match < 0)
            {
                throw new ArgumentException(
                    "A supplied SwapchainImage was not referenced by RecordedCommands or is stale.",
                    nameof(supplied));
            }
            matched[match] = true;
        }
    }

    private static VulkanRecordedCommandsLease RequireRecordedLease(
        VulkanQueue queue,
        in RecordedCommands recorded)
    {
        if (recorded.Lease is not VulkanRecordedCommandsLease lease ||
            !ReferenceEquals(lease.Queue, queue))
        {
            throw new ArgumentException(
                "RecordedCommands belong to another Vulkan Queue.",
                nameof(recorded));
        }
        return lease;
    }

    private static int ClaimCommands(
        VulkanQueue queue,
        ReadOnlySpan<RecordedCommands> source,
        Span<CommandBufferSubmitInfo> commands,
        Span<VulkanRecordedCommandsLease> leases,
        Span<ulong> sequences)
    {
        int claimed = 0;
        for (int index = 0; index < source.Length; index++)
        {
            RecordedCommands recorded = source[index];
            if (recorded.Lease is not VulkanRecordedCommandsLease lease ||
                !ReferenceEquals(lease.Queue, queue))
                throw new ArgumentException("RecordedCommands belong to another Vulkan Queue.", nameof(source));
            ulong sequence = recorded.Sequence;
            if (!lease.TryBeginSubmit(sequence))
                throw new InvalidOperationException("RecordedCommands are not executable.");
            leases[index] = lease;
            sequences[index] = sequence;
            commands[index] = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = lease.GetNative(sequence),
                DeviceMask = 0,
            };
            claimed++;
        }
        return claimed;
    }

    private static int ClaimSwapchainImages(
        VulkanQueue queue,
        ReadOnlySpan<SwapchainImage> source,
        Span<SemaphoreSubmitInfo> waits,
        Span<SemaphoreSubmitInfo> signals,
        Span<VulkanSwapchainImageLease> leases,
        Span<ulong> sequences)
    {
        int claimed = 0;
        for (int index = 0; index < source.Length; index++)
        {
            SwapchainImage image = source[index];
            if (image.Lease is not VulkanSwapchainImageLease lease ||
                !lease.ClaimSubmit(image.Sequence, queue))
                throw new InvalidOperationException("A SwapchainImage is stale or already submitted.");
            leases[index] = lease;
            sequences[index] = image.Sequence;
            waits[index] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = lease.AcquireSemaphore,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            signals[index] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = lease.RenderComplete,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            claimed++;
        }
        return claimed;
    }

    private int PrepareTimelineSemaphores(
        VulkanQueue queue,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        ReadOnlySpan<TimelineSignal> timelineSignals,
        Span<SemaphoreSubmitInfo> waits,
        Span<SemaphoreSubmitInfo> signals,
        Span<IVulkanRetained> retained)
    {
        int retainedCount = 0;
        try
        {
            for (int index = 0; index < timelineWaits.Length; index++)
            {
                VulkanExternalTimeline timeline = RequireExternalTimeline(
                    timelineWaits[index].Timeline,
                    nameof(timelineWaits));
                if (!ReferenceEquals(timeline.Device, queue.Device))
                    throw new ArgumentException("Timeline waits must belong to the submitted Device.", nameof(timelineWaits));
                timeline.RetainNative();
                retained[retainedCount++] = timeline;
                waits[index] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = timeline.Native,
                    Value = timelineWaits[index].Value,
                    StageMask = PipelineStageFlags2.AllCommandsBit,
                };
            }
            for (int index = 0; index < timelineSignals.Length; index++)
            {
                VulkanExternalTimeline timeline = RequireExternalTimeline(
                    timelineSignals[index].Timeline,
                    nameof(timelineSignals));
                if (!ReferenceEquals(timeline.Device, queue.Device))
                    throw new ArgumentException("Timeline signals must belong to the submitted Device.", nameof(timelineSignals));
                timeline.RetainNative();
                retained[retainedCount++] = timeline;
                signals[index] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = timeline.Native,
                    Value = timelineSignals[index].Value,
                    StageMask = PipelineStageFlags2.AllCommandsBit,
                };
            }
            return retainedCount;
        }
        catch
        {
            for (int index = retainedCount - 1; index >= 0; index--)
                retained[index].ReleaseNative();
            throw;
        }
    }

    internal bool IsComplete(in QueueCompletion completion)
    {
        VulkanQueue queue = RequireQueue(completion.Queue, nameof(completion));
        return queue.GetCompletedValue() >= completion.Value;
    }

    internal WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout)
    {
        VulkanQueue queue = RequireQueue(completion.Queue, nameof(completion));
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        ulong value = completion.Value;
        VkSemaphore semaphore = queue.CompletionSemaphore;
        SemaphoreWaitInfo wait = new()
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &value,
        };
        ulong nanoseconds = timeout == Timeout.InfiniteTimeSpan
            ? ulong.MaxValue
            : checked((ulong)timeout.Ticks * 100);
        VulkanDevice device = (VulkanDevice)queue.Device;
#if SOMEENGINE_TESTING
        FaultHooks.Before(VulkanCallPoint.WaitSemaphores);
        bool overridden = FaultHooks.TryOverride(
            VulkanCallPoint.WaitSemaphores,
            out Result injectedResult);
#endif
        Result result =
#if SOMEENGINE_TESTING
            overridden
                ? injectedResult
                :
#endif
            Api.WaitSemaphores(
            device.Native,
            &wait,
            nanoseconds);
#if SOMEENGINE_TESTING
        FaultHooks.After(VulkanCallPoint.WaitSemaphores);
#endif
        if (result == Result.Timeout)
            return WaitStatus.Timeout;
        device.ThrowIfDeviceCallFailed(result, "vkWaitSemaphores");
        queue.CollectCompleted();
        return WaitStatus.Completed;
    }

    private VulkanCommandContext RequireCommandContext(
        CommandContext context,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(context, parameterName);
        if (context is not VulkanCommandContext native ||
            native.Device is not VulkanDevice device ||
            !ReferenceEquals(device.Backend, this))
            throw new ArgumentException("The CommandContext belongs to a different graphics backend.", parameterName);
        native.ThrowIfDisposed();
        device.ThrowIfUnavailable();
        return native;
    }

    private VulkanQueue RequireQueue(RhiQueue queue, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(queue, parameterName);
        if (queue is not VulkanQueue native ||
            native.Device is not VulkanDevice device ||
            !ReferenceEquals(device.Backend, this))
            throw new ArgumentException("The Queue belongs to a different graphics backend.", parameterName);
        device.ThrowIfUnavailable();
        return native;
    }

    private sealed partial class VulkanCommandContext : CommandContext
    {
        private readonly object _gate = new();
        private readonly VulkanDevice _device;
        private readonly VulkanQueue _queue;
        private readonly Stack<VulkanCommandSlot> _freeSlots = [];
        private readonly HashSet<VulkanCommandSlot> _slots = [];
        private VulkanCommandSlot? _recording;
        private ulong _nextSequence;
        private bool _released;
        private int _unregistered;

        internal VulkanCommandContext(
            VulkanDevice device,
            VulkanQueue queue,
            in CommandContextDesc desc)
            : base(device, desc.QueueType, desc.QueueIndex, desc.Bundle, desc.Label)
        {
            _device = device;
            _queue = queue;
        }

        internal VkCommandBuffer NativeRecording => _recording?.Native
            ?? throw new InvalidOperationException("The CommandContext is not recording.");
        internal VulkanDescriptorArena RecordingDescriptorArena =>
            _recording?.DescriptorArena
            ?? throw new InvalidOperationException("The CommandContext is not recording.");

        internal void PrepareSlots(uint count)
        {
            for (uint index = 0; index < count; index++)
                _freeSlots.Push(CreateSlot());
        }

        internal void Begin(in CommandRecordingDesc desc)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_released)
                    throw new ObjectDisposedException(nameof(VulkanCommandContext));
                if (_recording is not null)
                    throw new InvalidOperationException("The CommandContext is already recording.");
                VulkanCommandSlot slot = _freeSlots.Count != 0
                    ? _freeSlots.Pop()
                    : CreateSlot();
                _device.ThrowIfDeviceCallFailed(
                    _device.Backend.Api.ResetCommandPool(
                        _device.Native,
                        slot.Pool,
                        CommandPoolResetFlags.None),
                    "vkResetCommandPool");
                slot.ResetCaptures(desc.InitialCapturedResourceCapacity);
                ResetRecordingState();
                CommandBufferBeginInfo begin = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                _device.ThrowIfDeviceCallFailed(
                    _device.Backend.Api.BeginCommandBuffer(slot.Native, &begin),
                    "vkBeginCommandBuffer");
                _recording = slot;
            }
        }

        internal RecordedCommands EndCommands()
        {
            VulkanCommandSlot slot;
            ulong sequence;
            lock (_gate)
            {
                slot = _recording
                    ?? throw new InvalidOperationException("The CommandContext is not recording.");
                FinalizeOptionalScopes();
                _device.ThrowIfDeviceCallFailed(
                    _device.Backend.Api.EndCommandBuffer(slot.Native),
                    "vkEndCommandBuffer");
                _recording = null;
                sequence = checked(++_nextSequence);
            }
            VulkanRecordedCommandsLease lease = slot.Lease;
            lease.ActivateCommands(sequence);
            slot.AttachLease(lease, sequence);
            return new RecordedCommands(lease, sequence);
        }

        internal void Discard()
        {
            VulkanCommandSlot slot;
            lock (_gate)
            {
                slot = _recording
                    ?? throw new InvalidOperationException("The CommandContext is not recording.");
                _recording = null;
                ResetRecordingState();
            }
            ReturnSlot(slot);
        }

        internal void Capture(IVulkanRetained value)
        {
            VulkanCommandSlot slot = _recording
                ?? throw new InvalidOperationException("The CommandContext is not recording.");
            VulkanTexture? texture = value switch
            {
                VulkanTexture direct => direct,
                IVulkanTextureView view => view.Texture,
                _ => null,
            };
            VulkanSwapchainUse? swapchainUse = null;
            if (texture?.SwapchainState is VulkanImageState imageState)
            {
                if (!imageState.TryGetCurrentUse(out VulkanSwapchainUse use))
                {
                    throw new InvalidOperationException(
                        "A Vulkan swapchain texture is not currently acquired.");
                }
                swapchainUse = use;
            }
            slot.Capture(value);
            if (swapchainUse is VulkanSwapchainUse captured)
                slot.AddSwapchainUse(captured);
        }

        internal void ReturnSlot(VulkanCommandSlot slot)
        {
            bool destroy;
            bool unregister;
            lock (_gate)
            {
                if (!_slots.Contains(slot))
                    return;
                slot.DetachLease();
                if (_released)
                {
                    destroy = _slots.Remove(slot);
                }
                else
                {
                    slot.ReleaseCaptures();
                    _freeSlots.Push(slot);
                    destroy = false;
                }
                unregister = _released && _slots.Count == 0;
            }
            if (destroy)
                DestroySlotNative(slot);
            if (unregister)
                UnregisterOnce();
        }

        internal override void Release(bool fromParent)
        {
            ReleaseSlots(
                abandonAll: fromParent ||
                    _device.IsDisposed ||
                    _device.Status != DeviceStatus.Active);
        }

        internal void ReleaseFromDeviceNoThrow()
        {
            try
            {
                ReleaseSlots(abandonAll: true);
            }
            catch (Exception exception)
            {
                _device.RecordReleaseFailure(exception);
            }
        }

        private void ReleaseSlots(bool abandonAll)
        {
            VulkanCommandSlot[] destroy;
            bool unregister;
            lock (_gate)
            {
                if (_released && !abandonAll)
                    return;
                _released = true;
                ResetRecordingState();
                if (abandonAll)
                {
                    destroy = _slots.ToArray();
                    _slots.Clear();
                }
                else
                {
                    var detached = new List<VulkanCommandSlot>(_freeSlots.Count + 1);
                    while (_freeSlots.TryPop(out VulkanCommandSlot? free))
                    {
                        if (_slots.Remove(free))
                            detached.Add(free);
                    }
                    if (_recording is not null && _slots.Remove(_recording))
                        detached.Add(_recording);
                    destroy = detached.ToArray();
                }
                _recording = null;
                _freeSlots.Clear();
                unregister = _slots.Count == 0;
            }
            if (abandonAll)
            {
                foreach (VulkanCommandSlot slot in destroy)
                    slot.DiscardExecutableFromDeviceNoThrow();
            }
            Exception? failure = null;
            foreach (VulkanCommandSlot slot in destroy)
            {
                try
                {
                    DestroySlotNative(slot);
                }
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }
            if (unregister)
                UnregisterOnce();
            if (failure is not null)
                throw failure;
        }

        internal void MarkDeviceLostNoThrow()
        {
            lock (_gate)
            {
                foreach (VulkanCommandSlot slot in _slots)
                    slot.MarkDeviceLostNoThrow();
            }
        }

        private VulkanCommandSlot CreateSlot()
        {
            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.TransientBit,
                QueueFamilyIndex = _queue.FamilyIndex,
            };
            CommandPool pool = default;
            _device.ThrowIfDeviceCallFailed(
                _device.Backend.Api.CreateCommandPool(_device.Native, &poolInfo, null, &pool),
                "vkCreateCommandPool");
            try
            {
                CommandBufferAllocateInfo allocateInfo = new()
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = pool,
                    Level = CommandBufferLevel.Primary,
                    CommandBufferCount = 1,
                };
                VkCommandBuffer commandBuffer = default;
                _device.ThrowIfDeviceCallFailed(
                    _device.Backend.Api.AllocateCommandBuffers(
                        _device.Native,
                        &allocateInfo,
                        &commandBuffer),
                    "vkAllocateCommandBuffers");
                var slot = new VulkanCommandSlot(_device, pool, commandBuffer);
                slot.InitializeLease(new VulkanRecordedCommandsLease(this, slot, _queue));
                _slots.Add(slot);
                return slot;
            }
            catch
            {
                _device.Backend.Api.DestroyCommandPool(_device.Native, pool, null);
                throw;
            }
        }

        private void DestroySlotNative(VulkanCommandSlot slot)
        {
            slot.ReleaseCaptures();
            slot.DescriptorArena.Release();
            if (_device.Native.Handle != 0)
                _device.Backend.Api.DestroyCommandPool(_device.Native, slot.Pool, null);
        }

        private void UnregisterOnce()
        {
            if (Interlocked.Exchange(ref _unregistered, 1) == 0)
                _device.UnregisterChild(this);
        }
    }

    private sealed partial class VulkanCommandSlot(
        VulkanDevice device,
        CommandPool pool,
        VkCommandBuffer native)
    {
        private readonly HashSet<IVulkanRetained> _captures = [];
        private VulkanSwapchainUse[] _swapchainUses = [];
        private int _swapchainUseCount;
        private VulkanRecordedCommandsLease? _activeLease;
        private ulong _activeSequence;

        internal CommandPool Pool { get; } = pool;
        internal VkCommandBuffer Native { get; } = native;
        internal VulkanDescriptorArena DescriptorArena { get; } = new(device);
        internal VulkanRecordedCommandsLease Lease { get; private set; } = null!;

        internal void InitializeLease(VulkanRecordedCommandsLease lease)
        {
            if (Lease is not null)
                throw new InvalidOperationException("A Vulkan command slot already has a reusable lease.");
            Lease = lease;
        }

        internal void AttachLease(VulkanRecordedCommandsLease lease, ulong sequence)
        {
            if (_activeLease is not null)
                throw new InvalidOperationException("A Vulkan command slot already has an active payload.");
            if (!ReferenceEquals(lease, Lease))
                throw new ArgumentException("The recorded-command lease belongs to another slot.", nameof(lease));
            _activeLease = lease;
            _activeSequence = sequence;
        }

        internal void DetachLease()
        {
            _activeLease = null;
            _activeSequence = 0;
        }

        internal void MarkDeviceLostNoThrow()
        {
            try
            {
                _activeLease?.MarkDeviceLost(_activeSequence);
            }
            catch
            {
            }
        }

        internal void DiscardExecutableFromDeviceNoThrow()
        {
            try
            {
                Lease.DiscardExecutableFromDeviceNoThrow();
            }
            catch
            {
            }
        }

        internal void ResetCaptures(uint capacity)
        {
            if (_captures.Count != 0 || _swapchainUseCount != 0)
                throw new InvalidOperationException("A Vulkan command slot still retains native dependencies.");
            _captures.EnsureCapacity(checked((int)capacity));
        }

        internal void AddSwapchainUse(in VulkanSwapchainUse use)
        {
            for (int index = 0; index < _swapchainUseCount; index++)
            {
                ref readonly VulkanSwapchainUse existing = ref _swapchainUses[index];
                if (ReferenceEquals(existing.Lease, use.Lease) &&
                    existing.Sequence == use.Sequence)
                {
                    return;
                }
            }
            if (_swapchainUses.Length == _swapchainUseCount)
            {
                int capacity = _swapchainUses.Length == 0
                    ? 4
                    : checked(_swapchainUses.Length * 2);
                Array.Resize(ref _swapchainUses, capacity);
            }
            _swapchainUses[_swapchainUseCount++] = use;
        }

        internal int SwapchainUseCount => _swapchainUseCount;

        internal void CopySwapchainUses(Span<VulkanSwapchainUse> destination)
        {
            if (destination.Length < _swapchainUseCount)
                throw new ArgumentException("The swapchain-use destination is too small.", nameof(destination));
            _swapchainUses.AsSpan(0, _swapchainUseCount).CopyTo(destination);
        }

        internal VulkanSwapchainUse GetSwapchainUse(int index)
        {
            if ((uint)index >= (uint)_swapchainUseCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _swapchainUses[index];
        }

        internal void Capture(IVulkanRetained value)
        {
            if (!_captures.Add(value))
                return;
            try
            {
                value.RetainNative();
            }
            catch
            {
                _captures.Remove(value);
                throw;
            }
        }

        internal void ReleaseCaptures()
        {
            foreach (IVulkanRetained value in _captures)
                value.ReleaseNative();
            _captures.Clear();
            DescriptorArena.ResetAfterCompletion();
            Array.Clear(_swapchainUses, 0, _swapchainUseCount);
            _swapchainUseCount = 0;
        }
    }

    private sealed class VulkanRecordedCommandsLease : RecordedCommandsLease
    {
        private readonly VulkanCommandContext _context;
        private readonly VulkanCommandSlot _slot;
        private int _returned = 1;

        internal VulkanRecordedCommandsLease(
            VulkanCommandContext context,
            VulkanCommandSlot slot,
            VulkanQueue queue)
            : base(context.Device, queue)
        {
            _context = context;
            _slot = slot;
        }

        internal void ActivateCommands(ulong sequence)
        {
            if (Interlocked.CompareExchange(ref _returned, 0, 1) != 1)
                throw new InvalidOperationException("The Vulkan command slot is already active.");
            try
            {
                Activate(sequence);
            }
            catch
            {
                Volatile.Write(ref _returned, 1);
                throw;
            }
        }

        internal VkCommandBuffer GetNative(ulong sequence)
        {
            EnsureSequence(sequence);
            return _slot.Native;
        }

        internal int GetSwapchainUseCount(ulong sequence)
        {
            EnsureSequence(sequence);
            return _slot.SwapchainUseCount;
        }

        internal void CopySwapchainUses(
            ulong sequence,
            Span<VulkanSwapchainUse> destination)
        {
            EnsureSequence(sequence);
            _slot.CopySwapchainUses(destination);
        }

        internal VulkanSwapchainUse GetSwapchainUse(ulong sequence, int index)
        {
            EnsureSequence(sequence);
            return _slot.GetSwapchainUse(index);
        }

        internal void DiscardExecutableFromDeviceNoThrow() =>
            _ = TryDiscardExecutableFromDevice(out _);

        internal void Retire(ulong sequence)
        {
            MarkCompleted(sequence);
            ReturnSlot(sequence);
        }

        protected override void DiscardUnsubmitted(ulong sequence) => ReturnSlot(sequence);

        private void ReturnSlot(ulong sequence)
        {
            EnsureSequence(sequence);
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                _context.ReturnSlot(_slot);
        }
    }

    private sealed class VulkanQueueWork
    {
        internal ulong CompletionValue;
        internal VulkanRecordedCommandsLease[] CommandLeases = [];
        internal ulong[] CommandSequences = [];
        internal int CommandCount;
        internal VulkanSwapchainImageLease[] ImageLeases = [];
        internal ulong[] ImageSequences = [];
        internal int ImageCount;
        internal VulkanSwapchainUse[] ReferencedImages = [];
        internal bool[] ImageMatches = [];
        internal int ReferencedImageCount;
        internal IVulkanRetained[] RetainedTimelines = [];
        internal int TimelineCount;
        internal SemaphoreSubmitInfo[] NativeWaits = [];
        internal SemaphoreSubmitInfo[] NativeSignals = [];
        internal CommandBufferSubmitInfo[] NativeCommands = [];
        internal VulkanQueueWork? Next;
        internal bool NativeAccepted;

        internal void EnsureCapacity(
            int commandCount,
            int imageCount,
            int referencedImageCount,
            int timelineCount,
            int waitCount,
            int signalCount)
        {
            EnsureArray(ref CommandLeases, commandCount);
            EnsureArray(ref CommandSequences, commandCount);
            EnsureArray(ref ImageLeases, imageCount);
            EnsureArray(ref ImageSequences, imageCount);
            EnsureArray(ref ReferencedImages, referencedImageCount);
            EnsureArray(ref ImageMatches, referencedImageCount);
            EnsureArray(ref RetainedTimelines, timelineCount);
            EnsureArray(ref NativeWaits, waitCount);
            EnsureArray(ref NativeSignals, signalCount);
            EnsureArray(ref NativeCommands, commandCount);
        }

        internal bool RetireNoThrow()
        {
            bool success = true;
            for (int index = 0; index < CommandCount; index++)
            {
                try
                {
                    CommandLeases[index].Retire(CommandSequences[index]);
                }
                catch
                {
                    success = false;
                }
            }
            for (int index = TimelineCount - 1; index >= 0; index--)
            {
                try
                {
                    RetainedTimelines[index].ReleaseNative();
                }
                catch
                {
                    success = false;
                }
            }
            return success;
        }

        internal void MarkDeviceLostNoThrow()
        {
            for (int index = 0; index < CommandCount; index++)
            {
                try
                {
                    CommandLeases[index].MarkDeviceLost(CommandSequences[index]);
                }
                catch
                {
                }
            }
            for (int index = 0; index < ImageCount; index++)
                ImageLeases[index].Invalidate(deviceLost: true);
        }

        internal void ReleaseLostReferencesNoThrow()
        {
            for (int index = TimelineCount - 1; index >= 0; index--)
            {
                try
                {
                    RetainedTimelines[index].ReleaseNative();
                }
                catch
                {
                }
            }
        }

        internal void ClearReferences()
        {
            Array.Clear(CommandLeases, 0, CommandCount);
            Array.Clear(ImageLeases, 0, ImageCount);
            Array.Clear(ReferencedImages, 0, ReferencedImageCount);
            Array.Clear(RetainedTimelines, 0, TimelineCount);
            CommandCount = 0;
            ImageCount = 0;
            ReferencedImageCount = 0;
            TimelineCount = 0;
            CompletionValue = 0;
            NativeAccepted = false;
            Next = null;
        }

        private static void EnsureArray<T>(ref T[] values, int required)
        {
            if (values.Length >= required)
                return;
            int doubled = values.Length == 0 ? 4 : checked(values.Length * 2);
            Array.Resize(ref values, Math.Max(required, doubled));
        }
    }
}

internal sealed unsafe partial class VulkanBackend
{
    private sealed partial class VulkanQueue
    {
        private VulkanQueueWork? _activeHead;
        private VulkanQueueWork? _activeTail;
        private VulkanQueueWork? _freeHead;

        internal VulkanQueueWork TakeWork()
        {
            lock (_gate)
            {
                VulkanQueueWork? work = _freeHead;
                if (work is not null)
                {
                    _freeHead = work.Next;
                    work.Next = null;
                    return work;
                }
            }
            return new VulkanQueueWork();
        }

        internal void ReturnWork(VulkanQueueWork work)
        {
            work.ClearReferences();
            lock (_gate)
            {
                work.Next = _freeHead;
                _freeHead = work;
            }
        }

        internal void AppendAcceptedNoThrow(VulkanQueueWork work)
        {
            if (_activeTail is null)
                _activeHead = work;
            else
                _activeTail.Next = work;
            _activeTail = work;
        }

        internal ulong GetCompletedValue()
        {
            ulong completed = 0;
#if SOMEENGINE_TESTING
            _device.Backend.FaultHooks.Before(VulkanCallPoint.GetSemaphoreCounter);
            bool overridden = _device.Backend.FaultHooks.TryOverride(
                VulkanCallPoint.GetSemaphoreCounter,
                out Result injectedResult);
#endif
            Result result =
#if SOMEENGINE_TESTING
                overridden
                    ? injectedResult
                    :
#endif
                _device.Backend.Api.GetSemaphoreCounterValue(
                _device.Native,
                _completion,
                &completed);
#if SOMEENGINE_TESTING
            _device.Backend.FaultHooks.After(VulkanCallPoint.GetSemaphoreCounter);
#endif
            _device.ThrowIfDeviceCallFailed(result, "vkGetSemaphoreCounterValue");
            return completed;
        }

        internal void CollectCompleted()
        {
            if (_completion.Handle == 0)
                return;
            ulong completed = GetCompletedValue();
            VulkanQueueWork? retired = DetachThrough(completed);
            RetireDetached(retired);
        }

        internal void CollectCompletedAfterIdle()
        {
            VulkanQueueWork? retired;
            lock (_gate)
            {
                retired = _activeHead;
                _activeHead = null;
                _activeTail = null;
            }
            RetireDetached(retired);
        }

        internal void MarkWorkDeviceLostNoThrow()
        {
            lock (_gate)
            {
                for (VulkanQueueWork? work = _activeHead;
                     work is not null;
                     work = work.Next)
                {
                    work.MarkDeviceLostNoThrow();
                }
            }
        }

        private VulkanQueueWork? DetachThrough(ulong completed)
        {
            lock (_gate)
            {
                VulkanQueueWork? retiredHead = _activeHead;
                VulkanQueueWork? retiredTail = null;
                while (_activeHead is VulkanQueueWork work &&
                       work.CompletionValue <= completed)
                {
                    retiredTail = work;
                    _activeHead = work.Next;
                }
                if (retiredTail is null)
                    return null;
                retiredTail.Next = null;
                if (_activeHead is null)
                    _activeTail = null;
                return retiredHead;
            }
        }

        private void RetireDetached(VulkanQueueWork? retired)
        {
            while (retired is VulkanQueueWork work)
            {
                retired = work.Next;
                work.Next = null;
                if (!work.RetireNoThrow())
                {
                    work.MarkDeviceLostNoThrow();
                    _ = _device.PublishInternalDeviceLoss(
                        "Vulkan completion retirement failed after native completion.");
                }
                ReturnWork(work);
            }
        }

        private void ReleaseWorkNoThrow()
        {
            VulkanQueueWork? active;
            lock (_gate)
            {
                active = _activeHead;
                _activeHead = null;
                _activeTail = null;
                _freeHead = null;
            }
            while (active is VulkanQueueWork work)
            {
                active = work.Next;
                work.Next = null;
                work.MarkDeviceLostNoThrow();
                work.ReleaseLostReferencesNoThrow();
                work.ClearReferences();
            }
        }
    }
}
