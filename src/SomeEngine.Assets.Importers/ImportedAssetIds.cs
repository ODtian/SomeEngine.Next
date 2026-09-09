using System.Security.Cryptography;
using System.Text;

namespace SomeEngine.Assets.Importers;

internal static class ImportedAssetIds
{
    public static Guid Create(SourceGuid sourceGuid, string subAssetKey)
    {
        if (sourceGuid.IsEmpty || string.IsNullOrWhiteSpace(subAssetKey))
        {
            return Guid.Empty;
        }

        Span<byte> namespaceBytes = stackalloc byte[16];
        if (!sourceGuid.Value.TryWriteBytes(namespaceBytes))
            throw new InvalidOperationException("Unable to encode the source GUID.");
        SwapOrder(namespaceBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        hash.AppendData(namespaceBytes);
        AppendUtf8(hash, subAssetKey);

        Span<byte> digest = stackalloc byte[20];
        if (!hash.TryGetHashAndReset(digest, out int written) || written != digest.Length)
            throw new CryptographicException("SHA-1 did not produce the UUID-v5 digest.");
        Span<byte> guidBytes = digest[..16];
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        SwapOrder(guidBytes);
        return new Guid(guidBytes);
    }

    private static void AppendUtf8(IncrementalHash hash, ReadOnlySpan<char> value)
    {
        Encoder encoder = Encoding.UTF8.GetEncoder();
        Span<byte> buffer = stackalloc byte[256];
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
        }
        while (true);
    }

    private static void SwapOrder(Span<byte> guid)
    {
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
