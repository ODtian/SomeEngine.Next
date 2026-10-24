using System.Collections.Generic;

namespace SomeEngine.Assets;

public sealed class ImportInputs
{
    public required string InputHash { get; init; }
    public required IReadOnlyList<FileDependency> Dependencies { get; init; }
    public required uint ImporterVersion { get; init; }
}

