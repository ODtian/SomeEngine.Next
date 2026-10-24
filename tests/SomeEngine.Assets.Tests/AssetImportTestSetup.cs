namespace SomeEngine.Assets.Importing;

internal static class AssetImportTestSetup
{
    internal static void EnableImporter<TSource, TAsset>(this ImportContext context, Guid id,
        IReadOnlyList<Guid> blocks, Func<TSource, ImportContext, ImportOutput, CancellationToken, ValueTask> convert)
        where TSource : class where TAsset : Asset =>
        context.EnableImporter<TSource, TAsset>(new TestImporter<TSource>(id, blocks, convert));

    private sealed class TestImporter<TSource>(Guid id, IReadOnlyList<Guid> blocks,
        Func<TSource, ImportContext, ImportOutput, CancellationToken, ValueTask> convert) : IAssetImporter<TSource>
        where TSource : class
    {
        public Guid Id => id;
        public IReadOnlyList<Guid> ProducedBlocks => blocks;
        public ValueTask ImportAsync(TSource source, ImportContext context, ImportOutput output,
            CancellationToken cancellationToken = default) => convert(source, context, output, cancellationToken);
    }
}
