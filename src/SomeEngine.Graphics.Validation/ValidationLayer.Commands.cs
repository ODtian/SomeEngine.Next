using System.Numerics;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer<TBackend>
{
    public CommandContext CreateCommandContext(Device device, in CommandContextDesc desc)
    {
        RequireDevice(device);
        CommandContext context = Track(Backend.CreateCommandContext(device, desc), device);
        lock (_gate)
        {
            _contexts.Add(
                context,
                new ContextValidationState
                {
                    Bundle = desc.Bundle,
                });
        }
        return context;
    }

    public void Begin(CommandContext context, in CommandRecordingDesc desc = default)
    {
        Require(context);
        ContextValidationState state = GetContextState(context);
        lock (state)
        {
            if (state.Recording)
                Reject("Commands", "CommandContext is already Recording.", context.Label);
            if (context.Device.RetirementType == RetirementType.Manual &&
                desc.InitialCapturedResourceCapacity != 0)
            {
                Report(
                    ValidationMessageType.Warning,
                    "Retirement",
                    "InitialCapturedResourceCapacity has no execution effect in Manual retirement mode.",
                    context.Label);
            }

            Backend.Begin(context, desc);
            state.ThreadId = Environment.CurrentManagedThreadId;
            state.Recording = true;
            state.Rendering = false;
            state.Pipeline = null;
            state.PipelineType = null;
            state.PipelineSignature = default;
            state.PipelineSignatureSet = false;
            state.WorkGraphProgram = false;
            state.EventDepth = 0;
            state.QueryEvents.Clear();
            state.QueryPhases.Clear();
            state.ResourceEvents.Clear();
            state.ResourceStates.Clear();
            state.Dependencies.Clear();
        }
    }

    public RecordedCommands End(CommandContext context)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            if (state.Bundle)
                Reject("Commands", "A bundle CommandContext must end with EndBundle.", context.Label);
            if (state.Rendering)
                Reject("Commands", "EndRendering is required before End.", context.Label);
            RecordedCommands result = Backend.End(context);
            var validation = new RecordedValidationState(
                state.QueryEvents.ToArray(),
                state.ResourceEvents.ToArray(),
                state.Dependencies.ToArray());
            lock (_gate)
                _recorded.Add(new RecordedCommandsKey(result), validation);
            state.QueryEvents.Clear();
            state.QueryPhases.Clear();
            state.ResourceEvents.Clear();
            state.ResourceStates.Clear();
            state.Dependencies.Clear();
            state.Recording = false;
            state.ThreadId = 0;
            state.Pipeline = null;
            state.PipelineType = null;
            state.PipelineSignature = default;
            state.PipelineSignatureSet = false;
            state.WorkGraphProgram = false;
            state.EventDepth = 0;
            return result;
        }
    }

    public RecordedBundle EndBundle(CommandContext context)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            if (!state.Bundle)
                Reject("Commands", "A non-bundle CommandContext must end with End.", context.Label);
            if (state.Rendering)
                Reject("Commands", "EndRendering is required before EndBundle.", context.Label);
            if (state.QueryEvents.Count != 0)
                Reject("Queries", "Bundles cannot contain query lifecycle operations.", context.Label);
            RecordedBundle result = Track(Backend.EndBundle(context), context.Device);
            _bundleStates.Add(result, new BundleValidationState(state.Dependencies.ToArray()));
            state.QueryEvents.Clear();
            state.QueryPhases.Clear();
            state.ResourceEvents.Clear();
            state.ResourceStates.Clear();
            state.Dependencies.Clear();
            state.Recording = false;
            state.ThreadId = 0;
            state.Pipeline = null;
            state.PipelineType = null;
            state.PipelineSignature = default;
            state.PipelineSignatureSet = false;
            state.WorkGraphProgram = false;
            state.EventDepth = 0;
            return result;
        }
    }

    public void Discard(CommandContext context)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            Backend.Discard(context);
            state.QueryEvents.Clear();
            state.QueryPhases.Clear();
            state.ResourceEvents.Clear();
            state.ResourceStates.Clear();
            state.Dependencies.Clear();
            state.Recording = false;
            state.Rendering = false;
            state.ThreadId = 0;
            state.Pipeline = null;
            state.PipelineType = null;
            state.PipelineSignature = default;
            state.PipelineSignatureSet = false;
            state.WorkGraphProgram = false;
            state.EventDepth = 0;
        }
    }

    public void Barrier(CommandContext context, in MemoryBarrier barrier)
    {
        RequireOutsideRendering(context);
        Backend.Barrier(context, barrier);
    }

    public void Barrier(CommandContext context, in BufferBarrier barrier)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, barrier.Buffer, "Buffer");
        ResourceValidationEvent validationEvent = CreateTransitionEvent(
            barrier.Buffer,
            null,
            barrier.SyncBefore,
            barrier.SyncAfter,
            barrier.AccessBefore,
            barrier.AccessAfter,
            null,
            null);
        lock (state)
        {
            ValidateLocalResourceEvent(state, validationEvent);
            Backend.Barrier(context, barrier);
            ApplyLocalResourceEvent(state, validationEvent);
        }
    }

    public void Barrier(CommandContext context, in TextureBarrier barrier)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, barrier.Texture, "Texture");
        ResourceValidationEvent validationEvent = CreateTransitionEvent(
            barrier.Texture,
            barrier.Range,
            barrier.SyncBefore,
            barrier.SyncAfter,
            barrier.AccessBefore,
            barrier.AccessAfter,
            barrier.LayoutBefore,
            barrier.LayoutAfter);
        lock (state)
        {
            ValidateLocalResourceEvent(state, validationEvent);
            Backend.Barrier(context, barrier);
            ApplyLocalResourceEvent(state, validationEvent);
        }
    }

    public void Barrier(CommandContext context, in AliasingBarrier barrier)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        if (barrier.Before.IsEmpty && barrier.After.IsEmpty)
            Reject("Barriers", "AliasingBarrier requires at least one resource.");
        foreach (AliasingResource resource in barrier.Before)
            RequireOnDevice(context.Device, resource.Resource, "Aliasing resource");
        foreach (AliasingResource resource in barrier.After)
            RequireOnDevice(context.Device, resource.Resource, "Aliasing resource");
        ResourceValidationEvent validationEvent = CreateAliasingEvent(barrier);
        lock (state)
        {
            ValidateLocalResourceEvent(state, validationEvent);
            Backend.Barrier(context, barrier);
            ApplyLocalResourceEvent(state, validationEvent);
        }
    }

    public void Barrier(CommandContext context, in QueueRelease barrier)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, barrier.Resource, "QueueRelease resource");
        ResourceValidationEvent validationEvent = CreateReleaseEvent(barrier);
        lock (state)
        {
            ValidateLocalResourceEvent(state, validationEvent);
            Backend.Barrier(context, barrier);
            ApplyLocalResourceEvent(state, validationEvent);
        }
    }

    public void Barrier(CommandContext context, in QueueAcquire barrier)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, barrier.Resource, "QueueAcquire resource");
        ResourceValidationEvent validationEvent = CreateAcquireEvent(barrier);
        lock (state)
        {
            ValidateLocalResourceEvent(state, validationEvent);
            Backend.Barrier(context, barrier);
            ApplyLocalResourceEvent(state, validationEvent);
        }
    }

    public void CopyBuffer(CommandContext context, in BufferCopy copy)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, copy.Source, "Copy source");
        RequireOnDevice(context.Device, copy.Destination, "Copy destination");
        Backend.CopyBuffer(context, copy);
        RecordCommandDependency(state, copy.Source);
        RecordCommandDependency(state, copy.Destination);
    }

    public void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, copy.Buffer, "Copy buffer");
        RequireOnDevice(context.Device, copy.Texture, "Copy texture");
        Backend.CopyBufferToTexture(context, copy);
        RecordCommandDependency(state, copy.Buffer);
        RecordCommandDependency(state, copy.Texture);
    }

    public void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, copy.Buffer, "Copy buffer");
        RequireOnDevice(context.Device, copy.Texture, "Copy texture");
        Backend.CopyTextureToBuffer(context, copy);
        RecordCommandDependency(state, copy.Buffer);
        RecordCommandDependency(state, copy.Texture);
    }

    public void CopyTexture(CommandContext context, in TextureCopy copy)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, copy.Source, "Copy source");
        RequireOnDevice(context.Device, copy.Destination, "Copy destination");
        Backend.CopyTexture(context, copy);
        RecordCommandDependency(state, copy.Source);
        RecordCommandDependency(state, copy.Destination);
    }

    public void ResolveTexture(CommandContext context, in TextureResolve resolve)
    {
        ContextValidationState state = RequireGraphicsOutsideRendering(context);
        RequireOnDevice(context.Device, resolve.Source, "Resolve source");
        RequireOnDevice(context.Device, resolve.Destination, "Resolve destination");
        Backend.ResolveTexture(context, resolve);
        RecordCommandDependency(state, resolve.Source);
        RecordCommandDependency(state, resolve.Destination);
    }

    public void ClearBuffer(CommandContext context, Buffer buffer, in BufferRange range, uint value = 0)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, buffer, "Buffer");
        Backend.ClearBuffer(context, buffer, range, value);
        RecordCommandDependency(state, buffer);
    }

    public void ClearTexture(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        in Vector4 color)
    {
        ContextValidationState state = RequireGraphicsOutsideRendering(context);
        RequireOnDevice(context.Device, texture, "Texture");
        Backend.ClearTexture(context, texture, range, color);
        RecordCommandDependency(state, texture);
    }

    public void ClearDepthStencil(
        CommandContext context,
        Texture texture,
        in TextureSubresourceRange range,
        float depth = 1,
        byte stencil = 0)
    {
        ContextValidationState state = RequireGraphicsOutsideRendering(context);
        RequireOnDevice(context.Device, texture, "Texture");
        Backend.ClearDepthStencil(context, texture, range, depth, stencil);
        RecordCommandDependency(state, texture);
    }

    public void BeginRendering(CommandContext context, in RenderingDesc desc)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            if (context.QueueType != QueueType.Graphics || context.Bundle)
                Reject("Commands", "BeginRendering requires a non-bundle Graphics CommandContext.", context.Label);
            if (state.Rendering)
                Reject("Commands", "Rendering scopes cannot nest.", context.Label);
            if (desc.Width == 0 || desc.Height == 0)
                Reject("Commands", "Rendering width and height must be nonzero.", context.Label);
            if (desc.Colors.Length > 8)
                Reject("Commands", "Rendering supports at most eight color attachments.", context.Label);
            if (desc.Colors.IsEmpty && desc.DepthStencil is null)
                Reject("Commands", "A rendering scope requires an attachment.", context.Label);
            foreach (ColorAttachmentDesc attachment in desc.Colors)
                RequireOnDevice(context.Device, attachment.View, "Color attachment");
            if (desc.DepthStencil is { } depthStencil)
                RequireOnDevice(context.Device, depthStencil.View, "Depth/stencil attachment");
            Backend.BeginRendering(context, desc);
            foreach (ColorAttachmentDesc attachment in desc.Colors)
                RecordCommandDependencyCore(state, attachment.View);
            if (desc.DepthStencil is { } recordedDepthStencil)
                RecordCommandDependencyCore(state, recordedDepthStencil.View);
            state.Rendering = true;
        }
    }

    public void EndRendering(CommandContext context)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            if (!state.Rendering)
                Reject("Commands", "EndRendering requires an open rendering scope.", context.Label);
            Backend.EndRendering(context);
            state.Rendering = false;
        }
    }

    public void SetPipeline(CommandContext context, Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ContextValidationState state = pipeline.Type switch
        {
            PipelineType.Graphics or PipelineType.Mesh => RequireGraphicsRecording(context),
            PipelineType.Compute or PipelineType.RayTracing => RequireComputeOutsideRendering(context),
            PipelineType.WorkGraph => RejectPipelineSelection(context),
            _ => RejectPipelineType(context, pipeline.Type),
        };
        RequireOnDevice(context.Device, pipeline, "Pipeline");
        lock (state)
        {
            Backend.SetPipeline(context, pipeline);
            state.Pipeline = pipeline;
            state.PipelineType = pipeline.Type;
            state.PipelineSignature = pipeline.Signature;
            state.PipelineSignatureSet = true;
            state.WorkGraphProgram = false;
            RecordCommandDependencyCore(state, pipeline);
        }
    }

    public void SetPersistentParameterBindings(
        CommandContext context,
        PersistentParameterBindings bindings)
    {
        ContextValidationState state = RequireRecording(context);
        RequirePipeline(state, context, "parameter bindings");
        RequireOnDevice(context.Device, bindings, "PersistentParameterBindings");
        if (bindings.Status != PersistentParameterBindingsStatus.Published)
            Reject("Descriptors", "PersistentParameterBindings has not been published.", bindings.Label);
        if (!_persistentBindingStates.TryGetValue(bindings, out BindingValidationState? bindingState))
        {
            Reject(
                "Ownership",
                "PersistentParameterBindings was not created through this Validation Layer.",
                bindings.Label);
        }
        RequireParameterBlockLayout(state, context, bindingState!.Layout.Layout);
        Backend.SetPersistentParameterBindings(context, bindings);
        RecordCommandDependency(state, bindings);
        RecordCommandDependencies(state, bindingState.Dependencies);
    }

    public void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings)
    {
        ContextValidationState state = RequireRecording(context);
        RequirePipeline(state, context, "parameter bindings");
        ValidationParameterBlockLayout reflectedLayout = RequireParameterBlockLayout(
            state,
            context,
            bindings.Layout);
        GraphicsObject[] dependencies = ValidateBindings(
            context.Device,
            bindings,
            reflectedLayout);
        Backend.SetTransientParameterBindings(context, bindings);
        RecordCommandDependencies(state, dependencies);
    }

    public void SetVertexBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<VertexBufferBinding> bindings)
    {
        ContextValidationState state = RequireGraphicsRecording(context);
        foreach (VertexBufferBinding binding in bindings)
            RequireOnDevice(context.Device, binding.Buffer, "Vertex Buffer");
        Backend.SetVertexBuffers(context, firstSlot, bindings);
        foreach (VertexBufferBinding binding in bindings)
            RecordCommandDependency(state, binding.Buffer);
    }

    public void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding)
    {
        ContextValidationState state = RequireGraphicsRecording(context);
        RequireOnDevice(context.Device, binding.Buffer, "Index Buffer");
        Backend.SetIndexBuffer(context, binding);
        RecordCommandDependency(state, binding.Buffer);
    }

    public void SetStreamOutputBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<StreamOutputBufferBinding> bindings)
    {
        ContextValidationState state = RequireGraphicsRecording(context);
        if (context.Bundle)
            Reject("Commands", "Stream-output targets are not legal in a command bundle.", context.Label);
        foreach (StreamOutputBufferBinding binding in bindings)
        {
            RequireOnDevice(context.Device, binding.Buffer, "Stream-output Buffer");
            if (binding.FilledSizeBuffer is not null)
                RequireOnDevice(context.Device, binding.FilledSizeBuffer, "Stream-output filled-size Buffer");
        }
        Backend.SetStreamOutputBuffers(context, firstSlot, bindings);
        foreach (StreamOutputBufferBinding binding in bindings)
        {
            RecordCommandDependency(state, binding.Buffer);
            if (binding.FilledSizeBuffer is not null)
                RecordCommandDependency(state, binding.FilledSizeBuffer);
        }
    }

    public void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports)
    {
        RequireGraphicsRecording(context);
        if (context.Bundle)
            Reject("Commands", "Viewports are not legal in a command bundle.", context.Label);
        Backend.SetViewports(context, viewports);
    }

    public void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors)
    {
        RequireGraphicsRecording(context);
        if (context.Bundle)
            Reject("Commands", "Scissors are not legal in a command bundle.", context.Label);
        Backend.SetScissors(context, scissors);
    }

    public void SetBlendConstants(CommandContext context, in Vector4 value)
    {
        RequireGraphicsRecording(context);
        Backend.SetBlendConstants(context, value);
    }

    public void SetStencilReference(CommandContext context, uint value)
    {
        RequireGraphicsRecording(context);
        Backend.SetStencilReference(context, value);
    }

    public void SetDepthBounds(CommandContext context, float minimum, float maximum)
    {
        RequireGraphicsRecording(context);
        Backend.SetDepthBounds(context, minimum, maximum);
    }

    public void SetDepthBias(CommandContext context, int bias, float clamp, float slopeScaledBias)
    {
        RequireGraphicsRecording(context);
        Backend.SetDepthBias(context, bias, clamp, slopeScaledBias);
    }

    public void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology)
    {
        RequireGraphicsRecording(context);
        Backend.SetPrimitiveTopology(context, topology);
    }

    public void SetStripCut(CommandContext context, StripCut stripCut)
    {
        RequireGraphicsRecording(context);
        Backend.SetStripCut(context, stripCut);
    }

    public void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        if (buffer is not null)
            RequireOnDevice(context.Device, buffer, "Predication Buffer");
        Backend.SetPredication(context, buffer, offset, operation);
        if (buffer is not null)
            RecordCommandDependency(state, buffer);
    }

    public void Draw(CommandContext context, in DrawArguments arguments)
    {
        ContextValidationState state = RequireDraw(context);
        RequirePipeline(state, context, PipelineType.Graphics, "Draw");
        Backend.Draw(context, arguments);
    }

    public void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments)
    {
        ContextValidationState state = RequireDraw(context);
        RequirePipeline(state, context, PipelineType.Graphics, "DrawIndexed");
        Backend.DrawIndexed(context, arguments);
    }

    public void Dispatch(CommandContext context, in DispatchArguments arguments)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        RequirePipeline(state, context, PipelineType.Compute, "Dispatch");
        Backend.Dispatch(context, arguments);
    }

    public void ExecuteBundle(CommandContext context, RecordedBundle bundle)
    {
        ContextValidationState state = RequireDraw(context);
        if (context.Bundle)
            Reject("Commands", "A bundle cannot execute another bundle.", context.Label);
        RequireOnDevice(context.Device, bundle, "RecordedBundle");
        if (!_bundleStates.TryGetValue(bundle, out BundleValidationState? bundleState))
            Reject("Ownership", "RecordedBundle was not created through this Validation Layer.", bundle.Label);
        Backend.ExecuteBundle(context, bundle);
        RecordCommandDependencies(state, bundleState!.Dependencies);
        lock (state)
        {
            state.Pipeline = null;
            state.PipelineType = null;
            state.PipelineSignature = default;
            state.PipelineSignatureSet = false;
            state.WorkGraphProgram = false;
        }
    }

    public void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            Backend.BeginEvent(context, utf8Label);
            state.EventDepth = checked(state.EventDepth + 1);
        }
    }

    public void EndEvent(CommandContext context)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            if (state.EventDepth == 0)
                Reject("Commands", "EndEvent requires a matching BeginEvent.", context.Label);
            Backend.EndEvent(context);
            state.EventDepth--;
        }
    }

    public void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        RequireRecording(context);
        Backend.SetMarker(context, utf8Label);
    }

    public QueueCompletion Submit(Queue queue, in QueueSubmitDesc desc)
    {
        RequireQueue(queue);
        object submissionGate = _queueSubmissionGates.GetValue(queue, static _ => new object());
        lock (submissionGate)
            return SubmitSerialized(queue, desc);
    }

    private QueueCompletion SubmitSerialized(Queue queue, in QueueSubmitDesc desc)
    {
        foreach (QueueCompletion wait in desc.CompletionWaits)
        {
            RequireQueue(wait.Queue);
            RequireSameDevice(queue.Device, wait.Queue.Device, "QueueCompletion");
        }
        foreach (TimelinePoint wait in desc.TimelineWaits)
            RequireOnDevice(queue.Device, wait.Timeline, "Timeline wait");
        foreach (RecordedCommands commands in desc.Commands)
        {
            if (commands.Status != RecordedCommandsStatus.Executable)
                Reject("Submission", "RecordedCommands is not Executable.");
            RequireSameDevice(queue.Device, commands.Device, "RecordedCommands");
            if (!ReferenceEquals(queue, commands.Queue))
                Reject("Submission", "RecordedCommands belongs to another Queue.");
        }

        var images = new HashSet<SwapchainImageLease>(ReferenceEqualityComparer.Instance);
        foreach (SwapchainImage image in desc.SwapchainImages)
        {
            RequireOnDevice(queue.Device, image.Swapchain, "SwapchainImage");
            if (!images.Add(image.Lease))
                Reject("Submission", "QueueSubmitDesc contains a duplicate SwapchainImage.");
        }
        foreach (TimelineSignal signal in desc.TimelineSignals)
            RequireOnDevice(queue.Device, signal.Timeline, "Timeline signal");

        RecordedValidationState[] recordings = GetRecordedSubmissionStates(desc.Commands);
        ManualSubmissionValidationState? manualReservation =
            ReserveManualSubmission(queue.Device, recordings);
        ResourceSubmissionReservation? resourceReservation = null;
        QuerySubmissionReservation? queryReservation = null;
        TimelineSignalReservation? timelineReservation = null;
        try
        {
            resourceReservation = ReserveResourceSubmission(
                queue,
                desc.CompletionWaits,
                desc.TimelineWaits,
                recordings);
            queryReservation = ReserveQuerySubmission(
                queue,
                desc.CompletionWaits,
                desc.TimelineWaits,
                recordings,
                desc.TimelineSignals);
            timelineReservation = ReserveTimelineSignals(desc.TimelineSignals);
            QueueCompletion completion = Backend.Submit(queue, desc);
            CompleteManualSubmission(manualReservation, completion, commit: true);
            CompleteResourceSubmission(
                resourceReservation,
                completion,
                desc.TimelineSignals,
                commit: true);
            CompleteQuerySubmission(queryReservation, queue, completion, commit: true);
            CompleteTimelineSignals(timelineReservation, commit: true);
            ForgetRecordedSubmission(desc.Commands, onlyConsumed: false);
            return completion;
        }
        catch
        {
            CompleteManualSubmission(manualReservation, default, commit: false);
            CompleteResourceSubmission(
                resourceReservation,
                default,
                [],
                commit: false);
            CompleteQuerySubmission(queryReservation, queue, default, commit: false);
            CompleteTimelineSignals(timelineReservation, commit: false);
            ForgetRecordedSubmission(desc.Commands, onlyConsumed: true);
            throw;
        }
    }

    private void ForgetRecordedSubmission(
        ReadOnlySpan<RecordedCommands> commands,
        bool onlyConsumed)
    {
        lock (_gate)
        {
            foreach (RecordedCommands command in commands)
            {
                if (!onlyConsumed || command.Status != RecordedCommandsStatus.Executable)
                    _recorded.Remove(new RecordedCommandsKey(command));
            }
        }
    }

    private ManualSubmissionValidationState? ReserveManualSubmission(
        Device device,
        ReadOnlySpan<RecordedValidationState> recordings)
    {
        if (device.RetirementType != RetirementType.Manual || recordings.IsEmpty)
            return null;

        var dependencies = new HashSet<GraphicsObject>(ReferenceEqualityComparer.Instance);
        foreach (RecordedValidationState recording in recordings)
        foreach (GraphicsObject dependency in recording.Dependencies)
            dependencies.Add(dependency);
        if (dependencies.Count == 0)
            return null;

        foreach (GraphicsObject dependency in dependencies)
        {
            if (dependency.IsDisposed)
            {
                Reject(
                    "Retirement",
                    "A Manual-retirement dependency was disposed before its recorded use was accepted by a Queue.",
                    dependency.Label);
            }
        }

        var reservation = new ManualSubmissionValidationState(dependencies.ToArray());
        lock (_gate)
        {
            _manualSubmissions.EnsureCapacity(checked(_manualSubmissions.Count + 1));
            _manualSubmissions.Add(reservation);
        }
        return reservation;
    }

    private void CompleteManualSubmission(
        ManualSubmissionValidationState? reservation,
        in QueueCompletion completion,
        bool commit)
    {
        if (reservation is null)
            return;

        lock (_gate)
        {
            if (commit)
            {
                reservation.Completion = completion;
                reservation.Accepted = true;
            }
            else
            {
                _manualSubmissions.Remove(reservation);
            }
        }
    }

    private RecordedValidationState[] GetRecordedSubmissionStates(
        ReadOnlySpan<RecordedCommands> commands)
    {
        if (commands.IsEmpty)
            return [];

        lock (_gate)
        {
            var keys = new HashSet<RecordedCommandsKey>();
            var recordings = new RecordedValidationState[commands.Length];
            for (int index = 0; index < commands.Length; index++)
            {
                var key = new RecordedCommandsKey(commands[index]);
                if (!keys.Add(key))
                    Reject("Submission", "QueueSubmitDesc contains duplicate RecordedCommands.");
                if (!_recorded.TryGetValue(key, out RecordedValidationState? recording))
                {
                    Reject(
                        "Ownership",
                        "RecordedCommands was not created through this Validation Layer.");
                }
                recordings[index] = recording!;
            }
            return recordings;
        }
    }

    private TimelineSignalReservation? ReserveTimelineSignals(
        ReadOnlySpan<TimelineSignal> signals)
    {
        if (signals.IsEmpty)
            return null;

        var proposed = new Dictionary<TimelineValidationState, ulong>(
            ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            foreach (TimelineSignal signal in signals)
            {
                if (!_timelines.TryGetValue(signal.Timeline, out TimelineValidationState? state))
                    Reject("Ownership", "ExternalTimeline was not created through this Validation Layer.");

                TimelineValidationState requiredState = state!;
                ulong previous = proposed.TryGetValue(requiredState, out ulong pending)
                    ? pending
                    : requiredState.LastSignalValue;
                bool previousKnown = proposed.ContainsKey(requiredState) || requiredState.LastSignalKnown;
                if (previousKnown && signal.Value < previous)
                {
                    Reject(
                        "Submission",
                        $"ExternalTimeline signal value {signal.Value} is lower than the prior value {previous}.",
                        signal.Timeline.Label);
                }
                proposed[requiredState] = signal.Value;
            }
            foreach (TimelineValidationState state in proposed.Keys)
            {
                if (state.SubmissionInProgress)
                {
                    Reject(
                        "Concurrency",
                        "ExternalTimeline is already being signaled by another Submit.");
                }
            }
            KeyValuePair<TimelineValidationState, ulong>[] entries = proposed.ToArray();
            foreach (TimelineValidationState state in proposed.Keys)
                state.SubmissionInProgress = true;

            return new TimelineSignalReservation(entries);
        }
    }

    private void CompleteTimelineSignals(
        TimelineSignalReservation? reservation,
        bool commit)
    {
        if (reservation is null)
            return;

        lock (_gate)
        {
            foreach ((TimelineValidationState state, ulong value) in reservation.Entries)
            {
                if (commit)
                {
                    state.LastSignalKnown = true;
                    state.LastSignalValue = value;
                }
                state.SubmissionInProgress = false;
            }
        }
    }

    private QuerySubmissionReservation? ReserveQuerySubmission(
        Queue queue,
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        ReadOnlySpan<RecordedValidationState> recordings,
        ReadOnlySpan<TimelineSignal> timelineSignals)
    {
        if (recordings.IsEmpty)
            return null;

        lock (_gate)
        {
            var simulated = new Dictionary<QuerySlot, QuerySubmissionEntry>();
            foreach (RecordedValidationState recording in recordings)
            {
                foreach (QueryValidationEvent queryEvent in recording.QueryEvents)
                {
                    QuerySlot slot = queryEvent.Slot;
                    if (queue.Device.RetirementType == RetirementType.Manual &&
                        slot.Pool.IsDisposed)
                    {
                        Reject(
                            "Lifetime",
                            "QueryPool was disposed before its RecordedCommands were submitted.",
                            slot.Pool.Label);
                    }

                    if (!simulated.TryGetValue(slot, out QuerySubmissionEntry entry))
                    {
                        if (!_queryStates.TryGetValue(slot, out QueryValidationState? state))
                            state = new QueryValidationState();
                        if (state.SubmissionInProgress)
                        {
                            Reject(
                                "Concurrency",
                                "Query index is already being submitted on another Queue.",
                                slot.Pool.Label);
                        }
                        if (state.HasCompletion &&
                            !IsQueryUseOrdered(
                                state,
                                queue,
                                completionWaits,
                                timelineWaits))
                        {
                            Reject(
                                "Queries",
                                "Cross-Queue query reuse requires an explicit completion or timeline wait.",
                                slot.Pool.Label);
                        }
                        entry = new QuerySubmissionEntry(state, state.Phase);
                    }

                    entry = entry with
                    {
                        Phase = ApplyQueryEvent(slot, entry.Phase, queryEvent.Type),
                    };
                    simulated[slot] = entry;
                }
            }

            if (simulated.Count == 0)
                return null;

            TimelinePoint[] signals = new TimelinePoint[timelineSignals.Length];
            for (int index = 0; index < timelineSignals.Length; index++)
            {
                TimelineSignal signal = timelineSignals[index];
                signals[index] = new TimelinePoint(signal.Timeline, signal.Value);
            }
            QuerySubmissionEntry[] entries = simulated.Values.ToArray();
            var reservation = new QuerySubmissionReservation(entries, signals);
            foreach ((QuerySlot slot, QuerySubmissionEntry entry) in simulated)
            {
                if (!_queryStates.ContainsKey(slot))
                    _queryStates.Add(slot, entry.State);
                entry.State.SubmissionInProgress = true;
            }
            return reservation;
        }
    }

    private bool IsQueryUseOrdered(
        QueryValidationState state,
        Queue queue,
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits)
    {
        if (ReferenceEquals(state.Queue, queue))
            return true;
        if (state.Queue is not null &&
            _completedQueueValues.TryGetValue(state.Queue, out ulong completed) &&
            completed >= state.Completion.Value)
        {
            return true;
        }
        foreach (QueueCompletion wait in completionWaits)
        {
            if (ReferenceEquals(wait.Queue, state.Completion.Queue) &&
                wait.Value >= state.Completion.Value)
            {
                return true;
            }
        }
        foreach (TimelinePoint wait in timelineWaits)
        {
            foreach (TimelinePoint signal in state.TimelineSignals)
            {
                if (ReferenceEquals(wait.Timeline, signal.Timeline) && wait.Value >= signal.Value)
                    return true;
            }
        }
        return false;
    }

    private QueryValidationPhase ApplyQueryEvent(
        in QuerySlot slot,
        QueryValidationPhase phase,
        QueryValidationEventType operation)
    {
        switch (operation)
        {
            case QueryValidationEventType.Begin:
                if (phase is QueryValidationPhase.Active or QueryValidationPhase.Ready)
                    Reject("Queries", "Query index was reused before its prior result was resolved.", slot.Pool.Label);
                return QueryValidationPhase.Active;
            case QueryValidationEventType.End:
                if (phase != QueryValidationPhase.Active)
                    Reject("Queries", "EndQuery has no matching BeginQuery in Queue execution order.", slot.Pool.Label);
                return QueryValidationPhase.Ready;
            case QueryValidationEventType.Write:
                if (phase is QueryValidationPhase.Active or QueryValidationPhase.Ready)
                    Reject("Queries", "Timestamp query index was reused before resolve.", slot.Pool.Label);
                return QueryValidationPhase.Ready;
            case QueryValidationEventType.Resolve:
                if (phase != QueryValidationPhase.Ready)
                    Reject("Queries", "ResolveQueries requires an ended or written query result.", slot.Pool.Label);
                return QueryValidationPhase.Resolved;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private void CompleteQuerySubmission(
        QuerySubmissionReservation? reservation,
        Queue queue,
        in QueueCompletion completion,
        bool commit)
    {
        if (reservation is null)
            return;

        lock (_gate)
        {
            foreach (QuerySubmissionEntry entry in reservation.Entries)
            {
                QueryValidationState state = entry.State;
                if (commit)
                {
                    state.Phase = entry.Phase;
                    state.Queue = queue;
                    state.Completion = completion;
                    state.HasCompletion = true;
                    state.TimelineSignals = reservation.TimelineSignals;
                }
                state.SubmissionInProgress = false;
            }
        }
    }

    private ContextValidationState GetContextState(CommandContext context)
    {
        lock (_gate)
        {
            if (!_contexts.TryGetValue(context, out ContextValidationState? state))
                Reject("Ownership", "CommandContext was not created through this Validation Layer.", context.Label);
            return state!;
        }
    }

    private ContextValidationState RequireRecording(CommandContext context)
    {
        Require(context);
        ContextValidationState state = GetContextState(context);
        lock (state)
        {
            if (!state.Recording)
                Reject("Commands", "CommandContext is not Recording.", context.Label);
            if (state.ThreadId != Environment.CurrentManagedThreadId)
                Reject("Concurrency", "CommandContext recording is externally synchronized and owned by another thread.", context.Label);
        }
        return state;
    }

    private void RecordCommandDependency(
        ContextValidationState state,
        GraphicsObject dependency)
    {
        lock (state)
            RecordCommandDependencyCore(state, dependency);
    }

    private void RecordCommandDependencyCore(
        ContextValidationState state,
        GraphicsObject dependency)
    {
        GraphicsObject? current = dependency;
        while (current is not null && current is not Device)
        {
            state.Dependencies.Add(current);
            current = _objects.TryGetValue(current, out ValidationObjectInfo? info)
                ? info.Parent
                : null;
        }
    }

    private void RecordCommandDependencies(
        ContextValidationState state,
        ReadOnlySpan<GraphicsObject> dependencies)
    {
        lock (state)
        {
            foreach (GraphicsObject dependency in dependencies)
                RecordCommandDependencyCore(state, dependency);
        }
    }

    private ContextValidationState RequireOutsideRendering(CommandContext context)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            if (state.Bundle)
                Reject("Commands", "This command is not legal in a bundle.", context.Label);
            if (state.Rendering)
                Reject("Commands", "This command is not legal inside a rendering scope.", context.Label);
        }
        return state;
    }

    private ContextValidationState RequireInsideRendering(CommandContext context)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            if (!state.Bundle && !state.Rendering)
                Reject("Commands", "This command requires an open rendering scope.", context.Label);
        }
        return state;
    }

    private ContextValidationState RequireGraphicsRecording(CommandContext context)
    {
        ContextValidationState state = RequireRecording(context);
        if (context.QueueType != QueueType.Graphics)
        {
            Reject(
                "Commands",
                "This command requires a Graphics CommandContext.",
                context.Label);
        }
        return state;
    }

    private ContextValidationState RequireGraphicsOutsideRendering(CommandContext context)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        if (context.QueueType != QueueType.Graphics)
        {
            Reject(
                "Commands",
                "This command requires a non-bundle Graphics CommandContext.",
                context.Label);
        }
        return state;
    }

    private ContextValidationState RequireComputeOutsideRendering(CommandContext context)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        if (context.QueueType == QueueType.Copy)
        {
            Reject(
                "Commands",
                "This command requires a Graphics or Compute CommandContext.",
                context.Label);
        }
        return state;
    }

    private ContextValidationState RequireDraw(CommandContext context)
    {
        ContextValidationState state = RequireInsideRendering(context);
        if (context.QueueType != QueueType.Graphics)
            Reject("Commands", "Draw commands require a Graphics CommandContext.", context.Label);
        return state;
    }

    private void RequirePipeline(
        ContextValidationState state,
        CommandContext context,
        string operation)
    {
        lock (state)
        {
            if (state.PipelineType is null)
                Reject("Commands", $"A Pipeline must be selected before {operation}.", context.Label);
        }
    }

    private ValidationParameterBlockLayout RequireParameterBlockLayout(
        ContextValidationState state,
        CommandContext context,
        SlangShaderSharp.VariableLayoutReflection layout)
    {
        Pipeline pipeline;
        lock (state)
        {
            pipeline = state.Pipeline ?? throw new InvalidOperationException(
                "Validation state is missing the selected Pipeline.");
        }
        ValidationParameterBlockLayout reflectedLayout = null!;
        if (!_pipelineBindingStates.TryGetValue(
                pipeline,
                out PipelineBindingValidationState? bindings) ||
            !bindings.TryGet(layout, out reflectedLayout))
        {
            Reject(
                "Bindings",
                "The Slang parameter layout is not part of the selected Pipeline.",
                context.Label);
        }
        return reflectedLayout;
    }

    private void RequirePipeline(
        ContextValidationState state,
        CommandContext context,
        PipelineType expected,
        string operation)
    {
        lock (state)
        {
            if (state.PipelineType != expected)
            {
                Reject(
                    "Commands",
                    $"{operation} requires a selected {expected} Pipeline.",
                    context.Label);
            }
        }
    }

    private ContextValidationState RejectPipelineSelection(CommandContext context)
    {
        Reject(
            "Commands",
            "A Work Graph Pipeline is selected by SetWorkGraphProgram.",
            context.Label);
        return null!;
    }

    private ContextValidationState RejectPipelineType(
        CommandContext context,
        PipelineType type)
    {
        Reject("Commands", $"Pipeline type {type} cannot be selected.", context.Label);
        return null!;
    }
}
