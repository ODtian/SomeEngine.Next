using System.Diagnostics;
using System.Globalization;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;

/// <summary>Measures the public byte acquisition path against consumers' direct array access.</summary>
internal static class AssetByteCacheMeasurements
{
    internal static async Task<int> RunAsync()
    {
        const int count = 4096, reads = 1048576, samples = 9;
        string directory = Path.Combine(Path.GetTempPath(), "SomeEngine-ByteCache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "data.shader.asset");
            using (var asset = new ShaderAsset { Name = "byte data" })
            {
                ShaderBlock block = asset.GetBlock<ShaderBlock>();
                for (int index = 0; index < count; index++)
                {
                    byte[] bytes = new byte[256];
                    bytes[0] = (byte)index;
                    block.SetBytes((ulong)index + 1, bytes);
                }
                await asset.SaveAsync(path);
            }
            var cache = new AssetByteCache(64L * 1024 * 1024);
            await using AssetFile file = await AssetFile.OpenAsync(path, ShaderAsset.WireType, byteCache: cache);
            var header = new AssetHeader();
            file.ReadRootInto(header);
            using var source = new AssetBlockSource(file,
                header.BlockDescriptions.Single(description => description.BlockTypeId == ShaderBlock.BlockTypeId));
            var direct = new ReadOnlyMemory<byte>[count];
            for (int index = 0; index < count; index++) direct[index] = await source.ReadBytesAsync((ulong)index + 1);
            _ = Measure(source, direct, reads, useCache: false);
            _ = Measure(source, direct, reads, useCache: true);
            var referenceTimes = new double[samples];
            var cacheTimes = new double[samples];
            long hitAllocations = 0;
            for (int sample = 0; sample < samples; sample++)
            {
                referenceTimes[sample] = Measure(source, direct, reads, useCache: false).Nanoseconds;
                var result = Measure(source, direct, reads, useCache: true);
                cacheTimes[sample] = result.Nanoseconds;
                hitAllocations += result.Allocated;
            }
            Array.Sort(referenceTimes);
            Array.Sort(cacheTimes);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"byte-cache: dotnet={Environment.Version} items={cache.CachedItems} data-bytes={cache.CachedBytes} index-bytes={cache.IndexBytes} samples={samples} reads-per-sample={reads} direct-p50={referenceTimes[samples / 2]:F2}ns acquisition-p50={cacheTimes[samples / 2]:F2}ns acquisition-max={cacheTimes[^1]:F2}ns hit-allocated={hitAllocations} checksum={s_checksum}"));
            return 0;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static long s_checksum;
    private static (double Nanoseconds, long Allocated) Measure(AssetBlockSource source,
        ReadOnlyMemory<byte>[] direct, int reads, bool useCache)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp(), sum = 0;
        uint random = 42;
        for (int index = 0; index < reads; index++)
        {
            random = random * 1664525 + 1013904223;
            int item = (int)(random & (uint)(direct.Length - 1));
            ReadOnlyMemory<byte> bytes = useCache ? source.ReadBytesAsync((ulong)item + 1).Result : direct[item];
            sum += bytes.Span[0];
        }
        long elapsed = Stopwatch.GetTimestamp() - started;
        s_checksum = sum;
        return (elapsed * 1_000_000_000.0 / Stopwatch.Frequency / reads,
            GC.GetAllocatedBytesForCurrentThread() - allocated);
    }
}
