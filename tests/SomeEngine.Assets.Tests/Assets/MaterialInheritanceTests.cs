using SomeEngine.Resources;

namespace SomeEngine.Assets.Tests;

public sealed class MaterialInheritanceTests
{
    [Fact]
    public void DerivedPropertiesPreserveInheritedValuesAndDefinitions()
    {
        var material = new TintedCoatingMaterial
        {
            Roughness = 0.25f,
            Coating = 0.75f,
            Tint = 0.5f,
        };

        Assert.Equal(0.25f, material.Roughness);
        Assert.Equal(0.75f, material.Coating);
        Assert.Equal(0.5f, material.Tint);
        Assert.Equal(3, material.PropertyCount);
        Assert.Equal(3, material.Properties.Select(property => property.Name).Distinct().Count());

        material.Coating = 0.9f;
        Assert.Equal(0.25f, material.Roughness);
        Assert.Equal(0.9f, material.Coating);
        Assert.Equal(0.5f, material.Tint);
    }
}

public abstract partial class RoughSurfaceMaterial : Material
{
    public partial float Roughness { get; set; }
}

public partial class SurfaceCoatingMaterial : RoughSurfaceMaterial
{
    public partial float Coating { get; set; }
}

public sealed partial class TintedCoatingMaterial : SurfaceCoatingMaterial
{
    public partial float Tint { get; set; }
}
