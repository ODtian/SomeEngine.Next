using System.Numerics;

namespace SomeEngine.Resources.Tests;

public sealed class MaterialTests
{
    [Fact]
    public void MaterialPropertiesKeepDefaultsAndRecordActualChanges()
    {
        var material = new StandardMaterial();
        Assert.Equal(0.5f, material.Roughness);
        uint index = material.Properties.Single(property => property.Name == nameof(StandardMaterial.Roughness)).Index;
        ulong revision = material.Revision;
        material.Roughness = 0.25f;
        Assert.Equal(revision + 1, material.Revision);
        Assert.Equal(1ul, material.GetPropertyRevision(index));
        material.Roughness = 0.25f;
        Assert.Equal(revision + 1, material.Revision);
        Assert.Equal(1ul, material.GetPropertyRevision(index));
        material.ApplyParameter(new(nameof(StandardMaterial.BaseColorTint), MaterialParameterKind.Vector4)
            { Vector4 = new(1, 2, 3, 4) }, null);
        Assert.Equal(new Vector4(1, 2, 3, 4), material.BaseColorTint);
    }

    [Fact]
    public void ConcreteUpdatePreservesIdentityAndAdvancesOneContentRevision()
    {
        var material = new StandardMaterial { Name = "before", Roughness = 0.25f };
        var replacement = new StandardMaterial { Name = "after", Roughness = 0.75f };
        ulong revision = material.Revision;
        long identity = material.MaterialId;
        material.Update(replacement);
        Assert.Equal(identity, material.MaterialId);
        Assert.Equal("after", material.Name);
        Assert.Equal(revision + 1, material.Revision);
        Assert.Equal(0.75f, material.Roughness);
    }

    [Fact]
    public void InvalidParameterKindsPreserveExistingValues()
    {
        var material = new StandardMaterial { Roughness = 0.25f };
        ulong revision = material.Revision;
        Assert.Throws<InvalidDataException>(() => material.ApplyParameter(
            new(nameof(StandardMaterial.Roughness), MaterialParameterKind.Vector4) { Vector4 = Vector4.One }, null));
        Assert.Equal(0.25f, material.Roughness);
        Assert.Equal(revision, material.Revision);
    }
}
