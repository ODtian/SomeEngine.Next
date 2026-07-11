using SomeEngine.Graphics;
using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class TextureViewShapeTests
{
    [Fact]
    public void Allowed_view_formats_are_an_immutable_normalized_value_set()
    {
        Format[] callerOwned =
        [
            Format.R8G8B8A8UNormSrgb,
            Format.R8G8B8A8UNorm,
            Format.R8G8B8A8UNormSrgb,
        ];
        TextureDesc first = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            AllowedViewFormats: callerOwned);
        callerOwned[0] = Format.R32Float;
        TextureDesc equivalent = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            AllowedViewFormats:
            [
                Format.R8G8B8A8UNorm,
                Format.R8G8B8A8UNormSrgb,
            ]);
        TextureDesc defaultSet = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled);

        first.Validate();
        equivalent.Validate();
        Assert.Equal(
            new[] { Format.R8G8B8A8UNorm, Format.R8G8B8A8UNormSrgb },
            first.AllowedViewFormats.ToArray());
        Assert.Equal(first, equivalent);
        Assert.Equal(first.GetHashCode(), equivalent.GetHashCode());
        Assert.Equal(new[] { Format.R8G8B8A8UNorm }, defaultSet.AllowedViewFormats.ToArray());
        Assert.Throws<ArgumentException>(() => new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            AllowedViewFormats: [Format.B8G8R8A8UNorm]).Validate());
    }

    [Fact]
    public void Requirements_distinguish_resource_dimension_cube_intent_and_view_format_set()
    {
        using Device device = new();
        TextureDesc twoDimensional = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled);
        TextureDesc threeDimensional = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            Depth: 1,
            Dimension: TextureDimension.Texture3D);
        TextureDesc cubeCompatible = new(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            ArrayLayers: 6,
            CubeCompatible: true);
        TextureDesc plainArray = cubeCompatible with { CubeCompatible = false };
        TextureDesc castable = twoDimensional with
        {
            Name = "constructor-retains-the-normalized-format-set",
        };
        castable = new TextureDesc(
            castable.Width,
            castable.Height,
            castable.Format,
            castable.Usage,
            AllowedViewFormats: [Format.R8G8B8A8UNormSrgb]);

        ulong dimensionClass = device.GetTextureRequirements(twoDimensional).CompatibilityClass;
        Assert.NotEqual(dimensionClass, device.GetTextureRequirements(threeDimensional).CompatibilityClass);
        Assert.NotEqual(
            device.GetTextureRequirements(cubeCompatible).CompatibilityClass,
            device.GetTextureRequirements(plainArray).CompatibilityClass);
        Assert.NotEqual(dimensionClass, device.GetTextureRequirements(castable).CompatibilityClass);
    }

    [Fact]
    public void Null_backend_creates_every_portable_explicit_view_dimension()
    {
        using Device device = new();
        List<(TextureHandle Texture, TextureViewHandle[] Views)> values = [];

        TextureHandle oneDimensional = device.CreateTexture(new TextureDesc(
            8,
            1,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            MipLevels: 2,
            ArrayLayers: 2,
            Dimension: TextureDimension.Texture1D));
        values.Add((oneDimensional,
        [
            CreateView(device, oneDimensional, new(0, 2, 0, 1, TextureAspect.Color), TextureViewDimension.Texture1D),
            CreateView(device, oneDimensional, new(0, 2, 0, 2, TextureAspect.Color), TextureViewDimension.Texture1DArray),
        ]));

        TextureHandle twoDimensional = device.CreateTexture(new TextureDesc(
            8,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            MipLevels: 2,
            ArrayLayers: 2));
        values.Add((twoDimensional,
        [
            CreateView(device, twoDimensional, new(0, 2, 0, 1, TextureAspect.Color), TextureViewDimension.Texture2D),
            CreateView(device, twoDimensional, new(0, 2, 0, 2, TextureAspect.Color), TextureViewDimension.Texture2DArray),
        ]));

        TextureHandle multisampled = device.CreateTexture(new TextureDesc(
            8,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.ColorAttachment,
            ArrayLayers: 2,
            SampleCount: 4));
        values.Add((multisampled,
        [
            CreateView(device, multisampled, new(0, 1, 0, 1, TextureAspect.Color), TextureViewDimension.Texture2DMS),
            CreateView(device, multisampled, new(0, 1, 0, 2, TextureAspect.Color), TextureViewDimension.Texture2DMSArray),
        ]));

        TextureHandle cube = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            MipLevels: 2,
            ArrayLayers: 12,
            CubeCompatible: true));
        values.Add((cube,
        [
            CreateView(device, cube, new(0, 2, 0, 6, TextureAspect.Color), TextureViewDimension.Cube),
            CreateView(device, cube, new(0, 2, 0, 12, TextureAspect.Color), TextureViewDimension.CubeArray),
        ]));

        TextureHandle volume = device.CreateTexture(new TextureDesc(
            8,
            4,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.Storage,
            Depth: 4,
            MipLevels: 2,
            Dimension: TextureDimension.Texture3D));
        values.Add((volume,
        [
            CreateView(device, volume, new(0, 2, 0, 1, TextureAspect.Color), TextureViewDimension.Texture3D),
            device.CreateTextureView(new TextureViewDesc(
                volume,
                new TextureSubresourceRange(1, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.Storage,
                Dimension: TextureViewDimension.Texture3D)),
        ]));

        TextureHandle castable = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled | TextureUsage.ColorAttachment,
            AllowedViewFormats: [Format.R8G8B8A8UNormSrgb]));
        values.Add((castable,
        [
            device.CreateTextureView(new TextureViewDesc(
                castable,
                new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
                TextureViewUsage.ShaderResource | TextureViewUsage.ColorAttachment,
                Format.R8G8B8A8UNormSrgb,
                Dimension: TextureViewDimension.Texture2D)),
        ]));

        foreach ((TextureHandle texture, TextureViewHandle[] views) in values)
        {
            foreach (TextureViewHandle view in views) device.DestroyTextureView(view);
            device.DestroyTexture(texture);
        }
    }

    [Fact]
    public void Null_backend_rejects_shape_format_and_usage_mismatches_before_allocating_a_view()
    {
        using Device device = new();
        TextureHandle array = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            ArrayLayers: 12));
        Assert.Throws<ArgumentException>(() => CreateView(
            device,
            array,
            new TextureSubresourceRange(0, 1, 0, 2, TextureAspect.Color),
            TextureViewDimension.Texture2D));
        Assert.Throws<ArgumentException>(() => CreateView(
            device,
            array,
            new TextureSubresourceRange(0, 1, 0, 6, TextureAspect.Color),
            TextureViewDimension.Cube));

        TextureHandle cube = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled,
            ArrayLayers: 12,
            CubeCompatible: true));
        Assert.Throws<ArgumentException>(() => CreateView(
            device,
            cube,
            new TextureSubresourceRange(0, 1, 6, 6, TextureAspect.Color),
            TextureViewDimension.Cube));

        TextureHandle exactFormat = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Sampled));
        Assert.Throws<ArgumentException>(() => device.CreateTextureView(new TextureViewDesc(
            exactFormat,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ShaderResource,
            Format.R8G8B8A8UNormSrgb,
            Dimension: TextureViewDimension.Texture2D)));

        TextureHandle castableStorage = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.Storage,
            AllowedViewFormats: [Format.R8G8B8A8UNormSrgb]));
        Assert.Throws<ArgumentException>(() => device.CreateTextureView(new TextureViewDesc(
            castableStorage,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.Storage,
            Format.R8G8B8A8UNormSrgb,
            Dimension: TextureViewDimension.Texture2D)));

        TextureHandle volume = device.CreateTexture(new TextureDesc(
            8,
            8,
            Format.R8G8B8A8UNorm,
            TextureUsage.ColorAttachment,
            Depth: 4,
            Dimension: TextureDimension.Texture3D));
        Assert.Throws<NotSupportedException>(() => device.CreateTextureView(new TextureViewDesc(
            volume,
            new TextureSubresourceRange(0, 1, 0, 1, TextureAspect.Color),
            TextureViewUsage.ColorAttachment,
            Dimension: TextureViewDimension.Texture3D)));

        device.DestroyTexture(volume);
        device.DestroyTexture(castableStorage);
        device.DestroyTexture(exactFormat);
        device.DestroyTexture(cube);
        device.DestroyTexture(array);
    }

    private static TextureViewHandle CreateView(
        Device device,
        TextureHandle texture,
        TextureSubresourceRange range,
        TextureViewDimension dimension) =>
        device.CreateTextureView(new TextureViewDesc(
            texture,
            range,
            TextureViewUsage.ShaderResource,
            Dimension: dimension));
}
