using SomeEngine.Assets.Schema;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tests;

public sealed class ShaderScalarLayoutTests
{
    [Fact]
    public void ShaderKeepsMultipleCanonicalLayoutsOnTheAssetItself()
    {
        ShaderMaterialScalarLayout first = Layout("FirstScalars", "BaseColorTint", 0);
        ShaderMaterialScalarLayout second = Layout("SecondScalars", "EmissiveFactor", 0);
        Shader shader = CreateShader(first, second);

        CookedShaderTestProgram.Load(shader);

        IList<ShaderMaterialScalarLayout> layouts = shader.Metadata!.MaterialScalarLayouts!;
        Assert.Equal(2, layouts.Count);
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
            () => CookedShaderTestProgram.Load(CreateShader(layout)));

        Assert.Contains("not canonical and ordered", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MaterialRuntimeParametersAreConcreteTypedSlots()
    {
        var value = new System.Numerics.Vector4(1, 2, 3, 4);
        var material = new StandardMaterial
        {
            Name = "DirectParameters",
            BaseColorTint = value,
        };

        Assert.Equal(value, material.BaseColorTint);
        MaterialPropertyDefinition slot = Assert.Single(
            material.Properties,
            static slot => slot.Name == nameof(StandardMaterial.BaseColorTint));
        Assert.Equal(typeof(System.Numerics.Vector4), slot.ValueType);
        Assert.Equal(value, material.BaseColorTint);
    }

    [Fact]
    public void EquivalentFieldOrdersProduceTheSameLayoutIdentity()
    {
        var first = new ScalarFieldLayout("Tint", 0, 16, 1, 4, 8);
        var second = new ScalarFieldLayout("Roughness", 16, 4, 1, 1, 8);
        ScalarLayout left = ScalarLayout.FromFields("Surface", [first, second], 32);
        ScalarLayout right = ScalarLayout.FromFields("Surface", [second, first], 32);
        Assert.Equal(left.LayoutHash, right.LayoutHash);
        Assert.Equal(left.Fields, right.Fields);
        Assert.Equal(left.ByteSize, right.ByteSize);
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
            Programs = [CookedShaderTestProgram.Create()],
            EntryPointReflections =
            [
                new ShaderEntryPointReflection
                {
                    Backend = "spirv",
                    EntryPoint = "VSMain",
                    Stage = ShaderStage.Vertex,
                },
            ],
            Metadata = new ShaderMetadata
            {
                MaterialScalarLayouts = [.. layouts],
            },
        };
}
