using SomeEngine.ECS.Components;

namespace SomeEngine.Views;

/// <summary>The exception from a view build and the World rebuild frame in which it occurred.</summary>
public struct BuildError : ISparseComponent
{
    public Exception Exception;
    public ulong Frame;
}
