namespace SomeEngine.Graphics.Vulkan;
internal sealed unsafe partial class VulkanBackend
{
    internal CommandContext CreateCommandContext(RhiDevice device, in CommandContextDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanQueue queue = nativeDevice.GetQueue(desc.QueueType, desc.QueueIndex);
        var context = new VulkanCommandContext(nativeDevice, queue, desc);
        try
        {
            context.PrepareBuffers(desc.InitialBufferCount);
            nativeDevice.RegisterChild(context);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    internal void Begin(CommandContext context, in CommandRecordingDesc desc = default) => ((VulkanCommandContext)context).Begin(desc);
    internal RecordedCommands End(CommandContext context) => ((VulkanCommandContext)context).EndCommands();
    internal RecordedBundle EndBundle(CommandContext context) => default!;
    internal void Discard(CommandContext context) => ((VulkanCommandContext)context).Discard();
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
        int referencedImageCapacity = CountReferencedSwapchainUses(nativeQueue, desc.Commands);
        VulkanQueueWork work = nativeQueue.TakeWork();
        int claimed = 0;
        int claimedImages = 0;
        int retainedTimelineCount = 0;
        try
        {
            work.EnsureCapacity(commandCount, imageCount, referencedImageCapacity, timelineCount, waitCount, signalCount);
            PrepareCompletionWaits(nativeQueue, desc.CompletionWaits, work.NativeWaits.AsSpan(0, completionWaitCount));
            work.ReferencedImageCount = CollectReferencedSwapchainUses(nativeQueue, desc.Commands, work.ReferencedImages);
            ValidateSwapchainImageSet(
                nativeQueue,
                desc.SwapchainImages,
                work.ReferencedImages.AsSpan(0, work.ReferencedImageCount),
                work.ImageMatches.AsSpan(0, work.ReferencedImageCount)
            );
            ClaimCommands(
                nativeQueue,
                desc.Commands,
                work.NativeCommands.AsSpan(0, commandCount),
                work.CommandData.AsSpan(0, commandCount),
                work.CommandSequences.AsSpan(0, commandCount),
                ref claimed
            );
            work.CommandCount = claimed;
            ClaimSwapchainImages(
                nativeQueue,
                desc.SwapchainImages,
                work.NativeWaits.AsSpan(completionWaitCount, imageCount),
                work.NativeSignals.AsSpan(0, imageCount),
                work.Images.AsSpan(0, imageCount),
                work.ImageSequences.AsSpan(0, imageCount),
                ref claimedImages
            );
            work.ImageCount = claimedImages;
            retainedTimelineCount = PrepareTimelineSemaphores(
                nativeQueue,
                desc.TimelineWaits,
                desc.TimelineSignals,
                work.NativeWaits.AsSpan(completionWaitCount + imageCount, timelineWaitCount),
                work.NativeSignals.AsSpan(imageCount, timelineSignalCount),
                work.RetainedTimelines.AsSpan(0, timelineCount)
            );
            work.TimelineCount = retainedTimelineCount;
            return SubmitBatch(
                nativeQueue,
                device,
                ref work,
                commandCount,
                claimed,
                claimedImages,
                waitCount,
                signalCount,
                imageCount,
                timelineSignalCount
            );
        }
        catch
        {
            RestoreSubmission(nativeQueue, device, work, claimed, claimedImages, retainedTimelineCount);
            throw;
        }
    }

    private QueueCompletion SubmitBatch(
        VulkanQueue queue,
        VulkanDevice device,
        ref VulkanQueueWork work,
        int commandCount,
        int claimedCommandCount,
        int claimedImageCount,
        int waitCount,
        int signalCount,
        int imageCount,
        int timelineSignalCount
    )
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
            Result result = QueueSubmitNative(queue, work, commandCount, waitCount, signalCount);
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
                    throw device.PublishInternalDeviceLoss($"vkQueueSubmit2 returned uncertain failure {result}.");
            }

            try
            {
                CommitSubmittedWork(queue, work, completionValue, claimedCommandCount, claimedImageCount);
            }
            catch
            {
                work.MarkDeviceLostNoThrow();
                queue.AppendAcceptedNoThrow(work);
                work = null !;
                throw device.PublishInternalDeviceLoss("Submission state commit failed after native acceptance.");
            }

            queue.AppendAcceptedNoThrow(work);
            work = null !;
            return new QueueCompletion(queue, completionValue);
        }
    }

    private Result QueueSubmitNative(VulkanQueue queue, VulkanQueueWork work, int commandCount, int waitCount, int signalCount)
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
            Result result = Api.QueueSubmit2(queue.Native, 1, &submit, default);
            return result;
        }
    }

    private static void CommitSubmittedWork(VulkanQueue queue, VulkanQueueWork work, ulong completionValue, int commandCount, int imageCount)
    {
        queue.MarkSubmitted(completionValue);
        for (int index = 0; index < imageCount; index++)
            work.Images[index].MarkSubmission(queue, completionValue);
    }

    private static void RestoreSubmission(
        VulkanQueue queue,
        VulkanDevice device,
        VulkanQueueWork? work,
        int commandCount,
        int imageCount,
        int timelineCount
    )
    {
        if (work is null || work.NativeAccepted)
            return;
        work.CommandCount = commandCount;
        work.ImageCount = imageCount;
        work.TimelineCount = timelineCount;
        if (device.Status == DeviceStatus.Lost)
        {
            work.MarkDeviceLostNoThrow();
            work.ReleaseLostReferencesNoThrow();
            queue.ReturnWork(work);
            return;
        }

        for (int index = 0; index < commandCount; index++)
            work.CommandData[index].RestoreExecutable(work.CommandSequences[index]);
        for (int index = 0; index < imageCount; index++)
            work.Images[index].Restore(work.ImageSequences[index]);
        for (int index = timelineCount - 1; index >= 0; index--)
            work.RetainedTimelines[index].ReleaseNative();
        queue.ReturnWork(work);
    }

    private void PrepareCompletionWaits(VulkanQueue queue, ReadOnlySpan<QueueCompletion> source, Span<SemaphoreSubmitInfo> destination)
    {
        for (int index = 0; index < source.Length; index++)
        {
            QueueCompletion wait = source[index];
            VulkanQueue waitQueue = RequireQueue(wait.QueueReference, nameof(source));
            destination[index] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = waitQueue.CompletionSemaphore,
                Value = wait.SubmittedValue,
                StageMask = PipelineStageFlags2.AllCommandsBit,
                DeviceIndex = 0,
            };
        }
    }

    private static int CountReferencedSwapchainUses(VulkanQueue queue, ReadOnlySpan<RecordedCommands> commands)
    {
        int count = 0;
        foreach (ref readonly RecordedCommands recorded in commands)
        {
            VulkanCommandRecording handle = GetRecordedData(queue, recorded);
            count = checked(count + handle.GetSwapchainUseCount(recorded.Sequence));
        }

        return count;
    }

    private static int CollectReferencedSwapchainUses(VulkanQueue queue, ReadOnlySpan<RecordedCommands> commands, Span<VulkanSwapchainUse> destination)
    {
        int count = 0;
        foreach (ref readonly RecordedCommands recorded in commands)
        {
            VulkanCommandRecording handle = GetRecordedData(queue, recorded);
            int commandUseCount = handle.GetSwapchainUseCount(recorded.Sequence);
            for (int useIndex = 0; useIndex < commandUseCount; useIndex++)
            {
                VulkanSwapchainUse use = handle.GetSwapchainUse(recorded.Sequence, useIndex);
                bool duplicate = false;
                for (int existingIndex = 0; existingIndex < count; existingIndex++)
                {
                    ref readonly VulkanSwapchainUse existing = ref destination[existingIndex];
                    if (ReferenceEquals(existing.Handle, use.Handle) && existing.Sequence == use.Sequence)
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
        Span<bool> matched
    )
    {
        matched.Clear();
        for (int suppliedIndex = 0; suppliedIndex < supplied.Length; suppliedIndex++)
        {
            SwapchainImage image = supplied[suppliedIndex];
            VulkanSwapchainImageUse handle = (VulkanSwapchainImageUse)(image.ImageUse!);
            int match = -1;
            for (int referencedIndex = 0; referencedIndex < referenced.Length; referencedIndex++)
            {
                if (matched[referencedIndex])
                    continue;
                ref readonly VulkanSwapchainUse expected = ref referenced[referencedIndex];
                if (ReferenceEquals(expected.Handle, handle) && expected.Sequence == image.Sequence && expected.Generation == handle.Swapchain.Info.Generation)
                {
                    match = referencedIndex;
                    break;
                }
            }

            matched[match] = true;
        }
    }

    private static VulkanCommandRecording GetRecordedData(VulkanQueue queue, in RecordedCommands recorded)
    {
        VulkanCommandRecording handle = (VulkanCommandRecording)(recorded.Data);
        return handle;
    }

    private static void ClaimCommands(
        VulkanQueue queue,
        ReadOnlySpan<RecordedCommands> source,
        Span<CommandBufferSubmitInfo> commands,
        Span<VulkanCommandRecording> leases,
        Span<ulong> sequences,
        ref int claimed
    )
    {
        for (int index = 0; index < source.Length; index++)
        {
            RecordedCommands recorded = source[index];
            VulkanCommandRecording handle = (VulkanCommandRecording)(recorded.Data);
            ulong sequence = recorded.Sequence;
            handle.TryBeginSubmit(sequence);
            leases[index] = handle;
            sequences[index] = sequence;
            claimed++;
            commands[index] = new CommandBufferSubmitInfo
            {
                SType = StructureType.CommandBufferSubmitInfo,
                CommandBuffer = handle.GetNative(sequence),
                DeviceMask = 0,
            };
        }
    }

    private static void ClaimSwapchainImages(
        VulkanQueue queue,
        ReadOnlySpan<SwapchainImage> source,
        Span<SemaphoreSubmitInfo> waits,
        Span<SemaphoreSubmitInfo> signals,
        Span<VulkanSwapchainImageUse> leases,
        Span<ulong> sequences,
        ref int claimed
    )
    {
        for (int index = 0; index < source.Length; index++)
        {
            SwapchainImage image = source[index];
            VulkanSwapchainImageUse handle = (VulkanSwapchainImageUse)(image.ImageUse!);
            leases[index] = handle;
            sequences[index] = image.Sequence;
            claimed++;
            waits[index] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = handle.AcquireSemaphore,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
            signals[index] = new SemaphoreSubmitInfo
            {
                SType = StructureType.SemaphoreSubmitInfo,
                Semaphore = handle.RenderComplete,
                StageMask = PipelineStageFlags2.AllCommandsBit,
            };
        }
    }

    private int PrepareTimelineSemaphores(
        VulkanQueue queue,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        ReadOnlySpan<TimelineSignal> timelineSignals,
        Span<SemaphoreSubmitInfo> waits,
        Span<SemaphoreSubmitInfo> signals,
        Span<IVulkanRetained> retained
    )
    {
        int retainedCount = 0;
        try
        {
            for (int index = 0; index < timelineWaits.Length; index++)
            {
                VulkanExternalTimeline timeline = RequireExternalTimeline(timelineWaits[index].Timeline, nameof(timelineWaits));
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
                VulkanExternalTimeline timeline = RequireExternalTimeline(timelineSignals[index].Timeline, nameof(timelineSignals));
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
        VulkanQueue queue = RequireQueue(completion.QueueReference, nameof(completion));
        return queue.GetCompletedValue() >= completion.SubmittedValue;
    }

    internal WaitStatus WaitCpu(in QueueCompletion completion, TimeSpan timeout)
    {
        VulkanQueue queue = RequireQueue(completion.QueueReference, nameof(completion));
        ulong value = completion.SubmittedValue;
        VkSemaphore semaphore = queue.CompletionSemaphore;
        SemaphoreWaitInfo wait = new()
        {
            SType = StructureType.SemaphoreWaitInfo,
            SemaphoreCount = 1,
            PSemaphores = &semaphore,
            PValues = &value,
        };
        ulong nanoseconds = timeout == Timeout.InfiniteTimeSpan ? ulong.MaxValue : checked((ulong)timeout.Ticks * 100);
        VulkanDevice device = (VulkanDevice)queue.Device;
        Result result = Api.WaitSemaphores(device.Native, &wait, nanoseconds);
        if (result == Result.Timeout)
            return WaitStatus.Timeout;
        device.ThrowIfDeviceCallFailed(result, "vkWaitSemaphores");
        queue.CollectCompleted();
        return WaitStatus.Completed;
    }

    private VulkanQueue RequireQueue(RhiQueue queue, string parameterName) => (VulkanQueue)queue;
    private sealed partial class VulkanCommandContext : CommandContext
    {
        private readonly object _gate = new();
        private readonly VulkanDevice _device;
        private readonly VulkanQueue _queue;
        private readonly Stack<VulkanCommandBuffer> _freeBuffers = [];
        private readonly HashSet<VulkanCommandBuffer> _buffers = [];
        private VulkanCommandBuffer? _recording;
        private ulong _nextSequence;
        private bool _released;
        private int _unregistered;
        internal VulkanCommandContext(VulkanDevice device, VulkanQueue queue, in CommandContextDesc desc) : base(device, desc.QueueType, desc.QueueIndex, desc.Bundle, desc.Label)
        {
            _device = device;
            _queue = queue;
        }

        internal VkCommandBuffer NativeRecording => _recording!.Native;
        internal VulkanDescriptorArena RecordingDescriptorArena => _recording!.DescriptorArena;

        internal void PrepareBuffers(uint count)
        {
            for (uint index = 0; index < count; index++)
                _freeBuffers.Push(CreateCommandBuffer());
        }

        internal void Begin(in CommandRecordingDesc desc)
        {
            lock (_gate)
            {
                VulkanCommandBuffer entry = _freeBuffers.Count != 0 ? _freeBuffers.Pop() : CreateCommandBuffer();
                _device.ThrowIfDeviceCallFailed(_device.Backend.Api.ResetCommandPool(_device.Native, entry.Pool, CommandPoolResetFlags.None), "vkResetCommandPool");
                ResetRecordingState();
                CommandBufferBeginInfo begin = new()
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                _device.ThrowIfDeviceCallFailed(_device.Backend.Api.BeginCommandBuffer(entry.Native, &begin), "vkBeginCommandBuffer");
                _recording = entry;
            }
        }

        internal RecordedCommands EndCommands()
        {
            VulkanCommandBuffer entry;
            ulong sequence;
            lock (_gate)
            {
                entry = _recording!;
                FinalizeOptionalScopes();
                _device.ThrowIfDeviceCallFailed(_device.Backend.Api.EndCommandBuffer(entry.Native), "vkEndCommandBuffer");
                _recording = null;
                sequence = checked(++_nextSequence);
            }

            VulkanCommandRecording handle = entry.Handle;
            handle.ActivateCommands(sequence);
            entry.AttachUse(handle, sequence);
            return new RecordedCommands(handle, sequence);
        }

        internal void Discard()
        {
            VulkanCommandBuffer entry;
            lock (_gate)
            {
                entry = _recording!;
                _recording = null;
                ResetRecordingState();
            }

            ReturnCommandBuffer(entry);
        }

        internal void RecordSwapchainUse(IVulkanRetained value)
        {
            VulkanCommandBuffer entry = _recording!;
            VulkanTexture? texture = value switch
            {
                VulkanTexture direct => direct,
                IVulkanTextureView view => view.Texture,
                _ => null,
            };
            VulkanSwapchainUse? swapchainUse = null;
            if (texture?.SwapchainState is VulkanImageState imageState)
            {
                imageState.TryGetCurrentUse(out VulkanSwapchainUse use);
                swapchainUse = use;
            }

            if (swapchainUse is VulkanSwapchainUse captured)
                entry.AddSwapchainUse(captured);
        }

        internal void RetainDescriptorData(IVulkanRetained data) => _recording!.RetainDescriptorData(data);
        internal void ReturnCommandBuffer(VulkanCommandBuffer entry)
        {
            bool destroy;
            bool unregister;
            lock (_gate)
            {
                if (!_buffers.Contains(entry))
                    return;
                entry.DetachUse();
                if (_released)
                {
                    destroy = _buffers.Remove(entry);
                }
                else
                {
                    entry.ResetAfterCompletion();
                    _freeBuffers.Push(entry);
                    destroy = false;
                }

                unregister = _released && _buffers.Count == 0;
            }

            if (destroy)
                DestroyCommandBuffer(entry);
            if (unregister)
                UnregisterOnce();
        }

        internal override void Release(bool fromParent)
        {
            ReleaseCommandBuffers(abandonAll: fromParent || _device.IsDisposed || _device.Status != DeviceStatus.Active);
        }

        internal void ReleaseFromDeviceNoThrow()
        {
            try
            {
                ReleaseCommandBuffers(abandonAll: true);
            }
            catch (Exception exception)
            {
                _device.RecordReleaseFailure(exception);
            }
        }

        private void ReleaseCommandBuffers(bool abandonAll)
        {
            VulkanCommandBuffer[] destroy;
            bool unregister;
            lock (_gate)
            {
                if (_released && !abandonAll)
                    return;
                _released = true;
                ResetRecordingState();
                if (abandonAll)
                {
                    destroy = _buffers.ToArray();
                    _buffers.Clear();
                }
                else
                {
                    var detached = new List<VulkanCommandBuffer>(_freeBuffers.Count + 1);
                    while (_freeBuffers.TryPop(out VulkanCommandBuffer? free))
                    {
                        if (_buffers.Remove(free))
                            detached.Add(free);
                    }

                    if (_recording is not null && _buffers.Remove(_recording))
                        detached.Add(_recording);
                    destroy = detached.ToArray();
                }

                _recording = null;
                _freeBuffers.Clear();
                unregister = _buffers.Count == 0;
            }

            if (abandonAll)
            {
                foreach (VulkanCommandBuffer entry in destroy)
                    entry.DiscardExecutableFromDeviceNoThrow();
            }

            Exception? failure = null;
            foreach (VulkanCommandBuffer entry in destroy)
            {
                try
                {
                    DestroyCommandBuffer(entry);
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
                foreach (VulkanCommandBuffer entry in _buffers)
                    entry.MarkDeviceLostNoThrow();
            }
        }

        private VulkanCommandBuffer CreateCommandBuffer()
        {
            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.TransientBit,
                QueueFamilyIndex = _queue.FamilyIndex,
            };
            CommandPool pool = default;
            _device.ThrowIfDeviceCallFailed(_device.Backend.Api.CreateCommandPool(_device.Native, &poolInfo, null, &pool), "vkCreateCommandPool");
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
                _device.ThrowIfDeviceCallFailed(_device.Backend.Api.AllocateCommandBuffers(_device.Native, &allocateInfo, &commandBuffer), "vkAllocateCommandBuffers");
                var entry = new VulkanCommandBuffer(_device, pool, commandBuffer);
                entry.InitializeData(new VulkanCommandRecording(this, entry, _queue));
                _buffers.Add(entry);
                return entry;
            }
            catch
            {
                _device.Backend.Api.DestroyCommandPool(_device.Native, pool, null);
                throw;
            }
        }

        private void DestroyCommandBuffer(VulkanCommandBuffer entry)
        {
            entry.ResetAfterCompletion();
            entry.DescriptorArena.Release();
            if (_device.Native.Handle != 0)
                _device.Backend.Api.DestroyCommandPool(_device.Native, entry.Pool, null);
        }

        private void UnregisterOnce()
        {
            if (Interlocked.Exchange(ref _unregistered, 1) == 0)
                _device.UnregisterChild(this);
        }
    }

    private sealed partial class VulkanCommandBuffer(VulkanDevice device, CommandPool pool, VkCommandBuffer native)
    {
        private readonly HashSet<IVulkanRetained> _descriptorData = [];
        private VulkanSwapchainUse[] _swapchainUses = [];
        private int _swapchainUseCount;
        private VulkanCommandRecording? _activeUse;
        private ulong _activeSequence;
        internal CommandPool Pool { get; } = pool;
        internal VkCommandBuffer Native { get; } = native;
        internal VulkanDescriptorArena DescriptorArena { get; } = new(device);
        internal VulkanCommandRecording Handle { get; private set; } = null !;

        internal void InitializeData(VulkanCommandRecording handle)
        {
            Handle = handle;
        }

        internal void AttachUse(VulkanCommandRecording handle, ulong sequence)
        {
            _activeUse = handle;
            _activeSequence = sequence;
        }

        internal void DetachUse()
        {
            _activeUse = null;
            _activeSequence = 0;
        }

        internal void MarkDeviceLostNoThrow()
        {
            try
            {
                _activeUse?.MarkDeviceLost(_activeSequence);
            }
            catch
            {
            }
        }

        internal void DiscardExecutableFromDeviceNoThrow()
        {
            try
            {
                Handle.DiscardExecutableFromDeviceNoThrow();
            }
            catch
            {
            }
        }

        internal void AddSwapchainUse(in VulkanSwapchainUse use)
        {
            for (int index = 0; index < _swapchainUseCount; index++)
            {
                ref readonly VulkanSwapchainUse existing = ref _swapchainUses[index];
                if (ReferenceEquals(existing.Handle, use.Handle) && existing.Sequence == use.Sequence)
                {
                    return;
                }
            }

            if (_swapchainUses.Length == _swapchainUseCount)
            {
                int capacity = _swapchainUses.Length == 0 ? 4 : checked(_swapchainUses.Length * 2);
                Array.Resize(ref _swapchainUses, capacity);
            }

            _swapchainUses[_swapchainUseCount++] = use;
        }

        internal int SwapchainUseCount => _swapchainUseCount;

        internal void CopySwapchainUses(Span<VulkanSwapchainUse> destination)
        {
            _swapchainUses.AsSpan(0, _swapchainUseCount).CopyTo(destination);
        }

        internal VulkanSwapchainUse GetSwapchainUse(int index)
        {
            return _swapchainUses[index];
        }

        internal void RetainDescriptorData(IVulkanRetained data)
        {
            if (_descriptorData.Add(data))
                data.RetainNative();
        }

        internal void ResetAfterCompletion()
        {
            foreach (IVulkanRetained value in _descriptorData)
                value.ReleaseNative();
            _descriptorData.Clear();
            DescriptorArena.ResetAfterCompletion();
            Array.Clear(_swapchainUses, 0, _swapchainUseCount);
            _swapchainUseCount = 0;
        }
    }

    private sealed class VulkanCommandRecording : RecordedCommandsData
    {
        private readonly VulkanCommandContext _context;
        private readonly VulkanCommandBuffer _commandBuffer;
        private int _returned = 1;
        internal VulkanCommandRecording(VulkanCommandContext context, VulkanCommandBuffer entry, VulkanQueue queue) : base(context.Device, queue)
        {
            _context = context;
            _commandBuffer = entry;
        }

        internal void ActivateCommands(ulong sequence)
        {
            _ = Interlocked.CompareExchange(ref _returned, 0, 1) != 1;
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
            return _commandBuffer.Native;
        }

        internal int GetSwapchainUseCount(ulong sequence)
        {
            return _commandBuffer.SwapchainUseCount;
        }

        internal void CopySwapchainUses(ulong sequence, Span<VulkanSwapchainUse> destination)
        {
            _commandBuffer.CopySwapchainUses(destination);
        }

        internal VulkanSwapchainUse GetSwapchainUse(ulong sequence, int index)
        {
            return _commandBuffer.GetSwapchainUse(index);
        }

        internal void DiscardExecutableFromDeviceNoThrow() => _ = TryDiscardExecutableFromDevice(out _);
        internal void Retire(ulong sequence)
        {
            ReturnCommandBuffer(sequence);
        }

        protected override void DiscardUnsubmitted(ulong sequence) => ReturnCommandBuffer(sequence);
        private void ReturnCommandBuffer(ulong sequence)
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                _context.ReturnCommandBuffer(_commandBuffer);
        }
    }

    private sealed class VulkanQueueWork
    {
        internal ulong CompletionValue;
        internal VulkanCommandRecording[] CommandData = [];
        internal ulong[] CommandSequences = [];
        internal int CommandCount;
        internal VulkanSwapchainImageUse[] Images = [];
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
            int signalCount
        )
        {
            EnsureArray(ref CommandData, commandCount);
            EnsureArray(ref CommandSequences, commandCount);
            EnsureArray(ref Images, imageCount);
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
                    CommandData[index].Retire(CommandSequences[index]);
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
                    CommandData[index].MarkDeviceLost(CommandSequences[index]);
                }
                catch
                {
                }
            }

            for (int index = 0; index < ImageCount; index++)
                Images[index].Invalidate(deviceLost: true);
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
            Array.Clear(CommandData, 0, CommandCount);
            Array.Clear(Images, 0, ImageCount);
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
            Result result = _device.Backend.Api.GetSemaphoreCounterValue(_device.Native, _completion, &completed);
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
                for (VulkanQueueWork? work = _activeHead; work is not null; work = work.Next)
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
                while (_activeHead is VulkanQueueWork work && work.CompletionValue <= completed)
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
                    _ = _device.PublishInternalDeviceLoss("Vulkan completion retirement failed after native completion.");
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
