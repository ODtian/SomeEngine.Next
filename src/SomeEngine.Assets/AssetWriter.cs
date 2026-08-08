using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

/// <summary>
/// Writes one exact-schema asset atomically through its source-generated closed descriptor.
/// </summary>
public static class AssetWriter
{
    public static void Write<T>(T asset, string path)
        where T : class
        => _ = WriteAndDescribe(asset, path);

    internal static AssetDescription WriteAndDescribe<T>(T asset, string path)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(asset);
        AssetTypeDescriptor<T> descriptor = AssetType<T>.Descriptor;
        BinaryDocumentWriter document = descriptor.CreateWriter(asset)
            ?? throw new InvalidOperationException("The generated asset writer returned no document.");
        ValidateRootContract(descriptor, document);
        AssetDescription info = AssetMetadata.Describe(asset, path);
        Write(document, path);
        return info;
    }

    internal static void ValidateRootContract<T>(
        AssetTypeDescriptor<T> descriptor,
        BinaryDocumentWriter document)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(document);
        SomeEngine.Serialization.Containers.BinaryWireTypeDescriptor expectedRoot = descriptor.WireType;
        if (document.RootDescriptor != expectedRoot)
        {
            throw new InvalidDataException(
                $"Asset writer for '{descriptor.AssetType}' returned root contract " +
                $"'{document.RootDescriptor.TypeId}' instead of exact contract '{expectedRoot.TypeId}'.");
        }
    }

    internal static void Write(BinaryDocumentWriter document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("Asset path has no parent directory.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                document.WriteAsync(stream).AsTask().GetAwaiter().GetResult();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }
}
