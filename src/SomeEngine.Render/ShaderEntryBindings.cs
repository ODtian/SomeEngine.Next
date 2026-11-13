using SlangShaderSharp;
using SomeEngine.Graphics;
using SomeEngine.Render.Assets;
using SomeEngine.RenderGraph;
using SomeEngine.Resources;

namespace SomeEngine.Render;

/// <summary>Resolves the resources read by one compute entry and binds unused global declarations as null.</summary>
public static class ShaderEntryBindings
{
    public static GraphParameterResourceBinding[] Resolve(Shader shader, LinkedComputePipeline pipeline,
        Func<string, ResourceBindingType, GraphParameterResourceBinding?> resolve)
    {
        VariableLayoutReflection layout = pipeline.ParameterLayout;
        Resource[] resources = ShaderResourceLayout.Resolve<Resource>(layout,
            static (name, type) => new Resource(name, type));
        TypeLayoutReflection contents = layout.TypeLayout.UnwrapArray();
        if (contents.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            contents = contents.ElementTypeLayout.UnwrapArray();
        int target = shader.RequireSlangTarget(pipeline.Pipeline.Device.Capabilities.CompileTarget);
        var bindings = new GraphParameterResourceBinding[resources.Length];
        int index = 0;
        for (nint range = 0; range < contents.BindingRangeCount; range++)
        {
            nint count = contents.GetBindingRangeDescriptorRangeCount(range);
            if (count == 0) continue;
            nint set = contents.GetBindingRangeDescriptorSetIndex(range);
            nint first = contents.GetBindingRangeFirstDescriptorRangeIndex(range);
            for (nint relative = 0; relative < count; relative++)
            {
                nint descriptor = first + relative;
                nint elements = contents.GetDescriptorSetDescriptorRangeDescriptorCount(set, descriptor);
                for (nint element = 0; element < elements; element++, index++)
                {
                    SlangDescriptorLocation location = SlangDescriptorLocation.FromLayout(layout, contents, set, descriptor, element);
                    Resource resource = resources[index];
                    bool used = shader.IsSlangParameterLocationUsed(target, pipeline.EntryPoint.Name,
                        location.Category, (nuint)location.Space, (nuint)location.Register);
                    bindings[index] = used
                        ? resolve(resource.Name, resource.Type) ?? throw new InvalidDataException(
                            $"Compute entry resource '{resource.Name}' has no installed binding.")
                        : GraphParameterResourceBinding.Null(resource.Type);
                }
            }
        }
        return bindings;
    }

    private readonly record struct Resource(string Name, ResourceBindingType Type);
}
