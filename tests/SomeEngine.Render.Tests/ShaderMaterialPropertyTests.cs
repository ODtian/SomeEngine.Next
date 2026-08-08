using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Tests;

public sealed class ShaderMaterialPropertyTests
{
    [Fact]
    public void ShaderExposesCanonicalMaterialPropertyWithoutAProjectionType()
    {
        byte[] sourceDefault = new byte[16];
        sourceDefault[0] = 23;
        Shader shader = CreateShader(sourceDefault);

        Shader.Validate(shader);
        ShaderMaterialInstanceProperty property = Assert.Single(
            shader.Metadata!.MaterialInstanceProperties!);

        Assert.Equal("test.material.tint", property.CanonicalId);
        Assert.Equal("TestScalars", property.MaterialScalarLayoutName);
        Assert.Equal("ArbitraryColor", property.MaterialScalarName);
        Assert.Equal("LoadTestTint", property.Accessor);
        Assert.Equal(16u, property.Size);
        Assert.Equal(16u, property.Alignment);
        Assert.Equal(1u, property.RowCount);
        Assert.Equal(4u, property.ColumnCount);
        Assert.Equal(8, property.ScalarType);
        Assert.Equal(23, property.DefaultValue!.Value.Span[0]);

        sourceDefault[0] = 99;
        Assert.Equal(99, property.DefaultValue.Value.Span[0]);
        Assert.True(property.DefaultValue.Value.Equals((Memory<byte>)sourceDefault));
    }

    [Fact]
    public void ShaderRejectsMaterialPropertyDefaultWithWrongSize()
    {
        Shader shader = CreateShader(new byte[4]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => Shader.Validate(shader));

        Assert.Contains("not canonical", error.Message, StringComparison.Ordinal);
    }

    private static Shader CreateShader(byte[] defaultValue)
        => new()
        {
            Name = "property-test",
            Variants =
            [
                new ShaderBytecode
                {
                    Backend = "test",
                    EntryPoint = "Main",
                    Stage = ShaderStage.Compute,
                    Data = new byte[] { 1 },
                },
            ],
            EntryPointReflections =
            [
                new ShaderEntryPointReflection
                {
                    Backend = "test",
                    EntryPoint = "Main",
                    Stage = ShaderStage.Compute,
                },
            ],
            Metadata = new ShaderMetadata
            {
                MaterialInstanceProperties =
                [
                    new ShaderMaterialInstanceProperty
                    {
                        CanonicalId = "test.material.tint",
                        MaterialScalarLayoutName = "TestScalars",
                        MaterialScalarName = "ArbitraryColor",
                        Accessor = "LoadTestTint",
                        Size = 16,
                        Alignment = 16,
                        RowCount = 1,
                        ColumnCount = 4,
                        ScalarType = 8,
                        DefaultValue = defaultValue,
                    },
                ],
            },
        };
}
