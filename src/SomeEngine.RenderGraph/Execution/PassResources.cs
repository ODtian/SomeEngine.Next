namespace SomeEngine.RenderGraph;

public readonly struct PassResources
{
    private readonly GraphInvocation _invocation;
    private readonly int _pass;

    internal PassResources(GraphInvocation invocation, int pass)
    {
        _invocation = invocation;
        _pass = pass;
    }

    public BufferHandle Get(BufferAccess access)
    {
        Validate(access.Owner, access.Pass, access.Access, access.Resource, ResourceNodeKind.Buffer);
        return _invocation.Buffers[access.Resource];
    }

    public TextureHandle Get(TextureAccess access)
    {
        Validate(access.Owner, access.Pass, access.Access, access.Resource, ResourceNodeKind.Texture);
        return _invocation.Textures[access.Resource];
    }

    public BufferViewHandle Get(BufferViewAccess access)
    {
        BufferAccess resourceAccess = access.ResourceAccess;
        Validate(resourceAccess.Owner, resourceAccess.Pass, resourceAccess.Access, resourceAccess.Resource, ResourceNodeKind.Buffer);
        FrozenAccess expected = _invocation.Frozen.Passes[_pass].Accesses[resourceAccess.Access];
        if (expected.View != access.View || (uint)access.View >= (uint)_invocation.BufferViews.Length)
            throw new ArgumentException("The buffer-view access token does not match the frozen declaration.", nameof(access));
        return _invocation.BufferViews[access.View];
    }

    public TextureViewHandle Get(TextureViewAccess access)
    {
        TextureAccess resourceAccess = access.ResourceAccess;
        Validate(resourceAccess.Owner, resourceAccess.Pass, resourceAccess.Access, resourceAccess.Resource, ResourceNodeKind.Texture);
        FrozenAccess expected = _invocation.Frozen.Passes[_pass].Accesses[resourceAccess.Access];
        if (expected.View != access.View || (uint)access.View >= (uint)_invocation.TextureViews.Length)
            throw new ArgumentException("The texture-view access token does not match the frozen declaration.", nameof(access));
        return _invocation.TextureViews[access.View];
    }

    public TextureViewHandle Get(ColorAttachmentAccess access)
    {
        if (access.Slot < 0) throw new ArgumentException("The color-attachment access token is invalid.", nameof(access));
        FrozenPass pass = _invocation.Frozen.Passes[_pass];
        if ((uint)access.Slot >= (uint)pass.ColorAttachments.Length || pass.ColorAttachments[access.Slot].Slot != access.Slot)
            throw new ArgumentException("The color-attachment access token has an invalid slot.", nameof(access));
        FrozenColorAttachment expected = pass.ColorAttachments[access.Slot];
        if (expected.View != access.ViewAccess.View || expected.Access != access.ViewAccess.ResourceAccess.Access || expected.Load != access.Load)
            throw new ArgumentException("The color-attachment access token does not match the frozen declaration.", nameof(access));
        return Get(access.ViewAccess);
    }

    private void Validate(GraphToken? owner, int pass, int access, int resource, ResourceNodeKind kind)
    {
        if (!ReferenceEquals(owner, _invocation.Frozen.Token) || pass != _pass)
            throw new ArgumentException("The access token does not belong to this pass invocation.");
        if ((uint)access >= (uint)_invocation.Frozen.Passes[pass].Accesses.Length)
            throw new ArgumentException("The access token has an invalid ordinal.");
        FrozenAccess expected = _invocation.Frozen.Passes[pass].Accesses[access];
        if (expected.Resource != resource || expected.Kind != kind)
            throw new ArgumentException("The access token does not match the frozen declaration.");
    }
}
