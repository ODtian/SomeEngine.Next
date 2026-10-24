using System.Buffers;
using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private void ValidateComputeSubgroupSize(VulkanDevice device, uint size)
    {
        PhysicalDeviceVulkan13Features features13 = new()
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
        };
        PhysicalDeviceFeatures2 features = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &features13,
        };
        Api.GetPhysicalDeviceFeatures2(device.PhysicalDevice, &features);
        PhysicalDeviceSubgroupSizeControlProperties subgroup = new()
        {
            SType = StructureType.PhysicalDeviceSubgroupSizeControlProperties,
        };
        PhysicalDeviceProperties2 properties = new()
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &subgroup,
        };
        Api.GetPhysicalDeviceProperties2(device.PhysicalDevice, &properties);
        if (!features13.SubgroupSizeControl ||
            (subgroup.RequiredSubgroupSizeStages & ShaderStageFlags.ComputeBit) == 0 ||
            size < subgroup.MinSubgroupSize ||
            size > subgroup.MaxSubgroupSize)
            throw new NotSupportedException($"The compute shader requires subgroup size {size}.");
    }

    internal Pipeline CreateGraphicsPipeline(
        RhiDevice device,
        in GraphicsPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache = null)
        => CreateGraphicsPipeline(device, desc, cache, null);

    private Pipeline CreateGraphicsPipeline(
        RhiDevice device,
        in GraphicsPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache,
        SlangShaderData? capturedSlangCode)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        ValidateGraphicsPipeline(nativeDevice, desc);
        ShaderReflection reflection = desc.Reflection;
        VulkanStreamOutputPlan? streamOutput = desc.HasStreamOutput
            ? VulkanStreamOutputPlan.Create(nativeDevice, desc.Vertex, desc.StreamOutput)
            : null;
        EntryPointReflection[] entries = [desc.Vertex, desc.Pixel];
        VulkanPipelineLayoutState layout = VulkanPipelineLayoutCompiler.Compile(
            nativeDevice,
            reflection,
            entries,
            desc.StaticSamplers);
        VkPipeline native = default;
        VulkanPipeline? pipeline = null;
        SlangShaderData? retainedCode = null;
        try
        {
            CompiledSpirv vertex = CompileSpirv(
                reflection,
                layout,
                desc.Vertex,
                desc.VertexCode,
                desc.VertexCodeBlob,
                SlangStage.Vertex,
                "vertex",
                streamOutput);
            CompiledSpirv pixel = CompileSpirv(
                reflection,
                layout,
                desc.Pixel,
                desc.PixelCode,
                desc.PixelCodeBlob,
                SlangStage.Fragment,
                "fragment");
            if (nativeCache is null)
                native = CreateGraphicsPipelineNative(nativeDevice, layout, vertex, pixel, desc, default);
            else
                lock (nativeCache.Gate)
                    native = CreateGraphicsPipelineNative(nativeDevice, layout, vertex, pixel, desc, nativeCache.Native);
            retainedCode = capturedSlangCode ?? desc.SlangCode;
            pipeline = new VulkanPipeline(
                nativeDevice,
                native,
                layout,
                retainedCode,
                PipelineType.Graphics,
                desc.Label);
            retainedCode = null;
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
        SlangShaderData slangCode = desc.SlangCode;
        GraphicsPipelineData data = new(desc);
        return EnqueuePipelineCreation(
            nativeDevice,
            nativeCache,
            slangCode,
            data,
            () => data.Create(this, nativeDevice, nativeCache, slangCode));
    }

    internal Pipeline CreateComputePipeline(
        RhiDevice device,
        in ComputePipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache = null)
        => CreateComputePipeline(device, desc, cache, null);

    private Pipeline CreateComputePipeline(
        RhiDevice device,
        in ComputePipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache,
        SlangShaderData? capturedSlangCode)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        ArgumentNullException.ThrowIfNull(desc.SlangCode);
        ShaderReflection reflection = desc.Reflection;
        VulkanPipelineLayoutState layout = VulkanPipelineLayoutCompiler.Compile(
            nativeDevice,
            reflection,
            [desc.Compute],
            desc.StaticSamplers.Span);
        VkPipeline native = default;
        VkShaderModule module = default;
        nint entryName = 0;
        VulkanPipeline? pipeline = null;
        SlangShaderData? retainedCode = null;
        try
        {
            CompiledSpirv compute = CompileSpirv(
                reflection,
                layout,
                desc.Compute,
                desc.Code,
                desc.CodeBlob,
                SlangStage.Compute,
                "compute");
            module = CreateShaderModule(nativeDevice, compute.Code);
            entryName = SilkMarshal.StringToPtr(compute.Name);
            PipelineShaderStageCreateInfo stage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)entryName,
            };
            PipelineShaderStageRequiredSubgroupSizeCreateInfo requiredSubgroup = new()
            {
                SType = StructureType.PipelineShaderStageRequiredSubgroupSizeCreateInfo,
                RequiredSubgroupSize = checked((uint)desc.Compute.ComputeWaveSize),
            };
            if (requiredSubgroup.RequiredSubgroupSize != 0)
            {
                ValidateComputeSubgroupSize(nativeDevice, requiredSubgroup.RequiredSubgroupSize);
                stage.PNext = &requiredSubgroup;
            }
            ComputePipelineCreateInfo createInfo = new()
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = layout.Native,
            };
            Result result;
            if (nativeCache is null)
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
            ThrowPipelineFailure(nativeDevice, result, "vkCreateComputePipelines");
            retainedCode = capturedSlangCode ?? desc.SlangCode;
            pipeline = new VulkanPipeline(
                nativeDevice,
                native,
                layout,
                retainedCode,
                PipelineType.Compute,
                desc.Label);
            retainedCode = null;
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
        SlangShaderData slangCode = desc.SlangCode;
        ComputePipelineData data = new(desc);
        return EnqueuePipelineCreation(
            nativeDevice,
            nativeCache,
            slangCode,
            data,
            () => data.Create(this, nativeDevice, nativeCache, slangCode));
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
        Result result = Api.CreateGraphicsPipelines(
            device.Native,
            cache,
            1,
            createInfo,
            null,
            native);
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
        ShaderReflection reflection,
        VulkanPipelineLayoutState layout,
        EntryPointReflection entry,
        ReadOnlySequence<byte> code,
        ISlangBlob? codeBlob,
        SlangStage expectedStage,
        string role,
        VulkanStreamOutputPlan? streamOutput = null)
    {
        if (entry == EntryPointReflection.Null || entry.Stage != expectedStage)
            throw new ArgumentException($"The {role} entry point has the wrong Slang stage.", nameof(entry));
        bool belongsToProgram = false;
        for (uint index = 0; index < reflection.EntryPointCount; index++)
        {
            if (reflection.GetEntryPointByIndex(index) == entry)
            {
                belongsToProgram = true;
                break;
            }
        }
        if (!belongsToProgram)
            throw new ArgumentException("The entry point does not belong to the supplied Slang program.", nameof(entry));
        if (code.IsEmpty && codeBlob is null)
            throw new GraphicsException(
                GraphicsError.ShaderCompilation,
                $"The cooked {role} entry point contains no SPIR-V.");
        byte[] source = CopyCode(code, codeBlob);
        byte[] bytes = NormalizeSpirvDescriptorBindings(
            source,
            layout.GetSpirvTargets(entry),
            out _);
        string reflectedName = string.IsNullOrWhiteSpace(entry.NameOverride)
            ? entry.Name
            : entry.NameOverride;
        string entryName = FindSpirvEntryPointName(bytes, reflectedName);
        if (string.IsNullOrWhiteSpace(entryName))
            throw new GraphicsException(
                GraphicsError.ShaderCompilation,
                $"The cooked {role} entry point has no target entry name.");
        if (streamOutput is not null)
            bytes = ApplySpirvStreamOutput(bytes, entryName, streamOutput);
        return new CompiledSpirv(bytes, entryName);
    }

    private static byte[] CopyCode(
        ReadOnlySequence<byte> code,
        ISlangBlob? codeBlob)
    {
        if (codeBlob is null)
            return code.ToArray();

        nuint length = codeBlob.GetBufferSize();
        void* pointer = codeBlob.GetBufferPointer();
        if (length > (nuint)Array.MaxLength || (length != 0 && pointer is null))
            throw new GraphicsException(
                GraphicsError.ShaderCompilation,
                "Slang returned an invalid code blob.");
        return new ReadOnlySpan<byte>(pointer, checked((int)length)).ToArray();
    }

    private static string FindSpirvEntryPointName(
        ReadOnlySpan<byte> code,
        string reflectedName)
    {
        if (code.Length < 20 || (code.Length & 3) != 0)
            return reflectedName;
        ReadOnlySpan<uint> words =
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(code);
        if (words[0] != 0x07230203)
            return reflectedName;

        string? soleName = null;
        int nameCount = 0;
        for (int index = 5; index < words.Length;)
        {
            uint instruction = words[index];
            int wordCount = checked((int)(instruction >> 16));
            if (wordCount <= 0 || index > words.Length - wordCount)
                break;
            if ((instruction & 0xFFFF) == 15 && wordCount >= 4)
            {
                ReadOnlySpan<byte> bytes =
                    System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                        words.Slice(index + 3, wordCount - 3));
                int terminator = bytes.IndexOf((byte)0);
                if (terminator >= 0)
                    bytes = bytes[..terminator];
                string name = System.Text.Encoding.UTF8.GetString(bytes);
                if (string.Equals(name, reflectedName, StringComparison.Ordinal))
                    return name;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    soleName = name;
                    nameCount++;
                }
            }
            index += wordCount;
        }
        return nameCount == 1 && soleName is not null ? soleName : reflectedName;
    }

    private static void ValidateGraphicsPipeline(
        VulkanDevice device,
        in GraphicsPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc.SlangCode);
        if (desc.Reflection == ShaderReflection.Null)
            throw new ArgumentException("A graphics pipeline requires Slang reflection.", nameof(desc));
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

    private static LogicOp ToNative(LogicOperation operation) => operation switch
    {
        LogicOperation.Clear => LogicOp.Clear,
        LogicOperation.Set => LogicOp.Set,
        LogicOperation.Copy => LogicOp.Copy,
        LogicOperation.CopyInverted => LogicOp.CopyInverted,
        LogicOperation.NoOperation => LogicOp.NoOp,
        LogicOperation.Invert => LogicOp.Invert,
        LogicOperation.And => LogicOp.And,
        LogicOperation.Nand => LogicOp.Nand,
        LogicOperation.Or => LogicOp.Or,
        LogicOperation.Nor => LogicOp.Nor,
        LogicOperation.Xor => LogicOp.Xor,
        LogicOperation.Equivalence => LogicOp.Equivalent,
        LogicOperation.AndReverse => LogicOp.AndReverse,
        LogicOperation.AndInverted => LogicOp.AndInverted,
        LogicOperation.OrReverse => LogicOp.OrReverse,
        LogicOperation.OrInverted => LogicOp.OrInverted,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

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
        string Name);

    private sealed class GraphicsPipelineData
    {
        private readonly SlangShaderData _slangCode;
        private readonly ShaderReflection _reflection;
        private readonly EntryPointReflection _vertex;
        private readonly ReadOnlySequence<byte>[] _code;
        private readonly ISlangBlob?[] _codeBlobs;
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

        internal GraphicsPipelineData(in GraphicsPipelineDesc desc)
        {
            _slangCode = desc.SlangCode;
            _reflection = desc.Reflection;
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
            _code = [desc.VertexCode, desc.PixelCode];
            _codeBlobs = [desc.VertexCodeBlob, desc.PixelCodeBlob];
        }

        internal Pipeline Create(
            VulkanBackend backend,
            VulkanDevice device,
            SomeEngine.Graphics.PipelineCache? cache,
            SlangShaderData capturedSlangCode)
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
                    _slangCode,
                    _reflection,
                    _vertex,
                    _code[0],
                    _pixel,
                    _code[1],
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
                     _staticSamplers,
                     _codeBlobs[0],
                     _codeBlobs[1]),
                    cache,
                    capturedSlangCode);
            }
            return backend.CreateGraphicsPipeline(
                device,
                new GraphicsPipelineDesc(
                    _slangCode,
                    _reflection,
                    _vertex,
                    _code[0],
                    _pixel,
                    _code[1],
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
                     _staticSamplers,
                     _codeBlobs[0],
                     _codeBlobs[1]),
                 cache,
                 capturedSlangCode);
        }
    }

    private sealed class ComputePipelineData
    {
        private readonly SlangShaderData _slangCode;
        private readonly ShaderReflection _reflection;
        private readonly EntryPointReflection _compute;
        private readonly ReadOnlySequence<byte>[] _code;
        private readonly ISlangBlob?[] _codeBlobs;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;

        internal ComputePipelineData(in ComputePipelineDesc desc)
        {
            _slangCode = desc.SlangCode;
            _reflection = desc.Reflection;
            _compute = desc.Compute;
            _label = desc.Label;
            _staticSamplers = desc.StaticSamplers.ToArray();
            _code = [desc.Code];
            _codeBlobs = [desc.CodeBlob];
        }

        internal Pipeline Create(
            VulkanBackend backend,
            VulkanDevice device,
            SomeEngine.Graphics.PipelineCache? cache,
            SlangShaderData capturedSlangCode) =>
            backend.CreateComputePipeline(
                device,
                new ComputePipelineDesc(
                    _slangCode,
                    _reflection,
                    _compute,
                     _code[0],
                     _label,
                     _staticSamplers,
                     _codeBlobs[0]),
                 cache,
                 capturedSlangCode);
    }

    private sealed class VulkanPipeline : Pipeline, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanLifetime _lifetime;
        private SlangShaderData? _slangCode;
        private VkPipeline _native;

        internal VulkanPipeline(
            VulkanDevice device,
            VkPipeline native,
            VulkanPipelineLayoutState layout,
            SlangShaderData slangCode,
            PipelineType type,
            string? label,
            VulkanRayTracingPipelineState? rayTracing = null)
            : base(device, type, label)
        {
            _device = device;
            _native = native;
            Layout = layout;
            _slangCode = slangCode;
            RayTracing = rayTracing;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VkPipeline Native => _native;
        internal VulkanPipelineLayoutState Layout { get; }
        internal VulkanRayTracingPipelineState? RayTracing { get; }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative()
        {
            if (_native.Handle != 0)
                _device.Backend.Api.DestroyPipeline(_device.Native, _native, null);
            _native = default;
            Layout.Release();
            _slangCode = null;
        }
    }
}
