using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

/// <summary>Callback-scoped pair rows forwarded between ECS bindings before emitting public identities.</summary>
public interface IQueryRelationOutput<TRow> : IRelationOutput<TRow>
{
    bool Add(in TRow row, scoped in QueryRow first, scoped in QueryRow second);
}

/// <summary>A component binding that can reuse the pair rows supplied by a query source.</summary>
public interface IQueryRelation<TRow> : IRelation<TRow>
{
    bool Matches(in TRow row, scoped in QueryRow first, scoped in QueryRow second);
}

internal ref struct QueryIdentityOutput<TOutput, TRow>(TOutput output) : IQueryRelationOutput<TRow>
    where TOutput : struct, IRelationOutput<TRow>, allows ref struct
{
    internal TOutput Output = output;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row) => Output.Add(in row);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row, scoped in QueryRow first, scoped in QueryRow second) => Output.Add(in row);
}
