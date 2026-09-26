using System.Numerics;
using SomeEngine.Serialization;

namespace SomeEngine.Resources.Tests;

public sealed class MaterialTests
{
    [Fact]
    public void MaterialPropertiesKeepTheirTypeIdentityAndRecordOnlyActualChanges()
    {
        using var material = new StandardMaterial();
        Assert.Equal(BinaryTypeId.FromLogicalName("SomeEngine.Assets.Schema.StandardMaterial"), material.MaterialTypeId);
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
    public void UpdatePreservesIdentityAndNotifiesEveryObserverAfterChangingProperties()
    {
        using var material = new StandardMaterial { Name = "before", Roughness = 0.25f };
        using var replacement = new StandardMaterial { Name = "after", Roughness = 0.75f };
        ulong revision = material.Revision;
        int notifications = 0;
        material.Changed += _ => throw new InvalidOperationException("observer");
        material.Changed += current =>
        {
            Assert.Same(material, current);
            Assert.Equal("after", current.Name);
            Assert.Equal(0.75f, material.Roughness);
            notifications++;
        };
        Assert.Throws<AggregateException>(() => material.Update(replacement));
        Assert.Equal(1, notifications);
        Assert.Equal(revision + 1, material.Revision);
        Assert.Equal(0.75f, material.Roughness);
    }

    [Fact]
    public void InvalidReplacementAndParameterKindsLeaveExistingValuesUnchanged()
    {
        using var material = new StandardMaterial { Roughness = 0.25f };
        using var other = new UnlitMaterial();
        ulong revision = material.Revision;
        Assert.Throws<InvalidDataException>(() => material.Update(other));
        Assert.Throws<ArgumentException>(() => material.Update(material));
        Assert.Throws<InvalidDataException>(() => material.ApplyParameter(
            new(nameof(StandardMaterial.Roughness), MaterialParameterKind.Vector4) { Vector4 = Vector4.One }, null));
        Assert.Equal(0.25f, material.Roughness);
        Assert.Equal(revision, material.Revision);
    }
}
