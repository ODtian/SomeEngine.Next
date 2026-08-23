using System.Reflection;
using SomeEngine.Assets;
using SomeEngine.Graphics;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterPipelineArchitectureTests
{
    [Fact]
    public void Executable_pipeline_types_share_the_program_and_rhi_pipeline_owner()
    {
        Assert.True(typeof(ClusterPipeline).IsAbstract);
        Assert.True(typeof(ClusterPipeline).IsAssignableFrom(typeof(ClusterComputePipeline)));
        Assert.True(typeof(ClusterPipeline).IsAssignableFrom(typeof(ClusterRasterPipeline)));

        string[] ownedProperties = typeof(ClusterPipeline)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["Pipeline", "Program"], ownedProperties);
    }

    [Fact]
    public void Fixed_pipeline_set_retains_pipelines_but_not_asset_reads_or_material_cache()
    {
        FieldInfo[] fields = typeof(ClusterPipelineSet).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Contains(fields, field =>
            field.FieldType == typeof(List<ClusterPipeline>));
        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(AssetLoader) ||
            IsAssetRead(field.FieldType) ||
            field.FieldType.Name.Contains("Dictionary", StringComparison.Ordinal));
    }

    [Fact]
    public void Frame_resource_value_does_not_own_the_reusable_scratch_arrays()
    {
        Type renderer = typeof(ClusterRendererSystem);
        Type? frameResources = renderer.GetNestedType(
            "FrameResources",
            BindingFlags.NonPublic);
        Type? scratch = renderer.GetNestedType(
            "FrameResourceScratch",
            BindingFlags.NonPublic);
        Assert.NotNull(frameResources);
        Assert.NotNull(scratch);

        Assert.False(typeof(IDisposable).IsAssignableFrom(frameResources));
        Assert.DoesNotContain(
            frameResources.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            static field => field.FieldType.IsArray);
        Assert.Contains(
            scratch.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            static field => field.FieldType.IsArray);
        Assert.Contains(
            renderer.GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == scratch);
    }

    [Fact]
    public void Pipeline_assembly_has_no_legacy_stage_or_wishful_type_names()
    {
        HashSet<string> names = typeof(ClusterRendererSystem).Assembly
            .GetTypes()
            .Select(static type => type.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("ClusterShaderLibrary", names);
        Assert.DoesNotContain("ClusterComputeShader", names);
        Assert.DoesNotContain("ClusterRasterShader", names);
        Assert.DoesNotContain("ClusterMaterialRuntime", names);
        Assert.DoesNotContain("ClusterMaterialState", names);
        Assert.DoesNotContain("ClusterRenderTargetSource", names);
    }

    [Fact]
    public void History_endpoints_and_frame_metrics_are_fixed_values_not_per_commit_objects()
    {
        FieldInfo[] historyFields = typeof(ClusterRenderHistory).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo[] endpoints = historyFields
            .Where(static field => field.Name.EndsWith("Endpoints", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, endpoints.Length);
        Assert.All(endpoints, static field =>
            Assert.Equal(typeof(TextureBoundaryState[]), field.FieldType));
        Assert.DoesNotContain(historyFields, static field =>
            field.FieldType == typeof(QueueCompletion[][]));
        Assert.True(typeof(ClusterFrameMetrics).IsValueType);
        Assert.True(typeof(ClusterMaterialSequence).IsValueType);
    }

    private static bool IsAssetRead(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(AssetRead<>);
}
