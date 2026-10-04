using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Graphics;
using SomeEngine.Layout;
using Texture = SomeEngine.Resources.Texture;

namespace SomeEngine.Elements;

/// <summary>Supplies ready application textures to image measurement through the layout data stage.</summary>
public sealed class ImageTextures(Func<AssetId<TextureAsset>, Texture?> resolve) : ILayoutDataUpdate
{
    public bool HasChanges(World world, Entity node) => world.Has<Image>(node) &&
        (!world.Has<ImageTexture>(node) || world.Read<ImageTexture>(node) != ReadTexture(world, node));

    public void Update(World world, Entity node, in Constraints constraints)
    {
        if (!world.Has<Image>(node)) return;
        ImageTexture value = ReadTexture(world, node);
        if (!world.Has<ImageTexture>(node)) world.Add(node, value);
        else if (world.Read<ImageTexture>(node) != value) world.Replace(node, value);
    }

    private ImageTexture ReadTexture(World world, Entity node)
    {
        Texture? texture = resolve(world.Read<Image>(node).Source);
        DevelopmentCheck.Require(texture is null || texture.SampledDimension == TextureViewDimension.Texture2D,
            "Image requires a two-dimensional sampled texture.");
        return texture is null ? new(null, new Size(1, 1), 0) :
            new(texture, new Size(texture.Width, texture.Height), texture.Revision);
    }
}

/// <summary>The image's ready texture and measured content at the game update boundary.</summary>
public readonly record struct ImageTexture(Texture? Value, Size Size, ulong Revision) : IComponent;
