using SomeEngine.Assets.Schema;

namespace SomeEngine.Render.Tests;

public sealed class ShaderScalarLayoutTests
{
    [Fact]
    public void ShaderKeepsMultipleCanonicalLayoutsOnTheAssetItself()
    {
        ShaderMaterialScalarLayout first = Layout("FirstScalars", "BaseColorTint", 0);
        ShaderMaterialScalarLayout second = Layout("SecondScalars", "EmissiveFactor", 0);
        Shader shader = CreateShader(first, second);

        Shader.Validate(shader);

        IList<ShaderMaterialScalarLayout> layouts = shader.Metadata!.MaterialScalarLayouts!;
        Assert.Equal(2, layouts.Count);
        Assert.Same(first, layouts[0]);
        Assert.Same(second, layouts[1]);
    }

    [Fact]
    public void ShaderRejectsOverlappingScalarFields()
    {
        var layout = new ShaderMaterialScalarLayout
        {
            Name = "Overlapping",
            Size = 32,
            Fields =
            [
                new ShaderMaterialScalarField
                {
                    Name = "First",
                    Offset = 0,
                    Size = 16,
                    RowCount = 1,
                    ColumnCount = 4,
                    ScalarType = 8,
                },
                new ShaderMaterialScalarField
                {
                    Name = "Second",
                    Offset = 8,
                    Size = 16,
                    RowCount = 1,
                    ColumnCount = 4,
                    ScalarType = 8,
                },
            ],
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => Shader.Validate(CreateShader(layout)));

        Assert.Contains("not canonical and ordered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterialRuntimeParametersAreTheSchemaValues()
    {
        var value = new Vec4Val { X = 1, Y = 2, Z = 3, W = 4 };
        var material = new Material
        {
            Name = "DirectParameters",
            Passes = [],
            Textures = [],
            Scalars =
            [
                new ScalarParam
                {
                    Name = "BaseColorTint",
                    Value = new ParamValue(value),
                },
            ],
        };

        ScalarParam parameter = Assert.Single(material.Scalars!);
        Assert.Same(value, parameter.Value!.Value.Vec4Val);
        Assert.Equal(4, parameter.Value.Value.Vec4Val.W);
    }

    private static ShaderMaterialScalarLayout Layout(
        string name,
        string field,
        uint offset)
        => new()
        {
            Name = name,
            Size = 16,
            Fields =
            [
                new ShaderMaterialScalarField
                {
                    Name = field,
                    Offset = offset,
                    Size = 16,
                    RowCount = 1,
                    ColumnCount = 4,
                    ScalarType = 8,
                },
            ],
        };

    private static Shader CreateShader(params ShaderMaterialScalarLayout[] layouts)
        => new()
        {
            Name = "layout-test",
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
                MaterialScalarLayouts = layouts,
            },
        };
}
