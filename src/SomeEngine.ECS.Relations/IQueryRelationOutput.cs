using SomeEngine.ECS.Queries;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

/// <summary>Callback-scoped pair rows forwarded between ECS bindings before emitting public identities.</summary>
public interface IQueryRelationOutput<TRow> : IRelationOutput<TRow>
{
    static virtual bool ReadsComponentRows => true;

    bool Add(in TRow row, scoped QueryRow first, scoped QueryRow second);
}

/// <summary>A component binding that can reuse the pair rows supplied by a query source.</summary>
public interface IQueryRelation<TRow> : IRelation<TRow>
{
    bool Matches(in TRow row, scoped QueryRow first, scoped QueryRow second);
}

internal ref struct QueryIdentityOutput<TOutput, TRow>(TOutput output) : IQueryRelationOutput<TRow>
    where TOutput : struct, IRelationOutput<TRow>, allows ref struct
{
    public static bool ReadsComponentRows => false;

    public TOutput Value = output;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row) => Value.Add(in row);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Add(in TRow row, scoped QueryRow first, scoped QueryRow second) => Value.Add(in row);
}
