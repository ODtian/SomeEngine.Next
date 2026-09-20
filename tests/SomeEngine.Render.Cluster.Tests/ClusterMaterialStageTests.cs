using System.Reflection;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterMaterialStageTests
{
    [Fact]
    public void Structurally_identical_assignment_preserves_publication_identity_and_version()
    {
        var assignments = new ClusterMaterialAssignments();
        Material material = new StandardMaterial { Name = "test" };
        ClusterMaterialAssignmentSet first = Assignment(material);
        ClusterMaterialAssignmentSet identical = Assignment(material);

        assignments.Publish(first);
        assignments.Publish(identical);

        Assert.Same(first, assignments.Current);
        Assert.Equal(1UL, first.TopologyVersion);
        Assert.Equal(0UL, identical.TopologyVersion);
        Assert.Same(material, first.Slots.Span[0]);

        Material replacement = new UnlitMaterial { Name = "replacement" };
        ClusterMaterialAssignmentSet changed = Assignment(replacement);
        assignments.Publish(changed);

        Assert.Same(changed, assignments.Current);
        Assert.Equal(2UL, changed.TopologyVersion);
    }

    [Fact]
    public void MaterialScalarEditsDoNotChangeSharedShaderContent()
    {
        var shader = new Shader { Name = "generated" };
        using var first = new StandardMaterial { Shader = shader, Roughness = 0.2f };
        using var second = new StandardMaterial { Shader = shader, Roughness = 0.8f };
        first.Roughness = 0.4f;
        Assert.Same(shader, first.Shader);
        Assert.Same(shader, second.Shader);
        Assert.Equal(0.8f, second.Roughness);
    }

    [Fact]
    public void Material_carries_user_shader_reference_independent_of_values()
    {
        var shader = new Shader { Name = "generated" };
        var first = new StandardMaterial { Name = "first", Roughness = 0.1f, Shader = shader };
        var second = new StandardMaterial { Name = "second", Roughness = 0.9f, Shader = shader };

        Assert.Equal(first.MaterialTypeId, second.MaterialTypeId);
        Assert.Same(shader, first.Shader);
        Assert.Same(shader, second.Shader);
        Assert.NotEqual(first.Roughness, second.Roughness);
    }

    [Fact]
    public void Different_materials_can_share_one_shade_compatibility_key()
    {
        SlangShaderSharp.ShaderProgram cooked = CookedShaderTestProgram.Create();
        var firstKey = new ClusterShadeCompatibilityKey(
            cooked,
            cooked.EntryPoints[0]);
        var secondKey = new ClusterShadeCompatibilityKey(
            cooked,
            cooked.EntryPoints[0]);

        Material first = new StandardMaterial { Name = "first" };
        Material second = new UnlitMaterial { Name = "second" };

        Assert.NotSame(first, second);
        Assert.Equal(firstKey, secondKey);
    }

    [Fact]
    public void Stage_bin_indices_survive_unrelated_registration_changes()
    {
        var bins = new ClusterBinIndexMap<string>();
        bins.Reconcile(["first", "second"]);
        uint first = bins.Get("first");
        uint second = bins.Get("second");

        bins.Reconcile(["second", "third"]);

        Assert.Equal(second, bins.Get("second"));
        Assert.Equal(first, bins.Get("third"));
        Assert.Equal(2u, bins.Range);
    }

    [Fact]
    public void Material_slot_value_changes_do_not_change_a_stage_key()
    {
        SlangShaderSharp.ShaderProgram cooked = CookedShaderTestProgram.Create();
        var key = new ClusterShadeCompatibilityKey(
            cooked,
            cooked.EntryPoints[0]);
        var material = new StandardMaterial();

        ulong initialRevision = material.Revision;
        material.Roughness = 0.75f;
        material.MaterialSampler = new SamplerDesc(
            FilterType.Nearest,
            FilterType.Linear,
            FilterType.Nearest,
            AddressType.ClampToEdge,
            AddressType.Repeat,
            AddressType.MirrorRepeat);
        var afterValuesChanged = new ClusterShadeCompatibilityKey(
            cooked,
            cooked.EntryPoints[0]);

        Assert.True(material.Revision > initialRevision);
        Assert.Equal(key, afterValuesChanged);
    }

    [Theory]
    [InlineData("finite", "Finite")]
    [InlineData("unbounded", "Unbounded")]
    public void Final_geometry_entry_declares_vertex_encoding_and_conservative_bounds(
        string boundsValue,
        string expectedBounds)
    {
        SlangShaderSharp.ShaderProgram cooked = CookedShaderTestProgram.Create();
        string entryPoint = cooked.EntryPoints[0].Name;
        var shader = new Shader
        {
            Programs = [cooked],
            EntryPointAttributes =
            [
                new ShaderEntryPointAttribute
                {
                    ProgramIndex = 0,
                    EntryPointIndex = 0,
                    Name = ClusterGeometryContracts.VertexEncodingAttribute,
                    Args = [ClusterTestAssets.StandardVertexEncoding],
                },
                new ShaderEntryPointAttribute
                {
                    ProgramIndex = 0,
                    EntryPointIndex = 0,
                    Name = ClusterGeometryContracts.ConservativeBoundsAttribute,
                    Args = [boundsValue],
                },
            ],
        };

        Assert.Equal(
            ClusterTestAssets.StandardVertexEncoding,
            ClusterGeometryContracts.RequireVertexEncoding(shader, entryPoint, "test geometry"));
        Assert.Equal(
            expectedBounds,
            ClusterGeometryContracts.RequireConservativeBounds(shader, entryPoint, "test geometry").ToString());
    }

    [Fact]
    public void Missing_conservative_bounds_declaration_fails_during_stage_resolution()
    {
        SlangShaderSharp.ShaderProgram cooked = CookedShaderTestProgram.Create();
        string entryPoint = cooked.EntryPoints[0].Name;
        var shader = new Shader
        {
            Programs = [cooked],
            EntryPointAttributes =
            [
                new ShaderEntryPointAttribute
                {
                    ProgramIndex = 0,
                    EntryPointIndex = 0,
                    Name = ClusterGeometryContracts.VertexEncodingAttribute,
                    Args = [ClusterTestAssets.StandardVertexEncoding],
                },
            ],
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ClusterGeometryContracts.RequireConservativeBounds(shader, entryPoint, "test geometry"));
        Assert.Contains("must declare", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Universal_material_device_record_types_do_not_exist()
    {
        HashSet<string> names = typeof(ClusterPipelineSystem).Assembly
            .GetTypes()
            .Select(static type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ClusterMaterialTable", names);
        Assert.DoesNotContain("ClusterMaterialSnapshot", names);
        Assert.DoesNotContain("ClusterMaterialExecutionKeys", names);
        Assert.DoesNotContain("ClusterMaterialTypeExecutionResolver", names);
        Assert.Contains(nameof(ClusterMaterialAssignments), names);
        Assert.DoesNotContain("ClusterDeformStages", names);
        Assert.Contains("ClusterRasterStages", names);
        Assert.Contains("ClusterShadeStages", names);
    }

    [Theory]
    [InlineData(
        "standard.shader.asset",
        "ClusterShade",
        "AlbedoMap",
        "NormalMap",
        "ARMMap")]
    [InlineData(
        "unlit.shader.asset",
        "ClusterShade",
        "AlbedoMap",
        null,
        null)]
    public async Task Cooked_material_entry_contains_template_core_and_typed_material_resources(
        string assetName,
        string entryPoint,
        string texture0,
        string? texture1,
        string? texture2)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders", assetName);
        Shader shader = await ClusterTestFiles.OpenShaderAsync(path);
        CookedShaderEntry entry = shader.RequireEntry(
            SomeEngine.Graphics.ShaderTarget.Spirv,
            entryPoint,
            ShaderStage.Compute);

        ClusterLinkedStageLayout linked = ClusterLinkedStageLayout.Create(
            shader,
            SomeEngine.Graphics.ShaderTarget.Spirv,
            entryPoint);
        string[] names = linked.Resources
            .ToArray()
            .Select(static resource => resource.Name)
            .ToArray();

        Assert.Contains("Uniforms", names);
        Assert.Contains("PixelCoordBuffer", names);
        Assert.Contains("OutputColor", names);
        Assert.Contains("MaterialScalarRegion", names);
        Assert.Contains("MaterialScalarOffsets", names);
        Assert.Contains(texture0, names);
        if (texture1 is not null) Assert.Contains(texture1, names);
        if (texture2 is not null) Assert.Contains(texture2, names);
        Assert.Contains("MaterialSampler", names);
        Assert.DoesNotContain(
            shader.EntryPointAttributes ?? [],
            static attribute => string.Equals(attribute.Name, "MaterialTarget", StringComparison.Ordinal));
        Assert.Single(shader.Metadata?.MaterialScalarLayouts ?? []);
    }

    [Theory]
    [InlineData("ClusterRasterSoftwareImageAtomic64", false)]
    [InlineData("ClusterRasterVertex", false)]
    [InlineData("ClusterShade", true)]
    public async Task Material_resources_only_separate_commands_that_use_them(string entry, bool separates)
    {
        using Shader shader = await ClusterTestFiles.OpenShaderAsync(Path.Combine(AppContext.BaseDirectory,
            "Content", "Shaders", "standard.shader.asset"));
        ClusterLinkedStageLayout layout = ClusterLinkedStageLayout.Create(shader, ShaderTarget.Spirv, entry);
        using var firstTexture = TestTexture();
        using var secondTexture = TestTexture();
        using var first = new StandardMaterial { AlbedoMap = firstTexture };
        using var second = new StandardMaterial { AlbedoMap = secondTexture };
        ClusterMaterialStageBinding[] bindings = layout.Resources.ToArray().Select(resource =>
        {
            MaterialPropertyDefinition slot = first.Properties.FirstOrDefault(slot => slot.Name == resource.Name);
            return new ClusterMaterialStageBinding(slot.Index, resource.Type, slot.Name is not null);
        }).ToArray();
        if (!separates)
        {
            // Exercise a global texture retained by the linked layout but absent
            // from this entry's reflection. Its material value must be irrelevant.
            int unused = Array.FindIndex(layout.Resources.ToArray(), resource =>
                !resource.IsUsed && resource.Type == ResourceBindingType.TextureSrv);
            Assert.True(unused >= 0);
            bindings[unused] = new ClusterMaterialStageBinding(
                first.Properties.Single(slot => slot.Name == nameof(StandardMaterial.AlbedoMap)).Index,
                ResourceBindingType.TextureSrv, true);
        }
        Assert.Equal(separates, !ClusterCommandBindingKey.Create(first, layout, bindings)
            .Equals(ClusterCommandBindingKey.Create(second, layout, bindings)));
    }

    private static SomeEngine.Resources.Texture TestTexture() => SomeEngine.Resources.Texture.Create(
        new SomeEngine.Graphics.TextureDesc(SomeEngine.Graphics.TextureDimension.Texture2D, 1, 1, 1, 1, 1, 1,
            SomeEngine.Graphics.Format.R8UNorm, SomeEngine.Graphics.TextureUsages.Sampled),
        [new TexturePixels(new TextureMipTile { Width = 1, Height = 1 }, new byte[] { 1 })],
        SomeEngine.Graphics.TextureViewDimension.Texture2D);

    private static ClusterMaterialAssignmentSet Assignment(Material material) => new(
        [new ClusterMaterialSequence([material], ClusterTestAssets.StandardVertexEncoding, 0)],
        [material],
        [ClusterTestAssets.StandardVertexEncoding],
        [material],
        [],
        []);
}
