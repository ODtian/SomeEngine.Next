using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

#if SOMEENGINE_RHI_BENCHMARK_TIMING
internal interface IBenchmarkCommandTiming
{
    void CloseCommandsForBenchmark(CommandContext context);
    RecordedCommands FinishCommandsForBenchmark(CommandContext context);
}
#endif

internal sealed unsafe partial class D3D12Backend
{
    public CommandContext CreateCommandContext(
        Device device,
        in CommandContextDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();
        if (desc.InitialSlotCount == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "InitialSlotCount must be nonzero.");

        D3D12Queue queue = nativeDevice.GetQueue(desc.QueueType, desc.QueueIndex);
        D3D12CommandContext result = new(nativeDevice, queue, desc);
        try
        {
            result.PrepareSlots(desc.InitialSlotCount);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Begin(CommandContext context, in CommandRecordingDesc desc = default) =>
        RequireCommandContext(context, nameof(context)).Begin(desc);

    public RecordedCommands End(CommandContext context) =>
        RequireCommandContext(context, nameof(context)).EndCommands();

#if SOMEENGINE_RHI_BENCHMARK_TIMING
    void IBenchmarkCommandTiming.CloseCommandsForBenchmark(CommandContext context) =>
        RequireCommandContext(context, nameof(context)).CloseCommandsForBenchmark();

    RecordedCommands IBenchmarkCommandTiming.FinishCommandsForBenchmark(
        CommandContext context) =>
        RequireCommandContext(context, nameof(context)).FinishCommandsForBenchmark();
#endif

    public RecordedBundle EndBundle(CommandContext context) =>
        RequireCommandContext(context, nameof(context)).EndBundle();

    public void Discard(CommandContext context) =>
        RequireCommandContext(context, nameof(context)).Discard();

    public QueueCompletion Submit(Queue queue, in QueueSubmitDesc desc)
    {
        D3D12Queue nativeQueue = RequireQueue(queue, nameof(queue));
        nativeQueue.Device.ThrowIfUnavailable();

        if (IsEmptySubmission(desc))
            return SignalEmptySubmission(nativeQueue);

        int completionWaitCount = desc.CompletionWaits.Length;
        int timelineWaitCount = desc.TimelineWaits.Length;
        int commandCount = desc.Commands.Length;
        int imageCount = desc.SwapchainImages.Length;
        int timelineSignalCount = desc.TimelineSignals.Length;
        int timelineCount = checked(timelineWaitCount + timelineSignalCount);
        D3D12PendingSubmission submission = nativeQueue.AcquireSubmission(
            commandCount,
            completionWaitCount,
            timelineCount,
            imageCount);
        int claimedImages = 0;
        int retainedTimelines = 0;
        bool accepted = false;
        bool transferred = false;

        try
        {
            PrepareSubmissionWaits(desc, submission);

            ClaimCommandPayloads(nativeQueue, desc.Commands, submission);
            CollectReferencedSwapchainImages(submission);
            ValidateSwapchainImages(nativeQueue, desc.SwapchainImages, submission);
            claimedImages = ClaimSwapchainImages(nativeQueue, submission);
            retainedTimelines = RetainSubmissionTimelines(submission);

            using (nativeQueue.Gate.EnterScope())
            {
                nativeQueue.Device.ThrowIfUnavailable();
                QueueCompletion completion = ExecuteSubmissionUnderGate(
                    nativeQueue,
                    timelineWaitCount,
                    timelineSignalCount,
                    submission,
                    ref accepted);
                transferred = true;
                return completion;
            }
        }
        catch (Exception exception)
        {
            if (!accepted &&
                exception is GraphicsException { Error: GraphicsError.DeviceLost } preAcceptanceLoss)
            {
                for (int index = 0; index < submission.PayloadCount; index++)
                {
                    submission.Payloads[index].MarkDeviceLostAndAbandon(
                        submission.PayloadSequences[index]);
                }
                for (int index = 0; index < retainedTimelines; index++)
                    submission.Timelines[index].ReleaseSubmission();
                throw nativeQueue.Device.Loss ?? preAcceptanceLoss;
            }

            if (!accepted)
            {
                for (int index = 0; index < claimedImages; index++)
                {
                    submission.Images[index].RestoreAcquired(
                        submission.ImageSequences[index]);
                }
                for (int index = 0; index < submission.PayloadCount; index++)
                {
                    submission.Payloads[index].RestoreExecutable(
                        submission.PayloadSequences[index]);
                }
                for (int index = 0; index < retainedTimelines; index++)
                    submission.Timelines[index].ReleaseSubmission();
                throw;
            }

            GraphicsException loss = exception as GraphicsException is { Error: GraphicsError.DeviceLost }
                ? (GraphicsException)exception
                : new GraphicsException(
                    GraphicsError.DeviceLost,
                    "The D3D12 Queue failed after accepting part of a submission.",
                    exception is GraphicsException graphics ? graphics.NativeCode : null,
                    innerException: exception);
            loss = nativeQueue.NativeDevice.MarkLost(loss);
            for (int index = 0; index < claimedImages; index++)
            {
                submission.Images[index].Invalidate(deviceLost: true);
                submission.Images[index].NativeSwapchain.MarkDeviceLost();
            }
            for (int index = 0; index < submission.PayloadCount; index++)
            {
                submission.Payloads[index].MarkDeviceLostRetained(
                    submission.PayloadSequences[index]);
            }
            nativeQueue.RegisterUntrustedSubmission(submission);
            transferred = true;
            throw loss;
        }
        finally
        {
            if (!transferred)
                nativeQueue.ReturnSubmission(submission);
        }
    }

    private static bool IsEmptySubmission(in QueueSubmitDesc desc) =>
        desc.CompletionWaits.IsEmpty &&
        desc.TimelineWaits.IsEmpty &&
        desc.Commands.IsEmpty &&
        desc.SwapchainImages.IsEmpty &&
        desc.TimelineSignals.IsEmpty;

    private static QueueCompletion SignalEmptySubmission(D3D12Queue queue)
    {
        using (queue.Gate.EnterScope())
        {
            queue.Device.ThrowIfUnavailable();
            return queue.SignalCompletionUnderGate();
        }
    }

    private void PrepareSubmissionWaits(
        in QueueSubmitDesc desc,
        D3D12PendingSubmission submission)
    {
        for (int index = 0; index < desc.CompletionWaits.Length; index++)
        {
            QueueCompletion wait = desc.CompletionWaits[index];
            submission.CompletionWaitQueues[index] = RequireQueue(wait.Queue, nameof(desc));
            submission.CompletionWaitValues[index] = wait.Value;
            submission.CompletionWaitCount = index + 1;
        }

        int timelineWaitCount = desc.TimelineWaits.Length;
        for (int index = 0; index < timelineWaitCount; index++)
        {
            TimelinePoint wait = desc.TimelineWaits[index];
            submission.Timelines[index] = RequireTimeline(wait.Timeline);
            submission.TimelineValues[index] = wait.Value;
            submission.TimelineCount = index + 1;
        }
        for (int index = 0; index < desc.TimelineSignals.Length; index++)
        {
            TimelineSignal signal = desc.TimelineSignals[index];
            int target = timelineWaitCount + index;
            submission.Timelines[target] = RequireTimeline(signal.Timeline);
            submission.TimelineValues[target] = signal.Value;
            submission.TimelineCount = target + 1;
        }
    }

    private static void ClaimCommandPayloads(
        D3D12Queue queue,
        ReadOnlySpan<RecordedCommands> commands,
        D3D12PendingSubmission submission)
    {
        int requiredSwapchainUseCapacity = 0;
        for (int index = 0; index < commands.Length; index++)
        {
            RecordedCommands command = commands[index];
            ulong sequence = command.Sequence;
            if (command.Lease is not D3D12RecordedCommandsLease payload ||
                !ReferenceEquals(payload.Queue, queue))
            {
                throw new ArgumentException(
                    "Every RecordedCommands payload must target the submitted Queue.",
                    nameof(commands));
            }
            if (!payload.TryBeginSubmit(sequence))
            {
                throw new InvalidOperationException(
                    "A RecordedCommands payload has no submission right.");
            }

            submission.Payloads[index] = payload;
            submission.PayloadSequences[index] = sequence;
            submission.PayloadCount = index + 1;
            submission.NativeLists[index] = (nint)payload.GetNativeList(sequence);
            requiredSwapchainUseCapacity = checked(
                requiredSwapchainUseCapacity + payload.GetSwapchainUseCount(sequence));
        }
        submission.EnsureSwapchainUseCapacity(requiredSwapchainUseCapacity);
    }

    private static void CollectReferencedSwapchainImages(
        D3D12PendingSubmission submission)
    {
        for (int index = 0; index < submission.PayloadCount; index++)
        {
            submission.ReferencedImageCount = submission.Payloads[index].AccumulateSwapchainUses(
                submission.PayloadSequences[index],
                submission.ReferencedImages,
                submission.ReferencedImageUses,
                submission.ReferencedImageCount);
        }
    }

    private static void ValidateSwapchainImages(
        D3D12Queue queue,
        ReadOnlySpan<SwapchainImage> images,
        D3D12PendingSubmission submission)
    {
        if (submission.ReferencedImageCount != images.Length)
        {
            throw new ArgumentException(
                "SwapchainImages must exactly match the images referenced by Commands.",
                nameof(images));
        }

        for (int index = 0; index < images.Length; index++)
        {
            SwapchainImage image = images[index];
            D3D12SwapchainImageLease nativeImage = image.Lease as D3D12SwapchainImageLease ??
                throw new ArgumentException(
                    "Every SwapchainImage must belong to this D3D12 backend.",
                    nameof(images));
            RequireUniqueSwapchainImage(submission.Images, index, nativeImage);
            int useIndex = FindReferencedImage(submission, nativeImage);
            if (useIndex < 0 ||
                submission.ReferencedImageUses[useIndex].Sequence != image.Sequence)
            {
                throw new ArgumentException(
                    "SwapchainImages does not match the exact acquisition referenced by Commands.",
                    nameof(images));
            }

            D3D12SubmittedSwapchainUse use = submission.ReferencedImageUses[useIndex];
            nativeImage.NativeSwapchain.ValidateSubmission(
                queue,
                nativeImage,
                image.Sequence,
                use.PresentReady);
            submission.Images[index] = nativeImage;
            submission.ImageSequences[index] = image.Sequence;
            submission.ImageUses[index] = use;
            submission.ImageCount = index + 1;
        }
    }

    private static void RequireUniqueSwapchainImage(
        D3D12SwapchainImageLease[] images,
        int count,
        D3D12SwapchainImageLease candidate)
    {
        for (int index = 0; index < count; index++)
        {
            if (ReferenceEquals(images[index], candidate))
            {
                throw new ArgumentException(
                    "SwapchainImages contains a duplicate image.",
                    nameof(images));
            }
        }
    }

    private static int FindReferencedImage(
        D3D12PendingSubmission submission,
        D3D12SwapchainImageLease image)
    {
        for (int index = 0; index < submission.ReferencedImageCount; index++)
        {
            if (ReferenceEquals(submission.ReferencedImages[index], image))
                return index;
        }
        return -1;
    }

    private static int ClaimSwapchainImages(
        D3D12Queue queue,
        D3D12PendingSubmission submission)
    {
        int claimed = 0;
        for (int index = 0; index < submission.ImageCount; index++)
        {
            if (!submission.Images[index].TryBeginSubmit(
                    submission.ImageSequences[index],
                    queue,
                    submission.ImageUses[index].PresentReady))
            {
                throw new InvalidOperationException(
                    "A SwapchainImage has no submission right.");
            }
            claimed++;
        }
        return claimed;
    }

    private static int RetainSubmissionTimelines(D3D12PendingSubmission submission)
    {
        int retained = 0;
        for (int index = 0; index < submission.TimelineCount; index++)
        {
            submission.Timelines[index].RetainSubmission();
            retained++;
        }
        return retained;
    }

    private static QueueCompletion ExecuteSubmissionUnderGate(
        D3D12Queue queue,
        int timelineWaitCount,
        int timelineSignalCount,
        D3D12PendingSubmission submission,
        ref bool accepted)
    {
        ExecuteCompletionWaits(queue, submission, ref accepted);
        ExecuteTimelineWaits(queue, timelineWaitCount, submission, ref accepted);
        ExecuteCommandLists(queue, submission, ref accepted);
        ExecuteTimelineSignals(
            queue,
            timelineWaitCount,
            timelineSignalCount,
            submission,
            ref accepted);

        QueueCompletion completion = queue.SignalCompletionUnderGate();
        accepted = true;
        for (int index = 0; index < submission.PayloadCount; index++)
        {
            submission.Payloads[index].MarkSubmitted(submission.PayloadSequences[index]);
        }
        for (int index = 0; index < submission.ImageCount; index++)
        {
            submission.Images[index].CommitSubmission(
                submission.ImageSequences[index],
                queue,
                completion.Value);
        }
        queue.RegisterSubmissionUnderGate(completion.Value, submission);
        return completion;
    }

    private static void ExecuteCompletionWaits(
        D3D12Queue queue,
        D3D12PendingSubmission submission,
        ref bool accepted)
    {
        for (int index = 0; index < submission.CompletionWaitCount; index++)
        {
            ThrowIfFailed(
                queue.NativeDevice,
                queue.Native->Wait(
                    submission.CompletionWaitQueues[index].Fence,
                    submission.CompletionWaitValues[index]),
                NativeOperationType.Ordinary,
                "ID3D12CommandQueue::Wait");
            accepted = true;
        }
    }

    private static void ExecuteTimelineWaits(
        D3D12Queue queue,
        int count,
        D3D12PendingSubmission submission,
        ref bool accepted)
    {
        for (int index = 0; index < count; index++)
        {
            ThrowIfFailed(
                queue.NativeDevice,
                queue.Native->Wait(
                    submission.Timelines[index].Native,
                    submission.TimelineValues[index]),
                NativeOperationType.Ordinary,
                "ID3D12CommandQueue::Wait(external timeline)");
            accepted = true;
        }
    }

    private static void ExecuteCommandLists(
        D3D12Queue queue,
        D3D12PendingSubmission submission,
        ref bool accepted)
    {
        if (submission.PayloadCount == 0)
            return;

        fixed (nint* lists = submission.NativeLists)
        {
            queue.Native->ExecuteCommandLists(
                checked((uint)submission.PayloadCount),
                (ID3D12CommandList**)lists);
        }
        accepted = true;
    }

    private static void ExecuteTimelineSignals(
        D3D12Queue queue,
        int firstSignal,
        int count,
        D3D12PendingSubmission submission,
        ref bool accepted)
    {
        for (int index = 0; index < count; index++)
        {
            int source = firstSignal + index;
            ThrowIfFailed(
                queue.NativeDevice,
                queue.Native->Signal(
                    submission.Timelines[source].Native,
                    submission.TimelineValues[source]),
                NativeOperationType.Ordinary,
                "ID3D12CommandQueue::Signal(external timeline)");
            accepted = true;
        }
    }

    private sealed partial class D3D12CommandContext : CommandContext
    {
        private readonly D3D12Backend _backend;
        private readonly D3D12Device _device;
        private readonly D3D12Queue _queue;
        private readonly uint _nodeMask;
        private readonly bool _enhancedBarriers;
        private readonly object _gate = new();
        private readonly List<D3D12CommandSlot> _slots = [];
        private D3D12CommandSlot? _recording;
        private ID3D12GraphicsCommandList10* _activeList;
        private ulong _nextSequence = 1;
#if SOMEENGINE_RHI_BENCHMARK_TIMING
        private ulong _benchmarkCloseSequence;
        private bool _benchmarkClosePending;
#endif

        internal D3D12CommandContext(
            D3D12Device device,
            D3D12Queue queue,
            in CommandContextDesc description)
            : base(
                device,
                description.QueueType,
                description.QueueIndex,
                description.Bundle,
                description.Label)
        {
            _backend = device.Backend;
            _device = device;
            _queue = queue;
            _nodeMask = queue.NodeMask;
            _enhancedBarriers = device.EnhancedBarriers;
            _nativeCommandListBorrowLease = new D3D12NativeCommandListBorrowLease(this);
        }

        internal D3D12Device NativeDevice => _device;
        internal D3D12Backend NativeBackend
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _backend;
        }
        internal D3D12Queue NativeQueue => _queue;
        internal uint NativeNodeMask => _nodeMask;
        internal bool EnhancedBarriers => _enhancedBarriers;
        internal ID3D12GraphicsCommandList10* List
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _activeList;
        }
        internal D3D12CommandSlot Recording
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _recording!;
        }

        internal void AddTransient(IUnknown* value) =>
            Recording.AddTransient(value);

        internal void Capture(D3D12Buffer value)
        {
            RequireSameDevice(value, nameof(value));
            RequireResourceVisible(value.Info.VisibleNodeMask, nameof(value));
            Recording.Capture(value.NativeLifetime, value.SparseState);
        }

        internal void Capture(D3D12TextureResource value)
        {
            RequireSameDevice(value.Owner, nameof(value));
            RequireResourceVisible(value.Info.VisibleNodeMask, nameof(value));
            Recording.CaptureTexture(value);
        }

        internal void Capture(GraphicsObject owner, NativeLease resource)
        {
            if (owner is DeviceResource deviceResource)
                RequireSameDevice(deviceResource, nameof(owner));
            Recording.Capture(resource);
        }

        internal void Capture(BufferCbv value)
        {
            RequireSameDevice(value, nameof(value));
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12Buffer resource = RequireD3D12.Buffer(value.Resource);
            RequireResourceVisible(resource.Info.VisibleNodeMask, nameof(value));
            Recording.Capture(descriptor.NativeDescriptor, resource.NativeLifetime);
        }

        internal void Capture(BufferSrv value)
        {
            RequireSameDevice(value, nameof(value));
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12Buffer resource = RequireD3D12.Buffer(value.Resource);
            RequireResourceVisible(resource.Info.VisibleNodeMask, nameof(value));
            Recording.Capture(descriptor.NativeDescriptor, resource.NativeLifetime);
        }

        internal void Capture(BufferUav value)
        {
            RequireSameDevice(value, nameof(value));
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12Buffer resource = RequireD3D12.Buffer(value.Resource);
            RequireResourceVisible(resource.Info.VisibleNodeMask, nameof(value));
            Recording.Capture(descriptor.NativeDescriptor, resource.NativeLifetime);
        }

        internal void Capture(TextureSrv value)
        {
            RequireSameDevice(value, nameof(value));
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12TextureResource resource = RequireD3D12.Texture(value.Resource);
            RequireResourceVisible(resource.Info.VisibleNodeMask, nameof(value));
            D3D12CommandSlot slot = Recording;
            slot.Capture(descriptor.NativeDescriptor, resource.NativeLifetime);
            slot.CaptureSwapchainUse(resource);
        }

        internal void Capture(TextureUav value)
        {
            RequireSameDevice(value, nameof(value));
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12TextureResource resource = RequireD3D12.Texture(value.Resource);
            RequireResourceVisible(resource.Info.VisibleNodeMask, nameof(value));
            D3D12CommandSlot slot = Recording;
            slot.Capture(descriptor.NativeDescriptor, resource.NativeLifetime);
            slot.CaptureSwapchainUse(resource);
        }

        internal CpuDescriptorHandle Capture(ColorAttachmentView value)
        {
            D3D12TextureResource resource = RequireAttachment(value);
            return Capture(value, resource);
        }

        internal D3D12TextureResource RequireAttachment(ColorAttachmentView value)
        {
            D3D12ColorAttachmentView native = value as D3D12ColorAttachmentView ??
                throw new ArgumentException(
                    "The ColorAttachmentView was not created by the Direct3D 12 backend.",
                    nameof(value));
            RequireSameDevice(native, nameof(value));
            D3D12TextureResource resource = native.NativeResource;
            resource.Owner.ThrowIfDisposed();
            RequireResourceVisible(resource.Info.VisibleNodeMask, nameof(value));
            return resource;
        }

        internal CpuDescriptorHandle Capture(
            ColorAttachmentView value,
            D3D12TextureResource resource)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12CommandSlot slot = Recording;
            slot.Capture(descriptor.NativeDescriptor, resource.NativeLifetime);
            slot.CaptureSwapchainUse(resource);
            return descriptor.NativeDescriptor.Cpu;
        }

        internal CpuDescriptorHandle Capture(DepthStencilView value)
        {
            D3D12TextureResource resource = RequireAttachment(value);
            return Capture(value, resource);
        }

        internal D3D12TextureResource RequireAttachment(DepthStencilView value)
        {
            D3D12DepthStencilView native = value as D3D12DepthStencilView ??
                throw new ArgumentException(
                    "The DepthStencilView was not created by the Direct3D 12 backend.",
                    nameof(value));
            RequireSameDevice(native, nameof(value));
            D3D12TextureResource resource = native.NativeResource;
            resource.Owner.ThrowIfDisposed();
            RequireResourceVisible(resource.Info.VisibleNodeMask, nameof(value));
            return resource;
        }

        internal CpuDescriptorHandle Capture(
            DepthStencilView value,
            D3D12TextureResource resource)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12CommandSlot slot = Recording;
            slot.Capture(descriptor.NativeDescriptor, resource.NativeLifetime);
            slot.CaptureSwapchainUse(resource);
            return descriptor.NativeDescriptor.Cpu;
        }

        internal void CaptureBundle(D3D12RecordedBundle value)
        {
            RequireSameDevice(value, nameof(value));
            Recording.CaptureBundle(value);
        }

        private void RequireSameDevice(DeviceResource value, string parameterName)
        {
            if (!ReferenceEquals(value.Device, _device))
            {
                throw new ArgumentException(
                    "The graphics object belongs to another Device.",
                    parameterName);
            }
        }

        internal void RequireResourceVisible(uint visibleNodeMask, string parameterName)
        {
            if ((visibleNodeMask & NativeNodeMask) == 0)
            {
                throw new ArgumentException(
                    "The resource is not visible from the CommandContext linked-adapter node.",
                    parameterName);
            }
        }

        internal void PrepareSlots(uint count)
        {
            _slots.EnsureCapacity(checked(_slots.Count + checked((int)count)));
            for (uint index = 0; index < count; index++)
                _slots.Add(new D3D12CommandSlot(_device, this));
        }

        internal void Begin(in CommandRecordingDesc description)
        {
            ThrowIfDisposed();
            _device.ThrowIfUnavailable();
            if (_recording is not null)
                throw new InvalidOperationException("The CommandContext is already recording.");

            D3D12CommandSlot? slot = null;
            foreach (D3D12CommandSlot candidate in _slots)
            {
                if (candidate.TryClaim())
                {
                    slot = candidate;
                    break;
                }
            }
            if (slot is null)
            {
                _slots.EnsureCapacity(checked(_slots.Count + 1));
                slot = new D3D12CommandSlot(_device, this);
                _slots.Add(slot);
                if (!slot.TryClaim())
                    throw new InvalidOperationException("A new command slot could not be claimed.");
            }

            try
            {
                slot.Reset(description);
                _recording = slot;
                _activeList = slot.List;
            }
            catch
            {
                slot.CompleteUse();
                throw;
            }
        }

        internal RecordedCommands EndCommands()
        {
            _device.ThrowIfUnavailable();
            RequireRenderingClosed();
            D3D12CommandSlot slot = Recording;
            ulong sequence = AllocateSequence();
            ThrowIfFailed(
                _device,
                slot.List->Close(),
                NativeOperationType.Ordinary,
                "ID3D12GraphicsCommandList::Close");
            return FinishClosedCommands(slot, sequence);
        }

#if SOMEENGINE_RHI_BENCHMARK_TIMING
        internal void CloseCommandsForBenchmark()
        {
            _device.ThrowIfUnavailable();
            RequireRenderingClosed();
            if (_benchmarkClosePending)
            {
                throw new InvalidOperationException(
                    "The benchmark command close is already pending finalization.");
            }
            D3D12CommandSlot slot = Recording;
            ulong sequence = AllocateSequence();
            ThrowIfFailed(
                _device,
                slot.List->Close(),
                NativeOperationType.Ordinary,
                "ID3D12GraphicsCommandList::Close(benchmark)");
            _benchmarkCloseSequence = sequence;
            _benchmarkClosePending = true;
        }

        internal RecordedCommands FinishCommandsForBenchmark()
        {
            if (!_benchmarkClosePending)
            {
                throw new InvalidOperationException(
                    "No benchmark command close is pending finalization.");
            }
            D3D12CommandSlot slot = Recording;
            ulong sequence = _benchmarkCloseSequence;
            try
            {
                return FinishClosedCommands(slot, sequence);
            }
            finally
            {
                _benchmarkCloseSequence = 0;
                _benchmarkClosePending = false;
            }
        }
#endif

        private RecordedCommands FinishClosedCommands(
            D3D12CommandSlot slot,
            ulong sequence)
        {
            D3D12RecordedCommandsLease lease;
            try
            {
                lease = slot.ActivateCommands(sequence);
            }
            catch
            {
                _recording = null;
                _activeList = null;
                slot.ReleaseEncodingReferences();
                ResetEncodingState();
                slot.CompleteUse();
                throw;
            }
            _recording = null;
            _activeList = null;
            slot.ReleaseEncodingReferences();
            ResetEncodingState();
            return new RecordedCommands(lease, sequence);
        }

        internal D3D12RecordedBundle EndBundle()
        {
            _device.ThrowIfUnavailable();
            RequireRenderingClosed();
            D3D12CommandSlot slot = Recording;
            ThrowIfFailed(
                _device,
                slot.List->Close(),
                NativeOperationType.Ordinary,
                "ID3D12GraphicsCommandList::Close(bundle)");
            D3D12RecordedBundle bundle;
            try
            {
                bundle = new D3D12RecordedBundle(_device, slot, Label);
            }
            catch
            {
                _recording = null;
                _activeList = null;
                slot.ReleaseEncodingReferences();
                ResetEncodingState();
                slot.CompleteUse();
                throw;
            }
            _recording = null;
            _activeList = null;
            slot.ReleaseEncodingReferences();
            ResetEncodingState();
            try
            {
                _device.RegisterChild(bundle);
                return bundle;
            }
            catch
            {
                bundle.Dispose();
                throw;
            }
        }

        internal void Discard()
        {
            _device.ThrowIfUnavailable();
            RequireRenderingClosed();
            D3D12CommandSlot slot = Recording;
#if SOMEENGINE_RHI_BENCHMARK_TIMING
            bool alreadyClosed = _benchmarkClosePending;
            _benchmarkCloseSequence = 0;
            _benchmarkClosePending = false;
            if (!alreadyClosed)
                _ = slot.List->Close();
#else
            _ = slot.List->Close();
#endif
            _recording = null;
            _activeList = null;
            slot.ReleaseEncodingReferences();
            ResetEncodingState();
            slot.CompleteUse();
        }

        internal void ReleaseSlot(D3D12CommandSlot slot) => slot.CompleteUse();

        internal void MarkDeviceLost()
        {
            D3D12CommandSlot? recording;
#if SOMEENGINE_RHI_BENCHMARK_TIMING
            bool alreadyClosed = false;
#endif
            lock (_gate)
            {
                recording = _recording;
                if (recording is not null)
                {
#if SOMEENGINE_RHI_BENCHMARK_TIMING
                    alreadyClosed = _benchmarkClosePending;
                    _benchmarkCloseSequence = 0;
                    _benchmarkClosePending = false;
#endif
                    _recording = null;
                    _activeList = null;
                    ResetEncodingState();
                }
            }
            if (recording is not null)
            {
#if SOMEENGINE_RHI_BENCHMARK_TIMING
                if (!alreadyClosed)
                    _ = recording.List->Close();
#else
                _ = recording.List->Close();
#endif
                recording.ReleaseEncodingReferences();
                recording.CompleteUse();
            }
            for (int index = 0; ; index++)
            {
                D3D12CommandSlot slot;
                lock (_gate)
                {
                    if (index >= _slots.Count)
                        break;
                    slot = _slots[index];
                }
                slot.MarkDeviceLost();
            }
        }

        internal override void Release(bool fromParent)
        {
            D3D12CommandSlot? recording;
            bool discardExecutable;
#if SOMEENGINE_RHI_BENCHMARK_TIMING
            bool alreadyClosed = false;
#endif
            lock (_gate)
            {
                recording = _recording;
                if (recording is not null)
                {
#if SOMEENGINE_RHI_BENCHMARK_TIMING
                    alreadyClosed = _benchmarkClosePending;
                    _benchmarkCloseSequence = 0;
                    _benchmarkClosePending = false;
#endif
                    _recording = null;
                    _activeList = null;
                    ResetEncodingState();
                }
                discardExecutable =
                    fromParent || _device.IsDisposed || _device.Status != DeviceStatus.Active;
            }
            if (recording is not null)
            {
#if SOMEENGINE_RHI_BENCHMARK_TIMING
                if (!alreadyClosed)
                    _ = recording.List->Close();
#else
                _ = recording.List->Close();
#endif
                recording.ReleaseEncodingReferences();
                recording.CompleteUse();
            }
            for (int index = 0; ; index++)
            {
                D3D12CommandSlot slot;
                lock (_gate)
                {
                    if (index >= _slots.Count)
                        break;
                    slot = _slots[index];
                }
                if (discardExecutable)
                    slot.DiscardExecutableFromDevice();
                slot.ReleaseOwner();
            }
            lock (_gate)
                _slots.Clear();
            _device.UnregisterChild(this);
        }

        private ulong AllocateSequence()
        {
            if (_nextSequence == ulong.MaxValue)
                throw new InvalidOperationException("The CommandContext sequence domain is exhausted.");
            return _nextSequence++;
        }
    }

    private sealed partial class D3D12CommandSlot
    {
        private readonly D3D12CommandContext _context;
        private readonly D3D12RecordedCommandsLease _commands;
        private readonly List<nint> _transientObjects = [];
        private int _transientObjectCapacity;
        private readonly CommandCaptures _captures = new();
        private readonly HashSet<D3D12RecordedBundle> _capturedBundles =
            new(ReferenceEqualityComparer.Instance);
        private int _capturedBundleCapacity;
        private DescriptorGeneration? _descriptorGeneration;
        private ID3D12CommandAllocator* _allocator;
        private ID3D12GraphicsCommandList10* _list;
        private int _references = 1;
        private int _busy;

        internal D3D12CommandSlot(D3D12Device device, D3D12CommandContext context)
        {
            _context = context;
            _commands = new D3D12RecordedCommandsLease(context, this, context.NativeQueue);
            CommandListType type = context.Bundle
                ? CommandListType.Bundle
                : ToCommandListType(context.QueueType);
            Guid allocatorIid = ID3D12CommandAllocator.Guid;
            ID3D12CommandAllocator* allocator = null;
            ThrowIfFailed(
                device,
                device.Native->CreateCommandAllocator(
                    type,
                    &allocatorIid,
                    (void**)&allocator),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateCommandAllocator");
            _allocator = allocator;
            string commandName = context.Label ??
                $"{context.QueueType} CommandContext[{context.QueueIndex}]";
            SetNativeName(allocator, $"{commandName} Allocator");
            try
            {
                Guid listIid = ID3D12GraphicsCommandList10.Guid;
                ID3D12GraphicsCommandList10* list = null;
                ThrowIfFailed(
                    device,
                    device.Native->CreateCommandList1(
                        context.NativeNodeMask,
                        type,
                        CommandListFlags.None,
                        &listIid,
                        (void**)&list),
                    NativeOperationType.Ordinary,
                    "ID3D12Device4::CreateCommandList1");
                _list = list;
                SetNativeName(list, $"{commandName} Command List");
            }
            catch
            {
                _ = _allocator->Release();
                _allocator = null;
                throw;
            }
        }

        internal ID3D12GraphicsCommandList10* List => _list;
        internal DescriptorGeneration Descriptors
        {
            get
            {
                DescriptorGeneration? current = _descriptorGeneration;
                if (current is not null)
                    return current;
                DescriptorPublisher publisher = _context.NativeDevice
                    .GetDescriptorPublisher(_context.NativeQueue.NodeIndex);
                current = publisher.CaptureCurrent();
                _descriptorGeneration = current;
                return current;
            }
        }
        internal D3D12RecordedCommandsLease ActivateCommands(ulong sequence)
        {
            _context.NativeDevice.ActivateCommandPayload(_commands, sequence);
            return _commands;
        }

        internal bool TryClaim()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
                return false;
            Interlocked.Increment(ref _references);
            return true;
        }

        internal void Reset(in CommandRecordingDesc description)
        {
            if (_context.QueueType == QueueType.Copy &&
                (description.InitialResourceDescriptorCapacity != 0 ||
                 description.InitialSamplerDescriptorCapacity != 0))
            {
                throw new ArgumentException(
                    "A copy CommandContext cannot reserve shader-visible descriptors.",
                    nameof(description));
            }

            PrepareInitialCaptureCapacity(description.InitialCapturedResourceCapacity);
            ResetOrdinaryDataArena();
            ResetTemporaryAttachmentDescriptors();
            if (_context.QueueType != QueueType.Copy)
                ResetDescriptorArenaState(description);
            ThrowIfFailed(_context.NativeDevice, _allocator->Reset(), NativeOperationType.Ordinary, "ID3D12CommandAllocator::Reset");
            ThrowIfFailed(
                _context.NativeDevice,
                _list->Reset(_allocator, null),
                NativeOperationType.Ordinary,
                "ID3D12GraphicsCommandList::Reset");

            if (_context.QueueType == QueueType.Copy)
                return;
        }

        internal void CompleteUse()
        {
            if (Interlocked.CompareExchange(ref _busy, -1, 1) != 1)
                return;
            try
            {
                ReleaseTransients();
                ReleaseReference();
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        internal void MarkDeviceLost() => _commands.MarkDeviceLostFromDevice();

        internal void DiscardExecutableFromDevice() =>
            _commands.DiscardExecutableFromDevice();

        internal void AddTransient(IUnknown* value) =>
            _transientObjects.Add((nint)value);

        internal void Capture(
            NativeLease resource,
            D3D12SparseState? sparseState = null) =>
            _captures.Capture(resource, sparseState);

        internal void CaptureTexture(D3D12TextureResource texture)
        {
            Capture(texture.NativeLifetime, texture.SparseState);
            CaptureSwapchainUse(texture);
        }

        internal void Capture(
            DescriptorLease descriptor,
            NativeLease? resource = null) =>
            _captures.Capture(descriptor, resource);

        internal void CaptureBundle(D3D12RecordedBundle bundle)
        {
            if (_capturedBundles.Add(bundle))
            {
                try
                {
                    bundle.RetainExecution();
                }
                catch
                {
                    _capturedBundles.Remove(bundle);
                    throw;
                }
            }
        }

        internal void ReleaseEncodingReferences() => ClearRecordedRayTables();

        internal void ReleaseOwner() => ReleaseReference();

        private void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            ReleaseTransients();
            ID3D12GraphicsCommandList10* list = _list;
            _list = null;
            if (list is not null)
                _ = list->Release();
            ID3D12CommandAllocator* allocator = _allocator;
            _allocator = null;
            if (allocator is not null)
                _ = allocator->Release();
            ReleaseTemporaryAttachmentDescriptors();
            ReleaseDescriptorArena();
        }

        private void ReleaseTransients()
        {
            foreach (nint value in _transientObjects)
            {
                if (value != 0)
                    _ = ((IUnknown*)value)->Release();
            }
            _transientObjects.Clear();
            _captures.ReleaseAll();
            foreach (D3D12RecordedBundle bundle in _capturedBundles)
                bundle.ReleaseExecution();
            _capturedBundles.Clear();
            ClearSwapchainUses();
            ClearRecordedRayTables();
            ReleaseBindingTransients();
            Interlocked.Exchange(ref _descriptorGeneration, null)?.Release();
        }

    }

    private sealed class CommandCaptures
    {
        private readonly HashSet<NativeLease> _resources =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<DescriptorLease> _descriptors =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SparseMappingGeneration> _sparseGenerations =
            new(ReferenceEqualityComparer.Instance);
        private int _resourceCapacity;
        private int _descriptorCapacity;
        private int _sparseGenerationCapacity;

        internal void PrepareCapacity(
            int resourceCount,
            int descriptorCount,
            int sparseGenerationCount)
        {
            int resourcesRequired = checked(_resources.Count + resourceCount);
            if (resourcesRequired > _resourceCapacity)
                _resourceCapacity = _resources.EnsureCapacity(resourcesRequired);
            int descriptorsRequired = checked(_descriptors.Count + descriptorCount);
            if (descriptorsRequired > _descriptorCapacity)
                _descriptorCapacity = _descriptors.EnsureCapacity(descriptorsRequired);
            int sparseRequired = checked(
                _sparseGenerations.Count + sparseGenerationCount);
            if (sparseRequired > _sparseGenerationCapacity)
            {
                _sparseGenerationCapacity =
                    _sparseGenerations.EnsureCapacity(sparseRequired);
            }
        }

        internal void Capture(
            NativeLease resource,
            D3D12SparseState? sparseState)
        {
            SparseMappingGeneration? generation = sparseState?.CaptureCurrent();
            bool addedResource = false;
            bool addedGeneration = false;
            bool generationConsumed = false;
            try
            {
                if (_resources.Add(resource))
                {
                    try
                    {
                        resource.Retain();
                    }
                    catch
                    {
                        _resources.Remove(resource);
                        throw;
                    }
                    addedResource = true;
                }
                if (generation is not null)
                {
                    if (_sparseGenerations.Add(generation))
                    {
                        addedGeneration = true;
                        generationConsumed = true;
                    }
                    else
                    {
                        generation.Release();
                        generationConsumed = true;
                    }
                }
            }
            catch
            {
                if (addedGeneration)
                {
                    _sparseGenerations.Remove(generation!);
                    generation!.Release();
                }
                else if (generation is not null && !generationConsumed)
                {
                    generation.Release();
                }
                if (addedResource)
                {
                    _resources.Remove(resource);
                    resource.Release();
                }
                throw;
            }
        }

        internal void Capture(
            DescriptorLease descriptor,
            NativeLease? resource)
        {
            bool addedDescriptor = false;
            bool addedResource = false;
            try
            {
                if (_descriptors.Add(descriptor))
                {
                    try
                    {
                        descriptor.Retain();
                    }
                    catch
                    {
                        _descriptors.Remove(descriptor);
                        throw;
                    }
                    addedDescriptor = true;
                }
                if (resource is not null && _resources.Add(resource))
                {
                    try
                    {
                        resource.Retain();
                    }
                    catch
                    {
                        _resources.Remove(resource);
                        throw;
                    }
                    addedResource = true;
                }
            }
            catch
            {
                if (addedResource)
                {
                    _resources.Remove(resource!);
                    resource!.Release();
                }
                if (addedDescriptor)
                {
                    _descriptors.Remove(descriptor);
                    descriptor.Release();
                }
                throw;
            }
        }

        internal void ReleaseAll()
        {
            foreach (DescriptorLease descriptor in _descriptors)
                descriptor.Release();
            _descriptors.Clear();
            foreach (NativeLease resource in _resources)
                resource.Release();
            _resources.Clear();
            foreach (SparseMappingGeneration generation in _sparseGenerations)
                generation.Release();
            _sparseGenerations.Clear();
        }
    }

    private sealed partial class D3D12RecordedCommandsLease : RecordedCommandsLease
    {
        private readonly D3D12CommandContext _context;
        private readonly D3D12CommandSlot _slot;
        internal D3D12RecordedCommandsLease? DeviceNext;
        internal D3D12RecordedCommandsLease? DevicePrevious;
        internal D3D12RecordedCommandsLease? DeviceLossWorkNext;
        internal bool DeviceRegistered;

        internal D3D12RecordedCommandsLease(
            D3D12CommandContext context,
            D3D12CommandSlot slot,
            D3D12Queue queue)
            : base(context.Device, queue)
        {
            _context = context;
            _slot = slot;
        }

        internal void ActivateCommands(ulong sequence) => Activate(sequence);

        internal void CancelCommandsActivation(ulong sequence) => CancelActivation(sequence);

        internal ID3D12GraphicsCommandList10* GetNativeList(ulong sequence)
        {
            EnsureSequence(sequence);
            return _slot.List;
        }

        protected override void DiscardUnsubmitted(ulong sequence) => ReleaseSlot(sequence);

        internal void Retire(ulong sequence)
        {
            MarkCompleted(sequence);
            ReleaseSlot(sequence);
        }

        internal void MarkDeviceLostAndAbandon(ulong sequence)
        {
            MarkDeviceLost(sequence);
            ReleaseSlot(sequence);
        }

        internal void MarkDeviceLostRetained(ulong sequence) => MarkDeviceLost(sequence);

        internal void MarkDeviceLostFromDevice()
        {
            if (TryMarkDeviceLostFromDevice(out ulong sequence, out bool abandon) && abandon)
                ReleaseSlot(sequence);
        }

        internal void DiscardExecutableFromDevice()
        {
            if (TryDiscardExecutableFromDevice(out ulong sequence))
                ReleaseSlot(sequence);
        }

        private void ReleaseSlot(ulong sequence)
        {
            EnsureSequence(sequence);
            _context.NativeDevice.UnregisterCommandPayload(this);
            _context.ReleaseSlot(_slot);
        }
    }

    private sealed partial class D3D12RecordedBundle : RecordedBundle
    {
        private readonly D3D12Device _device;
        private D3D12CommandSlot? _slot;
        private int _references = 1;

        internal D3D12RecordedBundle(
            D3D12Device device,
            D3D12CommandSlot slot,
            string? label)
            : base(device, label)
        {
            _device = device;
            _slot = slot;
        }

        internal ID3D12GraphicsCommandList10* NativeList
        {
            get
            {
                D3D12CommandSlot? slot = _slot;
                return slot is null
                    ? throw new ObjectDisposedException(nameof(D3D12RecordedBundle))
                    : slot.List;
            }
        }

        internal void RetainExecution()
        {
            int current = Volatile.Read(ref _references);
            while (current > 0)
            {
                int exchanged = Interlocked.CompareExchange(
                    ref _references,
                    checked(current + 1),
                    current);
                if (exchanged == current)
                    return;
                current = exchanged;
            }
            throw new ObjectDisposedException(nameof(D3D12RecordedBundle));
        }

        internal void ReleaseExecution() => ReleaseReference();

        internal override void Release(bool fromParent)
        {
            ReleaseReference();
            _device.UnregisterChild(this);
        }

        private void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            D3D12CommandSlot? slot = Interlocked.Exchange(ref _slot, null);
            slot?.CompleteUse();
        }
    }

    private sealed partial class D3D12Queue
    {
        private readonly List<D3D12PendingSubmission> _pendingSubmissions = [];
        private readonly List<D3D12PendingSubmission> _untrustedSubmissions = [];
        private readonly List<D3D12PendingSubmission> _freeSubmissions = [];
        private int _submissionCount;

        internal D3D12PendingSubmission AcquireSubmission(
            int payloadCapacity,
            int completionWaitCapacity,
            int timelineCapacity,
            int imageCapacity)
        {
            D3D12PendingSubmission submission;
            using (Gate.EnterScope())
            {
                if (_freeSubmissions.Count != 0)
                {
                    int index = _freeSubmissions.Count - 1;
                    submission = _freeSubmissions[index];
                    _freeSubmissions.RemoveAt(index);
                }
                else
                {
                    int newSubmissionCount = checked(_submissionCount + 1);
                    _pendingSubmissions.EnsureCapacity(newSubmissionCount);
                    _untrustedSubmissions.EnsureCapacity(newSubmissionCount);
                    _freeSubmissions.EnsureCapacity(newSubmissionCount);
                    submission = new D3D12PendingSubmission();
                    _submissionCount = newSubmissionCount;
                }
            }

            try
            {
                submission.EnsureCapacity(
                    payloadCapacity,
                    completionWaitCapacity,
                    timelineCapacity,
                    imageCapacity);
                return submission;
            }
            catch
            {
                ReturnSubmission(submission);
                throw;
            }
        }

        internal void RegisterSubmissionUnderGate(
            ulong completion,
            D3D12PendingSubmission submission)
        {
            submission.Completion = completion;
            submission.ReleaseScratchReferences();
            if (submission.PayloadCount == 0 && submission.TimelineCount == 0)
            {
                submission.ResetForReuse();
                _freeSubmissions.Add(submission);
            }
            else
            {
                _pendingSubmissions.Add(submission);
            }
        }

        internal void RegisterUntrustedSubmission(D3D12PendingSubmission submission)
        {
            using (Gate.EnterScope())
            {
                submission.Completion = ulong.MaxValue;
                submission.ReleaseScratchReferences();
                if (submission.PayloadCount == 0 && submission.TimelineCount == 0)
                {
                    submission.ResetForReuse();
                    _freeSubmissions.Add(submission);
                }
                else
                {
                    _untrustedSubmissions.Add(submission);
                }
            }
        }

        internal void ReturnSubmission(D3D12PendingSubmission submission)
        {
            submission.ResetForReuse();
            using (Gate.EnterScope())
            {
                _freeSubmissions.Add(submission);
            }
        }

        private void CollectRetiredPayloadsUnderGate(ulong completed)
        {
            if (completed == ulong.MaxValue)
            {
                throw PublishDeviceLoss(
                    _device,
                    DxgiErrorDeviceRemoved,
                    "D3D12 reported the device-removal completion sentinel.",
                    DxgiErrorDeviceRemoved);
            }

            int removeCount = 0;
            foreach (D3D12PendingSubmission pending in _pendingSubmissions)
            {
                if (pending.Completion > completed)
                    break;
                pending.Retire();
                pending.ResetForReuse();
                _freeSubmissions.Add(pending);
                removeCount++;
            }
            if (removeCount != 0)
                _pendingSubmissions.RemoveRange(0, removeCount);
        }

        private void DrainOrAbandonPayloads()
        {
            Exception? completionFailure = null;
            try
            {
                ulong target;
                using (Gate.EnterScope())
                {
                    ulong submissionTarget = _pendingSubmissions.Count == 0
                        ? 0
                        : _pendingSubmissions[^1].Completion;
                    target = Math.Max(
                        Math.Max(
                            submissionTarget,
                            GetPresentationRetirementTargetUnderGate()),
                        GetCapabilityRetirementTargetUnderGate());
                }
                if (target != 0 && !_device.NativeDeviceLossConfirmed)
                {
                    using (Gate.EnterScope())
                    {
                        WaitForCompletionUnderGate(target);
                        ulong completed = Fence->GetCompletedValue();
                        CollectRetiredPayloadsUnderGate(completed);
                        CollectPresentationRetirementsUnderGate(completed);
                        CollectCapabilityRetirementsUnderGate(completed);
                    }
                }
            }
            catch (Exception exception)
            {
                completionFailure = exception;
            }

            using (Gate.EnterScope())
            {
                if (!CanAbandonNativePayloadsUnderGate &&
                    (_pendingSubmissions.Count != 0 ||
                     _untrustedSubmissions.Count != 0 ||
                     GetPresentationRetirementTargetUnderGate() != 0 ||
                     HasUntrustedPresentationRetirementsUnderGate ||
                     GetCapabilityRetirementTargetUnderGate() != 0 ||
                     HasUntrustedCapabilityRetirementsUnderGate))
                {
                    throw completionFailure ?? new InvalidOperationException(
                        "D3D12 native payloads are retained because completion was not verified and device loss was not confirmed.");
                }
                foreach (D3D12PendingSubmission pending in _pendingSubmissions)
                {
                    pending.Abandon();
                    pending.ResetForReuse();
                    _freeSubmissions.Add(pending);
                }
                _pendingSubmissions.Clear();
                foreach (D3D12PendingSubmission pending in _untrustedSubmissions)
                {
                    pending.Abandon();
                    pending.ResetForReuse();
                    _freeSubmissions.Add(pending);
                }
                _untrustedSubmissions.Clear();
                AbandonPresentationRetirementsUnderGate();
                AbandonCapabilityRetirementsUnderGate();
            }
        }
    }

    private sealed class D3D12PendingSubmission
    {
        internal D3D12RecordedCommandsLease[] Payloads { get; private set; } = [];
        internal ulong[] PayloadSequences { get; private set; } = [];
        internal D3D12Queue[] CompletionWaitQueues { get; private set; } = [];
        internal ulong[] CompletionWaitValues { get; private set; } = [];
        internal D3D12ExternalTimeline[] Timelines { get; private set; } = [];
        internal ulong[] TimelineValues { get; private set; } = [];
        internal D3D12SwapchainImageLease[] Images { get; private set; } = [];
        internal ulong[] ImageSequences { get; private set; } = [];
        internal D3D12SubmittedSwapchainUse[] ImageUses { get; private set; } = [];
        internal D3D12SwapchainImageLease[] ReferencedImages { get; private set; } = [];
        internal D3D12SubmittedSwapchainUse[] ReferencedImageUses { get; private set; } = [];
        internal nint[] NativeLists { get; private set; } = [];

        internal ulong Completion { get; set; }
        internal int PayloadCount { get; set; }
        internal int CompletionWaitCount { get; set; }
        internal int TimelineCount { get; set; }
        internal int ImageCount { get; set; }
        internal int ReferencedImageCount { get; set; }

        internal void EnsureCapacity(
            int payloadCapacity,
            int completionWaitCapacity,
            int timelineCapacity,
            int imageCapacity)
        {
            Payloads = EnsureCapacity(Payloads, payloadCapacity);
            PayloadSequences = EnsureCapacity(PayloadSequences, payloadCapacity);
            CompletionWaitQueues = EnsureCapacity(
                CompletionWaitQueues,
                completionWaitCapacity);
            CompletionWaitValues = EnsureCapacity(
                CompletionWaitValues,
                completionWaitCapacity);
            Timelines = EnsureCapacity(Timelines, timelineCapacity);
            TimelineValues = EnsureCapacity(TimelineValues, timelineCapacity);
            Images = EnsureCapacity(Images, imageCapacity);
            ImageSequences = EnsureCapacity(ImageSequences, imageCapacity);
            ImageUses = EnsureCapacity(ImageUses, imageCapacity);
            NativeLists = EnsureCapacity(NativeLists, payloadCapacity);
        }

        internal void EnsureSwapchainUseCapacity(int capacity)
        {
            ReferencedImages = EnsureCapacity(ReferencedImages, capacity);
            ReferencedImageUses = EnsureCapacity(ReferencedImageUses, capacity);
        }

        internal void Retire()
        {
            for (int index = 0; index < PayloadCount; index++)
                Payloads[index].Retire(PayloadSequences[index]);
            for (int index = 0; index < TimelineCount; index++)
                Timelines[index].ReleaseSubmission();
        }

        internal void Abandon()
        {
            for (int index = 0; index < PayloadCount; index++)
                Payloads[index].MarkDeviceLostAndAbandon(PayloadSequences[index]);
            for (int index = 0; index < TimelineCount; index++)
                Timelines[index].ReleaseSubmission();
        }

        internal void MarkDeviceLostRetained()
        {
            for (int index = 0; index < PayloadCount; index++)
                Payloads[index].MarkDeviceLostRetained(PayloadSequences[index]);
        }

        internal void ReleaseScratchReferences()
        {
            Array.Clear(CompletionWaitQueues, 0, CompletionWaitCount);
            Array.Clear(Images, 0, ImageCount);
            Array.Clear(ReferencedImages, 0, ReferencedImageCount);
            Array.Clear(ReferencedImageUses, 0, ReferencedImageCount);
            CompletionWaitCount = 0;
            ImageCount = 0;
            ReferencedImageCount = 0;
        }

        internal void ResetForReuse()
        {
            Array.Clear(Payloads, 0, PayloadCount);
            Array.Clear(Timelines, 0, TimelineCount);
            Array.Clear(NativeLists, 0, PayloadCount);
            ReleaseScratchReferences();
            Completion = 0;
            PayloadCount = 0;
            TimelineCount = 0;
        }

        private static T[] EnsureCapacity<T>(T[] values, int capacity)
        {
            if (values.Length < capacity)
                Array.Resize(ref values, capacity);
            return values;
        }
    }

    private static partial class RequireD3D12
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static D3D12CommandContext CommandContext(CommandContext value)
        {
            D3D12CommandContext result = value as D3D12CommandContext ??
                throw new ArgumentException(
                    "The CommandContext was not created by the Direct3D 12 backend.",
                    nameof(value));
            result.BeginPublicCall();
            return result;
        }

        internal static D3D12RecordedBundle Bundle(RecordedBundle value) =>
            value as D3D12RecordedBundle ??
            throw new ArgumentException(
                "The RecordedBundle was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
