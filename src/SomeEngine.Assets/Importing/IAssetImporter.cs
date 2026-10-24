namespace SomeEngine.Assets.Importing;

/// <summary>Converts one native source object into the declared blocks of an asset.</summary>
/// <remarks>Settings must be immutable and implement value equality for their complete import semantics.</remarks>
public interface IAssetImporter<TSource, TSettings> where TSource : class where TSettings : notnull
{
    ValueTask ImportAsync(TSource source, TSettings settings, ImportContext context,
        ImportOutput output, CancellationToken cancellationToken);
}
