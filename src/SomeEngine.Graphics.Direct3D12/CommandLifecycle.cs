using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public CommandContext CreateCommandContext(
        Device device,
        in CommandContextDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        nativeDevice.ThrowIfUnavailable();
        if (desc.InitialSlotCount == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "InitialSlotCount must be nonzero.");
        if (desc.NodeIndex >= 32 ||
            (nativeDevice.EnabledNodeMask & (1u << checked((int)desc.NodeIndex))) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desc), "The selected node is not enabled.");
        }

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

    public void Begin(CommandContext context, in CommandRecordingDesc desc) =>
        NativeCast.CommandContext(context).Begin(desc);

    public RecordedCommands End(CommandContext context) =>
        NativeCast.CommandContext(context).EndCommands();

    public RecordedBundle EndBundle(CommandContext context) =>
        NativeCast.CommandContext(context).EndBundle();

    public void Discard(CommandContext context) =>
        NativeCast.CommandContext(context).Discard();

    public QueueCompletion Submit(Queue queue, in QueueSubmitDesc desc)
    {
        D3D12Queue nativeQueue = NativeCast.Queue(queue);
        nativeQueue.Device.ThrowIfUnavailable();

        if (desc.CompletionWaits.IsEmpty &&
            desc.TimelineWaits.IsEmpty &&
            desc.Commands.IsEmpty &&
            desc.SwapchainImages.IsEmpty &&
            desc.TimelineSignals.IsEmpty)
        {
            lock (nativeQueue.Gate)
            {
                nativeQueue.Device.ThrowIfUnavailable();
                return nativeQueue.SignalCompletionUnderGate();
            }
        }

        int timelineCount = checked(desc.TimelineWaits.Length + desc.TimelineSignals.Length);
        D3D12PendingSubmission submission = nativeQueue.AcquireSubmission(
            desc.Commands.Length,
            timelineCount,
            desc.SwapchainImages.Length);
        D3D12RecordedCommandsLease[] payloads = submission.Payloads;
        ulong[] payloadSequences = submission.PayloadSequences;
        D3D12ExternalTimeline[] timelines = submission.Timelines;
        D3D12SwapchainImageLease[] images = submission.Images;
        ulong[] imageSequences = submission.ImageSequences;
        D3D12SubmittedSwapchainUse[] imageUses = submission.ImageUses;
        int claimed = 0;
        int claimedImages = 0;
        int retainedTimelines = 0;
        int requiredSwapchainUseCapacity = 0;
        bool accepted = false;
        bool transferred = false;

        try
        {
            for (int index = 0; index < desc.TimelineWaits.Length; index++)
            {
                timelines[index] = NativeCast.Timeline(desc.TimelineWaits[index].Timeline);
                submission.TimelineCount = index + 1;
            }
            for (int index = 0; index < desc.TimelineSignals.Length; index++)
            {
                timelines[desc.TimelineWaits.Length + index] =
                    NativeCast.Timeline(desc.TimelineSignals[index].Timeline);
                submission.TimelineCount = desc.TimelineWaits.Length + index + 1;
            }

            for (int index = 0; index < desc.Commands.Length; index++)
            {
                RecordedCommands command = desc.Commands[index];
                RecordedCommandsLease lease = command.Lease;
                ulong sequence = command.Sequence;
                if (lease is not D3D12RecordedCommandsLease payload ||
                    !ReferenceEquals(payload.Queue, nativeQueue))
                {
                    throw new ArgumentException(
                        "Every RecordedCommands payload must target the submitted Queue.",
                        nameof(desc));
                }
                if (!payload.TryBeginSubmit(sequence))
                    throw new InvalidOperationException("A RecordedCommands payload has no submission right.");
                payloads[index] = payload;
                payloadSequences[index] = sequence;
                claimed++;
                submission.PayloadCount = claimed;
                requiredSwapchainUseCapacity = checked(
                    requiredSwapchainUseCapacity + payload.GetSwapchainUseCount(sequence));
            }

            submission.EnsureSwapchainUseCapacity(requiredSwapchainUseCapacity);
            for (int index = 0; index < claimed; index++)
            {
                submission.ReferencedImageCount = payloads[index].AccumulateSwapchainUses(
                    payloadSequences[index],
                    submission.ReferencedImages,
                    submission.ReferencedImageUses,
                    submission.ReferencedImageCount);
            }

            if (submission.ReferencedImageCount != desc.SwapchainImages.Length)
            {
                throw new ArgumentException(
                    "SwapchainImages must exactly match the images referenced by Commands.",
                    nameof(desc));
            }
            for (int index = 0; index < desc.SwapchainImages.Length; index++)
            {
                SwapchainImage image = desc.SwapchainImages[index];
                if (image.Lease is not D3D12SwapchainImageLease nativeImage)
                {
                    throw new ArgumentException(
                        "Every SwapchainImage must belong to this D3D12 backend.",
                        nameof(desc));
                }
                for (int prior = 0; prior < index; prior++)
                {
                    if (ReferenceEquals(images[prior], nativeImage))
                    {
                        throw new ArgumentException(
                            "SwapchainImages contains a duplicate image.",
                            nameof(desc));
                    }
                }

                int useIndex = 0;
                while (useIndex < submission.ReferencedImageCount &&
                       !ReferenceEquals(submission.ReferencedImages[useIndex], nativeImage))
                {
                    useIndex++;
                }
                if (useIndex == submission.ReferencedImageCount ||
                    submission.ReferencedImageUses[useIndex].Sequence != image.Sequence)
                {
                    throw new ArgumentException(
                        "SwapchainImages does not match the exact acquisition referenced by Commands.",
                        nameof(desc));
                }
                D3D12SubmittedSwapchainUse use = submission.ReferencedImageUses[useIndex];
                nativeImage.NativeSwapchain.ValidateSubmission(
                    nativeQueue,
                    nativeImage,
                    image.Sequence,
                    use.PresentReady);
                images[index] = nativeImage;
                imageSequences[index] = image.Sequence;
                imageUses[index] = use;
                submission.ImageCount = index + 1;
            }

            for (int index = 0; index < submission.ImageCount; index++)
            {
                if (!images[index].TryBeginSubmit(
                        imageSequences[index],
                        nativeQueue,
                        imageUses[index].PresentReady))
                {
                    throw new InvalidOperationException(
                        "A SwapchainImage has no submission right.");
                }
                claimedImages++;
            }

            for (int index = 0; index < timelineCount; index++)
            {
                timelines[index].RetainSubmission();
                retainedTimelines++;
            }

            lock (nativeQueue.Gate)
            {
                nativeQueue.Device.ThrowIfUnavailable();
                foreach (ref readonly QueueCompletion wait in desc.CompletionWaits)
                {
                    D3D12Queue waitQueue = NativeCast.Queue(wait.Queue);
                    ThrowIfDeviceFailed(
                        nativeQueue.NativeDevice,
                        nativeQueue.Native->Wait(waitQueue.Fence, wait.Value),
                        "ID3D12CommandQueue::Wait");
                    accepted = true;
                }

                for (int index = 0; index < desc.TimelineWaits.Length; index++)
                {
                    ThrowIfDeviceFailed(
                        nativeQueue.NativeDevice,
                        nativeQueue.Native->Wait(
                            timelines[index].Native,
                            desc.TimelineWaits[index].Value),
                        "ID3D12CommandQueue::Wait(external timeline)");
                    accepted = true;
                }

                if (claimed != 0)
                {
                    nint[] nativeLists = submission.NativeLists;
                    for (int index = 0; index < claimed; index++)
                        nativeLists[index] = (nint)payloads[index].GetNativeList(payloadSequences[index]);
                    fixed (nint* lists = nativeLists)
                    {
                        nativeQueue.Native->ExecuteCommandLists(
                            checked((uint)claimed),
                            (ID3D12CommandList**)lists);
                    }
                    accepted = true;
                }

                for (int index = 0; index < desc.TimelineSignals.Length; index++)
                {
                    ThrowIfDeviceFailed(
                        nativeQueue.NativeDevice,
                        nativeQueue.Native->Signal(
                            timelines[desc.TimelineWaits.Length + index].Native,
                            desc.TimelineSignals[index].Value),
                        "ID3D12CommandQueue::Signal(external timeline)");
                    accepted = true;
                }

                QueueCompletion completion = nativeQueue.SignalCompletionUnderGate();
                accepted = true;
                for (int index = 0; index < claimed; index++)
                    payloads[index].MarkSubmitted(payloadSequences[index]);
                for (int index = 0; index < submission.ImageCount; index++)
                {
                    images[index].CommitSubmission(
                        imageSequences[index],
                        nativeQueue,
                        completion.Value);
                }
                nativeQueue.RegisterSubmissionUnderGate(completion.Value, submission);
                transferred = true;
                return completion;
            }
        }
        catch (Exception exception)
        {
            if (!accepted &&
                exception is GraphicsException { Error: GraphicsError.DeviceLost } preAcceptanceLoss)
            {
                for (int index = 0; index < claimed; index++)
                    payloads[index].MarkDeviceLostAndAbandon(payloadSequences[index]);
                for (int index = 0; index < retainedTimelines; index++)
                    timelines[index].ReleaseSubmission();
                throw nativeQueue.Device.Loss ?? preAcceptanceLoss;
            }

            if (!accepted)
            {
                for (int index = 0; index < claimedImages; index++)
                    images[index].RestoreAcquired(imageSequences[index]);
                for (int index = 0; index < claimed; index++)
                    payloads[index].RestoreExecutable(payloadSequences[index]);
                for (int index = 0; index < retainedTimelines; index++)
                    timelines[index].ReleaseSubmission();
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
                images[index].Invalidate(deviceLost: true);
                images[index].NativeSwapchain.MarkDeviceLost();
            }
            for (int index = 0; index < claimed; index++)
                payloads[index].MarkDeviceLostRetained(payloadSequences[index]);
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

    private sealed partial class D3D12CommandContext : CommandContext
    {
        private readonly D3D12Device _device;
        private readonly D3D12Queue _queue;
        private readonly bool _enhancedBarriers;
        private readonly object _gate = new();
        private readonly List<D3D12CommandSlot> _slots = [];
        private D3D12CommandSlot? _recording;
        private ID3D12GraphicsCommandList10* _activeList;
        private ulong _nextSequence = 1;
        private int _released;

        internal D3D12CommandContext(
            D3D12Device device,
            D3D12Queue queue,
            in CommandContextDesc description)
            : base(
                device,
                description.QueueType,
                description.QueueIndex,
                description.NodeIndex,
                description.Bundle,
                description.Label)
        {
            _device = device;
            _queue = queue;
            _enhancedBarriers = device.EnhancedBarriers;
        }

        internal D3D12Device NativeDevice => _device;
        internal D3D12Queue NativeQueue => _queue;
        internal bool EnhancedBarriers => _enhancedBarriers;
        internal ID3D12GraphicsCommandList10* List
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _activeList;
        }
        internal DescriptorGeneration DescriptorGeneration =>
            Recording.DescriptorGeneration;
        internal D3D12CommandSlot Recording
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => _recording!;
        }

        internal void AddTransient(IUnknown* value) =>
            Recording.AddTransient(value);

        internal void Capture(D3D12Buffer value) =>
            Recording.Capture(value, value.NativeLifetime, value.SparseState);

        internal void Capture(D3D12TextureResource value) =>
            Recording.CaptureTexture(value);

        internal void Capture(GraphicsObject owner, NativeLease resource) =>
            Recording.Capture(owner, resource);

        internal void Capture(BufferCbv value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12Buffer resource = NativeCast.Buffer(value.Resource);
            Recording.Capture(value, descriptor.NativeDescriptor, resource.NativeLifetime);
        }

        internal void Capture(BufferSrv value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12Buffer resource = NativeCast.Buffer(value.Resource);
            Recording.Capture(value, descriptor.NativeDescriptor, resource.NativeLifetime);
        }

        internal void Capture(BufferUav value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12Buffer resource = NativeCast.Buffer(value.Resource);
            Recording.Capture(value, descriptor.NativeDescriptor, resource.NativeLifetime);
        }

        internal void Capture(TextureSrv value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12TextureResource resource = NativeCast.Texture(value.Resource);
            D3D12CommandSlot slot = Recording;
            slot.Capture(value, descriptor.NativeDescriptor, resource.NativeLifetime);
            slot.CaptureSwapchainUse(resource);
        }

        internal void Capture(TextureUav value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12TextureResource resource = NativeCast.Texture(value.Resource);
            D3D12CommandSlot slot = Recording;
            slot.Capture(value, descriptor.NativeDescriptor, resource.NativeLifetime);
            slot.CaptureSwapchainUse(resource);
        }

        internal void Capture(ColorAttachmentView value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12TextureResource resource = NativeCast.Texture(value.Resource);
            D3D12CommandSlot slot = Recording;
            slot.Capture(value, descriptor.NativeDescriptor, resource.NativeLifetime);
            slot.CaptureSwapchainUse(resource);
        }

        internal void Capture(DepthStencilView value)
        {
            INativeDescriptor descriptor = (INativeDescriptor)value;
            D3D12TextureResource resource = NativeCast.Texture(value.Resource);
            D3D12CommandSlot slot = Recording;
            slot.Capture(value, descriptor.NativeDescriptor, resource.NativeLifetime);
            slot.CaptureSwapchainUse(resource);
        }

        internal void CaptureBundle(D3D12RecordedBundle value) =>
            Recording.CaptureBundle(value);

        internal void PrepareSlots(uint count)
        {
            for (uint index = 0; index < count; index++)
                _slots.Add(new D3D12CommandSlot(_device, this));
        }

        internal void Begin(in CommandRecordingDesc description)
        {
            ThrowIfDisposed();
            _device.ThrowIfUnavailable();

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
            D3D12CommandSlot slot = Recording;
            ulong sequence = AllocateSequence();
            NativeCall.ThrowIfFailed(
                slot.List->Close(),
                "ID3D12GraphicsCommandList::Close");
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
            D3D12CommandSlot slot = Recording;
            NativeCall.ThrowIfFailed(
                slot.List->Close(),
                "ID3D12GraphicsCommandList::Close(bundle)");
            D3D12RecordedBundle bundle = new(_device, slot, Label);
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
            D3D12CommandSlot slot = Recording;
            _ = slot.List->Close();
            _recording = null;
            _activeList = null;
            slot.ReleaseEncodingReferences();
            ResetEncodingState();
            slot.CompleteUse();
        }

        internal void ReleaseSlot(D3D12CommandSlot slot) => slot.CompleteUse();

        internal void MarkDeviceLost()
        {
            lock (_gate)
            {
                if (_recording is D3D12CommandSlot recording)
                {
                    _ = recording.List->Close();
                    recording.ReleaseEncodingReferences();
                    recording.CompleteUse();
                    _recording = null;
                    _activeList = null;
                    ResetEncodingState();
                }
                foreach (D3D12CommandSlot slot in _slots)
                    slot.MarkDeviceLost();
            }
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            lock (_gate)
            {
                if (_recording is D3D12CommandSlot recording)
                {
                    _ = recording.List->Close();
                    recording.ReleaseEncodingReferences();
                    recording.CompleteUse();
                    _recording = null;
                    _activeList = null;
                    ResetEncodingState();
                }
                bool discardExecutable =
                    fromParent || _device.IsDisposed || _device.Status != DeviceStatus.Active;
                foreach (D3D12CommandSlot slot in _slots)
                {
                    if (discardExecutable)
                        slot.DiscardExecutableFromDevice();
                    slot.ReleaseOwner();
                }
                _slots.Clear();
            }
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
        private readonly AutomaticCaptureArena? _automaticCaptures;
        private readonly HashSet<D3D12RecordedBundle> _capturedBundles =
            new(ReferenceEqualityComparer.Instance);
        private DescriptorGeneration? _descriptorGeneration;
        private ID3D12CommandAllocator* _allocator;
        private ID3D12GraphicsCommandList10* _list;
        private int _pipelineSetters;
        private int _persistentBindingSetters;
        private int _viewportSetters;
        private int _scissorSetters;
        private int _references = 1;
        private int _busy;

        internal D3D12CommandSlot(D3D12Device device, D3D12CommandContext context)
        {
            _context = context;
            _commands = new D3D12RecordedCommandsLease(context, this, context.NativeQueue);
            if (device.RetirementType == RetirementType.Automatic)
                _automaticCaptures = new AutomaticCaptureArena();
            CommandListType type = context.Bundle
                ? CommandListType.Bundle
                : ToCommandListType(context.QueueType);
            Guid allocatorIid = ID3D12CommandAllocator.Guid;
            ID3D12CommandAllocator* allocator = null;
            NativeCall.ThrowIfFailed(
                device.Native->CreateCommandAllocator(
                    type,
                    &allocatorIid,
                    (void**)&allocator),
                "ID3D12Device::CreateCommandAllocator");
            _allocator = allocator;
            try
            {
                Guid listIid = ID3D12GraphicsCommandList10.Guid;
                ID3D12GraphicsCommandList10* list = null;
                NativeCall.ThrowIfFailed(
                    device.Native->CreateCommandList1(
                        1u << checked((int)context.NodeIndex),
                        type,
                        CommandListFlags.None,
                        &listIid,
                        (void**)&list),
                    "ID3D12Device4::CreateCommandList1");
                _list = list;
            }
            catch
            {
                _ = _allocator->Release();
                _allocator = null;
                throw;
            }
        }

        internal ID3D12GraphicsCommandList10* List => _list;
        internal DescriptorGeneration DescriptorGeneration => _descriptorGeneration
            ?? throw new InvalidOperationException("The command slot has no descriptor generation.");
        internal D3D12CommandStatistics Statistics => new(
            _pipelineSetters,
            _persistentBindingSetters,
            _viewportSetters,
            _scissorSetters);

        internal void RecordPipelineSetter() => _pipelineSetters++;
        internal void RecordPersistentBindingSetter() => _persistentBindingSetters++;
        internal void RecordViewportSetter() => _viewportSetters++;
        internal void RecordScissorSetter() => _scissorSetters++;

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

            ReleaseTransients();
            _pipelineSetters = 0;
            _persistentBindingSetters = 0;
            _viewportSetters = 0;
            _scissorSetters = 0;
            ResetOrdinaryDataArena();
            ResetTemporaryAttachmentDescriptors();
            if (_context.QueueType != QueueType.Copy)
            {
                _descriptorGeneration = _context.NativeDevice.Descriptors.CaptureCurrent();
                ValidateDescriptorArenaCapacity(description);
            }
            NativeCall.ThrowIfFailed(_allocator->Reset(), "ID3D12CommandAllocator::Reset");
            NativeCall.ThrowIfFailed(
                _list->Reset(_allocator, null),
                "ID3D12GraphicsCommandList::Reset");

            if (_context.QueueType == QueueType.Copy)
                return;

            try
            {
                ResetDescriptorArena(description);
            }
            catch
            {
                _ = _list->Close();
                throw;
            }
        }

        internal void CompleteUse()
        {
            if (Interlocked.Exchange(ref _busy, 0) == 0)
                return;
            ReleaseTransients();
            ReleaseReference();
        }

        internal void MarkDeviceLost() => _commands.MarkDeviceLostFromDevice();

        internal void DiscardExecutableFromDevice() =>
            _commands.DiscardExecutableFromDevice();

        internal void AddTransient(IUnknown* value) =>
            _transientObjects.Add((nint)value);

        internal void Capture(
            GraphicsObject owner,
            NativeLease resource,
            D3D12SparseState? sparseState = null) =>
            _automaticCaptures?.Capture(owner, resource, sparseState);

        internal void CaptureTexture(D3D12TextureResource texture)
        {
            Capture(texture.Owner, texture.NativeLifetime, texture.SparseState);
            CaptureSwapchainUse(texture);
        }

        internal void Capture(
            GraphicsObject owner,
            DescriptorLease descriptor,
            NativeLease? resource = null) =>
            _automaticCaptures?.Capture(owner, descriptor, resource);

        internal void CaptureBundle(D3D12RecordedBundle bundle)
        {
            if (_capturedBundles.Add(bundle))
                bundle.RetainExecution();
            _automaticCaptures?.CaptureObject(bundle);
        }

        internal void ReleaseEncodingReferences() => ClearRayTracingSnapshots();

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
            _automaticCaptures?.ReleaseAll();
            foreach (D3D12RecordedBundle bundle in _capturedBundles)
                bundle.ReleaseExecution();
            _capturedBundles.Clear();
            ClearSwapchainUses();
            ClearRayTracingSnapshots();
            ReleaseBindingTransients();
            Interlocked.Exchange(ref _descriptorGeneration, null)?.Release();
        }
    }

    private sealed class AutomaticCaptureArena
    {
        private readonly HashSet<NativeLease> _resources =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<DescriptorLease> _descriptors =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<GraphicsObject> _objects =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SparseMappingGeneration> _sparseGenerations =
            new(ReferenceEqualityComparer.Instance);

        internal int ObjectCount => _objects.Count;

        internal void PrepareCapacity(int bindingCount)
        {
            _descriptors.EnsureCapacity(checked(_descriptors.Count + bindingCount));
            _resources.EnsureCapacity(checked(_resources.Count + checked(bindingCount * 2)));
            _objects.EnsureCapacity(checked(_objects.Count + checked(bindingCount * 2)));
        }

        internal void Capture(
            GraphicsObject owner,
            NativeLease resource,
            D3D12SparseState? sparseState)
        {
            if (_resources.Add(resource))
                resource.Retain();
            if (sparseState is not null)
            {
                SparseMappingGeneration generation = sparseState.CaptureCurrent();
                if (!_sparseGenerations.Add(generation))
                    generation.Release();
            }
            _objects.Add(owner);
        }

        internal void Capture(
            GraphicsObject owner,
            DescriptorLease descriptor,
            NativeLease? resource)
        {
            if (_descriptors.Add(descriptor))
                descriptor.Retain();
            if (resource is not null && _resources.Add(resource))
                resource.Retain();
            _objects.Add(owner);
        }

        internal void CaptureObject(GraphicsObject value) => _objects.Add(value);

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
            _objects.Clear();
        }
    }

    private sealed partial class D3D12RecordedCommandsLease : RecordedCommandsLease
    {
        private readonly D3D12CommandContext _context;
        private readonly D3D12CommandSlot _slot;

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

        internal ID3D12GraphicsCommandList10* GetNativeList(ulong sequence)
        {
            EnsureSequence(sequence);
            return _slot.List;
        }

        internal D3D12CommandStatistics GetStatistics(ulong sequence)
        {
            EnsureSequence(sequence);
            return _slot.Statistics;
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
            int timelineCapacity,
            int imageCapacity)
        {
            D3D12PendingSubmission submission;
            lock (Gate)
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
                submission.EnsureCapacity(payloadCapacity, timelineCapacity, imageCapacity);
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
            lock (Gate)
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
            lock (Gate)
            {
                _freeSubmissions.Add(submission);
            }
        }

        internal void MarkDeviceLost()
        {
            lock (Gate)
            {
                foreach (D3D12PendingSubmission pending in _pendingSubmissions)
                    pending.MarkDeviceLostRetained();
            }
        }

        private void CollectRetiredPayloads(ulong completed)
        {
            if (completed == ulong.MaxValue)
            {
                throw CreateDeviceLoss(
                    _device,
                    DxgiErrorDeviceRemoved,
                    "D3D12 reported the device-removal completion sentinel.");
            }

            lock (Gate)
            {
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
        }

        private void DrainOrAbandonPayloads()
        {
            try
            {
                ulong target;
                lock (Gate)
                    target = _pendingSubmissions.Count == 0
                        ? 0
                        : _pendingSubmissions[^1].Completion;
                if (target != 0 && _device.Status != DeviceStatus.Lost)
                {
                    ulong completed = Fence->GetCompletedValue();
                    if (completed < target)
                    {
                        nint waitEvent = SilkMarshal.CreateWindowsEvent(
                            null,
                            bManualReset: false,
                            bInitialState: false,
                            null);
                        try
                        {
                            NativeCall.ThrowIfFailed(
                                Fence->SetEventOnCompletion(target, (void*)waitEvent),
                                "ID3D12Fence::SetEventOnCompletion");
                            _ = SilkMarshal.WaitWindowsObjects(waitEvent, uint.MaxValue);
                        }
                        finally
                        {
                            _ = SilkMarshal.CloseWindowsHandle(waitEvent);
                        }
                    }
                    CollectRetiredPayloads(Fence->GetCompletedValue());
                }
            }
            catch
            {
            }

            lock (Gate)
            {
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
            }
            DrainOrAbandonCapabilityRetirements();
        }
    }

    private sealed class D3D12PendingSubmission
    {
        internal D3D12RecordedCommandsLease[] Payloads { get; private set; } = [];
        internal ulong[] PayloadSequences { get; private set; } = [];
        internal D3D12ExternalTimeline[] Timelines { get; private set; } = [];
        internal D3D12SwapchainImageLease[] Images { get; private set; } = [];
        internal ulong[] ImageSequences { get; private set; } = [];
        internal D3D12SubmittedSwapchainUse[] ImageUses { get; private set; } = [];
        internal D3D12SwapchainImageLease[] ReferencedImages { get; private set; } = [];
        internal D3D12SubmittedSwapchainUse[] ReferencedImageUses { get; private set; } = [];
        internal nint[] NativeLists { get; private set; } = [];

        internal ulong Completion { get; set; }
        internal int PayloadCount { get; set; }
        internal int TimelineCount { get; set; }
        internal int ImageCount { get; set; }
        internal int ReferencedImageCount { get; set; }

        internal void EnsureCapacity(
            int payloadCapacity,
            int timelineCapacity,
            int imageCapacity)
        {
            Payloads = EnsureCapacity(Payloads, payloadCapacity);
            PayloadSequences = EnsureCapacity(PayloadSequences, payloadCapacity);
            Timelines = EnsureCapacity(Timelines, timelineCapacity);
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
            Array.Clear(Images, 0, ImageCount);
            Array.Clear(ReferencedImages, 0, ReferencedImageCount);
            Array.Clear(ReferencedImageUses, 0, ReferencedImageCount);
            ImageCount = 0;
            ReferencedImageCount = 0;
        }

        internal void ResetForReuse()
        {
            Array.Clear(Payloads, 0, PayloadCount);
            Array.Clear(Timelines, 0, TimelineCount);
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

    private static partial class NativeCast
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal static D3D12CommandContext CommandContext(CommandContext value)
        {
#if DEBUG
            return (D3D12CommandContext)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<CommandContext, D3D12CommandContext>(ref value);
#endif
        }

        internal static D3D12RecordedBundle Bundle(RecordedBundle value)
        {
#if DEBUG
            return (D3D12RecordedBundle)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<RecordedBundle, D3D12RecordedBundle>(ref value);
#endif
        }
    }
}
