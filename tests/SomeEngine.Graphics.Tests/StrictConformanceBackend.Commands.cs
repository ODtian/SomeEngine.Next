using System.Numerics;

namespace SomeEngine.Graphics.Tests;

internal sealed partial class StrictConformanceBackend
{
    public CommandContext CreateCommandContext(Device device, in CommandContextDesc desc)
    {
        ConformanceDevice native = RequireDevice(device);
        _ = native.GetQueue(desc.QueueType, desc.QueueIndex);
        if (desc.InitialSlotCount == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        var result = new ConformanceCommandContext(this, native, desc);
        native.Register(result);
        return result;
    }

    public void Begin(CommandContext context, in CommandRecordingDesc desc = default) =>
        RequireContext(context).Begin(desc);

    public RecordedCommands End(CommandContext context) => RequireContext(context).End();

    public RecordedBundle EndBundle(CommandContext context) => RequireContext(context).EndBundle();

    public void Discard(CommandContext context) => RequireContext(context).Discard();

    public void Barrier(CommandContext context, in MemoryBarrier barrier)
    {
        ValidateBarrierPhase(barrier.Phase);
        RequireContext(context).RecordNoOp();
    }

    public void Barrier(CommandContext context, in BufferBarrier barrier)
    {
        ConformanceCommandContext command = RequireContext(context);
        _ = RequireBuffer((ConformanceDevice)command.Device, barrier.Buffer, nameof(barrier));
        ValidateBarrierPhase(barrier.Phase);
        command.RecordNoOp();
    }

    public void Barrier(CommandContext context, in TextureBarrier barrier)
    {
        ConformanceCommandContext command = RequireContext(context);
        _ = RequireTexture((ConformanceDevice)command.Device, barrier.Texture, nameof(barrier));
        ValidateBarrierPhase(barrier.Phase);
        if (!Enum.IsDefined(barrier.LayoutBefore) || !Enum.IsDefined(barrier.LayoutAfter))
            throw new ArgumentOutOfRangeException(nameof(barrier));
        command.RecordNoOp();
    }

    public void Barrier(CommandContext context, in AliasingBarrier barrier)
    {
        ConformanceCommandContext command = RequireContext(context);
        foreach (ref readonly AliasingResource resource in barrier.Before)
            RequireCommandResource(command, resource.Resource, nameof(barrier));
        foreach (ref readonly AliasingResource resource in barrier.After)
            RequireCommandResource(command, resource.Resource, nameof(barrier));
        command.RecordNoOp();
    }

    public void Barrier(CommandContext context, in QueueRelease barrier)
    {
        ConformanceCommandContext command = RequireContext(context);
        RequireCommandResource(command, barrier.Resource, nameof(barrier));
        if (!Enum.IsDefined(barrier.DestinationQueueType))
            throw new ArgumentOutOfRangeException(nameof(barrier));
        command.RecordNoOp();
    }

    public void Barrier(CommandContext context, in QueueAcquire barrier)
    {
        ConformanceCommandContext command = RequireContext(context);
        RequireCommandResource(command, barrier.Resource, nameof(barrier));
        if (!Enum.IsDefined(barrier.SourceQueueType))
            throw new ArgumentOutOfRangeException(nameof(barrier));
        command.RecordNoOp();
    }

    public void CopyBuffer(CommandContext context, in BufferCopy copy)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceBuffer source = RequireBuffer(
            (ConformanceDevice)command.Device,
            copy.Source,
            nameof(copy));
        ConformanceBuffer destination = RequireBuffer(
            (ConformanceDevice)command.Device,
            copy.Destination,
            nameof(copy));
        if (copy.Size == 0 || copy.SourceOffset > source.Info.Size ||
            copy.Size > source.Info.Size - copy.SourceOffset ||
            copy.DestinationOffset > destination.Info.Size ||
            copy.Size > destination.Info.Size - copy.DestinationOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(copy));
        }
        byte[] sourceStorage = source.Storage;
        byte[] destinationStorage = destination.Storage;
        int sourceOffset = checked(source.StorageOffset + (int)copy.SourceOffset);
        int destinationOffset = checked(destination.StorageOffset + (int)copy.DestinationOffset);
        int count = checked((int)copy.Size);
        command.Record(() =>
            sourceStorage.AsSpan(sourceOffset, count)
                .CopyTo(destinationStorage.AsSpan(destinationOffset, count)));
    }

    public void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceBuffer source = RequireBuffer(
            (ConformanceDevice)command.Device,
            copy.Buffer,
            nameof(copy));
        ConformanceTexture destination = RequireTexture(
            (ConformanceDevice)command.Device,
            copy.Texture,
            nameof(copy));
        ValidateFlatTextureCopy(copy, destination);
        int count = GetFlatTextureCopyByteCount(copy, destination.Info.Format);
        int sourceOffset = checked(source.StorageOffset + (int)copy.BufferOffset);
        if (sourceOffset < source.StorageOffset ||
            count > source.Storage.Length - sourceOffset ||
            count > destination.Bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(copy));
        }
        byte[] sourceStorage = source.Storage;
        byte[] destinationStorage = destination.Storage;
        int destinationOffset = destination.StorageOffset;
        command.Record(() =>
            sourceStorage.AsSpan(sourceOffset, count)
                .CopyTo(destinationStorage.AsSpan(destinationOffset, count)));
    }

    public void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceBuffer destination = RequireBuffer(
            (ConformanceDevice)command.Device,
            copy.Buffer,
            nameof(copy));
        ConformanceTexture source = RequireTexture(
            (ConformanceDevice)command.Device,
            copy.Texture,
            nameof(copy));
        ValidateFlatTextureCopy(copy, source);
        int count = GetFlatTextureCopyByteCount(copy, source.Info.Format);
        int destinationOffset = checked(destination.StorageOffset + (int)copy.BufferOffset);
        if (destinationOffset < destination.StorageOffset ||
            count > destination.Storage.Length - destinationOffset ||
            count > source.Bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(copy));
        }
        byte[] sourceStorage = source.Storage;
        byte[] destinationStorage = destination.Storage;
        int sourceOffset = source.StorageOffset;
        command.Record(() =>
            sourceStorage.AsSpan(sourceOffset, count)
                .CopyTo(destinationStorage.AsSpan(destinationOffset, count)));
    }

    public void CopyTexture(CommandContext context, in TextureCopy copy)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceTexture source = RequireTexture(
            (ConformanceDevice)command.Device,
            copy.Source,
            nameof(copy));
        ConformanceTexture destination = RequireTexture(
            (ConformanceDevice)command.Device,
            copy.Destination,
            nameof(copy));
        if (source.Info.Format != destination.Info.Format ||
            copy.SourceMipLevel != 0 || copy.DestinationMipLevel != 0 ||
            copy.SourceArrayLayer != 0 || copy.DestinationArrayLayer != 0 ||
            copy.SourceX != 0 || copy.SourceY != 0 || copy.SourceZ != 0 ||
            copy.DestinationX != 0 || copy.DestinationY != 0 || copy.DestinationZ != 0)
        {
            throw new NotSupportedException(
                "The strict backend supports flat mip-zero Texture copies only.");
        }
        int count = checked((int)((ulong)copy.Width * copy.Height * copy.Depth *
            BytesPerPixel(source.Info.Format)));
        if (count > source.Bytes.Length || count > destination.Bytes.Length)
            throw new ArgumentOutOfRangeException(nameof(copy));
        byte[] sourceStorage = source.Storage;
        byte[] destinationStorage = destination.Storage;
        int sourceOffset = source.StorageOffset;
        int destinationOffset = destination.StorageOffset;
        command.Record(() =>
            sourceStorage.AsSpan(sourceOffset, count)
                .CopyTo(destinationStorage.AsSpan(destinationOffset, count)));
    }

    public void ResolveTexture(CommandContext context, in TextureResolve resolve)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceTexture source = RequireTexture(
            (ConformanceDevice)command.Device,
            resolve.Source,
            nameof(resolve));
        ConformanceTexture destination = RequireTexture(
            (ConformanceDevice)command.Device,
            resolve.Destination,
            nameof(resolve));
        if (resolve.SourceMipLevel != 0 || resolve.DestinationMipLevel != 0 ||
            resolve.SourceArrayLayer != 0 || resolve.DestinationArrayLayer != 0 ||
            resolve.Format != source.Info.Format || resolve.Format != destination.Info.Format)
        {
            throw new NotSupportedException(
                "The strict backend supports flat mip-zero resolves only.");
        }
        int count = Math.Min(source.Bytes.Length, destination.Bytes.Length);
        byte[] sourceStorage = source.Storage;
        byte[] destinationStorage = destination.Storage;
        int sourceOffset = source.StorageOffset;
        int destinationOffset = destination.StorageOffset;
        command.Record(() =>
            sourceStorage.AsSpan(sourceOffset, count)
                .CopyTo(destinationStorage.AsSpan(destinationOffset, count)));
    }

    public void ClearBuffer(
        CommandContext context,
        Buffer buffer,
        in BufferRange range,
        uint value = 0)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceBuffer target = RequireBuffer(
            (ConformanceDevice)command.Device,
            buffer,
            nameof(buffer));
        BufferRange resolved = range.Resolve(target.Info.Size);
        byte[] storage = target.Storage;
        int offset = checked(target.StorageOffset + (int)resolved.Offset);
        int count = checked((int)resolved.Size);
        command.Record(() =>
        {
            Span<byte> bytes = storage.AsSpan(offset, count);
            for (int index = 0; index + sizeof(uint) <= bytes.Length; index += sizeof(uint))
                BitConverter.TryWriteBytes(bytes.Slice(index, sizeof(uint)), value);
            int written = bytes.Length & ~(sizeof(uint) - 1);
            if (written != bytes.Length)
            {
                Span<byte> pattern = stackalloc byte[sizeof(uint)];
                BitConverter.TryWriteBytes(pattern, value);
                pattern[..(bytes.Length - written)].CopyTo(bytes[written..]);
            }
        });
    }

    public void ClearTexture(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        in Vector4 color)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceTexture target = RequireTexture(
            (ConformanceDevice)command.Device,
            texture,
            nameof(texture));
        byte red = checked((byte)Math.Clamp((int)MathF.Round(color.X * 255), 0, 255));
        byte green = checked((byte)Math.Clamp((int)MathF.Round(color.Y * 255), 0, 255));
        byte blue = checked((byte)Math.Clamp((int)MathF.Round(color.Z * 255), 0, 255));
        byte alpha = checked((byte)Math.Clamp((int)MathF.Round(color.W * 255), 0, 255));
        byte[] storage = target.Storage;
        int offset = target.StorageOffset;
        int count = target.Bytes.Length;
        command.Record(() =>
        {
            Span<byte> bytes = storage.AsSpan(offset, count);
            for (int index = 0; index < bytes.Length; index += 4)
            {
                bytes[index] = red;
                if (index + 1 < bytes.Length) bytes[index + 1] = green;
                if (index + 2 < bytes.Length) bytes[index + 2] = blue;
                if (index + 3 < bytes.Length) bytes[index + 3] = alpha;
            }
        });
    }

    public void ClearDepthStencil(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceTexture target = RequireTexture(
            (ConformanceDevice)command.Device,
            texture,
            nameof(texture));
        byte[] storage = target.Storage;
        int offset = target.StorageOffset;
        int count = target.Bytes.Length;
        command.Record(() => storage.AsSpan(offset, count).Clear());
    }

    public void BeginRendering(CommandContext context, in RenderingDesc desc)
    {
        ConformanceCommandContext command = RequireContext(context);
        foreach (ref readonly ColorAttachmentDesc color in desc.Colors)
        {
            _ = RequireResource(color.View);
            RequireSameDevice((ConformanceDevice)command.Device, color.View, nameof(desc));
            if (color.ResolveView is not null)
                RequireSameDevice((ConformanceDevice)command.Device, RequireResource(color.ResolveView), nameof(desc));
        }
        if (desc.DepthStencil is DepthStencilAttachmentDesc depth)
            RequireSameDevice((ConformanceDevice)command.Device, RequireResource(depth.View), nameof(desc));
        command.BeginRendering();
    }

    public void EndRendering(CommandContext context) => RequireContext(context).EndRendering();

    public void SetPipeline(CommandContext context, Pipeline pipeline)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformancePipeline native = RequireResource(pipeline) as ConformancePipeline
            ?? throw new ArgumentException("The Pipeline has the wrong backend type.", nameof(pipeline));
        RequireSameDevice((ConformanceDevice)command.Device, native, nameof(pipeline));
        command.SetPipeline(native);
    }

    public void SetPersistentParameterBindings(
        CommandContext context,
        PersistentParameterBindings bindings)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformancePersistentBindings native =
            RequireResource(bindings) as ConformancePersistentBindings
            ?? throw new ArgumentException(
                "The PersistentParameterBindings have the wrong backend type.",
                nameof(bindings));
        RequireSameDevice((ConformanceDevice)command.Device, native, nameof(bindings));
        command.RecordNoOp();
    }

    public void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings)
    {
        ConformanceCommandContext command = RequireContext(context);
        ValidateParameterBindings((ConformanceDevice)command.Device, bindings);
        command.RecordNoOp();
    }

    public void SetVertexBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<VertexBufferBinding> bindings)
    {
        ConformanceCommandContext command = RequireContext(context);
        foreach (ref readonly VertexBufferBinding binding in bindings)
            _ = RequireBuffer((ConformanceDevice)command.Device, binding.Buffer, nameof(bindings));
        command.RecordNoOp();
    }

    public void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding)
    {
        ConformanceCommandContext command = RequireContext(context);
        _ = RequireBuffer((ConformanceDevice)command.Device, binding.Buffer, nameof(binding));
        command.RecordNoOp();
    }

    public void SetStreamOutputBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<StreamOutputBufferBinding> bindings)
    {
        ConformanceCommandContext command = RequireContext(context);
        foreach (ref readonly StreamOutputBufferBinding binding in bindings)
            _ = RequireBuffer((ConformanceDevice)command.Device, binding.Buffer, nameof(bindings));
        command.RecordNoOp();
    }

    public void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports)
    {
        if (viewports.IsEmpty)
            throw new ArgumentException("At least one Viewport is required.", nameof(viewports));
        RequireContext(context).RecordNoOp();
    }

    public void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors)
    {
        if (scissors.IsEmpty)
            throw new ArgumentException("At least one ScissorRect is required.", nameof(scissors));
        RequireContext(context).RecordNoOp();
    }

    public void SetBlendConstants(CommandContext context, in Vector4 value) =>
        RequireContext(context).RecordNoOp();

    public void SetStencilReference(CommandContext context, uint value) =>
        RequireContext(context).RecordNoOp();

    public void SetDepthBounds(CommandContext context, float minimum, float maximum) =>
        throw Unsupported("DepthBounds");

    public void SetDepthBias(
        CommandContext context,
        int bias,
        float clamp,
        float slopeScaledBias) =>
        RequireContext(context).RecordNoOp();

    public void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology)
    {
        if (!Enum.IsDefined(topology))
            throw new ArgumentOutOfRangeException(nameof(topology));
        RequireContext(context).RecordNoOp();
    }

    public void SetStripCut(CommandContext context, StripCut stripCut)
    {
        if (!Enum.IsDefined(stripCut))
            throw new ArgumentOutOfRangeException(nameof(stripCut));
        RequireContext(context).RecordNoOp();
    }

    public void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        ConformanceCommandContext command = RequireContext(context);
        if (buffer is not null)
            _ = RequireBuffer((ConformanceDevice)command.Device, buffer, nameof(buffer));
        command.RecordNoOp();
    }

    public void Draw(CommandContext context, in DrawArguments arguments) =>
        RequireContext(context).RecordDraw(PipelineType.Graphics);

    public void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments) =>
        RequireContext(context).RecordDraw(PipelineType.Graphics);

    public void Dispatch(CommandContext context, in DispatchArguments arguments) =>
        RequireContext(context).RecordDraw(PipelineType.Compute);

    public void ExecuteBundle(CommandContext context, RecordedBundle bundle)
    {
        ConformanceCommandContext command = RequireContext(context);
        ConformanceBundle native = RequireResource(bundle) as ConformanceBundle
            ?? throw new ArgumentException("The RecordedBundle has the wrong backend type.", nameof(bundle));
        RequireSameDevice((ConformanceDevice)command.Device, native, nameof(bundle));
        command.Record(native.Actions);
    }

    public void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label) =>
        RequireContext(context).BeginEvent(utf8Label);

    public void EndEvent(CommandContext context) => RequireContext(context).EndEvent();

    public void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        if (utf8Label.IsEmpty)
            throw new ArgumentException("A marker label cannot be empty.", nameof(utf8Label));
        RequireContext(context).RecordNoOp();
    }

    public QueueCompletion Submit(Queue queue, in QueueSubmitDesc desc)
    {
        ConformanceQueue nativeQueue = RequireQueue(queue);
        foreach (ref readonly QueueCompletion wait in desc.CompletionWaits)
        {
            if (!IsComplete(wait))
                throw new InvalidOperationException("A completion wait is not satisfied.");
        }
        if (!desc.TimelineWaits.IsEmpty || !desc.TimelineSignals.IsEmpty)
            throw Unsupported(nameof(ExternalTimelines));

        var commandLeases = new (ConformanceRecordedLease Lease, ulong Sequence)[desc.Commands.Length];
        var imageLeases = new (ConformanceSwapchainImageLease Lease, ulong Sequence)[desc.SwapchainImages.Length];
        int commandCount = 0;
        int imageCount = 0;
        try
        {
            for (; commandCount < desc.Commands.Length; commandCount++)
            {
                RecordedCommands commands = desc.Commands[commandCount];
                if (!ReferenceEquals(commands.Queue, nativeQueue))
                    throw new ArgumentException("RecordedCommands target another Queue.", nameof(desc));
                ConformanceRecordedLease lease = commands.Lease as ConformanceRecordedLease
                    ?? throw new ArgumentException("RecordedCommands have the wrong backend type.", nameof(desc));
                if (!lease.TryBeginSubmit(commands.Sequence))
                    throw new InvalidOperationException("RecordedCommands have no submission right.");
                commandLeases[commandCount] = (lease, commands.Sequence);
            }
            for (; imageCount < desc.SwapchainImages.Length; imageCount++)
            {
                SwapchainImage image = desc.SwapchainImages[imageCount];
                ConformanceSwapchainImageLease lease =
                    image.Lease as ConformanceSwapchainImageLease
                    ?? throw new ArgumentException("SwapchainImage has the wrong backend type.", nameof(desc));
                if (!ReferenceEquals(lease.Swapchain.Device, nativeQueue.Device) ||
                    nativeQueue.Type != QueueType.Graphics ||
                    nativeQueue.Index != 0 ||
                    !lease.TryBeginSubmit(image.Sequence))
                {
                    throw new InvalidOperationException("SwapchainImage has no submission right.");
                }
                imageLeases[imageCount] = (lease, image.Sequence);
            }

            foreach ((ConformanceRecordedLease lease, _) in commandLeases)
                lease.Execute();
            foreach ((ConformanceRecordedLease lease, ulong sequence) in commandLeases)
                lease.MarkSubmitted(sequence);
            ulong completionValue = nativeQueue.CompleteNext();
            foreach ((ConformanceRecordedLease lease, ulong sequence) in commandLeases)
                lease.MarkCompleted(sequence);
            foreach ((ConformanceSwapchainImageLease lease, _) in imageLeases)
                lease.MarkSubmission(nativeQueue, completionValue);
            return new QueueCompletion(nativeQueue, completionValue);
        }
        catch
        {
            for (int index = imageCount - 1; index >= 0; index--)
            {
                (ConformanceSwapchainImageLease lease, ulong sequence) = imageLeases[index];
                if (lease is not null)
                    lease.RestoreAcquired(sequence);
            }
            for (int index = commandCount - 1; index >= 0; index--)
            {
                (ConformanceRecordedLease lease, ulong sequence) = commandLeases[index];
                if (lease is not null)
                    lease.RestoreExecutable(sequence);
            }
            throw;
        }
    }

    private ConformanceCommandContext RequireContext(CommandContext context) =>
        RequireResource(context) as ConformanceCommandContext
        ?? throw new ArgumentException("The CommandContext has the wrong backend type.", nameof(context));

    private void RequireCommandResource(
        ConformanceCommandContext command,
        Resource resource,
        string parameterName)
    {
        Resource native = RequireResource(resource);
        RequireSameDevice((ConformanceDevice)command.Device, native, parameterName);
    }

    private static void ValidateBarrierPhase(BarrierPhase phase)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
    }

    private static void ValidateFlatTextureCopy(
        in BufferTextureCopy copy,
        ConformanceTexture texture)
    {
        if (copy.MipLevel != 0 || copy.ArrayLayer != 0 ||
            copy.X != 0 || copy.Y != 0 || copy.Z != 0 ||
            copy.Width == 0 || copy.Height == 0 || copy.Depth == 0 ||
            copy.Width > texture.Info.Width || copy.Height > texture.Info.Height ||
            copy.Depth > texture.Info.Depth)
        {
            throw new NotSupportedException(
                "The strict backend supports flat mip-zero Texture copies only.");
        }
    }

    private static int GetFlatTextureCopyByteCount(
        in BufferTextureCopy copy,
        Format format) =>
        checked((int)((ulong)copy.Width * copy.Height * copy.Depth * BytesPerPixel(format)));

    private sealed class ConformanceCommandContext : CommandContext, IConformanceObject
    {
        private readonly object _gate = new();
        private readonly List<Action> _actions;
        private ulong _nextSequence = 1;
        private bool _recording;
        private bool _rendering;
        private int _eventDepth;
        private ConformancePipeline? _pipeline;

        internal ConformanceCommandContext(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            in CommandContextDesc desc)
            : base(device, desc.QueueType, desc.QueueIndex, desc.Bundle, desc.Label)
        {
            Owner = owner;
            _actions = new List<Action>(checked((int)desc.InitialSlotCount));
        }

        public StrictConformanceBackend Owner { get; }

        internal void Begin(in CommandRecordingDesc desc)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_recording)
                    throw new InvalidOperationException("The CommandContext is already recording.");
                _actions.Clear();
                _recording = true;
                _rendering = false;
                _eventDepth = 0;
                _pipeline = null;
            }
        }

        internal void Record(Action action)
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                _actions.Add(action);
            }
        }

        internal void Record(ReadOnlySpan<Action> actions)
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                foreach (Action action in actions)
                    _actions.Add(action);
            }
        }

        internal void RecordNoOp() => Record(static () => { });

        internal void SetPipeline(ConformancePipeline pipeline)
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                _pipeline = pipeline;
            }
        }

        internal void RecordDraw(PipelineType type)
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                if (_pipeline is null || _pipeline.Type != type)
                    throw new InvalidOperationException($"A {type} Pipeline is required.");
                _actions.Add(static () => { });
            }
        }

        internal void BeginRendering()
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                if (QueueType != QueueType.Graphics || _rendering)
                    throw new InvalidOperationException("Rendering state is invalid.");
                _rendering = true;
            }
        }

        internal void EndRendering()
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                if (!_rendering)
                    throw new InvalidOperationException("Rendering is not active.");
                _rendering = false;
            }
        }

        internal void BeginEvent(ReadOnlySpan<byte> label)
        {
            if (label.IsEmpty)
                throw new ArgumentException("An event label cannot be empty.", nameof(label));
            lock (_gate)
            {
                RequireRecordingUnderGate();
                _eventDepth = checked(_eventDepth + 1);
            }
        }

        internal void EndEvent()
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                if (_eventDepth == 0)
                    throw new InvalidOperationException("No event is active.");
                _eventDepth--;
            }
        }

        internal RecordedCommands End()
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                if (Bundle || _rendering || _eventDepth != 0)
                    throw new InvalidOperationException("The CommandContext cannot end in its current state.");
                Action[] actions = [.. _actions];
                _actions.Clear();
                _recording = false;
                ulong sequence = NextSequenceUnderGate();
                var lease = new ConformanceRecordedLease(
                    Device,
                    ((ConformanceDevice)Device).GetQueue(QueueType, QueueIndex),
                    actions,
                    sequence);
                return new RecordedCommands(lease, sequence);
            }
        }

        internal RecordedBundle EndBundle()
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                if (!Bundle || _rendering || _eventDepth != 0)
                    throw new InvalidOperationException("The bundle cannot end in its current state.");
                Action[] actions = [.. _actions];
                _actions.Clear();
                _recording = false;
                var result = new ConformanceBundle(Owner, (ConformanceDevice)Device, actions, Label);
                ((ConformanceDevice)Device).Register(result);
                return result;
            }
        }

        internal void Discard()
        {
            lock (_gate)
            {
                RequireRecordingUnderGate();
                _actions.Clear();
                _recording = false;
                _rendering = false;
                _eventDepth = 0;
                _pipeline = null;
            }
        }

        internal override void Release(bool fromParent)
        {
            lock (_gate)
            {
                _actions.Clear();
                _recording = false;
            }
            ((ConformanceDevice)Device).Unregister(this);
        }

        private void RequireRecordingUnderGate()
        {
            ThrowIfDisposed();
            if (!_recording)
                throw new InvalidOperationException("The CommandContext is not recording.");
        }

        private ulong NextSequenceUnderGate()
        {
            ulong result = _nextSequence;
            if (result is 0 or ulong.MaxValue)
                throw new InvalidOperationException("The command sequence domain is exhausted.");
            _nextSequence++;
            return result;
        }
    }

    private sealed class ConformanceRecordedLease : RecordedCommandsLease
    {
        private readonly Action[] _actions;

        internal ConformanceRecordedLease(
            Device device,
            Queue queue,
            Action[] actions,
            ulong sequence)
            : base(device, queue)
        {
            _actions = actions;
            Activate(sequence);
        }

        internal void Execute()
        {
            foreach (Action action in _actions)
                action();
        }

        protected override void DiscardUnsubmitted(ulong sequence) { }
    }

    private sealed class ConformanceBundle : RecordedBundle, IConformanceObject
    {
        internal ConformanceBundle(
            StrictConformanceBackend owner,
            ConformanceDevice device,
            Action[] actions,
            string? label)
            : base(device, label)
        {
            Owner = owner;
            Actions = actions;
        }

        public StrictConformanceBackend Owner { get; }
        internal Action[] Actions { get; }
        internal override void Release(bool fromParent) =>
            ((ConformanceDevice)Device).Unregister(this);
    }
}
