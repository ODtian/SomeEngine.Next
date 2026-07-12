namespace SomeEngine.RenderGraph;

/// <summary>
/// A graph-owned resource whose ownership was transferred after every producing GPU completion.
/// Exactly one of <see cref="Buffer"/> and <see cref="Texture"/> is valid. The recipient becomes
/// responsible for destroying the published handle through its originating device.
/// </summary>
public readonly record struct ResourceExport
{
    internal ResourceExport(
        BufferHandle buffer,
        TextureHandle texture,
        ResourceState finalState,
        GpuCompletionSet completion)
    {
        if (buffer.IsValid == texture.IsValid)
            throw new ArgumentException("A resource export must contain exactly one resource handle.");
        Buffer = buffer;
        Texture = texture;
        FinalState = finalState;
        Completion = completion;
    }

    public BufferHandle Buffer { get; }
    public TextureHandle Texture { get; }
    public ResourceState FinalState { get; }
    public GpuCompletionSet Completion { get; }
    public bool IsBuffer => Buffer.IsValid;
    public bool IsTexture => Texture.IsValid;
}
