using System.Numerics;
using System.Runtime.InteropServices;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    public CommandContext CreateCommandContext(Device device, in CommandContextDesc desc)
    {
        RequireDevice(device);
        var state = new ContextValidationState
        {
            Bundle = desc.Bundle,
            QueueNodeMask = _deviceStates.GetValue(
                device,
                static _ => throw new InvalidOperationException(
                    "The Device has no Validation queue metadata."))
                .GetQueueNodeMask(desc.QueueType, desc.QueueIndex),
        };
        CommandContextDesc createDesc = desc;
        var objectInfo = new ValidationObjectInfo(device);
        lock (_gate)
        {
            _objects.EnsureAdditionalCapacity();
            _contexts.EnsureAdditionalCapacity();
            CommandContext? result = null;
            bool objectAdded = false;
            bool stateAdded = false;
            try
            {
                result = Backend.CreateCommandContext(device, createDesc);
                _objects.Add(result, objectInfo);
                objectAdded = true;
                _contexts.Add(result, state);
                stateAdded = true;
                return result;
            }
            catch
            {
                if (stateAdded)
                    _contexts.Remove(result!);
                if (objectAdded)
                    _objects.Remove(result!);
                result?.Dispose();
                throw;
            }
        }
    }

    public void Begin(CommandContext context, in CommandRecordingDesc desc = default)
    {
        Require(context);
        ContextValidationState state = GetContextState(context);
        lock (state)
        {
            if (state.Recording)
                Reject("Commands", "CommandContext is already Recording.", context.Label);

            Backend.Begin(context, desc);
            state.ThreadId = Environment.CurrentManagedThreadId;
            state.Recording = true;
            state.Rendering = false;
            state.Pipeline = null;
            state.PipelineType = null;
            state.WorkGraphBound = false;
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
            RecordingValidationPayload payload = state.TransferPayload();
            RecordedValidationState validation = state.RentRecording(payload);
            lock (_gate)
                ReserveValidationCapacity(null, default, reserveRecorded: true);
            bool backendEnded = false;
            RecordedCommands result = default;
            try
            {
                result = Backend.End(context);
                backendEnded = true;
                lock (_gate)
                    _recorded.Add(new RecordedCommandsKey(result), validation);
                ResetContextRecordingState(state);
                return result;
            }
            catch
            {
                if (backendEnded)
                {
                    result.Dispose();
                    ResetContextRecordingState(state);
                }
                else
                {
                    _ = validation.ReleasePayload();
                    state.AvailableRecordings.Push(validation);
                    state.RestorePayload(payload);
                }
                throw;
            }
            finally
            {
                lock (_gate)
                    _recordedCapacityReservations--;
            }
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
            RecordingValidationPayload payload = state.TransferPayload();
            var bundleState = new BundleValidationState(payload.Dependencies);
            try
            {
                var objectInfo = new ValidationObjectInfo(context.Device);
                lock (_gate)
                {
                    _objects.EnsureAdditionalCapacity();
                    _bundleStates.EnsureAdditionalCapacity();
                    RecordedBundle? result = null;
                    bool objectAdded = false;
                    bool stateAdded = false;
                    try
                    {
                        result = Backend.EndBundle(context);
                        _objects.Add(result, objectInfo);
                        objectAdded = true;
                        _bundleStates.Add(result, bundleState);
                        stateAdded = true;
                        ResetContextRecordingState(state);
                        return result;
                    }
                    catch
                    {
                        if (stateAdded)
                            _bundleStates.Remove(result!);
                        if (objectAdded)
                            _objects.Remove(result!);
                        result?.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                if (state.Recording)
                    state.RestorePayload(payload);
                ResetContextRecordingState(state);
                throw;
            }
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
            state.WorkGraphBound = false;
            state.EventDepth = 0;
        }
    }

    public void Barrier(CommandContext context, in MemoryBarrier barrier)
    {
        RequireOutsideRendering(context);
        ValidateBarrierPhase(barrier.Phase);
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
            null,
            barrier.Phase);
        lock (state)
        {
            ValidateLocalResourceEvent(state, validationEvent);
            PrepareLocalResourceEvent(state, validationEvent);
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
            barrier.LayoutAfter,
            barrier.Phase);
        lock (state)
        {
            ValidateLocalResourceEvent(state, validationEvent);
            PrepareLocalResourceEvent(state, validationEvent);
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
            PrepareLocalResourceEvent(state, validationEvent);
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
            PrepareLocalResourceEvent(state, validationEvent);
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
            PrepareLocalResourceEvent(state, validationEvent);
            Backend.Barrier(context, barrier);
            ApplyLocalResourceEvent(state, validationEvent);
        }
    }

    public void CopyBuffer(CommandContext context, in BufferCopy copy)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, copy.Source, "Copy source");
        RequireOnDevice(context.Device, copy.Destination, "Copy destination");
        PrepareCommandDependencies(state, copy.Source, copy.Destination);
        Backend.CopyBuffer(context, copy);
        RecordCommandDependency(state, copy.Source);
        RecordCommandDependency(state, copy.Destination);
    }

    public void CopyBufferToTexture(CommandContext context, in BufferTextureCopy copy)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, copy.Buffer, "Copy buffer");
        RequireOnDevice(context.Device, copy.Texture, "Copy texture");
        PrepareCommandDependencies(state, copy.Buffer, copy.Texture);
        Backend.CopyBufferToTexture(context, copy);
        RecordCommandDependency(state, copy.Buffer);
        RecordCommandDependency(state, copy.Texture);
    }

    public void CopyTextureToBuffer(CommandContext context, in BufferTextureCopy copy)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, copy.Buffer, "Copy buffer");
        RequireOnDevice(context.Device, copy.Texture, "Copy texture");
        PrepareCommandDependencies(state, copy.Buffer, copy.Texture);
        Backend.CopyTextureToBuffer(context, copy);
        RecordCommandDependency(state, copy.Buffer);
        RecordCommandDependency(state, copy.Texture);
    }

    public void CopyTexture(CommandContext context, in TextureCopy copy)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, copy.Source, "Copy source");
        RequireOnDevice(context.Device, copy.Destination, "Copy destination");
        PrepareCommandDependencies(state, copy.Source, copy.Destination);
        Backend.CopyTexture(context, copy);
        RecordCommandDependency(state, copy.Source);
        RecordCommandDependency(state, copy.Destination);
    }

    public void ResolveTexture(CommandContext context, in TextureResolve resolve)
    {
        ContextValidationState state = RequireGraphicsOutsideRendering(context);
        RequireOnDevice(context.Device, resolve.Source, "Resolve source");
        RequireOnDevice(context.Device, resolve.Destination, "Resolve destination");
        PrepareCommandDependencies(state, resolve.Source, resolve.Destination);
        Backend.ResolveTexture(context, resolve);
        RecordCommandDependency(state, resolve.Source);
        RecordCommandDependency(state, resolve.Destination);
    }

    public void ClearBuffer(CommandContext context, Buffer buffer, in BufferRange range, uint value = 0)
    {
        ContextValidationState state = RequireOutsideRendering(context);
        RequireOnDevice(context.Device, buffer, "Buffer");
        PrepareCommandDependency(state, buffer);
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
        PrepareCommandDependency(state, texture);
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
        PrepareCommandDependency(state, texture);
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
            CommandMutationCapacity capacity = default;
            foreach (ColorAttachmentDesc attachment in desc.Colors)
                PrepareCommandDependencyCore(state, attachment.View, ref capacity);
            if (desc.DepthStencil is { } preparedDepthStencil)
                PrepareCommandDependencyCore(state, preparedDepthStencil.View, ref capacity);
            ReserveCommandMutation(state, capacity);
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
            CommandMutationCapacity capacity = default;
            PrepareCommandDependencyCore(state, pipeline, ref capacity);
            ReserveCommandMutation(state, capacity);
            Backend.SetPipeline(context, pipeline);
            state.Pipeline = pipeline;
            state.PipelineType = pipeline.Type;
            state.WorkGraphBound = false;
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
        if (!_persistentBindingStates.TryGetValue(bindings, out BindingValidationState? bindingState))
        {
            Reject(
                "Ownership",
                "PersistentParameterBindings was not created through this Validation Layer.",
                bindings.Label);
        }
        lock (state)
        {
            if (!ReferenceEquals(state.Pipeline, bindingState!.Pipeline))
                Reject(
                    "Bindings",
                    "PersistentParameterBindings can only be used with the exact Pipeline that created it.",
                    context.Label);
        }
        PrepareCommandDependencies(state, bindings, bindingState!.Dependencies);
        try
        {
            Backend.SetPersistentParameterBindings(context, bindings);
        }
        catch (Exception exception) when (exception is ArgumentException or GraphicsException)
        {
            Reject("Bindings", exception.Message, context.Label);
            throw;
        }
        RecordCommandDependency(state, bindings);
        RecordCommandDependencies(state, bindingState.Dependencies);
    }

    public void SetTransientParameterBindings(
        CommandContext context,
        in ParameterBlockBindings bindings)
    {
        ContextValidationState state = RequireRecording(context);
        RequirePipeline(state, context, "parameter bindings");
        VariableLayoutReflection reflectedLayout = RequireParameterBlockLayout(
            state,
            context,
            bindings.Layout);
        Pipeline selectedPipeline;
        lock (state)
            selectedPipeline = state.Pipeline!;
        if (!_pipelineBindingStates.TryGetValue(
                selectedPipeline, out PipelineBindingValidationState? pipelineBindings))
            Reject("Ownership", "The selected Pipeline has no binding validation state.", context.Label);
        if (DiagnoseParameterBindings(reflectedLayout, bindings.Resources,
                bindings.OrdinaryData, pipelineBindings) is string diagnostic)
            Reject("Bindings", diagnostic, context.Label);
        GraphicsObject[] dependencies = CollectBindingDependencies(
            context.Device, bindings.Resources);
        PrepareCommandDependencies(state, dependencies);
        try
        {
            Backend.SetTransientParameterBindings(context, bindings);
        }
        catch (Exception exception) when (exception is ArgumentException or
            GraphicsException or OverflowException)
        {
            Reject("Bindings", exception.Message, context.Label);
            throw;
        }
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
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            foreach (VertexBufferBinding binding in bindings)
                PrepareCommandDependencyCore(state, binding.Buffer, ref capacity);
            ReserveCommandMutation(state, capacity);
        }
        Backend.SetVertexBuffers(context, firstSlot, bindings);
        foreach (VertexBufferBinding binding in bindings)
            RecordCommandDependency(state, binding.Buffer);
    }

    public void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding)
    {
        ContextValidationState state = RequireGraphicsRecording(context);
        RequireOnDevice(context.Device, binding.Buffer, "Index Buffer");
        PrepareCommandDependency(state, binding.Buffer);
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
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            foreach (StreamOutputBufferBinding binding in bindings)
            {
                PrepareCommandDependencyCore(state, binding.Buffer, ref capacity);
                if (binding.FilledSizeBuffer is not null)
                    PrepareCommandDependencyCore(state, binding.FilledSizeBuffer, ref capacity);
            }
            ReserveCommandMutation(state, capacity);
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
        RequireDynamicState(context, DynamicStates.DepthBounds);
        Backend.SetDepthBounds(context, minimum, maximum);
    }

    public void SetDepthBias(CommandContext context, int bias, float clamp, float slopeScaledBias)
    {
        RequireDynamicState(context, DynamicStates.DepthBias);
        Backend.SetDepthBias(context, bias, clamp, slopeScaledBias);
    }

    public void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology)
    {
        RequireGraphicsRecording(context);
        Backend.SetPrimitiveTopology(context, topology);
    }

    public void SetStripCut(CommandContext context, StripCut stripCut)
    {
        RequireDynamicState(context, DynamicStates.StripCut);
        Backend.SetStripCut(context, stripCut);
    }

    private void RequireDynamicState(
        CommandContext context,
        DynamicStates state)
    {
        RequireGraphicsRecording(context);
        if ((context.Device.Capabilities.SupportedDynamicStates & state) == 0)
        {
            Reject(
                "Capabilities",
                $"Dynamic state {state} is unavailable on this Device.",
                context.Label);
        }
    }

    public void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        ContextValidationState state = RequireComputeOutsideRendering(context);
        if (buffer is not null)
        {
            RequireOnDevice(context.Device, buffer, "Predication Buffer");
            PrepareCommandDependency(state, buffer);
        }
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
        PrepareCommandDependencies(state, bundleState!.Dependencies);
        Backend.ExecuteBundle(context, bundle);
        RecordCommandDependencies(state, bundleState!.Dependencies);
        lock (state)
        {
            state.Pipeline = null;
            state.PipelineType = null;
            state.WorkGraphBound = false;
        }
    }

    public void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        ContextValidationState state = RequireRecording(context);
        lock (state)
        {
            int nextDepth = checked(state.EventDepth + 1);
            Backend.BeginEvent(context, utf8Label);
            state.EventDepth = nextDepth;
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

        SubmitValidationWorkspace workspace = _submitWorkspaces.GetValue(
            queue, static _ => new SubmitValidationWorkspace());
        workspace.Clear();
        foreach (SwapchainImage image in desc.SwapchainImages)
        {
            RequireOnDevice(queue.Device, image.Swapchain, "SwapchainImage");
            RequirePresentationQueue(queue, image.Swapchain);
            if (!workspace.Images.Add(image.Lease))
                Reject("Submission", "QueueSubmitDesc contains a duplicate SwapchainImage.");
        }
        foreach (TimelineSignal signal in desc.TimelineSignals)
            RequireOnDevice(queue.Device, signal.Timeline, "Timeline signal");

        GetRecordedSubmissionStates(desc.Commands, workspace);
        SubmitValidationReservation? reservation = null;
        try
        {
            reservation = ReserveSubmitValidation(
                queue,
                desc.CompletionWaits,
                desc.TimelineWaits,
                workspace.Recordings,
                desc.TimelineSignals,
                workspace);
            QueueCompletion completion = Backend.Submit(queue, desc);
            CompleteSubmitValidation(reservation, queue, completion, commit: true);
            ForgetRecordedSubmission(desc.Commands, onlyConsumed: false);
            return completion;
        }
        catch
        {
            CompleteSubmitValidation(reservation, queue, default, commit: false);
            ForgetRecordedSubmission(desc.Commands, onlyConsumed: true);
            throw;
        }
    }

    private SubmitValidationReservation? ReserveSubmitValidation(
        Queue queue,
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        List<RecordedValidationState> recordings,
        ReadOnlySpan<TimelineSignal> timelineSignals,
        SubmitValidationWorkspace workspace)
    {
        lock (_gate)
        {
            ReadOnlySpan<RecordedValidationState> recordingSpan =
                CollectionsMarshal.AsSpan(recordings);
            ResourceSubmissionReservation? resources = PlanResourceSubmission(
                queue,
                completionWaits,
                timelineWaits,
                recordingSpan,
                timelineSignals,
                workspace.Resources);
            QuerySubmissionReservation? queries = PlanQuerySubmission(
                queue,
                completionWaits,
                timelineWaits,
                recordingSpan,
                timelineSignals,
                workspace.Queries);
            TimelineSignalReservation? timelines = PlanTimelineSignals(timelineSignals);
            SubmitValidationReservation reservation = workspace.Reservation;
            reservation.Resources = resources;
            reservation.Queries = queries;
            reservation.Timelines = timelines;

            ReserveValidationCapacity(
                null,
                default,
                reserveRecorded: false,
                queryStateCapacity: queries?.NewStates.Count ?? 0,
                resourceCellCapacities: resources?.CellCapacities,
                reserveTimeline: timelines is not null,
                submitReservation: reservation);
            return reservation;
        }
    }

    private void CompleteSubmitValidation(
        SubmitValidationReservation? reservation,
        Queue queue,
        in QueueCompletion completion,
        bool commit)
    {
        if (reservation is null)
            return;
        lock (_gate)
        {
            CompleteResourceSubmission(reservation.Resources, completion, commit);
            CompleteQuerySubmission(reservation.Queries, queue, completion, commit);
            CompleteTimelineSignals(reservation.Timelines, commit);
            if (!commit && reservation.Queries is not null)
            {
                foreach ((QuerySlot slot, _) in reservation.Queries.NewStates)
                    _queryStates.Remove(slot);
            }
        }
    }

    private void RollbackSubmitPublication(SubmitValidationReservation reservation)
    {
        if (reservation.Resources is not null)
        {
            foreach (ResourceValidationState state in reservation.Resources.States)
                state.SubmissionInProgress = false;
        }
        if (reservation.Queries is not null)
        {
            foreach (QuerySubmissionEntry entry in reservation.Queries.Entries)
                entry.State.SubmissionInProgress = false;
            foreach ((QuerySlot slot, _) in reservation.Queries.NewStates)
                _queryStates.Remove(slot);
        }
        if (reservation.Timelines is not null)
        {
            foreach ((TimelineValidationState state, _) in reservation.Timelines.Entries)
                state.SubmissionInProgress = false;
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
                {
                    var key = new RecordedCommandsKey(command);
                    if (_recorded.Remove(key, out RecordedValidationState? recording))
                        recording.Owner.RecycleRecording(recording);
                }
            }
        }
    }

    private void GetRecordedSubmissionStates(
        ReadOnlySpan<RecordedCommands> commands,
        SubmitValidationWorkspace workspace)
    {
        if (commands.IsEmpty)
            return;

        lock (_gate)
        {
            workspace.CommandKeys.EnsureCapacity(commands.Length);
            workspace.Recordings.EnsureCapacity(commands.Length);
            for (int index = 0; index < commands.Length; index++)
            {
                var key = new RecordedCommandsKey(commands[index]);
                if (!workspace.CommandKeys.Add(key))
                    Reject("Submission", "QueueSubmitDesc contains duplicate RecordedCommands.");
                if (!_recorded.TryGetValue(key, out RecordedValidationState? recording))
                {
                    Reject(
                        "Ownership",
                        "RecordedCommands was not created through this Validation Layer.");
                }
                workspace.Recordings.Add(recording!);
            }
        }
    }

    private TimelineSignalReservation? PlanTimelineSignals(
        ReadOnlySpan<TimelineSignal> signals)
    {
        if (signals.IsEmpty)
            return null;

        var proposed = new Dictionary<TimelineValidationState, ulong>(
            ReferenceEqualityComparer.Instance);
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
        return new TimelineSignalReservation(proposed.ToArray());
    }

    private void CompleteTimelineSignals(
        TimelineSignalReservation? reservation,
        bool commit)
    {
        if (reservation is null)
            return;

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

    private QuerySubmissionReservation? PlanQuerySubmission(
        Queue queue,
        ReadOnlySpan<QueueCompletion> completionWaits,
        ReadOnlySpan<TimelinePoint> timelineWaits,
        ReadOnlySpan<RecordedValidationState> recordings,
        ReadOnlySpan<TimelineSignal> timelineSignals,
        QuerySubmissionReservation workspace)
    {
        if (recordings.IsEmpty)
            return null;

            workspace.Clear();
            Dictionary<QuerySlot, QuerySubmissionEntry> simulated = workspace.Simulated;
            foreach (RecordedValidationState recording in recordings)
            {
                foreach (QueryValidationEvent queryEvent in recording.QueryEvents)
                {
                    QuerySlot slot = queryEvent.Slot;
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

            TimelinePoint[] signals = timelineSignals.IsEmpty
                ? []
                : new TimelinePoint[timelineSignals.Length];
            for (int index = 0; index < timelineSignals.Length; index++)
            {
                TimelineSignal signal = timelineSignals[index];
                signals[index] = new TimelinePoint(signal.Timeline, signal.Value);
            }
            foreach ((QuerySlot slot, QuerySubmissionEntry entry) in simulated)
            {
                workspace.Entries.Add(entry);
                if (!_queryStates.ContainsKey(slot))
                    workspace.NewStates.Add(
                        new KeyValuePair<QuerySlot, QueryValidationState>(slot, entry.State));
            }
            workspace.TimelineSignals = signals;
            return workspace;
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

    private void PrepareCommandDependency(
        ContextValidationState state,
        GraphicsObject dependency)
    {
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            PrepareCommandDependencyCore(state, dependency, ref capacity);
            ReserveCommandMutation(state, capacity);
        }
    }

    private void PrepareCommandDependencyCore(
        ContextValidationState state,
        GraphicsObject dependency,
        ref CommandMutationCapacity capacity)
    {
        GraphicsObject? current = dependency;
        while (current is not null && current is not Device)
        {
            uint visibleNodeMask = current switch
            {
                Buffer buffer => buffer.Info.VisibleNodeMask,
                Texture texture => texture.Info.VisibleNodeMask,
                QueryPool pool => 1u << checked((int)pool.Description.NodeIndex),
                _ => uint.MaxValue,
            };
            if ((visibleNodeMask & state.QueueNodeMask) == 0)
            {
                Reject(
                    "LinkedAdapters",
                    $"Resource VisibleNodeMask 0x{visibleNodeMask:X} does not include " +
                    $"the executing queue node mask 0x{state.QueueNodeMask:X}.",
                    current.Label);
            }
            if (current is Heap heap &&
                _heapStates.TryGetValue(heap, out HeapValidationState? heapState) &&
                (heapState.VisibleNodeMask & state.QueueNodeMask) == 0)
            {
                Reject(
                    "Resources",
                    $"Placed resource heap VisibleNodeMask 0x{heapState.VisibleNodeMask:X} " +
                    $"does not include the executing queue node mask " +
                    $"0x{state.QueueNodeMask:X}.",
                    heap.Label);
            }
            capacity.Dependencies = checked(capacity.Dependencies + 1);
            current = _objects.TryGetValue(current, out ValidationObjectInfo? info)
                ? info.Parent
                : null;
        }
    }

    private void PrepareCommandDependencies(
        ContextValidationState state,
        ReadOnlySpan<GraphicsObject> dependencies)
    {
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            foreach (GraphicsObject dependency in dependencies)
                PrepareCommandDependencyCore(state, dependency, ref capacity);
            ReserveCommandMutation(state, capacity);
        }
    }

    private void PrepareCommandDependencies(
        ContextValidationState state,
        GraphicsObject first,
        GraphicsObject second)
    {
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            PrepareCommandDependencyCore(state, first, ref capacity);
            PrepareCommandDependencyCore(state, second, ref capacity);
            ReserveCommandMutation(state, capacity);
        }
    }

    private void PrepareCommandDependencies(
        ContextValidationState state,
        HashSet<GraphicsObject> dependencies)
    {
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            foreach (GraphicsObject dependency in dependencies)
                PrepareCommandDependencyCore(state, dependency, ref capacity);
            ReserveCommandMutation(state, capacity);
        }
    }

    private void RecordCommandDependencies(
        ContextValidationState state,
        HashSet<GraphicsObject> dependencies)
    {
        lock (state)
        {
            foreach (GraphicsObject dependency in dependencies)
                RecordCommandDependencyCore(state, dependency);
        }
    }

    private void PrepareCommandDependencies(
        ContextValidationState state,
        GraphicsObject first,
        ReadOnlySpan<GraphicsObject> remainder)
    {
        lock (state)
        {
            CommandMutationCapacity capacity = default;
            PrepareCommandDependencyCore(state, first, ref capacity);
            foreach (GraphicsObject dependency in remainder)
                PrepareCommandDependencyCore(state, dependency, ref capacity);
            ReserveCommandMutation(state, capacity);
        }
    }

    private void ReserveCommandMutation(
        ContextValidationState state,
        in CommandMutationCapacity capacity) =>
        ReserveValidationCapacity(state, capacity, reserveRecorded: false);

    private void ReserveValidationCapacity(
        ContextValidationState? state,
        in CommandMutationCapacity capacity,
        bool reserveRecorded,
        int queryStateCapacity = 0,
        Dictionary<ResourceValidationState, int>? resourceCellCapacities = null,
        int completionCapacity = 0,
        bool reserveTimeline = false,
        SubmitValidationReservation? submitReservation = null)
    {
        {
            if (state is not null)
            {
                state.Dependencies.EnsureCapacity(checked(state.Dependencies.Count + capacity.Dependencies));
                state.QueryEvents.EnsureCapacity(checked(state.QueryEvents.Count + capacity.QueryEvents));
                state.QueryPhases.EnsureCapacity(checked(state.QueryPhases.Count + capacity.QueryPhases));
                state.ResourceEvents.EnsureCapacity(checked(state.ResourceEvents.Count + capacity.ResourceEvents));
                state.ResourceStates.EnsureCapacity(checked(state.ResourceStates.Count + capacity.ResourceStates));
            }
            if (reserveRecorded)
            {
                _recorded.EnsureCapacity(checked(
                    _recorded.Count + _recordedCapacityReservations + 1));
                _recordedCapacityReservations = checked(_recordedCapacityReservations + 1);
            }
            if (queryStateCapacity != 0)
            {
                _queryStates.EnsureCapacity(checked(_queryStates.Count + queryStateCapacity));
            }
            if (resourceCellCapacities is not null)
            {
                foreach ((ResourceValidationState resourceState, int demand) in
                         resourceCellCapacities)
                {
                    resourceState.EnsureCellCapacity(demand);
                }
            }
            if (completionCapacity != 0)
            {
                _completedQueueValues.EnsureCapacity(checked(
                    _completedQueueValues.Count + _completionCapacityReservations +
                    completionCapacity));
                _completionCapacityReservations = checked(
                    _completionCapacityReservations + completionCapacity);
            }
            if (submitReservation is not null)
            {
                try
                {
                    if (submitReservation.Resources is not null)
                    {
                        foreach (ResourceValidationState resourceState in
                                 submitReservation.Resources.States)
                        {
                            resourceState.SubmissionInProgress = true;
                        }
                    }
                    if (submitReservation.Queries is not null)
                    {
                        foreach ((QuerySlot slot, QueryValidationState queryState) in
                                 submitReservation.Queries.NewStates)
                        {
                            _queryStates.Add(slot, queryState);
                        }
                        foreach (QuerySubmissionEntry entry in submitReservation.Queries.Entries)
                            entry.State.SubmissionInProgress = true;
                    }
                    if (submitReservation.Timelines is not null)
                    {
                        foreach ((TimelineValidationState timelineState, _) in
                                 submitReservation.Timelines.Entries)
                        {
                            timelineState.SubmissionInProgress = true;
                        }
                    }
                }
                catch
                {
                    RollbackSubmitPublication(submitReservation);
                    throw;
                }
            }
        }
    }

    private void ReserveCompletionCapacity(int capacity = 1)
        => ReserveValidationCapacity(
            null,
            default,
            reserveRecorded: false,
            completionCapacity: capacity);

    private void ReleaseCompletionCapacity(int capacity = 1)
    {
        lock (_gate)
            _completionCapacityReservations -= capacity;
    }

    private static void ResetContextRecordingState(ContextValidationState state)
    {
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
        state.WorkGraphBound = false;
        state.EventDepth = 0;
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

    private VariableLayoutReflection RequireParameterBlockLayout(
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
        if (!_pipelineBindingStates.TryGetValue(
                pipeline,
                out PipelineBindingValidationState? bindings) ||
            !bindings.Contains(layout))
        {
            Reject(
                "Bindings",
                "The Slang parameter layout is not part of the selected Pipeline.",
                context.Label);
        }
        return layout;
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
            "A Work Graph Pipeline is selected by BindWorkGraph.",
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
