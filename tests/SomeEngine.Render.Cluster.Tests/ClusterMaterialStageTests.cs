using System.Reflection;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterMaterialStageTests
{
    [Fact]
    public void Structurally_identical_assignment_preserves_publication_identity_and_version()
    {
        var assignments = new ClusterMaterialAssignments();
        var material = new Material { Name = "test" };
        ClusterMaterialAssignmentSet first = Assignment(material);
        ClusterMaterialAssignmentSet identical = Assignment(material);

        assignments.Publish(first);
        assignments.Publish(identical);

        Assert.Same(first, assignments.Current);
        Assert.Equal(1UL, first.TopologyVersion);
        Assert.Equal(0UL, identical.TopologyVersion);
        Assert.Same(material, first.Slots.Span[0]);

        var replacement = new Material { Name = "replacement" };
        ClusterMaterialAssignmentSet changed = Assignment(replacement);
        assignments.Publish(changed);

        Assert.Same(changed, assignments.Current);
        Assert.Equal(2UL, changed.TopologyVersion);
    }

    [Fact]
    public void Stage_compatibility_keys_are_independent_and_never_contain_material_values()
    {
        Assert.NotEqual(typeof(ClusterDeformCompatibilityKey), typeof(ClusterRasterCompatibilityKey));
        Assert.NotEqual(typeof(ClusterRasterCompatibilityKey), typeof(ClusterShadeCompatibilityKey));

        Type[] shadeFields = typeof(ClusterShadeCompatibilityKey)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(static field => field.FieldType)
            .ToArray();
        Assert.DoesNotContain(typeof(Material), shadeFields);
        Assert.DoesNotContain(shadeFields, static type =>
            type.Name.Contains("Descriptor", StringComparison.Ordinal) ||
            type.Name.Contains("Binding", StringComparison.Ordinal));
    }

    [Fact]
    public void Material_deform_compatibility_depends_only_on_the_final_shader_entry()
    {
        AssetGuid shader = AssetGuid.New();
        Material first = DeformMaterial(shader, "CSDeformWave");
        Material second = DeformMaterial(shader, "CSDeformWave");
        second.Scalars =
        [
            new ScalarParam
            {
                Name = "Amplitude",
                Value = new ParamValue(new FloatVal { V = 3.0f }),
            },
        ];
        Material differentEntry = DeformMaterial(shader, "CSDeformStatic");

        Assert.Equal(
            first.RequireDeformCompatibility(),
            second.RequireDeformCompatibility());
        Assert.NotEqual(
            first.RequireDeformCompatibility(),
            differentEntry.RequireDeformCompatibility());
    }

    [Fact]
    public void Different_materials_can_share_one_shade_compatibility_key()
    {
        CookedShaderProgram cooked = CookedShaderTestProgram.Create();
        var keyForFirst = new ClusterShadeCompatibilityKey(
            cooked.Program!,
            cooked.Program!.EntryPoints[0],
            ClusterShadeStages.Target);
        var keyForSecond = new ClusterShadeCompatibilityKey(
            cooked.Program,
            cooked.Program.EntryPoints[0],
            ClusterShadeStages.Target);

        var first = new Material { Name = "first" };
        var second = new Material { Name = "second" };
        var compatibility = new Dictionary<Material, ClusterShadeCompatibilityKey>
        {
            [first] = keyForFirst,
            [second] = keyForSecond,
        };

        Assert.NotSame(first, second);
        Assert.Equal(compatibility[first], compatibility[second]);
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
    public void Descriptor_and_scalar_value_changes_do_not_change_a_stage_key()
    {
        CookedShaderProgram cooked = CookedShaderTestProgram.Create();
        var key = new ClusterShadeCompatibilityKey(
            cooked.Program!,
            cooked.Program!.EntryPoints[0],
            ClusterShadeStages.Target);
        var material = new MutableMaterial();

        ulong initialRevision = material.Revision;
        material.Factor = 0.75f;
        material.Sampler = new SamplerDesc(
            FilterType.Nearest,
            FilterType.Linear,
            FilterType.Nearest,
            AddressType.ClampToEdge,
            AddressType.Repeat,
            AddressType.MirrorRepeat);
        var afterValuesChanged = new ClusterShadeCompatibilityKey(
            cooked.Program,
            cooked.Program.EntryPoints[0],
            ClusterShadeStages.Target);

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
        CookedShaderProgram cooked = CookedShaderTestProgram.Create();
        string entryPoint = cooked.Program!.EntryPoints[0].Name;
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
            ClusterGeometryContracts.RequireConservativeBounds(
                shader,
                entryPoint,
                "test geometry").ToString());
    }

    [Fact]
    public void Missing_conservative_bounds_declaration_fails_during_stage_resolution()
    {
        CookedShaderProgram cooked = CookedShaderTestProgram.Create();
        string entryPoint = cooked.Program!.EntryPoints[0].Name;
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
        Assert.Contains("ClusterDeformStages", names);
        Assert.Contains("ClusterRasterStages", names);
        Assert.Contains("ClusterShadeStages", names);
    }

    [Theory]
    [InlineData(
        "cluster_shade_material.shader.asset",
        "CSMaterialShadeCached",
        "AlbedoMap",
        "NormalMap",
        "ARMMap")]
    [InlineData(
        "cluster_shade_unlit.shader.asset",
        "CSUnlitShadeCached",
        "AlbedoMap",
        null,
        null)]
    public async Task Cooked_material_entry_contains_template_core_and_user_resources(
        string assetName,
        string entryPoint,
        string texture0,
        string? texture1,
        string? texture2)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Content", "Shaders", assetName);
        Shader shader = await Shader.ReadAsync(path);
        CookedShaderEntry entry = shader.RequireEntry(
            SomeEngine.Graphics.ShaderTarget.Spirv,
            entryPoint,
            ShaderStage.Compute);

        ClusterLinkedStageLayout linked = ClusterLinkedStageLayout.Create(
            entry.Program.Layout.GetGlobalParamsVarLayout());
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
        Assert.Contains(shader.EntryPointAttributes ?? [], attribute =>
            string.Equals(attribute.Name, "MaterialTarget", StringComparison.Ordinal) &&
            attribute.Args?.Contains(ClusterShadeStages.Target, StringComparer.Ordinal) == true &&
            shader.TryEntry(attribute, entryPoint, out _));
        Assert.Single(shader.Metadata?.MaterialScalarLayouts ?? []);
    }

    private static ClusterMaterialAssignmentSet Assignment(Material material) => new(
        [new ClusterMaterialSequence([material], ClusterTestAssets.StandardVertexEncoding, 0)],
        [material],
        [ClusterTestAssets.StandardVertexEncoding],
        [material],
        [],
        []);

    private static Material DeformMaterial(AssetGuid shader, string entryPoint) => new()
    {
        Name = entryPoint,
        Passes =
        [
            new PassEntry
            {
                Tags = [new TagEntry { Name = Material.DeformPassTag }],
                Shader = new ShaderRef
                {
                    AssetGuid = shader.ToFlatString(),
                    EntryPoint = entryPoint,
                    Stage = ShaderStage.Compute,
                },
            },
        ],
    };

    private sealed class MutableMaterial : Material
    {
        internal float Factor
        {
            get => GetSlot<float>(0);
            set => SetSlot(0, value);
        }

        internal SamplerDesc Sampler
        {
            get => GetSlot<SamplerDesc>(1);
            set => SetSlot(1, value);
        }

        protected override MaterialSlotDefinition[] CreateSlots() =>
        [
            new MaterialSlotDefinition(0, nameof(Factor), typeof(float)),
            new MaterialSlotDefinition(1, nameof(Sampler), typeof(SamplerDesc)),
        ];
    }
}
