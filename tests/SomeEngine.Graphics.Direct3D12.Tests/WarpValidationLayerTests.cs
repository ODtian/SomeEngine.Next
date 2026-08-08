using SlangShaderSharp;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Graphics.Validation;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class WarpValidationLayerTests
{
    [Fact]
    public void Inspect_slang_binding_ranges()
    {
        const string source = """
            Texture2D<float4> inputTextures[2];
            SamplerState inputSampler;
            RWStructuredBuffer<float4> outputValues;
            float multiplier;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTextures[id.x & 1]
                    .SampleLevel(inputSampler, float2(0.5, 0.5), 0) * multiplier;
            }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram shader = D3D12TestShaderProgram.Compile(
            "inspect_slang_binding_ranges",
            source,
            entries);
        VariableLayoutReflection layout = shader.Reflection.GetGlobalParamsVarLayout()
            ?? VariableLayoutReflection.Null;
        TypeLayoutReflection type = layout.TypeLayout;
        var lines = new List<string>
        {
            $"layout={layout.Name} kind={type.Kind} binding={layout.BindingIndex} space={layout.BindingSpace}",
            $"ranges={type.BindingRangeCount} sets={type.DescriptorSetCount} subobjects={type.SubObjectRangeCount}",
            $"uniformSize={type.GetSize(SlangParameterCategory.Uniform)}",
        };
        for (nint index = 0; index < type.BindingRangeCount; index++)
        {
            TypeLayoutReflection leafType = type.GetBindingRangeLeafTypeLayout(index);
            VariableReflection leaf = type.GetBindingRangeLeafVariable(index);
            lines.Add(
                $"range[{index}] type={type.GetBindingRangeType(index)} count={type.GetBindingRangeBindingCount(index)} " +
                $"leaf={leaf.Name} leafKind={leafType.Kind} shape={leafType.ResourceShape} access={leafType.ResourceAccess} " +
                $"set={type.GetBindingRangeDescriptorSetIndex(index)} first={type.GetBindingRangeFirstDescriptorRangeIndex(index)} ranges={type.GetBindingRangeDescriptorRangeCount(index)}");
        }
        for (nint set = 0; set < type.DescriptorSetCount; set++)
        {
            lines.Add($"set[{set}] space={type.GetDescriptorSetSpaceOffset(set)} ranges={type.GetDescriptorSetDescriptorRangeCount(set)}");
            for (nint range = 0; range < type.GetDescriptorSetDescriptorRangeCount(set); range++)
            {
                lines.Add(
                    $"  descriptor[{range}] type={type.GetDescriptorSetDescriptorRangeType(set, range)} " +
                    $"category={type.GetDescriptorSetDescriptorRangeCategory(set, range)} " +
                    $"offset={type.GetDescriptorSetDescriptorRangeIndexOffset(set, range)} " +
                    $"count={type.GetDescriptorSetDescriptorRangeDescriptorCount(set, range)}");
            }
        }
        throw new InvalidOperationException(string.Join(Environment.NewLine, lines));
    }

    [Fact]
    public void Throwing_diagnostic_sink_cannot_interrupt_backend_teardown()
    {
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        var direct = new D3D12Backend(new D3D12BackendOptions(validation));
        var backend = new ValidationLayer<D3D12Backend>(
            direct,
            new ValidationOptions(new ThrowingValidationMessageSink()));
        Device device = D3D12TestSupport.CreateWarpDevice(backend);

        Assert.Null(Record.Exception(backend.Dispose));
        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Null(Record.Exception(backend.Dispose));

        device.Dispose();
    }

    [Fact]
    public void Validated_and_direct_receivers_produce_the_same_native_copy_result()
    {
        Assert.True(OperatingSystem.IsWindows());
        byte[] source = Enumerable.Range(0, 769)
            .Select(static value => unchecked((byte)(value * 37 + 11)))
            .ToArray();

        byte[] direct;
        using (IGraphicsBackend backend = new D3D12Backend())
            direct = D3D12TestSupport.ExecuteCopyChain(backend, source);

        byte[] validated;
        using (var backend = new ValidationLayer<D3D12Backend>(new D3D12Backend()))
        using (Device device = D3D12TestSupport.CreateWarpDevice(backend))
        {
            Assert.True(backend.TryGetCapability(device, out D3D12Diagnostics? diagnostics));
            Assert.NotNull(diagnostics);
            if (!diagnostics.DebugLayerEnabled)
            {
                Assert.False(diagnostics.GpuBasedValidationEnabled);
                Assert.False(diagnostics.SynchronizedQueueValidationEnabled);
            }
            validated = D3D12TestSupport.ExecuteCopyChain(backend, device, source);
        }

        Assert.Equal(source, direct);
        Assert.Equal(direct, validated);
    }

    [Fact]
    public void Foreign_layer_resource_is_rejected_and_reported_before_command_forwarding()
    {
        var messages = new List<ValidationMessage>();
        using var validated = CreateFastLayer(messages);
        using IGraphicsBackend foreignBackend = new D3D12Backend();
        using Device device = D3D12TestSupport.CreateWarpDevice(validated);
        using Device foreignDevice = D3D12TestSupport.CreateWarpDevice(foreignBackend);
        using Buffer destination = validated.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Buffer foreignSource = foreignBackend.CreateBuffer(
            foreignDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using CommandContext context = validated.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));

        validated.Begin(context);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            validated.CopyBuffer(
                context,
                new BufferCopy(foreignSource, 0, destination, 0, 64)));
        validated.Discard(context);

        Assert.Contains("Validation Layer", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            messages,
            static message => message.Type == ValidationMessageType.Error &&
                              message.Area == "Ownership");
    }

    [Fact]
    public void Resource_from_another_device_in_the_same_layer_is_rejected_before_forwarding()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device firstDevice = D3D12TestSupport.CreateWarpDevice(backend);
        using Device secondDevice = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer first = backend.CreateBuffer(
            firstDevice,
            new BufferDesc(64, BufferUsages.CopyDestination),
            MemoryType.Readback);
        using Buffer second = backend.CreateBuffer(
            secondDevice,
            new BufferDesc(64, BufferUsages.CopySource),
            MemoryType.Upload);
        using CommandContext context = backend.CreateCommandContext(
            firstDevice,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));

        backend.Begin(context);
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            backend.CopyBuffer(context, new BufferCopy(second, 0, first, 0, 64)));
        backend.Discard(context);

        Assert.Contains("another Device", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            messages,
            static message => message.Area == "Ownership" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Query_lifecycle_rejects_reuse_before_resolve_and_remains_recordable()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using QueryPool pool = backend.CreateQueryPool(
            device,
            new QueryPoolDesc(QueryType.Timestamp, QueueType.Graphics, 1, Label: "timestamp pool"));
        using Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(pool.ResultInfo.ResultStride, BufferUsages.QueryResolve),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));

        backend.Begin(context);
        backend.WriteTimestamp(context, pool, 0);
        Assert.Throws<InvalidOperationException>(() => backend.WriteTimestamp(context, pool, 0));
        backend.ResolveQueries(
            context,
            pool,
            0,
            1,
            destination,
            new BufferRange(0, pool.ResultInfo.ResultStride));
        using RecordedCommands recorded = backend.End(context);
        RecordedCommands[] commands = [recorded];
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commands, [], []));

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        Assert.Contains(
            messages,
            static message => message.Area == "Queries" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Barrier_history_rejects_an_incorrect_local_Before_state_without_forwarding_it()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "local barrier history"));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 2));

        backend.Begin(context);
        backend.Barrier(context, new BufferBarrier(
            buffer,
            PipelineSync.None,
            PipelineSync.Copy,
            ResourceAccess.NoAccess,
            ResourceAccess.CopyDestination));
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            backend.Barrier(context, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource)));
        backend.Discard(context);

        Assert.Contains("Incorrect Before state", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Type == ValidationMessageType.Error &&
                              message.Text.Contains("Tracked state", StringComparison.Ordinal));
    }

    [Fact]
    public void Barrier_history_commits_only_at_Submit_and_preserves_a_valid_transition_chain()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "submitted barrier history"));
        Queue queue = backend.GetQueue(device, QueueType.Copy);

        using (CommandContext rejectedContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Copy, 0, 0, 1)))
        {
            backend.Begin(rejectedContext);
            backend.Barrier(rejectedContext, new BufferBarrier(
                buffer,
                PipelineSync.Copy,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                ResourceAccess.CopyDestination));
            using RecordedCommands rejected = backend.End(rejectedContext);
            RecordedCommands[] rejectedCommands = [rejected];
            Assert.Throws<InvalidOperationException>(() => backend.Submit(
                queue,
                new QueueSubmitDesc([], [], rejectedCommands, [], [])));
            Assert.Equal(RecordedCommandsStatus.Executable, rejected.Status);
        }

        using (CommandContext firstContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Copy, 0, 0, 2)))
        {
            backend.Begin(firstContext);
            backend.Barrier(firstContext, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopyDestination));
            backend.Barrier(firstContext, new BufferBarrier(
                buffer,
                PipelineSync.Copy,
                PipelineSync.Copy,
                ResourceAccess.CopyDestination,
                ResourceAccess.CopySource));
            using RecordedCommands first = backend.End(firstContext);
            SubmitAndWait(backend, queue, first);
        }

        using (CommandContext secondContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Copy, 0, 0, 1)))
        {
            backend.Begin(secondContext);
            backend.Barrier(secondContext, new BufferBarrier(
                buffer,
                PipelineSync.Copy,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                ResourceAccess.CopyDestination));
            using RecordedCommands second = backend.End(secondContext);
            SubmitAndWait(backend, queue, second);
        }

        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Text.Contains("Incorrect Before state", StringComparison.Ordinal));
    }

    [Fact]
    public void Queue_handoff_requires_an_exact_acquire_and_an_explicit_matching_wait()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "queue handoff"));
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);

        QueueCompletion releaseCompletion;
        using (CommandContext releaseContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Graphics, 0, 0, 2)))
        {
            backend.Begin(releaseContext);
            backend.Barrier(releaseContext, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource));
            backend.Barrier(releaseContext, new QueueRelease(
                buffer,
                null,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                null,
                QueueType.Copy));
            using RecordedCommands release = backend.End(releaseContext);
            RecordedCommands[] commands = [release];
            releaseCompletion = backend.Submit(
                graphicsQueue,
                new QueueSubmitDesc([], [], commands, [], []));
        }

        using CommandContext acquireContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        backend.Begin(acquireContext);
        backend.Barrier(acquireContext, new QueueAcquire(
            buffer,
            null,
            QueueType.Graphics,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            null));
        using RecordedCommands acquire = backend.End(acquireContext);
        RecordedCommands[] acquireCommands = [acquire];

        InvalidOperationException missingWait = Assert.Throws<InvalidOperationException>(() =>
            backend.Submit(
                copyQueue,
                new QueueSubmitDesc([], [], acquireCommands, [], [])));
        Assert.Contains("missing", missingWait.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RecordedCommandsStatus.Executable, acquire.Status);

        QueueCompletion[] waits = [releaseCompletion];
        QueueCompletion completion = backend.Submit(
            copyQueue,
            new QueueSubmitDesc(waits, [], acquireCommands, [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Text.Contains(
                                  "QueueCompletion or ExternalTimeline wait",
                                  StringComparison.Ordinal));
    }

    [Fact]
    public void Queue_handoff_accepts_the_ExternalTimeline_signal_wait_pair_named_by_the_source_Submit()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using ExternalTimeline timeline = backend.CreateExternalTimeline(
            device,
            0,
            "handoff timeline");
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "timeline handoff"));
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);

        using (CommandContext releaseContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Graphics, 0, 0, 2)))
        {
            backend.Begin(releaseContext);
            backend.Barrier(releaseContext, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource));
            backend.Barrier(releaseContext, new QueueRelease(
                buffer,
                null,
                PipelineSync.Copy,
                ResourceAccess.CopySource,
                null,
                QueueType.Copy));
            using RecordedCommands release = backend.End(releaseContext);
            RecordedCommands[] commands = [release];
            TimelineSignal[] signals = [new(timeline, 3)];
            _ = backend.Submit(
                graphicsQueue,
                new QueueSubmitDesc([], [], commands, [], signals));
        }

        using CommandContext acquireContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        backend.Begin(acquireContext);
        backend.Barrier(acquireContext, new QueueAcquire(
            buffer,
            null,
            QueueType.Graphics,
            PipelineSync.Copy,
            ResourceAccess.CopyDestination,
            null));
        using RecordedCommands acquire = backend.End(acquireContext);
        RecordedCommands[] acquireCommands = [acquire];
        TimelinePoint[] waits = [new(timeline, 3)];
        QueueCompletion completion = backend.Submit(
            copyQueue,
            new QueueSubmitDesc([], waits, acquireCommands, [], []));

        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Queue_ownership_rejects_use_on_another_Queue_without_release_and_acquire()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Buffer buffer = backend.CreateBuffer(
            device,
            new BufferDesc(
                256,
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                "queue ownership"));
        Queue graphicsQueue = backend.GetQueue(device, QueueType.Graphics);
        Queue copyQueue = backend.GetQueue(device, QueueType.Copy);

        using (CommandContext graphicsContext = backend.CreateCommandContext(
                   device,
                   new CommandContextDesc(QueueType.Graphics, 0, 0, 1)))
        {
            backend.Begin(graphicsContext);
            backend.Barrier(graphicsContext, new BufferBarrier(
                buffer,
                PipelineSync.None,
                PipelineSync.Copy,
                ResourceAccess.NoAccess,
                ResourceAccess.CopySource));
            using RecordedCommands commands = backend.End(graphicsContext);
            SubmitAndWait(backend, graphicsQueue, commands);
        }

        using CommandContext copyContext = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        backend.Begin(copyContext);
        backend.Barrier(copyContext, new BufferBarrier(
            buffer,
            PipelineSync.Copy,
            PipelineSync.Copy,
            ResourceAccess.CopySource,
            ResourceAccess.CopyDestination));
        using RecordedCommands copyCommandsValue = backend.End(copyContext);
        RecordedCommands[] copyCommands = [copyCommandsValue];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            backend.Submit(
                copyQueue,
                new QueueSubmitDesc([], [], copyCommands, [], [])));
        Assert.Contains("another Queue", exception.Message, StringComparison.Ordinal);
        Assert.Contains(
            messages,
            static message => message.Area == "Barriers" &&
                              message.Text.Contains("QueueRelease", StringComparison.Ordinal));
    }

    [Fact]
    public void Manual_retirement_rejects_a_disposed_recording_dependency_before_Queue_acceptance()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(
            backend,
            RetirementType.Manual);
        Buffer source = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopySource, "manual source"),
            MemoryType.Upload);
        using Buffer destination = backend.CreateBuffer(
            device,
            new BufferDesc(64, BufferUsages.CopyDestination, "manual destination"),
            MemoryType.Readback);
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));

        backend.Begin(context);
        backend.CopyBuffer(context, new BufferCopy(source, 0, destination, 0, 64));
        using RecordedCommands commands = backend.End(context);
        source.Dispose();
        RecordedCommands[] commandSpan = [commands];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            backend.Submit(
                backend.GetQueue(device, QueueType.Copy),
                new QueueSubmitDesc([], [], commandSpan, [], [])));

        Assert.Contains("Manual-retirement dependency", exception.Message, StringComparison.Ordinal);
        Assert.Equal(RecordedCommandsStatus.Executable, commands.Status);
        Assert.Contains(
            messages,
            static message => message.Area == "Retirement" &&
                              message.Type == ValidationMessageType.Error &&
                              message.Label == "manual source");
    }

    [Fact]
    public void Generic_validated_receiver_has_one_idempotent_owning_root()
    {
        var messages = new List<ValidationMessage>();
        var layer = CreateFastLayer(messages);
        var graphics = new Graphics<ValidationLayer<D3D12Backend>>(layer);
        AdapterInfo adapter = SelectWarp(graphics);
        DeviceQueueDesc[] queues = [new(QueueType.Copy)];
        Device device = graphics.CreateDevice(new DeviceDesc(
            adapter.Id,
            RetirementType.Automatic,
            queues,
            label: "validated generic owner"));

        graphics.Dispose();
        graphics.Dispose();
        device.Dispose();

        Assert.Equal(DeviceStatus.Disposed, device.Status);
        Assert.Contains(
            messages,
            static message => message.Area == "Lifetime" &&
                              message.Type == ValidationMessageType.Warning);
    }

    [Fact]
    public void Command_family_scope_pipeline_and_event_misuse_stays_in_the_validation_layer()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using CommandContext copy = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Copy, 0, 0, 1));
        using CommandContext graphics = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));

        backend.Begin(copy);
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetViewports(copy, [new Viewport(0, 0, 1, 1)]));
        Assert.Throws<InvalidOperationException>(() => backend.EndEvent(copy));
        backend.Discard(copy);

        backend.Begin(graphics);
        Assert.Throws<InvalidOperationException>(() =>
            backend.Draw(graphics, new DrawArguments(3, 1, 0, 0)));
        Assert.Throws<InvalidOperationException>(() =>
            backend.Dispatch(graphics, new DispatchArguments(1, 1, 1)));
        Assert.Throws<InvalidOperationException>(() =>
            backend.BeginRendering(graphics, new RenderingDesc([], null, 1, 1)));
        backend.Discard(graphics);

        Assert.True(
            messages.Count(static message =>
                message.Area == "Commands" &&
                message.Type == ValidationMessageType.Error) >= 5);
    }

    [Fact]
    public void Sampler_feedback_mip_region_contract_is_rejected_before_native_creation()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out SamplerFeedback? capability));
        Assert.NotNull(capability);
        Assert.True(capability.SupportedFormats.Contains(Format.R8G8B8A8UNorm));
        using Texture sampled = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                32,
                32,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled));

        Assert.Throws<InvalidOperationException>(() =>
            backend.CreateSamplerFeedbackTexture(
                device,
                new SamplerFeedbackTextureDesc(
                    sampled,
                    SamplerFeedbackType.MinimumMip,
                    32,
                    4)));

        Assert.Contains(
            messages,
            static message => message.Area == "SamplerFeedback" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Unavailable_or_incompatible_shading_rate_image_is_rejected_before_forwarding()
    {
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out VariableRateShading? capability));
        Assert.NotNull(capability);
        using Texture invalidImage = backend.CreateTexture(
            device,
            new TextureDesc(
                TextureDimension.Texture2D,
                1,
                1,
                1,
                1,
                1,
                1,
                Format.R8G8B8A8UNorm,
                TextureUsages.Sampled));
        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Graphics, 0, 0, 1));

        backend.Begin(context);
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetShadingRateImage(context, invalidImage));
        backend.Discard(context);

        Assert.Contains(
            messages,
            static message => message.Area == "VariableRateShading" &&
                              message.Type == ValidationMessageType.Error);
    }

    [Fact]
    public void Parameter_binding_shape_usage_and_pipeline_compatibility_are_diagnosed_before_forwarding()
    {
        const string bindingSource = """
            Texture2D<float4> inputTextures[2];
            SamplerState inputSampler;
            RWStructuredBuffer<float4> outputValues;
            float multiplier;

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
                outputValues[id.x] = inputTextures[id.x & 1]
                    .SampleLevel(inputSampler, float2(0.5, 0.5), 0) * multiplier;
            }
            """;
        const string otherSource = """
            [shader("compute")]
            [numthreads(1, 1, 1)]
            void computeMain(uint3 id : SV_DispatchThreadID)
            {
            }
            """;
        D3D12TestShaderEntry[] entries = [new("computeMain", SlangStage.Compute)];
        using D3D12TestShaderProgram bindingShader = D3D12TestShaderProgram.Compile(
            "rhi_validation_binding_contract",
            bindingSource,
            entries);
        using D3D12TestShaderProgram otherShader = D3D12TestShaderProgram.Compile(
            "rhi_validation_other_contract",
            otherSource,
            entries);
        VariableLayoutReflection layout =
            bindingShader.Reflection.GetGlobalParamsVarLayout() ?? VariableLayoutReflection.Null;
        Assert.NotEqual(VariableLayoutReflection.Null, layout);
        ParameterBindingContract contract = ParameterBindingContract.Compile(layout);
        Assert.True(contract.BoundedBindingCount >= 4);
        Assert.True(contract.OrdinaryDataSize > 0);
        Assert.Contains(contract.Leaves, static leaf => leaf.DescriptorCount >= 2);

        ResourceBinding[] validResources = CreateNullBindings(contract);
        byte[] validOrdinaryData = new byte[checked((int)contract.OrdinaryDataSize)];
        var messages = new List<ValidationMessage>();
        using var backend = CreateFastLayer(messages);
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        using Pipeline bindingPipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(bindingShader.Program, bindingShader.GetEntryPoint(0)));
        using Pipeline otherPipeline = backend.CreateComputePipeline(
            device,
            new ComputePipelineDesc(otherShader.Program, otherShader.GetEntryPoint(0)));

        byte[] wrongOrdinaryData = new byte[checked(validOrdinaryData.Length + 1)];
        Assert.Throws<InvalidOperationException>(() => CreateAndDisposeBindings(
            backend,
            device,
            layout,
            validResources,
            wrongOrdinaryData));

        ResourceBinding[] missingResource = validResources[..^1];
        Assert.Throws<InvalidOperationException>(() => CreateAndDisposeBindings(
            backend,
            device,
            layout,
            missingResource,
            validOrdinaryData));

        ResourceBinding[] wrongType = validResources.ToArray();
        ResourceBinding first = wrongType[0];
        wrongType[0] = ResourceBinding.Null(
            first.Type == ResourceBindingType.Sampler
                ? ResourceBindingType.TextureSrv
                : ResourceBindingType.Sampler,
            first.ArrayElement);
        Assert.Throws<InvalidOperationException>(() => CreateAndDisposeBindings(
            backend,
            device,
            layout,
            wrongType,
            validOrdinaryData));

        ResourceBinding[] duplicateArrayElement = validResources.ToArray();
        int arrayOrdinal = FindSecondArrayElementOrdinal(contract);
        ResourceBinding arrayBinding = duplicateArrayElement[arrayOrdinal];
        duplicateArrayElement[arrayOrdinal] = ResourceBinding.Null(arrayBinding.Type, 0);
        Assert.Throws<InvalidOperationException>(() => CreateAndDisposeBindings(
            backend,
            device,
            layout,
            duplicateArrayElement,
            validOrdinaryData));

        using PersistentParameterBindings persistent = backend.CreatePersistentParameterBindings(
            device,
            new ParameterBlockBindings(layout, validResources, validOrdinaryData));
        backend.PublishDescriptors(device);
        Assert.Equal(PersistentParameterBindingsStatus.Published, persistent.Status);

        using CommandContext context = backend.CreateCommandContext(
            device,
            new CommandContextDesc(QueueType.Compute, 0, 0, 1));
        backend.Begin(context, new CommandRecordingDesc(8, 2, 8));
        backend.SetPipeline(context, otherPipeline);
        Assert.Throws<InvalidOperationException>(() => SetTransientBindings(
            backend,
            context,
            layout,
            validResources,
            validOrdinaryData));
        Assert.Throws<InvalidOperationException>(() =>
            backend.SetPersistentParameterBindings(context, persistent));
        backend.SetPipeline(context, bindingPipeline);
        SetTransientBindings(
            backend,
            context,
            layout,
            validResources,
            validOrdinaryData);
        backend.SetPersistentParameterBindings(context, persistent);
        backend.Discard(context);

        Assert.True(
            messages.Count(static message =>
                message.Area == "Bindings" &&
                message.Type == ValidationMessageType.Error) >= 6);
    }

    private static ValidationLayer<D3D12Backend> CreateFastLayer(
        List<ValidationMessage> messages)
    {
        D3D12ValidationOptions validation = new(
            DisableGpuBasedValidation: true,
            DisableSynchronizedQueueValidation: true);
        var backend = new D3D12Backend(new D3D12BackendOptions(validation));
        return new ValidationLayer<D3D12Backend>(
            backend,
            new ValidationOptions(new DelegateValidationMessageSink(messages.Add)));
    }

    private static ResourceBinding[] CreateNullBindings(ParameterBindingContract contract)
    {
        var result = new ResourceBinding[contract.BoundedBindingCount];
        int ordinal = 0;
        foreach (ParameterBindingLeaf leaf in contract.Leaves)
        {
            if (leaf.Unbounded)
                continue;
            for (uint element = 0; element < leaf.DescriptorCount; element++)
                result[ordinal++] = ResourceBinding.Null(leaf.Type, element);
        }
        Assert.Equal(result.Length, ordinal);
        return result;
    }

    private static int FindSecondArrayElementOrdinal(ParameterBindingContract contract)
    {
        int ordinal = 0;
        foreach (ParameterBindingLeaf leaf in contract.Leaves)
        {
            if (leaf.Unbounded)
                continue;
            if (leaf.DescriptorCount >= 2)
                return checked(ordinal + 1);
            ordinal = checked(ordinal + checked((int)leaf.DescriptorCount));
        }
        throw new InvalidOperationException("The test shader has no bounded resource array.");
    }

    private static void CreateAndDisposeBindings(
        ValidationLayer<D3D12Backend> backend,
        Device device,
        VariableLayoutReflection layout,
        ResourceBinding[] resources,
        byte[] ordinaryData)
    {
        using PersistentParameterBindings bindings = backend.CreatePersistentParameterBindings(
            device,
            new ParameterBlockBindings(layout, resources, ordinaryData));
    }

    private static void SetTransientBindings(
        ValidationLayer<D3D12Backend> backend,
        CommandContext context,
        VariableLayoutReflection layout,
        ResourceBinding[] resources,
        byte[] ordinaryData) =>
        backend.SetTransientParameterBindings(
            context,
            new ParameterBlockBindings(layout, resources, ordinaryData));

    private static AdapterInfo SelectWarp(Graphics<ValidationLayer<D3D12Backend>> graphics)
    {
        AdapterEnumerationOptions options = new(
            AdapterPreference.HighPerformance,
            IncludeSoftware: true);
        _ = graphics.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(graphics.TryEnumerateAdapters(options, adapters, out int confirmed));
        Assert.Equal(adapters.Length, confirmed);
        return adapters.First(static adapter => !adapter.HardwareAccelerated);
    }

    private static void SubmitAndWait(
        ValidationLayer<D3D12Backend> backend,
        Queue queue,
        in RecordedCommands commands)
    {
        RecordedCommands[] commandSpan = [commands];
        QueueCompletion completion = backend.Submit(
            queue,
            new QueueSubmitDesc([], [], commandSpan, [], []));
        Assert.Equal(WaitStatus.Completed, backend.WaitCpu(completion, TimeSpan.FromSeconds(10)));
    }

    private sealed class ThrowingValidationMessageSink : IValidationMessageSink
    {
        public void Report(in ValidationMessage message) =>
            throw new InvalidOperationException("The diagnostic consumer failed.");
    }
}
