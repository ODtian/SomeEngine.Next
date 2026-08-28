using System.Reflection;
using SomeEngine.Render.Instances;

namespace SomeEngine.Render.Tests;

public sealed class RenderInstanceArchitectureTests
{
    [Fact]
    public void Public_instance_api_uses_typed_properties_instead_of_fixed_semantic_channels()
    {
        string[] forbidden =
        [
            "SetColor",
            "SetCustomData",
            "SetWind",
            "SetFade",
            "SetMaterialParameter",
        ];
        MethodInfo[] methods = typeof(RenderInstanceBuffer).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(methods, method => forbidden.Contains(method.Name, StringComparer.Ordinal));
        Assert.Contains(methods, static method =>
            method.Name == nameof(RenderInstanceBuffer.BeginUpdate) &&
            method.ReturnType == typeof(RenderInstanceUpdate));
        Assert.Contains(methods, static method =>
            method.IsGenericMethodDefinition &&
            method.Name == nameof(RenderInstanceBuffer.SetRange));
    }

    [Fact]
    public void Storage_publication_writer_is_not_a_user_facing_export()
    {
        Assembly render = typeof(RenderInstanceBuffer).Assembly;
        Assert.DoesNotContain(render.ExportedTypes, static type =>
            type.Name is "RenderInstanceSet" or "RenderInstanceSetWriter" or "RenderMeshInstanceWriter");
    }

    [Fact]
    public void Instanced_mesh_resource_accepts_an_opaque_instance_source()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(RenderMeshInstanceSet)
                .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            static candidate => candidate.GetParameters().Any(static parameter =>
                parameter.ParameterType == typeof(IRenderInstanceSource)));

        Assert.DoesNotContain(constructor.GetParameters(), static parameter =>
            typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
    }

    [Fact]
    public void Transaction_exposes_typed_range_sparse_and_commit_operations()
    {
        MethodInfo[] methods = typeof(RenderInstanceUpdate).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        Assert.Contains(methods, static method => method.Name == nameof(RenderInstanceUpdate.SetCount));
        Assert.Contains(methods, static method =>
            method.Name == nameof(RenderInstanceUpdate.WriteRange) && method.IsGenericMethodDefinition);
        Assert.Contains(methods, static method =>
            method.Name == nameof(RenderInstanceUpdate.WriteSparse) && method.IsGenericMethodDefinition);
        Assert.Contains(methods, static method => method.Name == nameof(RenderInstanceUpdate.Commit));
    }
}
