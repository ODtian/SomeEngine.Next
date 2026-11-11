using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Comparison;

internal static class FileStoreChecks
{
    internal static void Run()
    {
        Span<byte> payload = stackalloc byte[4];
        foreach (bool authenticated in new[] { false, true })
        {
            string path = Path.Combine("artifacts", authenticated ? "file-store-hmac.save" : "file-store-sha.save");
            try
            {
                DurableSaveStoreOptions options = authenticated
                    ? new() { AuthenticationKey = new byte[32] } : new();
                using var store = new DurableSaveStore(path, options);
                for (int generation = 1; generation <= 2; generation++)
                {
                    using var write = store.BeginWrite();
                    write.Stream.Write(new byte[] { 1, 2, 3, (byte)generation });
                    DurableSaveCommit committed = write.Commit();
                    if (committed.Generation != (ulong)generation || committed.PayloadLength != 4)
                        throw new InvalidOperationException("File commit generation or payload length differs.");
                }
                using var read = store.BeginRead();
                read.Stream.ReadExactly(payload);
                if (read.Generation != 2 || read.PayloadLength != 4 || !payload.SequenceEqual(new byte[] { 1, 2, 3, 2 })
                    || read.Stream.ReadByte() != -1)
                    throw new InvalidOperationException("Verified latest file generation differs.");
                Console.WriteLine($"File service authenticated={authenticated}: two commits, latest generation, verified payload and scope disposal passed.");
            }
            finally
            {
                File.Delete(path); File.Delete(path + ".previous"); File.Delete(path + ".lock");
            }
        }
    }
}
