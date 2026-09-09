using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

/// <summary>Writes disk content to a temporary file, then atomically replaces its destination.</summary>
public static class AssetWriter
{
    internal static ulong RawBytesSchema { get; }
        = SomeEngine.Serialization.BinaryFieldKey.FromName("SomeEngine.Assets.RawBytes.v1");

    public static void Write<T>(Asset<T> asset, string path) where T : class
        => WriteAsync(asset, path).AsTask().GetAwaiter().GetResult();

    public static async ValueTask WriteAsync<T>(Asset<T> asset, string path,
        CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!AssetFormat<T>.Current.MatchesPath(path))
            throw new InvalidDataException($"Asset type '{Asset<T>.FileType}' does not accept path '{path}'.");
        cancellationToken.ThrowIfCancellationRequested();
        await using AssetFile file = asset.DuplicateFile();
        await WriteFileAsync(path, file.WriteAsync, cancellationToken).ConfigureAwait(false);
    }

    internal static void Write(BinaryDocumentWriter document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        WriteFileAsync(path, (stream, token) => document.WriteAsync(stream, cancellationToken: token),
            cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    private static async ValueTask WriteFileAsync(string path,
        Func<FileStream, CancellationToken, ValueTask> write, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Asset path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Existing readers continue using the old file; new opens see the new contents.
            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, fullPath);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            throw;
        }
    }
}
