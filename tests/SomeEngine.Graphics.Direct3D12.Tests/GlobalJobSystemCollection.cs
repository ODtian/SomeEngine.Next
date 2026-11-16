using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

// These classes replace the process-wide JobSystem scheduler that other classes use for shared pipeline
// creation, so they run after the parallel classes and never beside them.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalJobSystemCollection
{
    public const string Name = "Global job system";
}
