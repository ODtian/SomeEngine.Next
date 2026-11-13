using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;
public sealed partial class ValidationLayer
{
    private static void ValidateGraphicsDescription(Device device, in GraphicsPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc.SlangCode);
        if (desc.Reflection == ShaderReflection.Null)
            throw new ArgumentException("A graphics pipeline requires Slang reflection.", nameof(desc));
        ValidatePipelineEntry(desc.Reflection, desc.Vertex, SlangStage.Vertex);
        ValidatePipelineEntry(desc.Reflection, desc.Pixel, SlangStage.Fragment);
        ValidateOutputState(device, desc.Attachments, desc.Multisample, desc.DepthStencil, desc.Blend);
        ValidateGraphicsFeatures(device, desc);
        const DynamicStates knownDynamicStates = DynamicStates.Viewport | DynamicStates.Scissor | DynamicStates.BlendConstants | DynamicStates.StencilReference | DynamicStates.DepthBounds | DynamicStates.DepthBias | DynamicStates.PrimitiveTopology | DynamicStates.StripCut;
        if (!Enum.IsDefined(desc.Topology) || !Enum.IsDefined(desc.StripCut) || (desc.DynamicStates & ~knownDynamicStates) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (device.Capabilities.CompileTarget != SlangCompileTarget.Dxil)
            return;
        HashSet<uint> buffers = [];
        foreach (ref readonly VertexBufferLayout buffer in desc.VertexBuffers)
        {
            if (buffer.Stride == 0 || !buffers.Add(buffer.BufferIndex) || (buffer.PerInstance && buffer.InstanceStepRate == 0))
                throw new ArgumentException("The vertex-buffer layout is invalid.", nameof(desc));
        }

        HashSet<uint> locations = [];
        foreach (ref readonly VertexAttribute attribute in desc.VertexAttributes)
        {
            if (!locations.Add(attribute.Location) || !buffers.Contains(attribute.BufferIndex))
                throw new ArgumentException("The vertex-attribute layout is invalid.", nameof(desc));
            VertexBufferLayout buffer = FindVertexBuffer(desc.VertexBuffers, attribute.BufferIndex);
            uint size = FormatElementSize(attribute.Format);
            if (attribute.Offset > buffer.Stride || size > buffer.Stride - attribute.Offset)
                throw new ArgumentException("A vertex attribute escapes its Buffer stride.", nameof(desc));
            if (!ContainsVertexSemantic(desc.Vertex, attribute.Location))
                throw new GraphicsException(GraphicsError.NativeFailure, $"Slang vertex reflection has no ATTRIBUTE{attribute.Location} input.");
        }
    }

    private static bool ContainsVertexSemantic(EntryPointReflection entry, uint location)
    {
        for (uint index = 0; index < entry.ParameterCount; index++)
            if (ContainsVertexSemantic(entry.GetParameterByIndex(index), location))
                return true;
        return false;
    }

    private static bool ContainsVertexSemantic(VariableLayoutReflection layout, uint location)
    {
        if (layout == VariableLayoutReflection.Null)
            return false;
        if (string.Equals(layout.SemanticName, "ATTRIBUTE", StringComparison.OrdinalIgnoreCase) && layout.SemanticIndex == location)
            return true;
        TypeLayoutReflection type = layout.TypeLayout.UnwrapArray();
        for (uint index = 0; index < type.FieldCount; index++)
            if (ContainsVertexSemantic(type.GetFieldByIndex(index), location))
                return true;
        return false;
    }

    private void ValidateMeshDescription(Device device, in MeshPipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc.SlangCode);
        if (desc.Reflection == ShaderReflection.Null)
            throw new ArgumentException("A mesh pipeline requires Slang reflection.", nameof(desc));
        ValidatePipelineEntry(desc.Reflection, desc.Mesh, SlangStage.Mesh);
        if(desc.Pixel != EntryPointReflection.Null) ValidatePipelineEntry(desc.Reflection,desc.Pixel,SlangStage.Fragment);
        if(desc.Amplification != EntryPointReflection.Null) ValidatePipelineEntry(desc.Reflection,desc.Amplification,SlangStage.Amplification);
        if (desc.Amplification != EntryPointReflection.Null && !RequireCapability<MeshShaders>(device).AmplificationShaders)
            throw new NotSupportedException("Amplification shaders are unavailable on this device.");
        ValidateRasterizerFeatures(device, desc.Rasterizer);
        ValidateOutputState(device, desc.Attachments, desc.Multisample, desc.DepthStencil, desc.Blend);
        const DynamicStates knownDynamicStates = DynamicStates.Viewport | DynamicStates.Scissor | DynamicStates.BlendConstants | DynamicStates.StencilReference | DynamicStates.DepthBounds | DynamicStates.DepthBias;
        if ((desc.DynamicStates & ~knownDynamicStates) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
    }

    private static void ValidateOutputState(Device device, in AttachmentFormatSignature attachments, in MultisampleState multisample, in DepthStencilState depthStencil, in BlendState blend)
    {
        if (attachments.ColorFormats.Length > device.Capabilities.Limits.MaximumColorAttachments)
            throw new ArgumentOutOfRangeException(nameof(attachments));
        if (multisample.SampleCount == 0 || attachments.SampleCount != multisample.SampleCount)
            throw new ArgumentException("Pipeline sample counts must be non-zero and equal.", nameof(multisample));
        foreach (Format format in attachments.ColorFormats)
        {
            if (IsDepthFormat(format))
                throw new ArgumentException("A color attachment uses a depth/stencil format.", nameof(attachments));
        }

        if (attachments.DepthStencilFormat is Format depth && !IsDepthFormat(depth))
            throw new ArgumentException("The depth/stencil attachment format is not depth/stencil.", nameof(attachments));
        if (attachments.DepthStencilFormat is null && (depthStencil.DepthTest || depthStencil.DepthWrite || depthStencil.StencilTest))
            throw new ArgumentException("Depth/stencil state requires a depth/stencil attachment.", nameof(depthStencil));
        if (!blend.Attachments.IsEmpty && blend.Attachments.Length != attachments.ColorFormats.Length)
            throw new ArgumentException("Blend attachment count must equal the color attachment count.", nameof(blend));
        if (blend.LogicOperation is LogicOperation logicOperation && !Enum.IsDefined(logicOperation))
            throw new ArgumentOutOfRangeException(nameof(blend));
        if (blend.LogicOperation.HasValue)
        {
            foreach (BlendAttachmentState attachment in blend.Attachments)
            {
                if (attachment.Enabled)
                {
                    throw new ArgumentException("Logic operations and color blending cannot be enabled together.", nameof(blend));
                }
            }
        }
    }

    private static void ValidateDepthBounds(Device device, in DepthStencilState state, DynamicStates dynamicStates)
    {
        if ((dynamicStates & DynamicStates.DepthBounds) != 0 && !state.DepthBoundsTest)
        {
            throw new ArgumentException("Dynamic depth bounds require DepthBoundsTest to be enabled in the Pipeline.", nameof(dynamicStates));
        }

        if (state.DepthBoundsTest && !device.Capabilities.SupportsDepthBounds)
            throw new NotSupportedException("Depth-bounds testing is unavailable on this Device.");
    }


    private static void ValidateAttachmentFormats(Device device, in AttachmentFormatSignature attachments, in BlendState blend)
    {
        for (int index = 0; index < attachments.ColorFormats.Length; index++)
        {
            Format format = attachments.ColorFormats[index];
            FormatSupport support = device.Capabilities.GetFormatSupport(format);
            FormatFeatures required = FormatFeatures.ColorAttachment;
            if (attachments.SampleCount > 1)
                required |= FormatFeatures.MultisampleColorAttachment;
            BlendAttachmentState state = blend.Attachments.IsEmpty ? new BlendAttachmentState() : blend.Attachments[index];
            if (state.Enabled)
                required |= FormatFeatures.ColorAttachmentBlend;
            if (blend.LogicOperation.HasValue)
                required |= FormatFeatures.LogicOperation;
            if ((support.Features & required) != required || !support.SupportsSampleCount(attachments.SampleCount))
            {
                throw new NotSupportedException($"Format {format} does not support the requested Pipeline attachment state.");
            }
        }

        if (attachments.DepthStencilFormat is Format depthFormat)
        {
            FormatSupport support = device.Capabilities.GetFormatSupport(depthFormat);
            if ((support.Features & FormatFeatures.DepthStencilAttachment) == 0 || !support.SupportsSampleCount(attachments.SampleCount))
            {
                throw new NotSupportedException($"Format {depthFormat} does not support the requested depth/stencil state.");
            }
        }
    }

    private static VertexBufferLayout FindVertexBuffer(ReadOnlySpan<VertexBufferLayout> buffers, uint index)
    {
        foreach (ref readonly VertexBufferLayout buffer in buffers)
        {
            if (buffer.BufferIndex == index)
                return buffer;
        }

        throw new ArgumentException("A vertex attribute references an absent Buffer layout.");
    }

    private static void ValidatePipelineEntry(ShaderReflection reflection, EntryPointReflection entry, SlangStage stage)
    {
        if (reflection == ShaderReflection.Null || entry == EntryPointReflection.Null || entry.Stage != stage)
            throw new ArgumentException("The pipeline entry must provide reflection for its shader stage.", nameof(entry));
        for (uint index = 0; index < reflection.EntryPointCount; index++)
            if (reflection.GetEntryPointByIndex(index) == entry)
                return;
        throw new ArgumentException("The entry must belong to the pipeline reflection.", nameof(entry));
    }

    private static void ValidateComputeDescription(in ComputePipelineDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc.SlangCode);
        ValidatePipelineEntry(desc.Reflection, desc.Compute, SlangStage.Compute);
    }
}
