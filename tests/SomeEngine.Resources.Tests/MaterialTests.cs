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

    [Fact]
    public void PropertyCountersTrackIndependentChangesForMultipleConsumers()
    {
        var material = new StandardMaterial();
        uint roughness = material.Properties.Single(property => property.Name == nameof(StandardMaterial.Roughness)).Index;
        uint metallic = material.Properties.Single(property => property.Name == nameof(StandardMaterial.MetallicFactor)).Index;
        ulong firstRoughness = material.GetPropertyRevision(roughness);
        ulong firstMetallic = material.GetPropertyRevision(metallic);
        ulong revision = material.Revision;
        Assert.Equal(0ul, firstRoughness);
        Assert.Equal(0ul, firstMetallic);

        material.Roughness = 0.25f;
        ulong secondRoughness = material.GetPropertyRevision(roughness);
        ulong secondMetallic = material.GetPropertyRevision(metallic);
        Assert.Equal(1ul, secondRoughness);
        Assert.Equal(0ul, secondMetallic);
        Assert.Equal(revision + 1, material.Revision);

        material.MetallicFactor = 0.75f;
        Assert.Equal(secondRoughness, material.GetPropertyRevision(roughness));
        Assert.Equal(1ul, material.GetPropertyRevision(metallic));
        Assert.Equal(revision + 2, material.Revision);
        Assert.NotEqual(firstRoughness, material.GetPropertyRevision(roughness));
        Assert.NotEqual(firstMetallic, material.GetPropertyRevision(metallic));
        Assert.Equal(secondRoughness, material.GetPropertyRevision(roughness));
        Assert.NotEqual(secondMetallic, material.GetPropertyRevision(metallic));

        material.Roughness = 0.75f;
        Assert.Equal(2ul, material.GetPropertyRevision(roughness));
        Assert.Equal(1ul, material.GetPropertyRevision(metallic));
        Assert.Equal(revision + 3, material.Revision);
    }

    [Fact]
    public void ConcreteUpdateBatchesRevisionAndCountsOnlyChangedProperties()
    {
        var material = new StandardMaterial { Name = "before", Roughness = 0.25f, MetallicFactor = 0.25f };
        var replacement = new StandardMaterial { Name = "after", Roughness = 0.75f, MetallicFactor = 0.75f };
        uint roughness = material.Properties.Single(property => property.Name == nameof(StandardMaterial.Roughness)).Index;
        uint metallic = material.Properties.Single(property => property.Name == nameof(StandardMaterial.MetallicFactor)).Index;
        uint tint = material.Properties.Single(property => property.Name == nameof(StandardMaterial.BaseColorTint)).Index;
        ulong roughnessRevision = material.GetPropertyRevision(roughness);
        ulong metallicRevision = material.GetPropertyRevision(metallic);
        ulong tintRevision = material.GetPropertyRevision(tint);
        ulong revision = material.Revision;
        long identity = material.MaterialId;

        material.Update(replacement);

        Assert.Equal(identity, material.MaterialId);
        Assert.Equal("after", material.Name);
        Assert.Equal(0.75f, material.Roughness);
        Assert.Equal(0.75f, material.MetallicFactor);
        Assert.Equal(Vector4.One, material.BaseColorTint);
        Assert.Equal(revision + 1, material.Revision);
        Assert.Equal(roughnessRevision + 1, material.GetPropertyRevision(roughness));
        Assert.Equal(metallicRevision + 1, material.GetPropertyRevision(metallic));
        Assert.Equal(tintRevision, material.GetPropertyRevision(tint));

        replacement.Roughness = 0.5f;
        material.Update(replacement);

        Assert.Equal(identity, material.MaterialId);
        Assert.Equal("after", material.Name);
        Assert.Equal(0.5f, material.Roughness);
        Assert.Equal(0.75f, material.MetallicFactor);
        Assert.Equal(Vector4.One, material.BaseColorTint);
        Assert.Equal(revision + 2, material.Revision);
        Assert.Equal(roughnessRevision + 2, material.GetPropertyRevision(roughness));
        Assert.Equal(metallicRevision + 1, material.GetPropertyRevision(metallic));
        Assert.Equal(tintRevision, material.GetPropertyRevision(tint));
    }
}
