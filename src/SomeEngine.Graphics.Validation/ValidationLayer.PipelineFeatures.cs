using SlangShaderSharp;

namespace SomeEngine.Graphics.Validation;

public sealed partial class ValidationLayer
{
    private static void ValidateGraphicsFeatures(Device device, in GraphicsPipelineDesc desc)
    {
        ValidateRasterizerFeatures(device, desc.Rasterizer);
        if (device is not IPipelineFeatures features)
            return;
        foreach (ref readonly VertexBufferLayout buffer in desc.VertexBuffers)
        {
            if (buffer.PerInstance &&
                (buffer.InstanceStepRate == 0 ? !features.VertexInstanceDivisorZero :
                    buffer.InstanceStepRate > features.MaximumVertexInstanceDivisor))
                throw new NotSupportedException("The vertex instance step rate exceeds the device's divisor support.");
        }
        if (desc.HasStreamOutput)
            ValidateStreamOutput(features, desc.Vertex, desc.StreamOutput);
    }

    private static void ValidateRasterizerFeatures(Device device, in RasterizerState rasterizer)
    {
        if (rasterizer.ConservativeRasterization && device is IPipelineFeatures { ConservativeRasterization: false })
            throw new NotSupportedException("Conservative rasterization is unavailable on this device.");
    }

    private static void ValidateStreamOutput(IPipelineFeatures features, EntryPointReflection vertex, in StreamOutputState state)
    {
        if (!features.StreamOutput)
            throw new NotSupportedException("Stream output is unavailable on this device.");
        if (state.BufferStrides.Length == 0 || state.BufferStrides.Length > features.StreamOutputBuffers)
            throw new ArgumentOutOfRangeException(nameof(state));
        if (state.RasterizedStreamIndex is uint stream && stream != 0)
            throw new NotSupportedException("The vertex pipeline rasterizes stream zero.");
        uint[] sizes = new uint[state.BufferStrides.Length];
        foreach (ref readonly StreamOutputElement element in state.Elements)
        {
            if (element.Stream != 0 || element.ComponentCount == 0 || element.ComponentCount > 4 ||
                element.StartComponent + element.ComponentCount > 4 || element.OutputBinding >= sizes.Length)
                throw new ArgumentException("The stream-output element exceeds its stream, components or buffers.", nameof(state));
            sizes[element.OutputBinding] += (uint)element.ComponentCount * sizeof(uint);
            if (!element.IsGap && (element.Variable == VariableLayoutReflection.Null ||
                    !ContainsOutputLayout(vertex.ResultVarLayout, element.Variable)))
                throw new ArgumentException("A stream-output variable must belong to the vertex output.", nameof(state));
        }
        ulong streamBytes = 0;
        for (int index = 0; index < sizes.Length; index++)
        {
            uint stride = state.BufferStrides[index];
            if ((stride & 3) != 0 || stride > features.StreamOutputStride || sizes[index] > stride ||
                sizes[index] > features.StreamOutputBufferBytes)
                throw new ArgumentOutOfRangeException(nameof(state));
            streamBytes += sizes[index];
        }
        if (streamBytes > features.StreamOutputBytes)
            throw new ArgumentOutOfRangeException(nameof(state));
    }

    private static bool ContainsOutputLayout(VariableLayoutReflection root, VariableLayoutReflection candidate)
    {
        if (root == candidate)
            return true;
        TypeLayoutReflection type = root.TypeLayout;
        for (uint index = 0; index < type.FieldCount; index++)
            if (ContainsOutputLayout(type.GetFieldByIndex(index), candidate))
                return true;
        return false;
    }

    private static void ValidateNullDescriptor(Device device, in ResourceBinding binding)
    {
        if (binding.IsNull && device is IPipelineFeatures { NullDescriptors: false })
            throw new NotSupportedException("Typed null descriptors are unavailable on this device.");
    }
}
