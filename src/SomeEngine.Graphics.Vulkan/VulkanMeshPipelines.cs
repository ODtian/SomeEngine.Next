using SlangShaderSharp;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private Pipeline CreateMeshPipelineCore(
        RhiDevice device,
        in MeshPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        if (!nativeDevice.TryGetCapability(out MeshShaders? capability) || capability is null)
            throw new NotSupportedException("The Device was not created with MeshShaders support.");
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        ShaderReflection reflection = GetProgramReflection(desc.Program);
        var entries = new List<EntryPointReflection> { desc.Mesh };
        if (desc.Amplification != EntryPointReflection.Null) entries.Add(desc.Amplification);
        if (desc.Pixel != EntryPointReflection.Null) entries.Add(desc.Pixel);
        VulkanPipelineLayoutState layout = VulkanPipelineLayoutCompiler.Compile(
            nativeDevice,
            reflection,
            entries.ToArray(),
            desc.StaticSamplers);
        var shaders = new List<MeshShaderStage>
        {
            new(CompileSpirv(
                desc.Program,
                reflection,
                layout,
                desc.Mesh,
                SlangStage.Mesh,
                "mesh"), ShaderStageFlags.MeshBitExt, desc.Mesh),
        };
        if (desc.Amplification != EntryPointReflection.Null)
        {
            shaders.Insert(0, new MeshShaderStage(
                CompileSpirv(
                    desc.Program,
                    reflection,
                    layout,
                    desc.Amplification,
                    SlangStage.Amplification,
                    "task"),
                ShaderStageFlags.TaskBitExt,
                desc.Amplification));
        }
        if (desc.Pixel != EntryPointReflection.Null)
        {
            shaders.Add(new MeshShaderStage(
                CompileSpirv(
                    desc.Program,
                    reflection,
                    layout,
                    desc.Pixel,
                    SlangStage.Fragment,
                    "fragment"),
                ShaderStageFlags.FragmentBit,
                desc.Pixel));
        }
        layout.ActivateEntryBindings(
            shaders.Select(static value => new VulkanSpirvEntryBindings(
                value.Entry,
                value.Shader.ActiveBindings)).ToArray());
        VkPipeline native = default;
        VulkanPipeline? pipeline = null;
        try
        {
            if (nativeCache is null)
                native = CreateMeshPipelineNative(nativeDevice, layout, shaders, desc, default);
            else
                lock (nativeCache.Gate)
                    native = CreateMeshPipelineNative(nativeDevice, layout, shaders, desc, nativeCache.Native);
            pipeline = new VulkanPipeline(nativeDevice, native, layout, PipelineType.Mesh, desc.Label);
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

    private Task<Pipeline> CreateMeshPipelineAsyncCore(
        RhiDevice device,
        in MeshPipelineDesc desc,
        SomeEngine.Graphics.PipelineCache? cache)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanPipelineCache? nativeCache = ResolvePipelineCache(nativeDevice, cache);
        RetainedSlangProgram program = RetainedSlangProgram.Capture(desc.Program);
        try
        {
            var snapshot = new MeshPipelineSnapshot(desc, program.Program);
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

    private VkPipeline CreateMeshPipelineNative(
        VulkanDevice device,
        VulkanPipelineLayoutState layout,
        List<MeshShaderStage> shaders,
        in MeshPipelineDesc desc,
        VkPipelineCache cache)
    {
        VkShaderModule[] modules = new VkShaderModule[shaders.Count];
        nint[] names = new nint[shaders.Count];
        PipelineShaderStageCreateInfo[] stages = new PipelineShaderStageCreateInfo[shaders.Count];
        try
        {
            for (int index = 0; index < shaders.Count; index++)
            {
                modules[index] = CreateShaderModule(device, shaders[index].Shader.Code);
                names[index] = SilkMarshal.StringToPtr(shaders[index].Shader.Name);
                stages[index] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = shaders[index].Stage,
                    Module = modules[index],
                    PName = (byte*)names[index],
                };
            }
            fixed (PipelineShaderStageCreateInfo* stagePointer = stages)
                return CreateMeshPipelineState(device, layout, stagePointer, stages.Length, desc, cache);
        }
        finally
        {
            for (int index = 0; index < names.Length; index++)
            {
                if (names[index] != 0) SilkMarshal.Free(names[index]);
                if (modules[index].Handle != 0)
                    Api.DestroyShaderModule(device.Native, modules[index], null);
            }
        }
    }

    private VkPipeline CreateMeshPipelineState(
        VulkanDevice device,
        VulkanPipelineLayoutState layout,
        PipelineShaderStageCreateInfo* stages,
        int stageCount,
        in MeshPipelineDesc desc,
        VkPipelineCache cache)
    {
        PipelineColorBlendAttachmentState[] blendAttachments = CreateBlendAttachments(
            desc.Blend,
            desc.Attachments.ColorFormats.Length);
        VkFormat[] colorFormats = new VkFormat[desc.Attachments.ColorFormats.Length];
        for (int index = 0; index < colorFormats.Length; index++)
            colorFormats[index] = VulkanFormats.ToNative(desc.Attachments.ColorFormats[index]);
        DynamicState[] dynamicStates = CreateDynamicStates(desc.DynamicStates);
        uint sampleMask = desc.Multisample.SampleMask;
        fixed (PipelineColorBlendAttachmentState* blendPointer = blendAttachments)
        fixed (VkFormat* colorFormatPointer = colorFormats)
        fixed (DynamicState* dynamicPointer = dynamicStates)
        {
            PipelineViewportStateCreateInfo viewport = new()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            PipelineRasterizationStateCreateInfo rasterization = CreateRasterization(
                desc.Rasterizer,
                desc.DynamicStates);
            var conservative = CreateConservativeRasterizationState();
            if (desc.Rasterizer.ConservativeRasterization)
            {
                if (!device.ExtendedFeatures.ConservativeRasterization)
                    throw new NotSupportedException(
                        "Conservative rasterization requires VK_EXT_conservative_rasterization.");
                rasterization.PNext = &conservative;
            }
            PipelineMultisampleStateCreateInfo multisample = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = ToNativeSampleCount(desc.Multisample.SampleCount),
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
                    (VulkanFormats.Aspects(stencil) & ImageAspectFlags.StencilBit) != 0
                        ? VulkanFormats.ToNative(stencil)
                        : VkFormat.Undefined,
            };
            GraphicsPipelineCreateInfo createInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = checked((uint)stageCount),
                PStages = stages,
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
                "vkCreateGraphicsPipelines(mesh)");
            return native;
        }
    }

    private readonly record struct MeshShaderStage(
        CompiledSpirv Shader,
        ShaderStageFlags Stage,
        EntryPointReflection Entry);

    private sealed class MeshPipelineSnapshot
    {
        private readonly IComponentType _program;
        private readonly EntryPointReflection _mesh;
        private readonly EntryPointReflection _task;
        private readonly EntryPointReflection _pixel;
        private readonly RasterizerState _rasterizer;
        private readonly MultisampleState _multisample;
        private readonly DepthStencilState _depthStencil;
        private readonly BlendAttachmentState[] _blend;
        private readonly bool _independentBlend;
        private readonly LogicOperation? _logic;
        private readonly RhiFormat[] _colors;
        private readonly RhiFormat? _depth;
        private readonly DynamicStates _dynamic;
        private readonly string? _label;
        private readonly StaticSamplerBinding[] _staticSamplers;

        internal MeshPipelineSnapshot(
            in MeshPipelineDesc desc,
            IComponentType program)
        {
            _program = program;
            _mesh = desc.Mesh;
            _task = desc.Amplification;
            _pixel = desc.Pixel;
            _rasterizer = desc.Rasterizer;
            _multisample = desc.Multisample;
            _depthStencil = desc.DepthStencil;
            _blend = desc.Blend.Attachments.ToArray();
            _independentBlend = desc.Blend.IndependentBlend;
            _logic = desc.Blend.LogicOperation;
            _colors = desc.Attachments.ColorFormats.ToArray();
            _depth = desc.Attachments.DepthStencilFormat;
            _dynamic = desc.DynamicStates;
            _label = desc.Label;
            _staticSamplers = desc.StaticSamplers.ToArray();
        }

        internal Pipeline Create(
            VulkanBackend backend,
            VulkanDevice device,
            SomeEngine.Graphics.PipelineCache? cache)
        {
            BlendState blend = new(_blend, _independentBlend, _logic);
            AttachmentFormatSignature attachments = new(_colors, _depth, _multisample.SampleCount);
            MeshPipelineDesc desc = new(
                _program,
                _mesh,
                _task,
                _pixel,
                _rasterizer,
                _multisample,
                _depthStencil,
                blend,
                attachments,
                _dynamic,
                _label,
                _staticSamplers);
            return backend.CreateMeshPipelineCore(device, desc, cache);
        }
    }
}
