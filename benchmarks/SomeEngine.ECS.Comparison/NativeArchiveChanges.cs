using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class NativeFullArchiveWorld<TValues>
    where TValues : struct, INativeArchiveValues
{
    private struct RebaseVisitor<T>(ulong version, Func<T, ulong, T> update) : INativeArchiveVisitor<T> where T : struct
    {
        public void Visit(ReadOnlySpan<ArchiveIdentity> identities, Span<T> values)
        { for (int i = 0; i < values.Length; i++) values[i] = update(values[i], version); }
    }
    private void Rebase<T>(Func<T, ulong, T> update) where T : struct, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent
    { var visitor = new RebaseVisitor<T>(Version, update); Store.Visit<T, RebaseVisitor<T>>(ref visitor); }
    private void RebaseChanges()
    {
        Rebase<SaveNativeVersions>(static (v, version) => new(version, version));
        Rebase<SaveNativeBuffer>(static (v, version) => { v.Added = v.Written = version; return v; });
        Rebase<ReferenceParent<Entity>>(static (v, version) => { v.Added = v.Written = version; return v; });
        Rebase<ReferenceChildren<Entity>>(static (v, version) => { v.Added = v.Written = version; return v; });
        Rebase<ReferenceRelationEndpoints<Entity>>(static (v, version) => { v.Added = v.Written = version; return v; });
        var sparse = _sparse.Store<SaveSparseRow>();
        for (int i = 0; i < sparse.Count; i++) sparse.Values[i] = new(sparse.Values[i].Value, Version, Version);
    }
}
