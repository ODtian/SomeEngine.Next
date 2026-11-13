using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public CommandContext CreateCommandContext(
        Device device,
        in CommandContextDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandLifecycle);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        nativeDevice.ThrowIfUnavailable();

        D3D12Queue queue = nativeDevice.GetQueue(desc.QueueType, desc.QueueIndex);
        D3D12CommandContext result = new(nativeDevice, queue, desc);
        try
        {
            result.PrepareBuffers(desc.InitialBufferCount);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public void Begin(CommandContext context, in CommandRecordingDesc desc = default)
    {
        ((D3D12CommandContext)context).BeginPublicCall();
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandLifecycle);
        ((D3D12CommandContext)context).Begin(desc);
    }
    public RecordedCommands End(CommandContext context)
    {
        ((D3D12CommandContext)context).BeginPublicCall();
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandLifecycle);
        return ((D3D12CommandContext)context).EndCommands();
    }
    public RecordedBundle EndBundle(CommandContext context)
    {
        ((D3D12CommandContext)context).BeginPublicCall();
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        return ((D3D12CommandContext)context).EndBundle();
    }
    public void Discard(CommandContext context)
    {
        ((D3D12CommandContext)context).BeginPublicCall();
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandLifecycle);
        ((D3D12CommandContext)context).Discard();
    }
    public QueueCompletion Submit(Queue queue, in QueueSubmitDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiSubmit);
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
        D3D12QueueSubmission submission = nativeQueue.AcquireSubmission(
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
            // A native signal may succeed after removal; the completion fence still reports the loss.
            _ = queue.ReadCompletedValue();
            return queue.SignalCompletionUnderGate();
        }
    }

    private void PrepareSubmissionWaits(
        in QueueSubmitDesc desc,
        D3D12QueueSubmission submission)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiSubmitPrepareSubmissionWaits);
        for (int index = 0; index < desc.CompletionWaits.Length; index++)
        {
            QueueCompletion wait = desc.CompletionWaits[index];
            submission.CompletionWaitQueues[index] = RequireQueue(wait.QueueReference, nameof(desc));
            submission.CompletionWaitValues[index] = wait.SubmittedValue;
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
        D3D12QueueSubmission submission)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiSubmitClaimCommandPayloads);
        int requiredSwapchainUseCapacity = 0;
        for (int index = 0; index < commands.Length; index++)
        {
            RecordedCommands command = commands[index];
            ulong sequence = command.Sequence;
            D3D12CommandRecording payload = (D3D12CommandRecording)(command.Data);
            payload.TryBeginSubmit(sequence);

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
        D3D12QueueSubmission submission)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiSubmitCollectReferencedSwapchainImages);
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
        D3D12QueueSubmission submission)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiSubmitValidateSwapchainImages);

        for (int index = 0; index < images.Length; index++)
        {
            SwapchainImage image = images[index];
            D3D12SwapchainImageUse nativeImage = (D3D12SwapchainImageUse)(image.ImageUse!);
            int useIndex = FindReferencedImage(submission, nativeImage);

            D3D12SubmittedSwapchainUse use = submission.ReferencedImageUses[useIndex];
            submission.Images[index] = nativeImage;
            submission.ImageSequences[index] = image.Sequence;
            submission.ImageUses[index] = use;
            submission.ImageCount = index + 1;
        }
    }

    private static int FindReferencedImage(
        D3D12QueueSubmission submission,
        D3D12SwapchainImageUse image)
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
        D3D12QueueSubmission submission)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiSubmitClaimSwapchainImages);
        int claimed = 0;
        for (int index = 0; index < submission.ImageCount; index++)
        {
            submission.Images[index].TryBeginSubmit(
                    submission.ImageSequences[index],
                    queue,
                    submission.ImageUses[index].PresentReady);
            claimed++;
        }
        return claimed;
    }

    private static int RetainSubmissionTimelines(D3D12QueueSubmission submission)
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
        D3D12QueueSubmission submission,
        ref bool accepted)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiSubmitExecuteSubmissionUnderGate);
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
        for (int index = 0; index < submission.ImageCount; index++)
        {
            submission.Images[index].CommitSubmission(
                submission.ImageSequences[index],
                queue,
                completion.SubmittedValue);
        }
        queue.RegisterSubmissionUnderGate(completion.SubmittedValue, submission);
        return completion;
    }

    private static void ExecuteCompletionWaits(
        D3D12Queue queue,
        D3D12QueueSubmission submission,
        ref bool accepted)
    {
        for (int index = 0; index < submission.CompletionWaitCount; index++)
        {
            accepted |= queue.WaitForCompletionUnderGate(
                submission.CompletionWaitQueues[index],
                submission.CompletionWaitValues[index]);
        }
    }

    private static void ExecuteTimelineWaits(
        D3D12Queue queue,
        int count,
        D3D12QueueSubmission submission,
        ref bool accepted)
    {
        for (int index = 0; index < count; index++)
        {
            ThrowIfFailed(
                queue.NativeDevice,
                    QueueTrace.Wait(
                queue.Native, submission.Timelines[index].Native,
                    submission.TimelineValues[index]),
                NativeOperationType.Ordinary,
                "ID3D12CommandQueue::Wait(external timeline)");
            accepted = true;
        }
    }

    private static void ExecuteCommandLists(
        D3D12Queue queue,
        D3D12QueueSubmission submission,
        ref bool accepted)
    {
        if (submission.PayloadCount == 0)
            return;

        fixed (nint* lists = submission.NativeLists)
        {
            QueueTrace.ExecuteCommandLists(
                        queue.Native, checked((uint)submission.PayloadCount),
                            (ID3D12CommandList**)lists);
        }
        accepted = true;
    }

    private static void ExecuteTimelineSignals(
        D3D12Queue queue,
        int firstSignal,
        int count,
        D3D12QueueSubmission submission,
        ref bool accepted)
    {
        for (int index = 0; index < count; index++)
        {
            int source = firstSignal + index;
            ThrowIfFailed(
                queue.NativeDevice,
                    QueueTrace.Signal(
                queue.Native, submission.Timelines[source].Native,
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
        private readonly List<D3D12CommandBuffer> _buffers = [];
        private D3D12CommandBuffer? _recording;
        private ID3D12GraphicsCommandList10* _activeList;
        private ulong _nextSequence = 1;

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
            _nativeCommandListHandle = new D3D12NativeCommandListHandle(this);
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
        internal D3D12CommandBuffer Recording
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _recording!;
        }

        internal void RecordBoundSwapchainImage(in ResourceBinding binding)
        {
            if (binding.Value is TextureSrv srv) Recording.RecordSwapchainUse(RequireD3D12.Texture(srv.Resource));
            else if (binding.Value is TextureUav uav) Recording.RecordSwapchainUse(RequireD3D12.Texture(uav.Resource));
        }

        internal void RecordSwapchainImage(D3D12TextureResource texture) => Recording.RecordSwapchainUse(texture);

        internal void AddTransient(IUnknown* value) =>
            Recording.AddTransient(value);

        internal CpuDescriptorHandle GetAttachmentDescriptor(ColorAttachmentView value)
        {
            D3D12TextureResource resource = RequireAttachment(value);
            return GetAttachmentDescriptor(value, resource);
        }

        internal D3D12TextureResource RequireAttachment(ColorAttachmentView value)
        {
            D3D12ColorAttachmentView native = (D3D12ColorAttachmentView)(value);
            D3D12TextureResource resource = native.NativeResource;
            return resource;
        }

        internal CpuDescriptorHandle GetAttachmentDescriptor(
            ColorAttachmentView value,
            D3D12TextureResource resource)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            Recording.RecordSwapchainUse(resource);
            return descriptor.NativeDescriptor.Cpu;
        }

        internal CpuDescriptorHandle GetAttachmentDescriptor(DepthStencilView value)
        {
            D3D12TextureResource resource = RequireAttachment(value);
            return GetAttachmentDescriptor(value, resource);
        }

        internal D3D12TextureResource RequireAttachment(DepthStencilView value)
        {
            D3D12DepthStencilView native = (D3D12DepthStencilView)(value);
            D3D12TextureResource resource = native.NativeResource;
            return resource;
        }

        internal CpuDescriptorHandle GetAttachmentDescriptor(
            DepthStencilView value,
            D3D12TextureResource resource)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            Recording.RecordSwapchainUse(resource);
            return descriptor.NativeDescriptor.Cpu;
        }

        internal void PrepareBuffers(uint count)
        {
            _buffers.EnsureCapacity(checked(_buffers.Count + checked((int)count)));
            for (uint index = 0; index < count; index++)
                _buffers.Add(new D3D12CommandBuffer(_device, this));
        }

        internal void Begin(in CommandRecordingDesc description)
        {
            _device.ThrowIfUnavailable();

            _repeatCommands = null;

            D3D12CommandBuffer? entry = null;
            foreach (D3D12CommandBuffer candidate in _buffers)
            {
                if (candidate.TryClaim())
                {
                    entry = candidate;
                    break;
                }
            }
            if (entry is null)
            {
                _buffers.EnsureCapacity(checked(_buffers.Count + 1));
                entry = new D3D12CommandBuffer(_device, this);
                _buffers.Add(entry);
                entry.TryClaim();
            }

            try
            {
                entry.Reset(description);
                _recording = entry;
                _activeList = entry.List;
            }
            catch
            {
                entry.CompleteUse();
                throw;
            }
        }

        internal RecordedCommands EndCommands()
        {
            _device.ThrowIfUnavailable();
            D3D12CommandBuffer entry = Recording;
            ulong sequence = AllocateSequence();
            entry.FinishCommandData();
            return FinishClosedCommands(entry, sequence);
        }

        private RecordedCommands FinishClosedCommands(
            D3D12CommandBuffer entry,
            ulong sequence)
        {
            D3D12CommandRecording handle;
            try
            {
                handle = entry.ActivateCommands(sequence);
            }
            catch
            {
                _recording = null;
                _activeList = null;
                entry.ReleaseEncodingReferences();
                ResetEncodingState();
                entry.CompleteUse();
                throw;
            }
            _recording = null;
            _activeList = null;
            entry.ReleaseEncodingReferences();
            ResetEncodingState();
            _repeatCommands = entry;
            return new RecordedCommands(handle, sequence);
        }

        internal D3D12RecordedBundle EndBundle()
        {
            _device.ThrowIfUnavailable();
            D3D12CommandBuffer entry = Recording;
            entry.FinishCommandData();
            D3D12RecordedBundle bundle;
            try
            {
                bundle = new D3D12RecordedBundle(_device, entry, Label);
            }
            catch
            {
                _recording = null;
                _activeList = null;
                entry.ReleaseEncodingReferences();
                ResetEncodingState();
                entry.CompleteUse();
                throw;
            }
            _recording = null;
            _activeList = null;
            entry.ReleaseEncodingReferences();
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
            D3D12CommandBuffer entry = Recording;
            entry.AbortCommandData();
            _recording = null;
            _activeList = null;
            entry.ReleaseEncodingReferences();
            ResetEncodingState();
            entry.CompleteUse();
        }

        internal void ReleaseCommandBuffer(D3D12CommandBuffer entry) => entry.CompleteUse();

        internal void MarkDeviceLost()
        {
            D3D12CommandBuffer? recording;
            lock (_gate)
            {
                recording = _recording;
                if (recording is not null)
                {
                    _recording = null;
                    _activeList = null;
                    ResetEncodingState();
                }
            }
            if (recording is not null)
            {
                recording.AbortCommandData();
                recording.ReleaseEncodingReferences();
                recording.CompleteUse();
            }
            for (int index = 0; ; index++)
            {
                D3D12CommandBuffer entry;
                lock (_gate)
                {
                    if (index >= _buffers.Count)
                        break;
                    entry = _buffers[index];
                }
                entry.MarkDeviceLost();
            }
        }

        internal override void Release(bool fromParent)
        {
            D3D12CommandBuffer? recording;
            bool discardExecutable;
            lock (_gate)
            {
                recording = _recording;
                if (recording is not null)
                {
                    _recording = null;
                    _activeList = null;
                    ResetEncodingState();
                }
                discardExecutable =
                    fromParent || _device.IsDisposed || _device.Status != DeviceStatus.Active;
            }
            if (recording is not null)
            {
                recording.AbortCommandData();
                recording.ReleaseEncodingReferences();
                recording.CompleteUse();
            }
            for (int index = 0; ; index++)
            {
                D3D12CommandBuffer entry;
                lock (_gate)
                {
                    if (index >= _buffers.Count)
                        break;
                    entry = _buffers[index];
                }
                if (discardExecutable)
                    entry.DiscardExecutableFromDevice();
                entry.ReleaseContextReference();
            }
            lock (_gate)
                _buffers.Clear();
            _device.UnregisterChild(this);
        }

        private ulong AllocateSequence()
        {
            
            return _nextSequence++;
        }
    }

    private sealed partial class D3D12CommandBuffer
    {
        private readonly D3D12CommandContext _context;
        private readonly D3D12CommandRecording _commands;
        private readonly List<nint> _transientObjects = [];
        private int _transientObjectCapacity;
        private DescriptorGeneration? _descriptorGeneration;
        private int _references = 1;
        private int _busy;

        internal D3D12CommandBuffer(D3D12Device device, D3D12CommandContext context)
        {
            _context = context;
            _commands = new D3D12CommandRecording(context, this, context.NativeQueue);
            _nativeRecording = CreateCachedCommands();
            _cachedCommands[0] = _nativeRecording;
        }

        internal ID3D12GraphicsCommandList10* List => _nativeRecording.List;
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
        internal D3D12CommandRecording ActivateCommands(ulong sequence)
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

            ResetOrdinaryDataArena();
            ResetTemporaryAttachmentDescriptors();
            if (_context.QueueType != QueueType.Copy)
                ResetDescriptorArenaState(description);
            BeginCommandData(description.Repeatable);
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

        internal void ReleaseEncodingReferences() => ClearRecordedRayTables();

        internal void ReleaseContextReference() => ReleaseReference();

        private void ReleaseReference()
        {
            if (Interlocked.Decrement(ref _references) != 0)
                return;
            ReleaseTransients();
            ReleaseCachedCommands();
            ReleaseTemporaryAttachmentDescriptors();
            ReleaseDescriptorArena();
        }

        private void ReleaseTransients()
        {
            foreach (nint value in _transientObjects)
            {
                if (value != 0)
                    _ = ResourceTrace.Release(((IUnknown*)value));
            }
            _transientObjects.Clear();
            ClearSwapchainUses();
            ClearRecordedRayTables();
            ReleaseBindingTransients();
            Interlocked.Exchange(ref _descriptorGeneration, null)?.Release();
        }

    }

    private sealed partial class D3D12CommandRecording : RecordedCommandsData
    {
        private readonly D3D12CommandContext _context;
        private readonly D3D12CommandBuffer _commandBuffer;
        internal D3D12CommandRecording? DeviceNext;
        internal D3D12CommandRecording? DevicePrevious;
        internal D3D12CommandRecording? DeviceLossWorkNext;
        internal bool DeviceRegistered;

        internal D3D12CommandRecording(
            D3D12CommandContext context,
            D3D12CommandBuffer entry,
            D3D12Queue queue)
            : base(context.Device, queue)
        {
            _context = context;
            _commandBuffer = entry;
        }

        internal void ActivateCommands(ulong sequence) => Activate(sequence);

        internal ID3D12GraphicsCommandList10* GetNativeList(ulong sequence)
        {

            return _commandBuffer.List;
        }

        protected override void DiscardUnsubmitted(ulong sequence) => ReleaseCommandBuffer(sequence);

        internal void Retire(ulong sequence)
        {

            ReleaseCommandBuffer(sequence);
        }

        internal void MarkDeviceLostAndAbandon(ulong sequence)
        {
            MarkDeviceLost(sequence);
            ReleaseCommandBuffer(sequence);
        }

        internal void MarkDeviceLostRetained(ulong sequence) => MarkDeviceLost(sequence);

        internal void MarkDeviceLostFromDevice()
        {
            if (TryMarkDeviceLostFromDevice(out ulong sequence, out bool abandon) && abandon)
                ReleaseCommandBuffer(sequence);
        }

        internal void DiscardExecutableFromDevice()
        {
            if (TryDiscardExecutableFromDevice(out ulong sequence))
                ReleaseCommandBuffer(sequence);
        }

        private void ReleaseCommandBuffer(ulong sequence)
        {

            _context.NativeDevice.UnregisterCommandPayload(this);
            _context.ReleaseCommandBuffer(_commandBuffer);
        }
    }

    private sealed partial class D3D12RecordedBundle : RecordedBundle
    {
        private readonly D3D12Device _device;
        private D3D12CommandBuffer? _commandBuffer;
        private int _references = 1;

        internal D3D12RecordedBundle(
            D3D12Device device,
            D3D12CommandBuffer entry,
            string? label)
            : base(device, label)
        {
            _device = device;
            _commandBuffer = entry;
        }

        internal ID3D12GraphicsCommandList10* NativeList
        {
            get
            {
                D3D12CommandBuffer? entry = _commandBuffer;
                return entry is null
                    ? default!
                    : entry.List;
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
            return;
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
            D3D12CommandBuffer? entry = Interlocked.Exchange(ref _commandBuffer, null);
            entry?.CompleteUse();
        }
    }

    private sealed partial class D3D12Queue
    {
        private readonly List<D3D12QueueSubmission> _submissionQueue = [];
        private readonly List<D3D12QueueSubmission> _untrustedSubmissions = [];
        private readonly List<D3D12QueueSubmission> _freeSubmissions = [];
        private int _submissionCount;

        internal D3D12QueueSubmission AcquireSubmission(
            int payloadCapacity,
            int completionWaitCapacity,
            int timelineCapacity,
            int imageCapacity)
        {
            D3D12QueueSubmission submission;
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
                    _submissionQueue.EnsureCapacity(newSubmissionCount);
                    _untrustedSubmissions.EnsureCapacity(newSubmissionCount);
                    _freeSubmissions.EnsureCapacity(newSubmissionCount);
                    submission = new D3D12QueueSubmission();
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
            D3D12QueueSubmission submission)
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
                _submissionQueue.Add(submission);
            }
        }

        internal void RegisterUntrustedSubmission(D3D12QueueSubmission submission)
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

        internal void ReturnSubmission(D3D12QueueSubmission submission)
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
                    "D3D12 reported the device-removal completion sentinel.");
            }

            int removeCount = 0;
            foreach (D3D12QueueSubmission submission in _submissionQueue)
            {
                if (submission.Completion > completed)
                    break;
                submission.Retire();
                submission.ResetForReuse();
                _freeSubmissions.Add(submission);
                removeCount++;
            }
            if (removeCount != 0)
                _submissionQueue.RemoveRange(0, removeCount);
        }

        private void DrainOrAbandonPayloads()
        {
            Exception? completionFailure = null;
            try
            {
                ulong target;
                using (Gate.EnterScope())
                {
                    ulong submissionTarget = _submissionQueue.Count == 0
                        ? 0
                        : _submissionQueue[^1].Completion;
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
                        ulong completed = ResourceTrace.GetCompletedValue(Fence);
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
                    (_submissionQueue.Count != 0 ||
                     _untrustedSubmissions.Count != 0 ||
                     GetPresentationRetirementTargetUnderGate() != 0 ||
                     HasUntrustedPresentationRetirementsUnderGate ||
                     GetCapabilityRetirementTargetUnderGate() != 0 ||
                     HasUntrustedCapabilityRetirementsUnderGate))
                {
                    throw completionFailure ?? new InvalidOperationException(
                        "D3D12 native payloads are retained because completion was not verified and device loss was not confirmed.");
                }
                foreach (D3D12QueueSubmission submission in _submissionQueue)
                {
                    submission.Abandon();
                    submission.ResetForReuse();
                    _freeSubmissions.Add(submission);
                }
                _submissionQueue.Clear();
                foreach (D3D12QueueSubmission submission in _untrustedSubmissions)
                {
                    submission.Abandon();
                    submission.ResetForReuse();
                    _freeSubmissions.Add(submission);
                }
                _untrustedSubmissions.Clear();
                AbandonPresentationRetirementsUnderGate();
                AbandonCapabilityRetirementsUnderGate();
            }
        }
    }

    private sealed class D3D12QueueSubmission
    {
        internal D3D12CommandRecording[] Payloads { get; private set; } = [];
        internal ulong[] PayloadSequences { get; private set; } = [];
        internal D3D12Queue[] CompletionWaitQueues { get; private set; } = [];
        internal ulong[] CompletionWaitValues { get; private set; } = [];
        internal D3D12ExternalTimeline[] Timelines { get; private set; } = [];
        internal ulong[] TimelineValues { get; private set; } = [];
        internal D3D12SwapchainImageUse[] Images { get; private set; } = [];
        internal ulong[] ImageSequences { get; private set; } = [];
        internal D3D12SubmittedSwapchainUse[] ImageUses { get; private set; } = [];
        internal D3D12SwapchainImageUse[] ReferencedImages { get; private set; } = [];
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
=>(D3D12CommandContext)value;

        internal static D3D12RecordedBundle Bundle(RecordedBundle value) =>(D3D12RecordedBundle)value;
    }
}
