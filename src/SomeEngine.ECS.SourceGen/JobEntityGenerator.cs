using Microsoft.CodeAnalysis;

namespace SomeEngine.ECS.SourceGen;

/// <summary>Uses the component and chunk job generator through the existing analyzer entry.</summary>
public sealed class JobEntityGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context) => new EcsGenerator().Initialize(context);
}