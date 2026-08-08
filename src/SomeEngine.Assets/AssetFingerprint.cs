using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using SomeEngine.Serialization;

namespace SomeEngine.Assets;

public static class AssetFingerprint
{
    public static AssetImportFingerprint Create(
        IReadOnlyList<DependencyEntryData> dependencies,
        uint importerVersion,
        params string[] extraParts)
    {
        return new AssetImportFingerprint
        {
            ContentFingerprint = ComputeContentFingerprint(dependencies, importerVersion, extraParts),
            Dependencies = dependencies
                .OrderBy(static dependency => dependency.RelativePath, StringComparer.Ordinal)
                .ToArray(),
            ImporterVersion = importerVersion,
        };
    }

    public static DependencyEntryData? TryFileDep(string projectRoot, string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        if (!File.Exists(fullPath))
        {
            return null;
        }

        return new DependencyEntryData
        {
            RelativePath = MakeRelativePath(projectRoot, fullPath),
            ContentHash = FileSha256(fullPath),
        };
    }

    public static string ComputeContentFingerprint(
        IReadOnlyList<DependencyEntryData> dependencies,
        uint importerVersion,
        params string[] extraParts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (DependencyEntryData dependency in dependencies.OrderBy(static x => x.RelativePath, StringComparer.Ordinal))
        {
            AppendUtf8(hash, dependency.RelativePath);
            AppendUtf8(hash, ":");
            AppendUtf8(hash, dependency.ContentHash);
            AppendUtf8(hash, "\n");
        }

        foreach (string part in extraParts)
        {
            AppendUtf8(hash, "||");
            AppendUtf8(hash, part);
        }

        AppendUtf8(hash, "||");
        Span<char> version = stackalloc char[10];
        if (!importerVersion.TryFormat(version, out int written))
            throw new InvalidOperationException("Unable to format the importer version.");
        AppendUtf8(hash, version[..written]);
        return CompleteHex(hash);
    }

    public static string FileSha256(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
                hash.AppendData(buffer.AsSpan(0, read));
            return CompleteHex(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static string ComputeSha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendUtf8(hash, value);
        return CompleteHex(hash);
    }

    private static void AppendUtf8(IncrementalHash hash, ReadOnlySpan<char> value)
    {
        Encoder encoder = Encoding.UTF8.GetEncoder();
        Span<byte> buffer = stackalloc byte[1024];
        do
        {
            encoder.Convert(
                value,
                buffer,
                flush: true,
                out int charsUsed,
                out int bytesUsed,
                out bool completed);
            if (bytesUsed != 0)
                hash.AppendData(buffer[..bytesUsed]);
            value = value[charsUsed..];
            if (completed)
                return;
            if (charsUsed == 0 && bytesUsed == 0)
                throw new InvalidOperationException("UTF-8 fingerprint encoder made no progress.");
        }
        while (true);
    }

    private static string CompleteHex(IncrementalHash hash)
    {
        Digest256 digest = Digest256.Finish(hash);
        Span<byte> bytes = stackalloc byte[Digest256.Size];
        digest.Write(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private static string MakeRelativePath(string projectRoot, string fullPath)
    {
        string relativePath = Path.GetRelativePath(Path.GetFullPath(projectRoot), Path.GetFullPath(fullPath));
        return relativePath.Replace('\\', '/');
    }
}

