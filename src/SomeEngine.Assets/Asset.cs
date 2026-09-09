using System.ComponentModel;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets;

/// <summary>Opened disk content for a domain type. It never contains an instance of that type.</summary>
public sealed class Asset<T> : IDisposable, IAsyncDisposable where T : class
{
    private readonly object _gate = new();
    private AssetFile? _file;

    internal Asset(AssetFile file) => _file = file;

    public AssetId<T> Id => new(File.Entry.AssetGuid);
    public Guid Version => File.Entry.Version;
    public string Name => File.Entry.Name;
    public IReadOnlyList<Guid> Dependencies => File.Entry.Dependencies;
    public static string FileType => AssetFormat<T>.Current.AssetType;
    public static string PathSuffix => AssetFormat<T>.Current.PathSuffix;
    public static ulong SchemaHash => AssetFormat<T>.Current.WireType.SchemaFingerprint;

    internal AssetFile File => Volatile.Read(ref _file)
        ?? throw new ObjectDisposedException(nameof(Asset<T>));

    // These methods are emitted into the assembly declaring the file fields. They are not a
    // second asset API: only generated code knows the associated concrete field type.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RegisterGenerated<TFields>(
        string assetType,
        string pathSuffix,
        Func<TFields, Guid> getId,
        Func<TFields, string> getName,
        Func<TFields, string, IReadOnlyList<Guid>> getDependencies,
        Func<TFields, BinaryDocumentWriter> createWriter,
        Func<TFields, TFields> copyFields)
        where TFields : class, IBinaryContract<TFields>
    {
        AssetFormat<T>.Register(new AssetFormat<T>(
            assetType, pathSuffix,
            new(TFields.TypeId, TFields.SchemaFingerprint, TFields.Compatibility, TFields.SchemaEpoch),
            async (source, entry, limits, ct) =>
            {
                BinaryDocument<TFields> document = await BinaryDocument<TFields>.OpenAsync(
                    source, ownsSource: true, limits, ct).ConfigureAwait(false);
                try
                {
                    TFields fields = document.Root;
                    Guid id = getId(fields);
                    if (id == Guid.Empty || (entry.AssetGuid != Guid.Empty && id != entry.AssetGuid))
                        throw new InvalidDataException($"Asset {entry.AssetGuid} root declares GUID {id}.");
                    return new AssetFile<TFields>(
                        entry with
                        {
                            AssetGuid = id,
                            Version = document.Generation,
                            Name = getName(fields),
                            Dependencies = Array.AsReadOnly(getDependencies(fields, entry.Path).ToArray()),
                        }, fields, document, createWriter, copyFields);
                }
                catch
                {
                    await document.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }));
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public static Asset<T> CreateGenerated<TFields>(
        TFields fields,
        Guid id,
        string name,
        IReadOnlyList<Guid> dependencies,
        Func<TFields, BinaryDocumentWriter> createWriter,
        Func<TFields, TFields> copyFields)
        where TFields : class, IBinaryContract<TFields>
    {
        AssetFormat<T> format = AssetFormat<T>.Current;
        if (TFields.TypeId != format.WireType.TypeId)
            throw new ArgumentException("The file fields do not belong to this asset type.", nameof(fields));
        return new Asset<T>(new AssetFile<TFields>(
            new AssetEntry(id, format.AssetType, format.WireType.SchemaFingerprint, Guid.Empty)
            { Name = name, Dependencies = Array.AsReadOnly(dependencies.ToArray()) },
            copyFields(fields), null, createWriter, copyFields));
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public TFields GetFieldsGenerated<TFields>() where TFields : class, IBinaryContract<TFields>
        => ((AssetFile<TFields>)File).CopyFields();

    internal AssetFile DuplicateFile()
    {
        lock (_gate)
            return File.Duplicate();
    }

    /// <summary>Generated domain code obtains its own concrete document access for streaming.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public BinaryDocument<TFields>? OpenDocumentGenerated<TFields>()
        where TFields : class, IBinaryContract<TFields>
    {
        lock (_gate)
            return ((AssetFile<TFields>)File).Document?.Duplicate();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        AssetFile? file;
        lock (_gate)
            file = Interlocked.Exchange(ref _file, null);
        return file?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

internal abstract class AssetFile(AssetEntry entry) : IAsyncDisposable
{
    internal AssetEntry Entry { get; } = entry;
    internal abstract long RootByteLength { get; }
    internal abstract AssetFile Duplicate();
    internal abstract ValueTask WriteAsync(FileStream destination, CancellationToken cancellationToken);
    public abstract ValueTask DisposeAsync();
}

internal sealed class AssetFile<TFields>(
    AssetEntry entry,
    TFields fields,
    BinaryDocument<TFields>? document,
    Func<TFields, BinaryDocumentWriter> createWriter,
    Func<TFields, TFields> copyFields) : AssetFile(entry)
    where TFields : class, IBinaryContract<TFields>
{
    internal TFields Fields { get; } = fields;
    internal BinaryDocument<TFields>? Document { get; } = document;
    internal override long RootByteLength => Document?.RootByteLength ?? 0;
    internal TFields CopyFields() => copyFields(Fields);

    internal override AssetFile Duplicate()
        => new AssetFile<TFields>(Entry, Fields, Document?.Duplicate(), createWriter, copyFields);

    internal override async ValueTask WriteAsync(FileStream destination, CancellationToken cancellationToken)
    {
        if (Document is not null)
        {
            await Document.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return;
        }
        BinaryDocumentWriter writer = createWriter(CopyFields())
            ?? throw new InvalidDataException("The asset encoder returned no document.");
        if (writer.RootDescriptor != new BinaryWireTypeDescriptor(
            TFields.TypeId, TFields.SchemaFingerprint, TFields.Compatibility, TFields.SchemaEpoch))
            throw new InvalidDataException("The asset encoder returned a different file format.");
        await writer.WriteAsync(destination, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync() => Document?.DisposeAsync() ?? ValueTask.CompletedTask;
}
