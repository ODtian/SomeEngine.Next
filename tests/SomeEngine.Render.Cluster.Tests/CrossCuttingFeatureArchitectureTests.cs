using System.Reflection;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster.Pipeline;
using SomeEngine.Render.Features.Debugging;
using SomeEngine.Render.Features.GeometryExport;
using SomeEngine.Render.Features.Lighting;
using SomeEngine.Render.Features.Motion;
using SomeEngine.Render.Features.Shadows;
using SomeEngine.Render.Features.Temporal;
using SomeEngine.Render.Features.Translucency;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class CrossCuttingFeatureArchitectureTests
{
    [Fact]
    public void Cross_cutting_features_have_independent_owners_and_lifecycles()
    {
        Type[] features =
        [
            typeof(LightingFeature),
            typeof(TemporalFeature),
            typeof(TranslucencyFeature),
            typeof(DebugFeature),
            typeof(VirtualShadowFeature),
            typeof(StreamOutputFeature),
            typeof(RayTracingGeometryFeature),
        ];
        Assembly featureAssembly = typeof(LightingFeature).Assembly;

        Assert.All(features, type =>
        {
            Assert.Equal(featureAssembly, type.Assembly);
            Assert.True(type.IsPublic);
            Assert.True(type.IsSealed);
            Assert.True(typeof(IDisposable).IsAssignableFrom(type));
            Assert.NotEmpty(type.GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        });

        Assembly clusterAssembly = typeof(ClusterPipelineSystem).Assembly;
        Assert.DoesNotContain(
            clusterAssembly.GetReferencedAssemblies(),
            reference => reference.Name == featureAssembly.GetName().Name);
        Assert.Contains(
            featureAssembly.GetReferencedAssemblies(),
            reference => reference.Name == clusterAssembly.GetName().Name);
    }

    [Fact]
    public void Cluster_core_abi_contains_only_geometry_roles_and_outputs()
    {
        Assert.Equal(
        [
            "None",
            "BvhTraversal",
            "CullReset",
            "CullPhaseOne",
            "CullPhaseTwo",
            "RasterBinningReset",
            "RasterBinningCount",
            "RasterBinningReserve",
            "RasterBinningScatter",
            "SoftwareVisibilityRaster",
            "HardwareVisibilityRaster",
            "HiZInitialize",
            "HiZReduce",
            "HiZReducePair",
            "MaterialBinningReset",
            "MaterialBinningCount",
            "MaterialBinningReserve",
            "MaterialBinningScatter",
            "VisibilityResolve",
            "TessellationBinningReset",
            "TessellationBinningCount",
            "TessellationBinningReserve",
            "TessellationBinningScatter",
            "BvhTraversalInitialize",
            "BvhDeferredResume",
            "VisibilityClearImage",
            "VisibilityClearBuffer",
            "VisibilityResolveImage",
            "VisibilityResolveBuffer",
            "SceneBounds",
        ],
            Enum.GetNames<ClusterShaderOperationRole>());

        string[] outputProperties = typeof(ClusterPipelineOutputs)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "Depth",
            "Geometry",
            "Height",
            "IsValid",
            "MaterialColor",
            "MotionVectors",
            "ViewCount",
            "Visibility",
            "Width",
        ],
            outputProperties);

        Assert.DoesNotContain(
            typeof(ClusterPipelineSystem).GetMembers(BindingFlags.Instance | BindingFlags.Public),
            static member => MemberTypes(member).Any(static type =>
                type.Namespace?.StartsWith("SomeEngine.Render.Features", StringComparison.Ordinal) == true));
    }

    [Fact]
    public void Lighting_is_resolved_once_through_the_generic_stage_resource_boundary()
    {
        Assert.Contains(
            typeof(LightingFeature).GetInterfaces(),
            static contract => contract == typeof(IClusterStageResourceProvider));

        Type invocation = typeof(ClusterPipelineSystem).Assembly.GetType(
            "SomeEngine.Render.Cluster.Pipeline.ClusterShadeInvocation",
            throwOnError: true)!;
        PropertyInfo binders = invocation.GetProperty(
            "FeatureResourceBinders",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(ClusterStageResourceBinder[]), binders.PropertyType);
        Assert.DoesNotContain(
            invocation.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static property => property.PropertyType == typeof(IClusterStageResourceProvider));
    }

    [Fact]
    public void Feature_outputs_are_explicit_and_invalid_until_backed_by_graph_resources()
    {
        Assert.False(default(LightingFeatureResources).IsValid);
        Assert.False(default(MotionFeatureOutput).IsValid);
        Assert.False(default(TemporalFeatureOutput).IsValid);
        Assert.False(default(TranslucencyFeatureOutput).IsValid);
        Assert.False(default(DebugFeatureOutput).IsValid);
        Assert.False(default(StreamOutputFeatureOutput).IsValid);
        Assert.False(default(RayTracingGeometryFeatureOutput).IsValid);
        Assert.Equal(28u, RayTracingGeometryFeature.RecordStride);
    }

    private static IEnumerable<Type> MemberTypes(MemberInfo member) => member switch
    {
        MethodInfo method => method.GetParameters()
            .Select(static parameter => parameter.ParameterType)
            .Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        EventInfo @event when @event.EventHandlerType is not null => [@event.EventHandlerType],
        _ => [],
    };
}
