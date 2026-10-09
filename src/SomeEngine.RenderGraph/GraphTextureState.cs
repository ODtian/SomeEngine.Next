namespace SomeEngine.RenderGraph;

/// <summary>Reads the registered texture's latest submitted states from their graph declaration.</summary>
public sealed class GraphTextureState
{
    private readonly GraphTexture _texture;
    internal GraphTextureState(GraphTexture texture, GraphTextureId id) => (_texture, Texture) = (texture, id);
    public GraphTextureId Texture { get; }
    public ReadOnlySpan<TextureBoundaryState> States => _texture.BoundaryStates;
}
