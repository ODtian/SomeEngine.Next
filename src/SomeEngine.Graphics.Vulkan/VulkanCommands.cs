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

        int completionWaitCount = desc.CompletionWaits.Length;
        int imageCount = desc.SwapchainImages.Length;
        int timelineWaitCount = desc.TimelineWaits.Length;
        int timelineSignalCount = desc.TimelineSignals.Length;
        int waitCount = checked(completionWaitCount + imageCount + timelineWaitCount);
        int signalCount = checked(imageCount + timelineSignalCount + 1);
        int commandCount = desc.Commands.Length;
        SemaphoreSubmitInfo[] waits = new SemaphoreSubmitInfo[waitCount];
        SemaphoreSubmitInfo[] signals = new SemaphoreSubmitInfo[signalCount];
        CommandBufferSubmitInfo[] commands = new CommandBufferSubmitInfo[commandCount];
        VulkanRecordedCommandsLease[] leases = new VulkanRecordedCommandsLease[commandCount];
        ulong[] sequences = new ulong[commandCount];
        VulkanSwapchainImageLease[] imageLeases = new VulkanSwapchainImageLease[imageCount];
        ulong[] imageSequences = new ulong[imageCount];
        IVulkanRetained[] retainedTimelines = new IVulkanRetained[
            checked(timelineWaitCount + timelineSignalCount)];
        int claimed = 0;
        int claimedImages = 0;
        int retainedTimelineCount = 0;
        bool accepted = false;
        try
        {
            PrepareCompletionWaits(nativeQueue, desc.CompletionWaits, waits);
            claimed = ClaimCommands(
                nativeQueue,
                desc.Commands,
                commands,
                leases,
                sequences);
            claimedImages = ClaimSwapchainImages(
                nativeQueue,
                desc.SwapchainImages,
                waits.AsSpan(completionWaitCount),
                signals,
                imageLeases,
                imageSequences);
            retainedTimelineCount = PrepareTimelineSemaphores(
                nativeQueue,
                desc.TimelineWaits,
                desc.TimelineSignals,
                waits.AsSpan(completionWaitCount + imageCount),
                signals.AsSpan(imageCount),
                retainedTimelines);

            lock (nativeQueue.SubmitGate)
            {
                ((VulkanDevice)nativeQueue.Device).ThrowIfUnavailable();
                ulong completionValue = nativeQueue.ReserveCompletionValue();
                signals[imageCount + timelineSignalCount] = new SemaphoreSubmitInfo
                {
                    SType = StructureType.SemaphoreSubmitInfo,
                    Semaphore = nativeQueue.CompletionSemaphore,
                    Value = completionValue,
                    StageMask = PipelineStageFlags2.AllCommandsBit,
                    DeviceIndex = 0,
                };
                var pending = new VulkanPendingSubmission(
                    completionValue,
                    leases,
                    sequences,
                    claimed,
                    retainedTimelines,
                    retainedTimelineCount);
                nativeQueue.PrepareSubmission(pending);
                fixed (SemaphoreSubmitInfo* waitPointer = waits)
                fixed (SemaphoreSubmitInfo* signalPointer = signals)
                fixed (CommandBufferSubmitInfo* commandPointer = commands)
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
                    ThrowIfFailed(
                        Api.QueueSubmit2(nativeQueue.Native, 1, &submit, default),
                        "vkQueueSubmit2");
                }
                accepted = true;
                for (int index = 0; index < claimed; index++)
                    leases[index].MarkSubmitted(sequences[index]);
                for (int index = 0; index < claimedImages; index++)
                    imageLeases[index].MarkSubmission(nativeQueue, completionValue);
                nativeQueue.RegisterSubmission(pending);
                return new QueueCompletion(nativeQueue, completionValue);
            }
        }
        catch
        {
            if (!accepted)
            {
                for (int index = 0; index < claimed; index++)
                    leases[index].RestoreExecutable(sequences[index]);
                for (int index = 0; index < claimedImages; index++)
                    imageLeases[index].Restore(imageSequences[index]);
                for (int index = retainedTimelineCount - 1; index >= 0; index--)
                    retainedTimelines[index].ReleaseNative();
            }
            throw;
        }
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
        Result result = Api.WaitSemaphores(
            ((VulkanDevice)queue.Device).Native,
            &wait,
            nanoseconds);
        if (result == Result.Timeout)
            return WaitStatus.Timeout;
        ThrowIfFailed(result, "vkWaitSemaphores");
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
                ThrowIfFailed(
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
                ThrowIfFailed(
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
                ThrowIfFailed(
                    _device.Backend.Api.EndCommandBuffer(slot.Native),
                    "vkEndCommandBuffer");
                _recording = null;
                sequence = checked(++_nextSequence);
            }
            var lease = new VulkanRecordedCommandsLease(this, slot, _queue);
            lease.ActivateCommands(sequence);
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
            slot.Capture(value);
        }

        internal void ReturnSlot(VulkanCommandSlot slot)
        {
            slot.ReleaseCaptures();
            lock (_gate)
            {
                if (_released)
                {
                    DestroySlot(slot);
                    return;
                }
                _freeSlots.Push(slot);
            }
        }

        internal override void Release(bool fromParent)
        {
            VulkanCommandSlot[] destroy;
            VulkanCommandSlot? recording;
            lock (_gate)
            {
                if (_released)
                    return;
                _released = true;
                recording = _recording;
                _recording = null;
                destroy = _freeSlots.ToArray();
                _freeSlots.Clear();
            }
            if (recording is not null)
                ReturnSlot(recording);
            foreach (VulkanCommandSlot slot in destroy)
                DestroySlot(slot);
            _device.UnregisterChild(this);
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
            ThrowIfFailed(
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
                ThrowIfFailed(
                    _device.Backend.Api.AllocateCommandBuffers(
                        _device.Native,
                        &allocateInfo,
                        &commandBuffer),
                    "vkAllocateCommandBuffers");
                var slot = new VulkanCommandSlot(pool, commandBuffer);
                _slots.Add(slot);
                return slot;
            }
            catch
            {
                _device.Backend.Api.DestroyCommandPool(_device.Native, pool, null);
                throw;
            }
        }

        private void DestroySlot(VulkanCommandSlot slot)
        {
            if (!_slots.Remove(slot))
                return;
            slot.ReleaseCaptures();
            if (_device.Native.Handle != 0)
                _device.Backend.Api.DestroyCommandPool(_device.Native, slot.Pool, null);
        }
    }

    private sealed class VulkanCommandSlot(CommandPool pool, VkCommandBuffer native)
    {
        private readonly HashSet<IVulkanRetained> _captures = [];

        internal CommandPool Pool { get; } = pool;
        internal VkCommandBuffer Native { get; } = native;

        internal void ResetCaptures(uint capacity)
        {
            if (_captures.Count != 0)
                throw new InvalidOperationException("A Vulkan command slot still retains native dependencies.");
            _captures.EnsureCapacity(checked((int)capacity));
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
        }
    }

    private sealed class VulkanRecordedCommandsLease : RecordedCommandsLease
    {
        private readonly VulkanCommandContext _context;
        private readonly VulkanCommandSlot _slot;
        private int _returned;

        internal VulkanRecordedCommandsLease(
            VulkanCommandContext context,
            VulkanCommandSlot slot,
            VulkanQueue queue)
            : base(context.Device, queue)
        {
            _context = context;
            _slot = slot;
        }

        internal void ActivateCommands(ulong sequence) => Activate(sequence);

        internal VkCommandBuffer GetNative(ulong sequence)
        {
            EnsureSequence(sequence);
            return _slot.Native;
        }

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

    private sealed class VulkanPendingSubmission(
        ulong completionValue,
        VulkanRecordedCommandsLease[] leases,
        ulong[] sequences,
        int count,
        IVulkanRetained[] retained,
        int retainedCount)
    {
        internal ulong CompletionValue { get; } = completionValue;

        internal void Retire()
        {
            for (int index = 0; index < count; index++)
                leases[index].Retire(sequences[index]);
            for (int index = retainedCount - 1; index >= 0; index--)
                retained[index].ReleaseNative();
        }
    }
}

internal sealed unsafe partial class VulkanBackend
{
    private sealed partial class VulkanQueue
    {
        private readonly List<VulkanPendingSubmission> _pending = [];

        internal ulong GetCompletedValue()
        {
            ulong completed = 0;
            ThrowIfFailed(
                _device.Backend.Api.GetSemaphoreCounterValue(
                    _device.Native,
                    _completion,
                    &completed),
                "vkGetSemaphoreCounterValue");
            return completed;
        }

        internal void PrepareSubmission(VulkanPendingSubmission submission)
        {
            _pending.EnsureCapacity(checked(_pending.Count + 1));
        }

        internal void RegisterSubmission(VulkanPendingSubmission submission) =>
            _pending.Add(submission);

        internal void CollectCompleted()
        {
            if (_completion.Handle == 0)
                return;
            ulong completed = GetCompletedValue();
            int retireCount = 0;
            while (retireCount < _pending.Count &&
                _pending[retireCount].CompletionValue <= completed)
            {
                _pending[retireCount].Retire();
                retireCount++;
            }
            if (retireCount != 0)
                _pending.RemoveRange(0, retireCount);
        }
    }
}
