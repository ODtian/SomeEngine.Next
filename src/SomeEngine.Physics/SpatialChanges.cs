using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.Physics;

/// <summary>Executes within the callback that supplies one concrete chunk-change reader.</summary>
public interface ISpatialChangeExecution
{
    void Execute<TChanges>(TChanges changes)
        where TChanges : struct, IQueryRowChanges, allows ref struct;
}

internal readonly struct UnchangedQueryRows : IQueryRowChanges
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasChanged(int row) => false;
}

internal readonly ref struct SpatialRowChanges<TFirst, TSecond> : IQueryRowChanges
    where TFirst : struct, IQueryRowChanges, allows ref struct
    where TSecond : struct, IQueryRowChanges, allows ref struct
{
    private readonly TFirst _first;
    private readonly TSecond _second;

    public SpatialRowChanges(TFirst first, TSecond second)
    {
        _first = first;
        _second = second;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasChanged(int row) => _first.HasChanged(row) || _second.HasChanged(row);
}

internal readonly ref struct ReadSecondSpatialChanges<TGeometry, TSecond, TSecondResults, TExecution> : ISpatialChangeExecution
    where TGeometry : struct
    where TSecond : struct, ISpatialFilter<TSecondResults>
    where TSecondResults : unmanaged
    where TExecution : struct, ISpatialChangeExecution, allows ref struct
{
    private readonly TSecond _second;
    private readonly QueryChunkView _chunk;
    private readonly TExecution _execution;

    public ReadSecondSpatialChanges(TSecond second, QueryChunkView chunk, TExecution execution)
    {
        _second = second;
        _chunk = chunk;
        _execution = execution;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Execute<TChanges>(TChanges changes)
        where TChanges : struct, IQueryRowChanges, allows ref struct =>
        _second.ReadChanges<TGeometry, CombineSpatialChanges<TChanges, TExecution>>(_chunk, new(changes, _execution));
}

internal readonly ref struct CombineSpatialChanges<TFirst, TExecution> : ISpatialChangeExecution
    where TFirst : struct, IQueryRowChanges, allows ref struct
    where TExecution : struct, ISpatialChangeExecution, allows ref struct
{
    private readonly TFirst _first;
    private readonly TExecution _execution;

    public CombineSpatialChanges(TFirst first, TExecution execution)
    {
        _first = first;
        _execution = execution;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Execute<TChanges>(TChanges changes)
        where TChanges : struct, IQueryRowChanges, allows ref struct =>
        _execution.Execute(new SpatialRowChanges<TFirst, TChanges>(_first, changes));
}
