using SomeEngine.Render.Materials;
using SomeEngine.Resources;

namespace SomeEngine.Render.Tests;

public sealed class ShaderScalarLayoutTests
{
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
}
