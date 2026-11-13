using SomeEngine.Render;
using System.Reflection;

namespace SomeEngine.Render.Tests.Architecture;

public sealed class VsmTileArchitectureChecks
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Fact, Trait("Architecture", "ARCH-26")]
    public void MaterialRenderingUsesItsShaderAndLayer()
    {
        Type material = typeof(SomeEngine.Resources.Material);
        Assert.Null(material.GetProperty("State", Members));
        Assert.Null(material.Assembly.GetType("SomeEngine.Resources.MaterialState"));
        Assert.Null(material.Assembly.GetType("SomeEngine.Resources.SurfaceMode"));
    }

    [Fact, Trait("Architecture", "ARCH-27")]
    public void LayerCompositionReplacesClusterCompositionFiles()
    {
        string repository = FindRepository();
        string[] shaderPatterns = ["cluster_transparency*.slang", "material_decal_cells.slang"];
        foreach (string pattern in shaderPatterns)
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(repository, "assets", "Shaders"), pattern));
        string directory = Path.Combine(repository, "src", "SomeEngine.Render.Cluster.Pipeline");
        string[] compositionFiles = ["Transparency", "TransparencyShaders", "Decals", "Ordered", "BlendMeasurements"];
        foreach (string name in compositionFiles)
            Assert.False(File.Exists(Path.Combine(directory, $"ClusterPipelineSystem.{name}.cs")), $"ARCH-27: ClusterPipelineSystem.{name}.cs");
        Assert.Empty(Directory.EnumerateFiles(directory, "ClusterStencil.cs", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(directory, "ClusterBlend*.cs", SearchOption.AllDirectories));
    }

    [Fact, Trait("Architecture", "ARCH-27")]
    public void ClusterOptionsDescribeGeometryWork()
    {
        Type options = Assembly.Load("SomeEngine.Render.Cluster.Pipeline").GetType("SomeEngine.Render.Cluster.Pipeline.ClusterPipelineOptions", throwOnError: true)!;
        Assert.DoesNotContain(options.GetMembers(Members), member => DataName(member.Name).StartsWith("Blend", StringComparison.Ordinal));
    }

    [Fact, Trait("Architecture", "ARCH-31")]
    public void ShadowPageCacheIdentityUsesTheLightRange()
    {
        Assert.DoesNotContain(typeof(Shadows.VirtualShadowPageMetadata).GetFields(Members), field => field.Name == "ViewIndex");
    }

    [Fact, Trait("Architecture", "ARCH-35")]
    public void LayerAndDecalTypesUseTheirOwnGridData()
    {
        Assembly features = typeof(Features.Lighting.LightingFeature).Assembly;
        foreach (Type type in features.GetTypes().Where(type => type.Namespace is { } space
            && (space.Contains(".Layers", StringComparison.Ordinal) || space.Contains(".Decals", StringComparison.Ordinal))
            || type.Name is "LayerComposition" or "MaterialDecalBinning"))
        {
            foreach (FieldInfo field in type.GetFields(Members))
                Assert.False(UsesLightingGrid(field.FieldType), $"ARCH-35: {type.FullName}.{field.Name}");
            foreach (PropertyInfo property in type.GetProperties(Members))
                Assert.False(UsesLightingGrid(property.PropertyType), $"ARCH-35: {type.FullName}.{property.Name}");
            foreach (MethodInfo method in type.GetMethods(Members))
            {
                Assert.False(UsesLightingGrid(method.ReturnType), $"ARCH-35: {type.FullName}.{method.Name}");
                foreach (ParameterInfo parameter in method.GetParameters())
                    Assert.False(UsesLightingGrid(parameter.ParameterType), $"ARCH-35: {type.FullName}.{method.Name}({parameter.Name})");
            }
        }
    }

    private static bool UsesLightingGrid(Type type) => type.Name is "ClusteredLightGridParameters" or "LightingFeature" or "LightingViews"
        || type.HasElementType && UsesLightingGrid(type.GetElementType()!)
        || type.IsGenericType && type.GetGenericArguments().Any(UsesLightingGrid);

    private static string DataName(string name)
    {
        int end = name.IndexOf('>');
        return name.StartsWith('<') && end > 0 ? name[1..end] : name;
    }

    private static string FindRepository()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory!.FullName;
    }
}
