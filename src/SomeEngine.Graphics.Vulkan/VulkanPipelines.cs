using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    internal Pipeline CreateGraphicsPipeline(
        RhiDevice device,
        in GraphicsPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache = null)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        ValidateGraphicsPipeline(nativeDevice, desc);
        ShaderReflection reflection = GetProgramReflection(desc.Program);
        VulkanStreamOutputPlan? streamOutput = desc.HasStreamOutput
            ? VulkanStreamOutputPlan.Create(nativeDevice, desc.Vertex, desc.StreamOutput)
            : null;
        EntryPointReflection[] entries = [desc.Vertex, desc.Pixel];
        VulkanPipelineLayoutState layout = VulkanPipelineLayoutCompiler.Compile(
            nativeDevice,
            reflection,
            entries,
            desc.StaticSamplers);
        CompiledSpirv vertex = CompileSpirv(
            desc.Program,
            reflection,
            layout,
            desc.Vertex,
            SlangStage.Vertex,
            "vertex",
            streamOutput);
        CompiledSpirv pixel = CompileSpirv(
            desc.Program, reflection, layout, desc.Pixel, SlangStage.Fragment, "fragment");
        layout.ActivateEntryBindings(
        [
            new VulkanSpirvEntryBindings(desc.Vertex, vertex.ActiveBindings),
            new VulkanSpirvEntryBindings(desc.Pixel, pixel.ActiveBindings),
        ]);
        VkPipeline native = default;
        VulkanPipeline? pipeline = null;
        try
        {
            if (nativeCache is null)
                native = CreateGraphicsPipelineNative(nativeDevice, layout, vertex, pixel, desc, default);
            else
                lock (nativeCache.Gate)
                    native = CreateGraphicsPipelineNative(nativeDevice, layout, vertex, pixel, desc, nativeCache.Native);
            pipeline = new VulkanPipeline(nativeDevice, native, layout, PipelineType.Graphics, desc.Label);
            return RegisterChildOrDispose(nativeDevice, pipeline);
        }
        catch
        {
            if (pipeline is null && native.Handle != 0)
                Api.DestroyPipeline(nativeDevice.Native, native, null);
            if (pipeline is null)
                layout.Release();
            throw;
        }
    }

    internal Task<Pipeline> CreateGraphicsPipelineAsync(
        RhiDevice device,
        in GraphicsPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache = null)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        RetainedSlangProgram program = RetainedSlangProgram.Capture(desc.Program);
        try
        {
            GraphicsPipelineSnapshot snapshot = new(desc, program.Program);
            return EnqueuePipelineCreation(
                nativeDevice,
                nativeCache,
                program,
                () => snapshot.Create(this, nativeDevice, nativeCache));
        }
        catch
        {
            program.Dispose();
            throw;
        }
    }

    internal Pipeline CreateComputePipeline(
        RhiDevice device,
        in ComputePipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache = null)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        ArgumentNullException.ThrowIfNull(desc.Program);
        ShaderReflection reflection = GetProgramReflection(desc.Program);
        VulkanPipelineLayoutState layout = VulkanPipelineLayoutCompiler.Compile(
            nativeDevice,
            reflection,
            [desc.Compute],
            desc.StaticSamplers.Span);
        CompiledSpirv compute = CompileSpirv(
            desc.Program,
            reflection,
            layout,
            desc.Compute,
            SlangStage.Compute,
            "compute");
        layout.ActivateEntryBindings(
            [new VulkanSpirvEntryBindings(desc.Compute, compute.ActiveBindings)]);
        VkPipeline native = default;
        VkShaderModule module = default;
        nint entryName = 0;
        VulkanPipeline? pipeline = null;
        try
        {
            module = CreateShaderModule(nativeDevice, compute.Code);
            entryName = SilkMarshal.StringToPtr(compute.Name);
            PipelineShaderStageCreateInfo stage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)entryName,
            };
            ComputePipelineCreateInfo createInfo = new()
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = layout.Native,
            };
            Result result;
#if SOMEENGINE_TESTING
            FaultHooks.Before(VulkanCallPoint.CreatePipeline);
            bool overridden = FaultHooks.TryOverride(
                VulkanCallPoint.CreatePipeline,
                out Result injectedResult);
#endif
#if SOMEENGINE_TESTING
            if (overridden)
            {
                result = injectedResult;
            }
            else if (nativeCache is null)
#else
            if (nativeCache is null)
#endif
            {
                result = Api.CreateComputePipelines(
                    nativeDevice.Native, default, 1, &createInfo, null, &native);
            }
            else
            {
                lock (nativeCache.Gate)
                    result = Api.CreateComputePipelines(
                        nativeDevice.Native, nativeCache.Native, 1, &createInfo, null, &native);
            }
#if SOMEENGINE_TESTING
            FaultHooks.After(VulkanCallPoint.CreatePipeline);
#endif
            ThrowPipelineFailure(nativeDevice, result, "vkCreateComputePipelines");
            pipeline = new VulkanPipeline(nativeDevice, native, layout, PipelineType.Compute, desc.Label);
            return RegisterChildOrDispose(nativeDevice, pipeline);
        }
        catch
        {
            if (pipeline is null && native.Handle != 0)
                Api.DestroyPipeline(nativeDevice.Native, native, null);
            if (pipeline is null)
                layout.Release();
            throw;
        }
        finally
        {
            if (entryName != 0)
                SilkMarshal.Free(entryName);
            if (module.Handle != 0)
                Api.DestroyShaderModule(nativeDevice.Native, module, null);
        }
    }

    internal Task<Pipeline> CreateComputePipelineAsync(
        RhiDevice device,
        in ComputePipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache = null)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        RetainedSlangProgram program = RetainedSlangProgram.Capture(desc.Program);
        try
        {
            ComputePipelineSnapshot snapshot = new(desc, program.Program);
            return EnqueuePipelineCreation(
                nativeDevice,
                nativeCache,
                program,
                () => snapshot.Create(this, nativeDevice, nativeCache));
        }
        catch
        {
            program.Dispose();
            throw;
        }
    }

    private VkPipeline CreateGraphicsPipelineNative(
        VulkanDevice device,
        VulkanPipelineLayoutState layout,
        in CompiledSpirv vertex,
        in CompiledSpirv pixel,
        in GraphicsPipelineDesc desc,
        VkPipelineCache cache)
    {
        VkShaderModule vertexModule = default;
        VkShaderModule pixelModule = default;
        nint vertexName = 0;
        nint pixelName = 0;
        try
        {
            vertexModule = CreateShaderModule(device, vertex.Code);
            pixelModule = CreateShaderModule(device, pixel.Code);
            vertexName = SilkMarshal.StringToPtr(vertex.Name);
            pixelName = SilkMarshal.StringToPtr(pixel.Name);
            PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertexModule,
                PName = (byte*)vertexName,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = pixelModule,
                PName = (byte*)pixelName,
            };
            return CreateGraphicsPipelineState(device, layout, stages, desc, cache);
        }
        finally
        {
            if (vertexName != 0) SilkMarshal.Free(vertexName);
            if (pixelName != 0) SilkMarshal.Free(pixelName);
            if (vertexModule.Handle != 0) Api.DestroyShaderModule(device.Native, vertexModule, null);
            if (pixelModule.Handle != 0) Api.DestroyShaderModule(device.Native, pixelModule, null);
        }
    }

    private VkPipeline CreateGraphicsPipelineState(
        VulkanDevice device,
        VulkanPipelineLayoutState layout,
        PipelineShaderStageCreateInfo* stages,
        in GraphicsPipelineDesc desc,
        VkPipelineCache cache)
    {
        VertexInputBindingDescription[] bindings = CreateVertexBindings(desc.VertexBuffers);
        VertexInputBindingDivisorDescription[] divisors = CreateVertexDivisors(
            device,
            desc.VertexBuffers);
        VertexInputAttributeDescription[] attributes = CreateVertexAttributes(desc.VertexAttributes);
        PipelineColorBlendAttachmentState[] blendAttachments = CreateBlendAttachments(
            desc.Blend,
            desc.Attachments.ColorFormats.Length);
        VkFormat[] colorFormats = new VkFormat[desc.Attachments.ColorFormats.Length];
        for (int index = 0; index < colorFormats.Length; index++)
            colorFormats[index] = VulkanFormats.ToNative(desc.Attachments.ColorFormats[index]);
        DynamicState[] dynamicStates = CreateDynamicStates(desc.DynamicStates);
        uint sampleMask = desc.Multisample.SampleMask;
        fixed (VertexInputBindingDescription* bindingPointer = bindings)
        fixed (VertexInputBindingDivisorDescription* divisorPointer = divisors)
        fixed (VertexInputAttributeDescription* attributePointer = attributes)
        fixed (PipelineColorBlendAttachmentState* blendPointer = blendAttachments)
        fixed (VkFormat* colorFormatPointer = colorFormats)
        fixed (DynamicState* dynamicPointer = dynamicStates)
        {
            VulkanVertexInputState vertexInput = default;
            vertexInput.Initialize(
                bindingPointer,
                bindings.Length,
                attributePointer,
                attributes.Length,
                divisorPointer,
                divisors.Length);
            PipelineInputAssemblyStateCreateInfo assembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = ToNative(desc.Topology),
                PrimitiveRestartEnable = desc.StripCut != StripCut.Disabled,
            };
            PipelineViewportStateCreateInfo viewport = new()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            PipelineRasterizationStateCreateInfo rasterization = CreateRasterization(desc.Rasterizer, desc.DynamicStates);
            if (desc.HasStreamOutput && !desc.StreamOutput.RasterizedStreamIndex.HasValue)
                rasterization.RasterizerDiscardEnable = true;
            var conservative = CreateConservativeRasterizationState();
            if (desc.Rasterizer.ConservativeRasterization)
                rasterization.PNext = &conservative;
            PipelineMultisampleStateCreateInfo multisample = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = ToNativeSampleCount(desc.Multisample.SampleCount),
                SampleShadingEnable = false,
                PSampleMask = &sampleMask,
                AlphaToCoverageEnable = desc.Multisample.AlphaToCoverage,
            };
            PipelineDepthStencilStateCreateInfo depthStencil = CreateDepthStencil(desc.DepthStencil);
            PipelineColorBlendStateCreateInfo blend = new()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = desc.Blend.LogicOperation.HasValue,
                LogicOp = ToNative(desc.Blend.LogicOperation.GetValueOrDefault()),
                AttachmentCount = checked((uint)blendAttachments.Length),
                PAttachments = blendPointer,
            };
            PipelineDynamicStateCreateInfo dynamic = new()
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = checked((uint)dynamicStates.Length),
                PDynamicStates = dynamicPointer,
            };
            PipelineRenderingCreateInfo rendering = new()
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = checked((uint)colorFormats.Length),
                PColorAttachmentFormats = colorFormatPointer,
                DepthAttachmentFormat = desc.Attachments.DepthStencilFormat is RhiFormat depth
                    ? VulkanFormats.ToNative(depth)
                    : VkFormat.Undefined,
                StencilAttachmentFormat = desc.Attachments.DepthStencilFormat is RhiFormat stencil &&
                    VulkanFormats.Aspects(stencil).HasFlag(ImageAspectFlags.StencilBit)
                        ? VulkanFormats.ToNative(stencil)
                        : VkFormat.Undefined,
            };
            GraphicsPipelineCreateInfo createInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput.Native,
                PInputAssemblyState = &assembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &blend,
                PDynamicState = &dynamic,
                Layout = layout.Native,
            };
            VkPipeline native = default;
            ThrowPipelineFailure(
                device,
                CreateGraphicsPipelineNative(device, cache, &createInfo, &native),
                "vkCreateGraphicsPipelines");
            return native;
        }
    }

    private Result CreateGraphicsPipelineNative(
        VulkanDevice device,
        VkPipelineCache cache,
        GraphicsPipelineCreateInfo* createInfo,
        VkPipeline* native)
    {
#if SOMEENGINE_TESTING
        FaultHooks.Before(VulkanCallPoint.CreatePipeline);
        if (FaultHooks.TryOverride(VulkanCallPoint.CreatePipeline, out Result injectedResult))
        {
            FaultHooks.After(VulkanCallPoint.CreatePipeline);
            return injectedResult;
        }
#endif
        Result result = Api.CreateGraphicsPipelines(
            device.Native,
            cache,
            1,
            createInfo,
            null,
            native);
#if SOMEENGINE_TESTING
        FaultHooks.After(VulkanCallPoint.CreatePipeline);
#endif
        return result;
    }

    private static VertexInputBindingDescription[] CreateVertexBindings(
        ReadOnlySpan<VertexBufferLayout> source)
    {
        var result = new VertexInputBindingDescription[source.Length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new VertexInputBindingDescription(
                source[index].BufferIndex,
                source[index].Stride,
                source[index].PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex);
        }
        return result;
    }

    private struct VulkanVertexInputState
    {
        internal PipelineVertexInputStateCreateInfo Native;
        private PipelineVertexInputDivisorStateCreateInfoEXT _divisor;

        internal unsafe void Initialize(
            VertexInputBindingDescription* bindings,
            int bindingCount,
            VertexInputAttributeDescription* attributes,
            int attributeCount,
            VertexInputBindingDivisorDescription* divisors,
            int divisorCount)
        {
            _divisor = new PipelineVertexInputDivisorStateCreateInfoEXT
            {
                SType = StructureType.PipelineVertexInputDivisorStateCreateInfoExt,
                VertexBindingDivisorCount = checked((uint)divisorCount),
                PVertexBindingDivisors = divisors,
            };
            Native = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                PNext = divisorCount == 0 ? null : Unsafe.AsPointer(ref _divisor),
                VertexBindingDescriptionCount = checked((uint)bindingCount),
                PVertexBindingDescriptions = bindings,
                VertexAttributeDescriptionCount = checked((uint)attributeCount),
                PVertexAttributeDescriptions = attributes,
            };
        }
    }

    private static VertexInputBindingDivisorDescription[] CreateVertexDivisors(
        VulkanDevice device,
        ReadOnlySpan<VertexBufferLayout> source)
    {
        var result = new List<VertexInputBindingDivisorDescription>();
        foreach (ref readonly VertexBufferLayout layout in source)
        {
            if (!layout.PerInstance || layout.InstanceStepRate == 1)
                continue;
            bool supported = layout.InstanceStepRate == 0
                ? device.ExtendedFeatures.VertexAttributeInstanceRateZeroDivisor
                : device.ExtendedFeatures.VertexAttributeInstanceRateDivisor &&
                    layout.InstanceStepRate <=
                        device.ExtendedFeatures.MaximumVertexAttributeDivisor;
            if (!supported)
            {
                throw new NotSupportedException(
                    "The requested vertex instance step rate requires " +
                    "VK_EXT_vertex_attribute_divisor support.");
            }
            result.Add(new VertexInputBindingDivisorDescription(
                layout.BufferIndex,
                layout.InstanceStepRate));
        }
        return result.ToArray();
    }

    private static VertexInputAttributeDescription[] CreateVertexAttributes(
        ReadOnlySpan<VertexAttribute> source)
    {
        var result = new VertexInputAttributeDescription[source.Length];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = new VertexInputAttributeDescription(
                source[index].Location,
                source[index].BufferIndex,
                VulkanFormats.ToNative(source[index].Format),
                source[index].Offset);
        }
        return result;
    }

    private VkShaderModule CreateShaderModule(VulkanDevice device, byte[] code)
    {
        if (code.Length < sizeof(uint) || (code.Length & 3) != 0 ||
            BitConverter.ToUInt32(code) != 0x07230203)
            throw new GraphicsException(GraphicsError.ShaderCompilation, "Slang target zero did not produce SPIR-V.");
        fixed (byte* codePointer = code)
        {
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = checked((nuint)code.Length),
                PCode = (uint*)codePointer,
            };
            VkShaderModule module = default;
            device.ThrowIfDeviceCallFailed(
                Api.CreateShaderModule(device.Native, &createInfo, null, &module),
                "vkCreateShaderModule");
            return module;
        }
    }

    private static CompiledSpirv CompileSpirv(
        IComponentType program,
        ShaderReflection reflection,
        VulkanPipelineLayoutState layout,
        EntryPointReflection entry,
        SlangStage expectedStage,
        string role,
        VulkanStreamOutputPlan? streamOutput = null)
    {
        if (entry == EntryPointReflection.Null || entry.Stage != expectedStage)
            throw new ArgumentException($"The {role} entry point has the wrong Slang stage.", nameof(entry));
        int selected = -1;
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            if (reflection.GetEntryPointByIndex(index) == entry)
            {
                selected = checked((int)index);
                break;
            }
        }
        if (selected < 0)
            throw new ArgumentException("The entry point does not belong to the supplied Slang program.", nameof(entry));
        ISlangBlob? code = null;
        ISlangBlob? diagnostics = null;
        try
        {
            SlangResult result = program.GetEntryPointCode(selected, 0, out code!, out diagnostics);
            if (!result.Succeeded || code is null || code.GetBufferPointer() is null || code.GetBufferSize() == 0)
                throw new GraphicsException(GraphicsError.ShaderCompilation, FormatSlangFailure($"Slang {role} SPIR-V generation failed", diagnostics));
            byte[] bytes = NormalizeSpirvDescriptorBindings(
                new ReadOnlySpan<byte>(
                    (void*)code.GetBufferPointer(),
                    checked((int)code.GetBufferSize())),
                layout.GetSpirvTargets(entry),
                out VulkanSpirvBindingTarget[] activeTargets);
            string entryName = FindSpirvEntryPointName(bytes, entry.Name);
            if (streamOutput is not null)
                bytes = ApplySpirvStreamOutput(bytes, entryName, streamOutput);
            return new CompiledSpirv(bytes, entryName, activeTargets);
        }
        finally
        {
            ReleaseSlang(code);
            ReleaseSlang(diagnostics);
        }
    }

    private static string FindSpirvEntryPointName(
        ReadOnlySpan<byte> code,
        string expectedName)
    {
        ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(code);
        string? onlyName = null;
        int nameCount = 0;
        for (int index = 5; index < words.Length;)
        {
            uint instruction = words[index];
            int wordCount = checked((int)(instruction >> 16));
            uint opcode = instruction & 0xFFFF;
            if (wordCount <= 0 || index > words.Length - wordCount)
                break;
            if (opcode == 15 && wordCount >= 4)
            {
                ReadOnlySpan<byte> nameBytes = MemoryMarshal.AsBytes(words.Slice(index + 3, wordCount - 3));
                int terminator = nameBytes.IndexOf((byte)0);
                if (terminator >= 0)
                    nameBytes = nameBytes[..terminator];
                string name = System.Text.Encoding.UTF8.GetString(nameBytes);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    if (string.Equals(name, expectedName, StringComparison.Ordinal))
                        return name;
                    onlyName = name;
                    nameCount++;
                }
            }
            index += wordCount;
        }
        if (nameCount == 1 && onlyName is not null)
            return onlyName;
        throw new GraphicsException(
            GraphicsError.ShaderCompilation,
            $"SPIR-V does not contain the requested entry point '{expectedName}'.");
    }

    private static ShaderReflection GetProgramReflection(IComponentType program)
    {
        ArgumentNullException.ThrowIfNull(program);
        ISlangBlob? diagnostics = null;
        try
        {
            ShaderReflection reflection = program.GetLayout(0, out diagnostics);
            if (reflection == ShaderReflection.Null)
                throw new GraphicsException(GraphicsError.ShaderCompilation, FormatSlangFailure("Slang program reflection failed", diagnostics));
            if (program.GetSpecializationParamCount() != 0)
                throw new GraphicsException(GraphicsError.PipelineCreation, "Pipeline creation requires a fully specialized Slang program.");
            return reflection;
        }
        finally
        {
            ReleaseSlang(diagnostics);
        }
    }

    private static string FormatSlangFailure(string prefix, ISlangBlob? diagnostics) =>
        string.IsNullOrWhiteSpace(diagnostics?.AsString)
            ? prefix
            : $"{prefix}: {diagnostics.AsString.Trim()}";

    private static void ReleaseSlang(object? value)
    {
        if (value is System.Runtime.InteropServices.Marshalling.ComObject wrapper)
            wrapper.FinalRelease();
    }

    private static void ValidateGraphicsPipeline(
        VulkanDevice device,
        in GraphicsPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc.Program);
        if (!desc.Blend.Attachments.IsEmpty &&
            desc.Attachments.ColorFormats.Length != desc.Blend.Attachments.Length)
            throw new ArgumentException("Blend attachment count must match color attachment count.", nameof(desc));
        if (desc.Rasterizer.ConservativeRasterization)
        {
            if (!device.ExtendedFeatures.ConservativeRasterization)
                throw new NotSupportedException(
                    "Conservative rasterization requires VK_EXT_conservative_rasterization.");
        }
        if ((desc.DynamicStates & ~device.Capabilities.SupportedDynamicStates) != 0)
            throw new NotSupportedException("The Vulkan Device does not support every requested dynamic state.");
    }

    private static PipelineRasterizationStateCreateInfo CreateRasterization(
        in RasterizerState state,
        DynamicStates dynamicStates) => new()
    {
        SType = StructureType.PipelineRasterizationStateCreateInfo,
        DepthClampEnable = !state.DepthClip,
        RasterizerDiscardEnable = false,
        PolygonMode = state.Fill == FillType.Wireframe ? PolygonMode.Line : PolygonMode.Fill,
        CullMode = state.Cull switch
        {
            CullType.None => CullModeFlags.None,
            CullType.Front => CullModeFlags.FrontBit,
            CullType.Back => CullModeFlags.BackBit,
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        },
        FrontFace = state.FrontFace == SomeEngine.Graphics.FrontFace.Clockwise
            ? Silk.NET.Vulkan.FrontFace.Clockwise
            : Silk.NET.Vulkan.FrontFace.CounterClockwise,
        DepthBiasEnable = state.DepthBias != 0 || state.DepthBiasClamp != 0 ||
            state.SlopeScaledDepthBias != 0 || (dynamicStates & DynamicStates.DepthBias) != 0,
        DepthBiasConstantFactor = state.DepthBias,
        DepthBiasClamp = state.DepthBiasClamp,
        DepthBiasSlopeFactor = state.SlopeScaledDepthBias,
        LineWidth = 1,
    };

    private static PipelineRasterizationConservativeStateCreateInfoEXT
        CreateConservativeRasterizationState() => new()
        {
            SType = StructureType.PipelineRasterizationConservativeStateCreateInfoExt,
            ConservativeRasterizationMode =
                ConservativeRasterizationModeEXT.OverestimateExt,
        };

    private static PipelineDepthStencilStateCreateInfo CreateDepthStencil(in DepthStencilState state) => new()
    {
        SType = StructureType.PipelineDepthStencilStateCreateInfo,
        DepthTestEnable = state.DepthTest,
        DepthWriteEnable = state.DepthWrite,
        DepthCompareOp = ToNative(state.DepthComparison),
        DepthBoundsTestEnable = state.DepthBoundsTest,
        StencilTestEnable = state.StencilTest,
        Front = ToNative(state.Front, state.StencilReadMask, state.StencilWriteMask),
        Back = ToNative(state.Back, state.StencilReadMask, state.StencilWriteMask),
        MinDepthBounds = 0,
        MaxDepthBounds = 1,
    };

    private static StencilOpState ToNative(
        in StencilFaceState state,
        byte readMask,
        byte writeMask) => new(
        ToNative(state.Fail),
        ToNative(state.Pass),
        ToNative(state.DepthFail),
        ToNative(state.Comparison),
        readMask,
        writeMask,
        0);

    private static PipelineColorBlendAttachmentState[] CreateBlendAttachments(
        in BlendState blend,
        int colorAttachmentCount)
    {
        var result = new PipelineColorBlendAttachmentState[colorAttachmentCount];
        BlendAttachmentState defaultState = new();
        for (int index = 0; index < result.Length; index++)
        {
            BlendAttachmentState state = blend.Attachments.IsEmpty
                ? defaultState
                : blend.Attachments[blend.IndependentBlend ? index : 0];
            result[index] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = state.Enabled,
                SrcColorBlendFactor = ToNative(state.SourceColor),
                DstColorBlendFactor = ToNative(state.DestinationColor),
                ColorBlendOp = ToNative(state.ColorOperation),
                SrcAlphaBlendFactor = ToNative(state.SourceAlpha),
                DstAlphaBlendFactor = ToNative(state.DestinationAlpha),
                AlphaBlendOp = ToNative(state.AlphaOperation),
                ColorWriteMask = (ColorComponentFlags)state.WriteMask,
            };
        }
        return result;
    }

    private static DynamicState[] CreateDynamicStates(DynamicStates states)
    {
        var result = new List<DynamicState> { DynamicState.Viewport, DynamicState.Scissor };
        if ((states & DynamicStates.BlendConstants) != 0) result.Add(DynamicState.BlendConstants);
        if ((states & DynamicStates.StencilReference) != 0) result.Add(DynamicState.StencilReference);
        if ((states & DynamicStates.DepthBounds) != 0) result.Add(DynamicState.DepthBounds);
        if ((states & DynamicStates.DepthBias) != 0) result.Add(DynamicState.DepthBias);
        if ((states & DynamicStates.PrimitiveTopology) != 0) result.Add(DynamicState.PrimitiveTopologyExt);
        if ((states & DynamicStates.StripCut) != 0) result.Add(DynamicState.PrimitiveRestartEnableExt);
        return result.Distinct().ToArray();
    }

    private static Silk.NET.Vulkan.PrimitiveTopology ToNative(SomeEngine.Graphics.PrimitiveTopology topology) => topology switch
    {
        SomeEngine.Graphics.PrimitiveTopology.PointList => Silk.NET.Vulkan.PrimitiveTopology.PointList,
        SomeEngine.Graphics.PrimitiveTopology.LineList => Silk.NET.Vulkan.PrimitiveTopology.LineList,
        SomeEngine.Graphics.PrimitiveTopology.LineStrip => Silk.NET.Vulkan.PrimitiveTopology.LineStrip,
        SomeEngine.Graphics.PrimitiveTopology.TriangleList => Silk.NET.Vulkan.PrimitiveTopology.TriangleList,
        SomeEngine.Graphics.PrimitiveTopology.TriangleStrip => Silk.NET.Vulkan.PrimitiveTopology.TriangleStrip,
        _ => throw new ArgumentOutOfRangeException(nameof(topology)),
    };

    private static StencilOp ToNative(StencilOperation operation) => operation switch
    {
        StencilOperation.Keep => StencilOp.Keep,
        StencilOperation.Zero => StencilOp.Zero,
        StencilOperation.Replace => StencilOp.Replace,
        StencilOperation.IncrementClamp => StencilOp.IncrementAndClamp,
        StencilOperation.DecrementClamp => StencilOp.DecrementAndClamp,
        StencilOperation.Invert => StencilOp.Invert,
        StencilOperation.IncrementWrap => StencilOp.IncrementAndWrap,
        StencilOperation.DecrementWrap => StencilOp.DecrementAndWrap,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static Silk.NET.Vulkan.BlendFactor ToNative(SomeEngine.Graphics.BlendFactor factor) => factor switch
    {
        SomeEngine.Graphics.BlendFactor.Zero => Silk.NET.Vulkan.BlendFactor.Zero,
        SomeEngine.Graphics.BlendFactor.One => Silk.NET.Vulkan.BlendFactor.One,
        SomeEngine.Graphics.BlendFactor.SourceColor => Silk.NET.Vulkan.BlendFactor.SrcColor,
        SomeEngine.Graphics.BlendFactor.OneMinusSourceColor => Silk.NET.Vulkan.BlendFactor.OneMinusSrcColor,
        SomeEngine.Graphics.BlendFactor.SourceAlpha => Silk.NET.Vulkan.BlendFactor.SrcAlpha,
        SomeEngine.Graphics.BlendFactor.OneMinusSourceAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha,
        SomeEngine.Graphics.BlendFactor.DestinationAlpha => Silk.NET.Vulkan.BlendFactor.DstAlpha,
        SomeEngine.Graphics.BlendFactor.OneMinusDestinationAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusDstAlpha,
        SomeEngine.Graphics.BlendFactor.DestinationColor => Silk.NET.Vulkan.BlendFactor.DstColor,
        SomeEngine.Graphics.BlendFactor.OneMinusDestinationColor => Silk.NET.Vulkan.BlendFactor.OneMinusDstColor,
        SomeEngine.Graphics.BlendFactor.SourceAlphaSaturate => Silk.NET.Vulkan.BlendFactor.SrcAlphaSaturate,
        SomeEngine.Graphics.BlendFactor.BlendConstant => Silk.NET.Vulkan.BlendFactor.ConstantColor,
        SomeEngine.Graphics.BlendFactor.OneMinusBlendConstant => Silk.NET.Vulkan.BlendFactor.OneMinusConstantColor,
        SomeEngine.Graphics.BlendFactor.Source1Color => Silk.NET.Vulkan.BlendFactor.Src1Color,
        SomeEngine.Graphics.BlendFactor.OneMinusSource1Color => Silk.NET.Vulkan.BlendFactor.OneMinusSrc1Color,
        SomeEngine.Graphics.BlendFactor.Source1Alpha => Silk.NET.Vulkan.BlendFactor.Src1Alpha,
        SomeEngine.Graphics.BlendFactor.OneMinusSource1Alpha => Silk.NET.Vulkan.BlendFactor.OneMinusSrc1Alpha,
        _ => throw new ArgumentOutOfRangeException(nameof(factor)),
    };

    private static BlendOp ToNative(BlendOperation operation) => operation switch
    {
        BlendOperation.Add => BlendOp.Add,
        BlendOperation.Subtract => BlendOp.Subtract,
        BlendOperation.ReverseSubtract => BlendOp.ReverseSubtract,
        BlendOperation.Minimum => BlendOp.Min,
        BlendOperation.Maximum => BlendOp.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private static LogicOp ToNative(LogicOperation operation) => (LogicOp)operation;

    private static void ThrowPipelineFailure(
        VulkanDevice device,
        Result result,
        string operation)
    {
        if (result == Result.Success)
            return;
        if (result == Result.ErrorDeviceLost)
            throw device.PublishDeviceLoss(result, operation);
        throw new GraphicsException(
            result is Result.ErrorOutOfHostMemory or Result.ErrorOutOfDeviceMemory
                ? GraphicsError.OutOfMemory
                : GraphicsError.PipelineCreation,
            $"{operation} failed with Vulkan result {result}.",
            (long)result);
    }

    private readonly record struct CompiledSpirv(
        byte[] Code,
        string Name,
        VulkanSpirvBindingTarget[] ActiveBindings);

    private sealed class GraphicsPipelineSnapshot
    {
        private readonly IComponentType _program;
        private readonly EntryPointReflection _vertex;
        private readonly EntryPointReflection _pixel;
        private readonly VertexBufferLayout[] _vertexBuffers;
        private readonly VertexAttribute[] _vertexAttributes;
        private readonly SomeEngine.Graphics.PrimitiveTopology _topology;
        private readonly StripCut _stripCut;
        private readonly RasterizerState _rasterizer;
        private readonly MultisampleState _multisample;
        private readonly DepthStencilState _depthStencil;
        private readonly BlendAttachmentState[] _blendAttachments;
        private readonly bool _independentBlend;
        private readonly LogicOperation? _logicOperation;
        private readonly RhiFormat[] _colorFormats;
        private readonly RhiFormat? _depthFormat;
        private readonly DynamicStates _dynamicStates;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;
        private readonly StreamOutputElement[] _streamOutputElements;
        private readonly uint[] _streamOutputStrides;
        private readonly uint? _rasterizedStreamIndex;
        private readonly bool _hasStreamOutput;

        internal GraphicsPipelineSnapshot(
            in GraphicsPipelineDesc desc,
            IComponentType program)
        {
            _program = program;
            _vertex = desc.Vertex;
            _pixel = desc.Pixel;
            _vertexBuffers = desc.VertexBuffers.ToArray();
            _vertexAttributes = desc.VertexAttributes.ToArray();
            _topology = desc.Topology;
            _stripCut = desc.StripCut;
            _rasterizer = desc.Rasterizer;
            _multisample = desc.Multisample;
            _depthStencil = desc.DepthStencil;
            _blendAttachments = desc.Blend.Attachments.ToArray();
            _independentBlend = desc.Blend.IndependentBlend;
            _logicOperation = desc.Blend.LogicOperation;
            _colorFormats = desc.Attachments.ColorFormats.ToArray();
            _depthFormat = desc.Attachments.DepthStencilFormat;
            _dynamicStates = desc.DynamicStates;
            _label = desc.Label;
            _staticSamplers = desc.StaticSamplers.ToArray();
            _hasStreamOutput = desc.HasStreamOutput;
            _streamOutputElements = desc.HasStreamOutput
                ? desc.StreamOutput.Elements.ToArray()
                : [];
            _streamOutputStrides = desc.HasStreamOutput
                ? desc.StreamOutput.BufferStrides.ToArray()
                : [];
            _rasterizedStreamIndex = desc.HasStreamOutput
                ? desc.StreamOutput.RasterizedStreamIndex
                : null;
        }

        internal Pipeline Create(
            VulkanBackend backend,
            VulkanDevice device,
            SomeEngine.Graphics.PipelineCache? cache)
        {
            BlendState blend = new(_blendAttachments, _independentBlend, _logicOperation);
            AttachmentFormatSignature attachments = new(_colorFormats, _depthFormat, _multisample.SampleCount);
            if (_hasStreamOutput)
            {
                StreamOutputState streamOutput = new(
                    _streamOutputElements,
                    _streamOutputStrides,
                    _rasterizedStreamIndex);
                return backend.CreateGraphicsPipeline(
                    device,
                    new GraphicsPipelineDesc(
                    _program,
                    _vertex,
                    _pixel,
                    _vertexBuffers,
                    _vertexAttributes,
                    _topology,
                    _stripCut,
                    _rasterizer,
                    _multisample,
                    _depthStencil,
                    blend,
                    attachments,
                    streamOutput,
                    _dynamicStates,
                    _label,
                    _staticSamplers),
                    cache);
            }
            return backend.CreateGraphicsPipeline(
                device,
                new GraphicsPipelineDesc(
                    _program,
                    _vertex,
                    _pixel,
                    _vertexBuffers,
                    _vertexAttributes,
                    _topology,
                    _stripCut,
                    _rasterizer,
                    _multisample,
                    _depthStencil,
                    blend,
                    attachments,
                    _dynamicStates,
                    _label,
                    _staticSamplers),
                cache);
        }
    }

    private sealed class ComputePipelineSnapshot
    {
        private readonly IComponentType _program;
        private readonly EntryPointReflection _compute;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;

        internal ComputePipelineSnapshot(
            in ComputePipelineDesc desc,
            IComponentType program)
        {
            _program = program;
            _compute = desc.Compute;
            _label = desc.Label;
            _staticSamplers = desc.StaticSamplers.ToArray();
        }

        internal Pipeline Create(
            VulkanBackend backend,
            VulkanDevice device,
            SomeEngine.Graphics.PipelineCache? cache) =>
            backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    _program,
                    _compute,
                    _label,
                    _staticSamplers),
                cache);
    }

    private sealed class VulkanPipeline : Pipeline, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanLifetime _lifetime;
        private VkPipeline _native;

        internal VulkanPipeline(
            VulkanDevice device,
            VkPipeline native,
            VulkanPipelineLayoutState layout,
            PipelineType type,
            string? label,
            VulkanRayTracingPipelineState? rayTracing = null)
            : base(device, type, label)
        {
            _device = device;
            _native = native;
            Layout = layout;
            RayTracing = rayTracing;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VkPipeline Native => _native;
        internal VulkanPipelineLayoutState Layout { get; }
        internal VulkanRayTracingPipelineState? RayTracing { get; }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroyPipeline(_device.Native, _native, null); _native = default; Layout.Release(); }
    }
}
