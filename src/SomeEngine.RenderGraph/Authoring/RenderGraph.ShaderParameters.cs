namespace SomeEngine.RenderGraph;

public sealed partial class RenderGraph
{
    private void DeclareShaderArgument(
        int pass,
        BufferViewHandle viewHandle,
        GraphAccess flags)
    {
        int view = ValidateBufferView(viewHandle);
        GraphBindingType type = _bufferViewTypes[view];
        int access = AddBufferViewAccess(pass, viewHandle, flags);
        AddOrderedShaderArgument(pass, type, access, view);
    }

    private void DeclareShaderArgument(
        int pass,
        TextureViewHandle viewHandle,
        GraphAccess flags)
    {
        int view = ValidateTextureView(viewHandle);
        GraphTextureViewUsage usage = _textureViewUsages[view];
        GraphBindingType type;
        if ((flags & GraphAccess.ReadWrite) == GraphAccess.Read &&
            (usage & GraphTextureViewUsage.ShaderResource) != 0)
        {
            type = GraphBindingType.SampledTexture;
        }
        else if ((usage & GraphTextureViewUsage.Storage) != 0)
        {
            type = GraphBindingType.StorageTexture;
        }
        else
        {
            throw new ArgumentException(
                "The texture view cannot satisfy the declared shader access.",
                nameof(viewHandle));
        }

        int access = AddTextureViewAccess(pass, viewHandle, flags);
        AddOrderedShaderArgument(pass, type, access, view);
    }

    private void DeclareShaderArgument(
        int pass,
        SamplerHandle samplerHandle)
    {
        _ = GetSampler(samplerHandle);
        int binding = GetPass(pass).ShaderArgumentCount;
        AddShaderArgument(
            pass,
            group: 0,
            checked((uint)binding),
            element: 0,
            GraphBindingType.Sampler,
            accessOrdinal: -1,
            view: -1,
            samplerHandle.Ordinal);
    }

    private void DeclareShaderArgument(
        int pass,
        AccelerationStructureHandle accelerationStructure)
    {
        int view = ValidateAccelerationStructure(accelerationStructure);
        int access = AddAccelerationStructureAccess(pass, accelerationStructure);
        AddOrderedShaderArgument(
            pass,
            GraphBindingType.AccelerationStructure,
            access,
            view);
    }

    private void AddOrderedShaderArgument(
        int pass,
        GraphBindingType type,
        int access,
        int view)
    {
        int binding = GetPass(pass).ShaderArgumentCount;
        AddShaderArgument(
            pass,
            group: 0,
            checked((uint)binding),
            element: 0,
            type,
            access,
            view);
    }
}
