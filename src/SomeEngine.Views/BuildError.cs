using SomeEngine.ECS.Components;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>The exception from a view calculation and the World rebuild frame in which it occurred.</summary>
public struct BuildError : ISparseComponent
{
    public Exception Exception;
    public ulong Frame;
}

internal static class BuildErrors
{
    internal static void Record(World world, Entity entity, Exception exception, ulong frame, Type? viewType = null)
    {
        var error = new BuildError { Exception = exception, Frame = frame };
        if (world.HasSparse<BuildError>(entity))
            world.ReplaceSparse(entity, in error);
        else
            world.AddSparse(entity, in error);
        DevelopmentCheck.Report($"{(viewType is null ? "Calculation" : $"View {viewType.Name}")} on {entity} " +
            $"failed in frame {frame}: {exception}");
    }
}
